using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal sealed record SpeculativePlannerInput(
    FrameObservation Surface,
    string SessionId,
    int Revision,
    AutoMappingCampaignState? Campaign,
    ApplicationPlanningProfile ApplicationProfile,
    UiKnowledgeGraph? ExistingGraph = null);

internal sealed record SpeculativePlannerResult(
    string SurfaceFingerprint,
    IReadOnlyList<SpeculativePredictionState> Predictions,
    long ElapsedMilliseconds,
    bool SoftBudgetExceeded);

internal static class SpeculativeFrontierPlanner
{
    internal const int MaximumWidth = 3;
    internal const int MaximumDepth = 2;
    internal const int MaximumPredictedStates = 12;
    internal static readonly TimeSpan SoftBudget = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan HardBudget = TimeSpan.FromMilliseconds(500);
    internal static int MaximumWorkers => Math.Min(4, Environment.ProcessorCount);

    public static async Task<SpeculativePlannerResult> PlanAsync(
        SpeculativePlannerInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var timer = Stopwatch.StartNew();
        using var hardDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardDeadline.CancelAfter(HardBudget);
        var surfaceFingerprint = SurfaceFingerprint(input.Surface);
        var analyzers = new Func<IReadOnlyList<PlanningCandidate>>[]
        {
            () => AnalyzeTabs(input.Surface),
            () => AnalyzeCommands(input.Surface),
            () => AnalyzeDialogLaunchers(input.Surface),
            () => AnalyzeBackstage(input.Surface),
            () => AnalyzeDisclosures(input.Surface),
            () => AnalyzeOrdinaryControls(input.Surface)
        };

        using var concurrency = new SemaphoreSlim(MaximumWorkers, MaximumWorkers);
        var tasks = analyzers.Select((analyzer, ordinal) => Task.Run(async () =>
        {
            await concurrency.WaitAsync(hardDeadline.Token).ConfigureAwait(false);
            try
            {
                hardDeadline.Token.ThrowIfCancellationRequested();
                return (ordinal, candidates: analyzer());
            }
            finally
            {
                concurrency.Release();
            }
        }, hardDeadline.Token)).ToArray();

        var results = new List<(int ordinal, IReadOnlyList<PlanningCandidate> candidates)>();
        foreach (var task in tasks)
        {
            try { results.Add(await task.ConfigureAwait(false)); }
            catch (OperationCanceledException) when (hardDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        }
        cancellationToken.ThrowIfCancellationRequested();

        var ranked = results.OrderBy(result => result.ordinal)
            .SelectMany(result => result.candidates)
            .GroupBy(candidate => candidate.ActionFingerprint, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.BaseConfidence)
                .ThenBy(candidate => candidate.SourceOrdinal)
                .ThenBy(candidate => candidate.ActionFingerprint, StringComparer.Ordinal)
                .First())
            .Select(candidate => Score(candidate, input))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.ActionFingerprint, StringComparer.Ordinal)
            .ToArray();

        var roots = ranked.Take(MaximumWidth).ToArray();
        var predictions = new List<SpeculativePredictionState>(MaximumPredictedStates);
        foreach (var root in roots)
        {
            var rootPrediction = CreatePrediction(input, surfaceFingerprint, root, null, 1, root.Confidence);
            predictions.Add(rootPrediction);
            foreach (var child in ranked.Where(candidate => candidate.ActionFingerprint != root.ActionFingerprint)
                         .Take(MaximumWidth))
            {
                if (predictions.Count >= MaximumPredictedStates) break;
                predictions.Add(CreatePrediction(
                    input, surfaceFingerprint, child, rootPrediction.PredictionId, 2,
                    Math.Clamp(root.Confidence * child.Confidence * 0.92, 0, 1)));
            }
        }

        timer.Stop();
        return new(surfaceFingerprint, predictions, timer.ElapsedMilliseconds, timer.Elapsed > SoftBudget);
    }

    public static string SurfaceFingerprint(FrameObservation frame)
    {
        var material = string.Join('\n', frame.Automation
            .Select(control => AutoMappingTargetFingerprint.Create(control, frame.Window.Bounds))
            .Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }

    private static RankedCandidate Score(PlanningCandidate candidate, SpeculativePlannerInput input)
    {
        var campaignItem = input.Campaign?.Items.FirstOrDefault(item =>
            string.Equals(item.TargetFingerprint, candidate.LegacyFingerprint, StringComparison.Ordinal));
        var localMatched = input.ExistingGraph?.Nodes.Any(node =>
            node.Kind == GraphNodeKind.State && Property(node, "layer") == "prediction" &&
            Property(node, "actionFingerprint") == candidate.ActionFingerprint &&
            Property(node, "predictionStatus") == SpeculativePredictionStatus.Matched.ToString()) == true;
        var appRule = input.ApplicationProfile.Rules.FirstOrDefault(rule =>
            string.Equals(rule.ActionFingerprint, candidate.ActionFingerprint, StringComparison.Ordinal) && rule.IsReusable);

        var confidence = candidate.BaseConfidence;
        var source = candidate.KnowledgeSource;
        if (campaignItem?.Status == AutoMappingWorkStatus.Succeeded || localMatched)
        {
            confidence += 0.28;
            source = "local-confirmed";
        }
        else if (appRule is not null)
        {
            confidence += Math.Min(0.22, appRule.SuccessRate * 0.22);
            source = "application-profile";
        }
        confidence += InformationGain(candidate.Kind) * 0.12;
        return new(candidate, Math.Clamp(confidence, 0.05, 0.99), source);
    }

    private static SpeculativePredictionState CreatePrediction(
        SpeculativePlannerInput input,
        string surfaceFingerprint,
        RankedCandidate candidate,
        string? parentId,
        int depth,
        double confidence)
    {
        var material = $"{surfaceFingerprint}\n{parentId}\n{candidate.ActionFingerprint}\n{depth}";
        var id = "prediction:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
        return new(
            id,
            parentId,
            surfaceFingerprint,
            candidate.ActionFingerprint,
            candidate.DisplayName,
            candidate.Kind,
            candidate.ExpectedOutcomeKind,
            confidence,
            depth,
            input.Revision,
            candidate.KnowledgeSource,
            SpeculativePredictionStatus.Predicted,
            input.SessionId,
            input.Surface.Sequence,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<PlanningCandidate> AnalyzeTabs(FrameObservation frame) =>
        AutoTabDiscovery.Discover(frame).Select(candidate => Candidate(
            candidate.Observation,
            candidate.IsBackstage ? AutoMappingWorkKind.Backstage : AutoMappingWorkKind.Tab,
            candidate.IsBackstage ? "backstage" : "surface",
            candidate.DisplayName,
            candidate.IsSelected ? 0.48 : 0.68,
            0,
            "surface-structure",
            frame)).ToArray();

    private static IReadOnlyList<PlanningCandidate> AnalyzeCommands(FrameObservation frame)
    {
        var selected = AutoTabDiscovery.Discover(frame).FirstOrDefault(candidate => candidate.IsSelected);
        return selected is null ? [] : AutoRibbonCommandDiscovery.Discover(frame, selected)
            .Select(candidate => Candidate(candidate.Observation, AutoMappingWorkKind.Command, "popup-or-inline",
                candidate.DisplayName, 0.64, 1, "surface-structure", frame)).ToArray();
    }

    private static IReadOnlyList<PlanningCandidate> AnalyzeDialogLaunchers(FrameObservation frame)
    {
        var selected = AutoTabDiscovery.Discover(frame).FirstOrDefault(candidate => candidate.IsSelected);
        return selected is null ? [] : AutoRibbonDialogLauncherDiscovery.Discover(frame, selected)
            .Select(candidate => Candidate(candidate.Observation, AutoMappingWorkKind.DialogLauncher, "dialog",
                candidate.DisplayName, 0.72, 2, "surface-structure", frame)).ToArray();
    }

    private static IReadOnlyList<PlanningCandidate> AnalyzeBackstage(FrameObservation frame) =>
        AutoTabDiscovery.Discover(frame).Where(candidate => candidate.IsBackstage)
            .Select(candidate => Candidate(candidate.Observation, AutoMappingWorkKind.Backstage, "backstage",
                candidate.DisplayName, 0.66, 3, "surface-structure", frame)).ToArray();

    private static IReadOnlyList<PlanningCandidate> AnalyzeDisclosures(FrameObservation frame) => frame.Automation
        .Where(IsObservedSafeControl)
        .Where(control => control.SupportedPatterns?.Any(pattern => pattern.Contains("ExpandCollapse", StringComparison.OrdinalIgnoreCase)) == true)
        .Select(control => Candidate(control, AutoMappingWorkKind.Disclosure, "expanded-surface", DisplayName(control),
            0.57, 4, "affordance", frame)).ToArray();

    private static IReadOnlyList<PlanningCandidate> AnalyzeOrdinaryControls(FrameObservation frame) => frame.Automation
        .Where(IsObservedSafeControl)
        .Where(control => control.SupportedPatterns?.Any(pattern =>
            pattern.Contains("Invoke", StringComparison.OrdinalIgnoreCase) ||
            pattern.Contains("SelectionItem", StringComparison.OrdinalIgnoreCase)) == true)
        .Where(control => !AutoRibbonCommandDiscovery.IsForbiddenAutomaticAction(control))
        .Select(control => Candidate(control, AutoMappingWorkKind.NavigationItem, "surface-change", DisplayName(control),
            0.34, 5, "affordance", frame)).ToArray();

    private static bool IsObservedSafeControl(AutomationObservation control) =>
        control.IsEnabled && !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0;

    private static PlanningCandidate Candidate(
        AutomationObservation control,
        AutoMappingWorkKind kind,
        string expectedOutcome,
        string displayName,
        double confidence,
        int sourceOrdinal,
        string source,
        FrameObservation frame) => new(
            SpeculativeActionFingerprint.Create(control, frame),
            AutoMappingTargetFingerprint.Create(control, frame.Window.Bounds),
            displayName,
            kind,
            expectedOutcome,
            confidence,
            sourceOrdinal,
            source);

    private static string DisplayName(AutomationObservation control) =>
        !string.IsNullOrWhiteSpace(control.Name) ? control.Name.Trim() :
        !string.IsNullOrWhiteSpace(control.AutomationId) ? control.AutomationId.Trim() : control.ControlType.Trim();

    private static double InformationGain(AutoMappingWorkKind kind) => kind switch
    {
        AutoMappingWorkKind.Tab or AutoMappingWorkKind.Backstage => 1.0,
        AutoMappingWorkKind.DialogLauncher => 0.92,
        AutoMappingWorkKind.Command => 0.82,
        AutoMappingWorkKind.Disclosure => 0.62,
        _ => 0.45
    };

    private static string? Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;

    private sealed record PlanningCandidate(
        string ActionFingerprint,
        string LegacyFingerprint,
        string DisplayName,
        AutoMappingWorkKind Kind,
        string ExpectedOutcomeKind,
        double BaseConfidence,
        int SourceOrdinal,
        string KnowledgeSource);

    private sealed record RankedCandidate(PlanningCandidate Candidate, double Confidence, string KnowledgeSource)
    {
        public string ActionFingerprint => Candidate.ActionFingerprint;
        public string DisplayName => Candidate.DisplayName;
        public AutoMappingWorkKind Kind => Candidate.Kind;
        public string ExpectedOutcomeKind => Candidate.ExpectedOutcomeKind;
    }
}

internal sealed class SpeculativePlanningCoordinator : IDisposable
{
    private readonly RecorderWorkspace _workspace;
    private readonly ApplicationPlanningProfileStore _profileStore;
    private ApplicationPlanningProfile _profile;
    private CancellationTokenSource? _revisionCancellation;
    private SpeculativePlanningState _state;
    private readonly UiKnowledgeGraph? _existingGraph;
    private FrameObservation? _currentFrame;

    public SpeculativePlanningCoordinator(RecorderWorkspace workspace, WindowTarget target)
    {
        _workspace = workspace;
        _profileStore = new ApplicationPlanningProfileStore(new LocalArtifactCatalog().Root);
        var key = new ApplicationPlanningProfileKey(
            target.ProcessName,
            target.ProductName ?? string.Empty,
            MajorVersion(target.ProductVersion),
            target.ClassName ?? string.Empty);
        try { _profile = _profileStore.Load(key); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _profile = ApplicationPlanningProfile.Empty(key, DateTimeOffset.UtcNow);
        }
        _state = SpeculativePlanningRecovery.Recover(
            workspace.SpeculativePlanning,
            workspace.RecordingEvidence(),
            DateTimeOffset.UtcNow);
        if (workspace.SpeculativePlanning is not null && _state != workspace.SpeculativePlanning)
            workspace.SaveSpeculativePlanning(_state);
        try { _existingGraph = File.Exists(workspace.MapPath) ? SqliteGraphStore.Load(workspace.MapPath) : null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _existingGraph = null;
        }
    }

    public SpeculativePlanningState State => _state;

    public bool IsSurfaceObserved(FrameObservation frame) =>
        (_state.ObservedSurfaceFingerprints ?? []).Contains(
            SpeculativeFrontierPlanner.SurfaceFingerprint(frame), StringComparer.Ordinal);

    public async Task PrepareAsync(
        FrameObservation frame,
        AutoMappingCampaignState? campaign,
        string sessionId,
        UiKnowledgeGraph? graph,
        CancellationToken cancellationToken)
    {
        var fingerprint = SpeculativeFrontierPlanner.SurfaceFingerprint(frame);
        if (_state.SurfaceFingerprint == fingerprint && _state.Predictions.Any(prediction =>
                prediction.Status == SpeculativePredictionStatus.Predicted &&
                prediction.Revision == _state.SurfaceRevision && prediction.SourceSessionId == sessionId))
        {
            _currentFrame = frame;
            return;
        }

        _revisionCancellation?.Cancel();
        _revisionCancellation?.Dispose();
        _revisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var revision = _state.SurfaceRevision + 1;
        SpeculativePlannerResult planned;
        try
        {
            planned = await SpeculativeFrontierPlanner.PlanAsync(
                new(frame, sessionId, revision, campaign, _profile, graph ?? _existingGraph),
                _revisionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_revisionCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var historical = _state.Predictions.Select(prediction =>
            prediction.Status == SpeculativePredictionStatus.Predicted
                ? prediction with { Status = SpeculativePredictionStatus.Stale, UpdatedUtc = DateTimeOffset.UtcNow }
                : prediction).ToDictionary(prediction => prediction.PredictionId, StringComparer.Ordinal);
        foreach (var prediction in planned.Predictions)
            historical[prediction.PredictionId] = prediction;
        var predictions = TrimHistory(historical.Values);
        var newSurface = !string.Equals(_state.SurfaceFingerprint, planned.SurfaceFingerprint, StringComparison.Ordinal);
        var observedSurfaces = (_state.ObservedSurfaceFingerprints ?? [])
            .Append(planned.SurfaceFingerprint).Distinct(StringComparer.Ordinal).ToArray();
        _state = new(
            SpeculativePlanningState.CurrentFormatVersion,
            revision,
            planned.SurfaceFingerprint,
            predictions,
            _state.Coverage with
            {
                ControlsObserved = Math.Max(_state.Coverage.ControlsObserved,
                    frame.Automation.Count(control => control.IsEnabled && !control.IsOffscreen)),
                SurfacesObserved = _state.Coverage.SurfacesObserved + (newSurface ? 1 : 0)
            },
            _state.Metrics with
            {
                Prepared = _state.Metrics.Prepared + planned.Predictions.Count,
                LastPlanningMilliseconds = planned.ElapsedMilliseconds
            },
            DateTimeOffset.UtcNow,
            observedSurfaces);
        _currentFrame = frame;
        _workspace.SaveSpeculativePlanning(_state);
    }

    public IReadOnlyList<T> RankObserved<T>(
        IEnumerable<T> candidates,
        Func<T, AutomationObservation> observation,
        RectI rootBounds)
    {
        var ranks = _state.Predictions
            .Where(item => item.Depth == 1 &&
                           item.Status is SpeculativePredictionStatus.Predicted or SpeculativePredictionStatus.Matched)
            .GroupBy(item => item.ActionFingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Confidence), StringComparer.Ordinal);
        return candidates.OrderByDescending(candidate =>
                ranks.GetValueOrDefault(ActionFingerprint(observation(candidate), rootBounds), -1))
            .ThenBy(candidate => ActionFingerprint(observation(candidate), rootBounds), StringComparer.Ordinal)
            .ToArray();
    }

    public void MarkReused(AutomationObservation target, RectI rootBounds)
    {
        var fingerprint = ActionFingerprint(target, rootBounds);
        if (!_state.Predictions.Any(item => item.Status == SpeculativePredictionStatus.Predicted &&
                                           item.ActionFingerprint == fingerprint))
            return;
        _state = _state with
        {
            Metrics = _state.Metrics with { Reused = _state.Metrics.Reused + 1 },
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        _workspace.SaveSpeculativePlanning(_state);
    }

    public void RecordOutcome(
        AutomationObservation target,
        RectI rootBounds,
        bool confirmed,
        string resultSessionId,
        long? resultFrameSequence)
    {
        var fingerprint = ActionFingerprint(target, rootBounds);
        var affected = _state.Predictions.Where(item => item.Revision == _state.SurfaceRevision &&
            item.Depth == 1 && item.ActionFingerprint == fingerprint &&
            item.Status == SpeculativePredictionStatus.Predicted).ToArray();
        if (affected.Length == 0)
            return;
        var now = DateTimeOffset.UtcNow;
        var status = confirmed ? SpeculativePredictionStatus.Matched : SpeculativePredictionStatus.Rejected;
        var ids = affected.Select(item => item.PredictionId).ToHashSet(StringComparer.Ordinal);
        var predictions = _state.Predictions.Select(item => ids.Contains(item.PredictionId)
            ? item with
            {
                Status = status,
                ResultSessionId = resultFrameSequence is null ? null : resultSessionId,
                ResultFrameSequence = resultFrameSequence,
                UpdatedUtc = now
            }
            : item).ToArray();
        _state = _state with
        {
            SurfaceFingerprint = string.Empty,
            Predictions = predictions,
            Coverage = _state.Coverage with
            {
                TransitionsConfirmed = _state.Coverage.TransitionsConfirmed + (confirmed ? 1 : 0)
            },
            Metrics = _state.Metrics with
            {
                Matched = _state.Metrics.Matched + (confirmed ? affected.Length : 0),
                Rejected = _state.Metrics.Rejected + (confirmed ? 0 : affected.Length)
            },
            UpdatedUtc = now
        };
        _workspace.SaveSpeculativePlanning(_state);

        var representative = affected.OrderBy(item => item.Depth).First();
        try
        {
            _profile = _profileStore.RecordOutcome(
                _profile, fingerprint, representative.Kind, representative.ExpectedOutcomeKind, confirmed, now);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Map-local learning is already persisted; the cross-map profile is best effort.
        }
    }

    public void Dispose()
    {
        _revisionCancellation?.Cancel();
        _revisionCancellation?.Dispose();
    }

    private static IReadOnlyList<SpeculativePredictionState> TrimHistory(IEnumerable<SpeculativePredictionState> source)
    {
        var revisions = source.GroupBy(item => item.Revision).OrderByDescending(group => group.Key).Take(160)
            .SelectMany(group => group).OrderBy(item => item.Revision).ThenBy(item => item.Depth)
            .ThenBy(item => item.PredictionId, StringComparer.Ordinal).ToArray();
        return revisions.Length <= 2_000 ? revisions : revisions[^2_000..];
    }

    private static string MajorVersion(string? version)
    {
        var value = version?.Trim() ?? string.Empty;
        var separator = value.IndexOf('.');
        return separator > 0 ? value[..separator] : value;
    }

    private string ActionFingerprint(AutomationObservation target, RectI rootBounds) =>
        _currentFrame is not null
            ? SpeculativeActionFingerprint.Create(target, _currentFrame)
            : AutoMappingTargetFingerprint.Create(target, rootBounds);
}

internal static class SpeculativePlanningRecovery
{
    public static SpeculativePlanningState Recover(
        SpeculativePlanningState? stored,
        IReadOnlyList<LogicalMapSessionRecording> recordings,
        DateTimeOffset now)
    {
        if (stored is null)
            return SpeculativePlanningState.Empty(now);
        var framesBySession = ReadFrames(recordings);
        var changed = false;
        var predictions = stored.Predictions.Select(prediction =>
        {
            if (!framesBySession.TryGetValue(prediction.SourceSessionId, out var sourceFrames) ||
                !sourceFrames.Contains(prediction.SourceFrameSequence))
            {
                changed |= prediction.Status != SpeculativePredictionStatus.Stale;
                return prediction with { Status = SpeculativePredictionStatus.Stale, UpdatedUtc = now };
            }
            if (prediction.Status == SpeculativePredictionStatus.Matched &&
                (prediction.ResultSessionId is null || prediction.ResultFrameSequence is null ||
                 !framesBySession.TryGetValue(prediction.ResultSessionId, out var resultFrames) ||
                 !resultFrames.Contains(prediction.ResultFrameSequence.Value)))
            {
                changed = true;
                return prediction with
                {
                    Status = SpeculativePredictionStatus.Predicted,
                    ResultSessionId = null,
                    ResultFrameSequence = null,
                    UpdatedUtc = now
                };
            }
            return prediction;
        }).ToArray();
        return changed ? stored with
        {
            SurfaceFingerprint = string.Empty,
            Predictions = predictions,
            Metrics = stored.Metrics with
            {
                Matched = predictions.Count(item => item.Status == SpeculativePredictionStatus.Matched),
                Rejected = predictions.Count(item => item.Status == SpeculativePredictionStatus.Rejected)
            },
            UpdatedUtc = now
        } : stored;
    }

    private static IReadOnlyDictionary<string, HashSet<long>> ReadFrames(
        IReadOnlyList<LogicalMapSessionRecording> recordings)
    {
        var result = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var recording in recordings)
        {
            try
            {
                if (!File.Exists(recording.RecordingPath) || !RecordingBundleValidator.Validate(recording.RecordingPath).IsValid)
                    continue;
                using var bundle = RecordingBundle.Open(recording.RecordingPath);
                result[recording.SessionId] = bundle.Entries
                    .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                                    entry.EndsWith(".json", StringComparison.Ordinal))
                    .Select(bundle.ReadJson<FrameObservation>)
                    .Select(frame => frame.Sequence)
                    .ToHashSet();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Unsealed or malformed recordings cannot confirm speculative evidence.
            }
        }
        return result;
    }
}

internal static class SpeculativeActionFingerprint
{
    public static string Create(AutomationObservation target, FrameObservation frame)
    {
        var byRuntimeId = frame.Automation.Where(item => !string.IsNullOrWhiteSpace(item.RuntimeId))
            .GroupBy(item => item.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ancestors = new Stack<string>();
        var parentId = target.ParentRuntimeId;
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(parentId) &&
             byRuntimeId.TryGetValue(parentId, out var parent); depth++)
        {
            ancestors.Push(StructuralPart(parent));
            parentId = parent.ParentRuntimeId;
        }
        var material = string.Join('\n',
            Normalize(frame.Window.ClassName),
            string.Join('/', ancestors),
            StructuralPart(target),
            NormalizedBounds(target.Bounds, frame.Window.Bounds));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }

    private static string StructuralPart(AutomationObservation control) => string.Join('|',
        Normalize(control.AutomationId), NormalizeControlType(control.ControlType), Normalize(control.ClassName));

    private static string NormalizedBounds(RectI bounds, RectI root)
    {
        if (root.Width <= 0 || root.Height <= 0) return "unknown";
        static int Bucket(int value, int origin, int extent) =>
            Math.Clamp((int)Math.Round((value - origin) * 100.0 / extent), -25, 125);
        static int Size(int value, int extent) => Math.Clamp((int)Math.Round(value * 100.0 / extent), 0, 125);
        return string.Join('|', Bucket(bounds.X, root.X, root.Width), Bucket(bounds.Y, root.Y, root.Height),
            Size(bounds.Width, root.Width), Size(bounds.Height, root.Height));
    }

    private static string NormalizeControlType(string? value)
    {
        var normalized = Normalize(value);
        const string prefix = "controltype.";
        return normalized.StartsWith(prefix, StringComparison.Ordinal) ? normalized[prefix.Length..] : normalized;
    }

    private static string Normalize(string? value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

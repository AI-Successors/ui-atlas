using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal sealed record AutoMappingPlan<T>(
    IReadOnlyList<T> Ready,
    IReadOnlyList<string> AmbiguousItemIds);

internal static class AutoMappingCampaignPlanner
{
    public static AutoMappingPlan<T> Plan<T>(
        IEnumerable<T> candidates,
        Func<T, string> itemId,
        AutoMappingCampaignTracker campaign)
    {
        var groups = candidates.GroupBy(itemId, StringComparer.Ordinal).ToArray();
        var ambiguous = groups.Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        campaign.MarkAmbiguous(ambiguous);
        var ready = groups
            .Where(group => group.Count() == 1 && campaign.CanAttempt(group.Key))
            .Select(group => group.Single())
            .ToArray();
        return new(ready, ambiguous);
    }
}

internal sealed class AutoMappingCampaignExecutor(
    AutoMappingCampaignTracker campaign,
    string sessionId)
{
    public void Begin(string itemId, string interactionId) =>
        campaign.Start(itemId, sessionId, interactionId);

    public void Confirm(string itemId, string interactionId, IReadOnlyList<long> resultFrames) =>
        campaign.Succeed(itemId, sessionId, interactionId, resultFrames);

    public bool Reject(string itemId, string interactionId, string diagnosticCode) =>
        campaign.Fail(itemId, sessionId, interactionId, diagnosticCode);
}

internal sealed class AutoMappingCampaignTracker
{
    public const int MaximumAttempts = 2;

    private readonly Dictionary<string, AutoMappingWorkItemState> _items;
    private readonly Action<AutoMappingCampaignState> _persist;
    private int _revision;

    public AutoMappingCampaignTracker(
        AutoMappingCampaignState? state,
        Action<AutoMappingCampaignState> persist,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(persist);
        _persist = persist;
        var initial = state ?? AutoMappingCampaignState.Empty(now);
        _revision = initial.Revision;
        _items = initial.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);

        var changed = false;
        foreach (var item in _items.Values.Where(item => item.Status == AutoMappingWorkStatus.Running).ToArray())
        {
            _items[item.ItemId] = item with
            {
                Status = AutoMappingWorkStatus.Pending,
                Attempts = Math.Max(0, item.Attempts - 1),
                DiagnosticCode = "interrupted-before-checkpoint",
                UpdatedUtc = now
            };
            changed = true;
        }

        if (changed)
            Persist(now);
    }

    public AutoMappingCampaignState Snapshot(DateTimeOffset? now = null) =>
        new(
            AutoMappingCampaignState.CurrentFormatVersion,
            _revision,
            AutoMappingCampaignState.CurrentIdentityVersion,
            _items.Values.OrderBy(item => item.ItemId, StringComparer.Ordinal).ToArray(),
            now ?? DateTimeOffset.UtcNow);

    public string Register(
        AutoMappingWorkKind kind,
        AutomationObservation target,
        RectI rootBounds,
        string parentFingerprint = "")
    {
        ArgumentNullException.ThrowIfNull(target);
        var fingerprint = AutoMappingTargetFingerprint.Create(target, rootBounds);
        var itemId = AutoMappingTargetFingerprint.ItemId(kind, parentFingerprint, fingerprint);
        if (_items.TryGetValue(itemId, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.DisplayName))
            {
                _items[itemId] = existing with { DisplayName = DisplayName(target), UpdatedUtc = DateTimeOffset.UtcNow };
                Persist(DateTimeOffset.UtcNow);
            }
            return itemId;
        }

        var legacy = _items.Values.SingleOrDefault(item =>
            item.Kind == kind &&
            item.ParentFingerprint.Length == 0 &&
            string.Equals(item.TargetFingerprint, fingerprint, StringComparison.Ordinal));
        if (legacy is not null && parentFingerprint.Length > 0)
        {
            _items.Remove(legacy.ItemId);
            _items[itemId] = legacy with
            {
                ItemId = itemId,
                ParentFingerprint = parentFingerprint,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            Persist(DateTimeOffset.UtcNow);
            return itemId;
        }

        _items[itemId] = new(
            itemId,
            kind,
            AutoMappingWorkStatus.Pending,
            fingerprint,
            parentFingerprint,
            0,
            "",
            null,
            null,
            [],
            DateTimeOffset.UtcNow,
            DisplayName(target));
        Persist(DateTimeOffset.UtcNow);
        return itemId;
    }

    public void MarkAmbiguous(IEnumerable<string> itemIds)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var itemId in itemIds.Distinct(StringComparer.Ordinal))
        {
            if (!_items.TryGetValue(itemId, out var item) || item.Status == AutoMappingWorkStatus.Succeeded)
                continue;
            _items[itemId] = item with
            {
                Status = AutoMappingWorkStatus.NeedsManual,
                DiagnosticCode = "ambiguous-target",
                UpdatedUtc = now
            };
            changed = true;
        }
        if (changed)
            Persist(now);
    }

    public bool CanAttempt(string itemId) =>
        _items.TryGetValue(itemId, out var item) &&
        item.Status is AutoMappingWorkStatus.Pending or AutoMappingWorkStatus.Failed &&
        item.Attempts < MaximumAttempts;

    public bool IsSucceeded(string itemId) =>
        _items.TryGetValue(itemId, out var item) && item.Status == AutoMappingWorkStatus.Succeeded;

    public bool IsTerminal(string itemId) =>
        _items.TryGetValue(itemId, out var item) &&
        item.Status is AutoMappingWorkStatus.Succeeded or AutoMappingWorkStatus.NeedsManual or AutoMappingWorkStatus.Skipped;

    public int Attempts(string itemId) => _items.TryGetValue(itemId, out var item) ? item.Attempts : 0;

    public AutoMappingWorkStatus? Status(string itemId) =>
        _items.TryGetValue(itemId, out var item) ? item.Status : null;

    public IReadOnlyList<AutoMappingWorkItemState> NeedsManualItems() => _items.Values
        .Where(item => item.Status == AutoMappingWorkStatus.NeedsManual)
        .OrderBy(item => item.Kind)
        .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.ItemId, StringComparer.Ordinal)
        .ToArray();

    public bool ConfirmManual(
        AutomationObservation target,
        RectI rootBounds,
        string sessionId,
        string interactionId,
        IReadOnlyList<long> resultFrames,
        string parentFingerprint = "")
    {
        var fingerprint = AutoMappingTargetFingerprint.Create(target, rootBounds);
        var matches = _items.Values.Where(item =>
            item.Status == AutoMappingWorkStatus.NeedsManual &&
            string.Equals(item.TargetFingerprint, fingerprint, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(item.ParentFingerprint) || string.IsNullOrWhiteSpace(parentFingerprint) ||
             string.Equals(item.ParentFingerprint, parentFingerprint, StringComparison.Ordinal))).ToArray();
        if (matches.Length != 1)
            return false;

        Succeed(matches[0].ItemId, sessionId, interactionId, resultFrames);
        return true;
    }

    public void Start(string itemId, string sessionId, string interactionId)
    {
        Update(itemId, item => item with
        {
            Status = AutoMappingWorkStatus.Running,
            Attempts = item.Attempts + 1,
            DiagnosticCode = "",
            LastSessionId = sessionId,
            LastInteractionId = interactionId,
            ResultFrameSequences = [],
            UpdatedUtc = DateTimeOffset.UtcNow
        });
    }

    public void Succeed(string itemId, string sessionId, string interactionId, IReadOnlyList<long> resultFrames)
    {
        Update(itemId, item => item with
        {
            Status = AutoMappingWorkStatus.Succeeded,
            DiagnosticCode = "",
            LastSessionId = sessionId,
            LastInteractionId = interactionId,
            ResultFrameSequences = resultFrames.ToArray(),
            UpdatedUtc = DateTimeOffset.UtcNow
        });
    }

    public void CompleteObservedSurface(string itemId, string sessionId, long frameSequence)
    {
        Update(itemId, item => item with
        {
            Status = AutoMappingWorkStatus.Succeeded,
            DiagnosticCode = "surface-observed",
            LastSessionId = sessionId,
            LastInteractionId = null,
            ResultFrameSequences = [frameSequence],
            UpdatedUtc = DateTimeOffset.UtcNow
        });
    }

    public bool Fail(string itemId, string sessionId, string interactionId, string diagnosticCode)
    {
        var needsManual = false;
        Update(itemId, item =>
        {
            needsManual = item.Attempts >= MaximumAttempts;
            return item with
            {
                Status = needsManual ? AutoMappingWorkStatus.NeedsManual : AutoMappingWorkStatus.Failed,
                DiagnosticCode = diagnosticCode,
                LastSessionId = sessionId,
                LastInteractionId = interactionId,
                ResultFrameSequences = [],
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        });
        return needsManual;
    }

    public void CompleteParent(string itemId, string sessionId, string interactionId, IReadOnlyList<long> resultFrames) =>
        Succeed(itemId, sessionId, interactionId, resultFrames);

    public string ProgressSummary()
    {
        var tabs = _items.Values.Where(item => item.Kind is AutoMappingWorkKind.Tab or AutoMappingWorkKind.Backstage).ToArray();
        var controls = _items.Values.Where(item => item.Kind is not (AutoMappingWorkKind.Tab or AutoMappingWorkKind.Backstage)).ToArray();
        var doneTabs = tabs.Count(item => item.Status == AutoMappingWorkStatus.Succeeded);
        var doneControls = controls.Count(item => item.Status == AutoMappingWorkStatus.Succeeded);
        var manual = _items.Values.Count(item => item.Status == AutoMappingWorkStatus.NeedsManual);
        var pending = _items.Values.Count(item => item.Status is AutoMappingWorkStatus.Pending or AutoMappingWorkStatus.Running or AutoMappingWorkStatus.Failed);
        return $"Tabs {doneTabs}/{tabs.Length} · elements {doneControls}/{controls.Length} · remaining {pending} · manual review {manual}";
    }

    private void Update(string itemId, Func<AutoMappingWorkItemState, AutoMappingWorkItemState> update)
    {
        if (!_items.TryGetValue(itemId, out var item))
            throw new InvalidOperationException("Auto-mapping work item is not registered.");
        _items[itemId] = update(item);
        Persist(DateTimeOffset.UtcNow);
    }

    private void Persist(DateTimeOffset now)
    {
        _revision++;
        _persist(Snapshot(now));
    }

    private static string DisplayName(AutomationObservation target)
    {
        if (!string.IsNullOrWhiteSpace(target.Name)) return target.Name.Trim();
        if (!string.IsNullOrWhiteSpace(target.AutomationId)) return target.AutomationId.Trim();
        return string.IsNullOrWhiteSpace(target.ControlType) ? "Unnamed control" : target.ControlType.Trim();
    }
}

internal static class AutoMappingTargetFingerprint
{
    public static string Create(AutomationObservation target, RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(target);
        var relative = NormalizeBounds(target.Bounds, rootBounds);
        var material = string.Join("\n",
            Normalize(target.AutomationId),
            NormalizeControlType(target.ControlType),
            Normalize(target.ClassName),
            relative);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }

    public static string ItemId(AutoMappingWorkKind kind, string parentFingerprint, string targetFingerprint)
    {
        var material = $"{kind}\n{parentFingerprint}\n{targetFingerprint}";
        return "auto:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }

    private static string NormalizeBounds(RectI bounds, RectI root)
    {
        if (root.Width <= 0 || root.Height <= 0)
            return "unknown";
        static int Bucket(int value, int origin, int extent) =>
            Math.Clamp((int)Math.Round((value - origin) * 100.0 / extent), -25, 125);
        static int SizeBucket(int value, int extent) =>
            Math.Clamp((int)Math.Round(value * 100.0 / extent), 0, 125);
        return string.Join('|',
            Bucket(bounds.X, root.X, root.Width),
            Bucket(bounds.Y, root.Y, root.Height),
            SizeBucket(bounds.Width, root.Width),
            SizeBucket(bounds.Height, root.Height));
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeControlType(string? value)
    {
        var normalized = Normalize(value);
        const string prefix = "controltype.";
        return normalized.StartsWith(prefix, StringComparison.Ordinal) ? normalized[prefix.Length..] : normalized;
    }
}

internal static class AutoMappingCampaignRecovery
{
    public static AutoMappingCampaignState Recover(
        AutoMappingCampaignState? stored,
        IReadOnlyList<LogicalMapSessionRecording> recordings,
        DateTimeOffset now)
    {
        var interactions = ReadInteractions(recordings);
        var items = (stored?.Items ?? []).ToDictionary(item => item.ItemId, StringComparer.Ordinal);

        if (stored is null)
            ImportLegacyInteractions(items, interactions, now);

        foreach (var item in items.Values.ToArray())
        {
            if (item.Status == AutoMappingWorkStatus.Succeeded && item.DiagnosticCode == "surface-observed")
            {
                if (item.LastSessionId is not null && item.ResultFrameSequences.Count > 0 &&
                    RecordingContainsFrames(recordings, item.LastSessionId, item.ResultFrameSequences))
                    continue;
                items[item.ItemId] = item with
                {
                    Status = AutoMappingWorkStatus.Pending,
                    DiagnosticCode = "missing-surface-evidence",
                    LastSessionId = null,
                    ResultFrameSequences = [],
                    UpdatedUtc = now
                };
                continue;
            }

            if (item.Status == AutoMappingWorkStatus.Running)
            {
                if (item.Kind != AutoMappingWorkKind.Tab &&
                    item.LastSessionId is not null && item.LastInteractionId is not null &&
                    interactions.TryGetValue((item.LastSessionId, item.LastInteractionId), out var runningEvidence) &&
                    EvidenceConfirms(item, runningEvidence))
                {
                    items[item.ItemId] = item with
                    {
                        Status = AutoMappingWorkStatus.Succeeded,
                        DiagnosticCode = "recovered-confirmed-checkpoint",
                        ResultFrameSequences = runningEvidence.Interaction.ResultFrameSequences.ToArray(),
                        UpdatedUtc = now
                    };
                    continue;
                }

                items[item.ItemId] = item with
                {
                    Status = AutoMappingWorkStatus.Pending,
                    Attempts = Math.Max(0, item.Attempts - 1),
                    DiagnosticCode = "interrupted-before-checkpoint",
                    UpdatedUtc = now
                };
                continue;
            }

            if (item.Status is not (AutoMappingWorkStatus.Succeeded or AutoMappingWorkStatus.Failed or AutoMappingWorkStatus.NeedsManual) ||
                item.DiagnosticCode == "ambiguous-target")
                continue;
            if (item.LastSessionId is null || item.LastInteractionId is null ||
                !interactions.TryGetValue((item.LastSessionId, item.LastInteractionId), out var evidence))
            {
                items[item.ItemId] = item with
                {
                    Status = AutoMappingWorkStatus.Pending,
                    DiagnosticCode = "missing-recording-evidence",
                    LastSessionId = null,
                    LastInteractionId = null,
                    ResultFrameSequences = [],
                    UpdatedUtc = now
                };
                continue;
            }

            if (item.Status == AutoMappingWorkStatus.Succeeded &&
                !EvidenceConfirms(item, evidence))
            {
                items[item.ItemId] = item with
                {
                    Status = AutoMappingWorkStatus.Pending,
                    DiagnosticCode = "checkpoint-not-confirmed",
                    ResultFrameSequences = [],
                    UpdatedUtc = now
                };
            }
        }

        return new(
            AutoMappingCampaignState.CurrentFormatVersion,
            Math.Max(0, stored?.Revision ?? 0) + 1,
            AutoMappingCampaignState.CurrentIdentityVersion,
            items.Values.OrderBy(item => item.ItemId, StringComparer.Ordinal).ToArray(),
            now);
    }

    private static bool RecordingContainsFrames(
        IReadOnlyList<LogicalMapSessionRecording> recordings,
        string sessionId,
        IReadOnlyList<long> requiredFrames)
    {
        var recording = recordings.FirstOrDefault(item => item.SessionId == sessionId);
        if (recording is null || !File.Exists(recording.RecordingPath))
            return false;
        try
        {
            if (!RecordingBundleValidator.Validate(recording.RecordingPath).IsValid)
                return false;
            using var bundle = RecordingBundle.Open(recording.RecordingPath);
            var available = bundle.Entries
                .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                                entry.EndsWith(".json", StringComparison.Ordinal))
                .Select(bundle.ReadJson<FrameObservation>)
                .Select(frame => frame.Sequence)
                .ToHashSet();
            return requiredFrames.All(available.Contains);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    private static bool EvidenceConfirms(AutoMappingWorkItemState item, RecordingEvidence evidence)
    {
        var interaction = evidence.Interaction;
        var sourceFingerprint = interaction.SourceControl is null
            ? string.Empty
            : AutoMappingTargetFingerprint.Create(interaction.SourceControl, evidence.RootBounds);
        var expectedFrames = item.ResultFrameSequences.Count > 0
            ? item.ResultFrameSequences
            : interaction.ResultFrameSequences;
        return interaction.Outcome == InteractionOutcome.Succeeded &&
               interaction.ResultFrameSequences.Count > 0 &&
               expectedFrames.Count > 0 &&
               expectedFrames.All(sequence => evidence.FrameSequences.Contains(sequence)) &&
               string.Equals(sourceFingerprint, item.TargetFingerprint, StringComparison.Ordinal);
    }

    private static Dictionary<(string SessionId, string InteractionId), RecordingEvidence> ReadInteractions(
        IReadOnlyList<LogicalMapSessionRecording> recordings)
    {
        var result = new Dictionary<(string, string), RecordingEvidence>();
        foreach (var recording in recordings)
        {
            if (!File.Exists(recording.RecordingPath))
                continue;
            try
            {
                if (!RecordingBundleValidator.Validate(recording.RecordingPath).IsValid)
                    continue;
                using var bundle = RecordingBundle.Open(recording.RecordingPath);
                if (!bundle.Entries.Contains("raw/interactions.jsonl"))
                    continue;
                var frames = bundle.Entries
                    .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                                    entry.EndsWith(".json", StringComparison.Ordinal))
                    .Select(bundle.ReadJson<FrameObservation>)
                    .ToDictionary(frame => frame.Sequence);
                foreach (var line in bundle.ReadText("raw/interactions.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var interaction = JsonSerializer.Deserialize<InteractionObservation>(line, JsonDefaults.Options);
                    if (interaction is not null)
                    {
                        var rootBounds = frames.TryGetValue(interaction.SourceFrameSequence, out var sourceFrame)
                            ? sourceFrame.Window.Bounds
                            : interaction.SourceControl?.Bounds ?? new RectI(0, 0, 0, 0);
                        result[(recording.SessionId, interaction.InteractionId)] = new(
                            interaction,
                            frames.Keys.ToHashSet(),
                            rootBounds);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                // Invalid bundles are rejected by the normal map path. Recovery only
                // avoids trusting them as evidence for an automatic click.
            }
        }
        return result;
    }

    private static void ImportLegacyInteractions(
        Dictionary<string, AutoMappingWorkItemState> items,
        IReadOnlyDictionary<(string SessionId, string InteractionId), RecordingEvidence> interactions,
        DateTimeOffset now)
    {
        foreach (var pair in interactions)
        {
            var interaction = pair.Value.Interaction;
            var kind = LegacyKind(interaction.OperationId);
            if (kind is null || interaction.SourceControl is null)
                continue;
            var fingerprint = AutoMappingTargetFingerprint.Create(interaction.SourceControl, pair.Value.RootBounds);
            var itemId = AutoMappingTargetFingerprint.ItemId(kind.Value, "", fingerprint);
            var succeeded = interaction.Outcome == InteractionOutcome.Succeeded && interaction.ResultFrameSequences.Count > 0;
            var previous = items.GetValueOrDefault(itemId);
            var attempts = Math.Max(previous?.Attempts ?? 0, interaction.Attempt);
            items[itemId] = new(
                itemId,
                kind.Value,
                succeeded ? AutoMappingWorkStatus.Succeeded : attempts >= AutoMappingCampaignTracker.MaximumAttempts
                    ? AutoMappingWorkStatus.NeedsManual
                    : AutoMappingWorkStatus.Failed,
                fingerprint,
                "",
                attempts,
                succeeded ? "" : interaction.DiagnosticCode,
                pair.Key.SessionId,
                interaction.InteractionId,
                interaction.ResultFrameSequences.ToArray(),
                now);
        }
    }

    private static AutoMappingWorkKind? LegacyKind(string operationId)
    {
        if (operationId.StartsWith("auto-command:", StringComparison.Ordinal)) return AutoMappingWorkKind.Command;
        if (operationId.StartsWith("auto-dialog:", StringComparison.Ordinal)) return AutoMappingWorkKind.DialogLauncher;
        if (operationId.StartsWith("auto-backstage:", StringComparison.Ordinal)) return AutoMappingWorkKind.Backstage;
        if (operationId.StartsWith("auto-tab:", StringComparison.Ordinal) || operationId.StartsWith("auto-menu:", StringComparison.Ordinal))
            return AutoMappingWorkKind.Tab;
        if (operationId.StartsWith("auto-outlook:", StringComparison.Ordinal)) return AutoMappingWorkKind.NavigationItem;
        if (operationId.StartsWith("auto-adobe:", StringComparison.Ordinal)) return AutoMappingWorkKind.Disclosure;
        return null;
    }

    private sealed record RecordingEvidence(
        InteractionObservation Interaction,
        IReadOnlySet<long> FrameSequences,
        RectI RootBounds);
}

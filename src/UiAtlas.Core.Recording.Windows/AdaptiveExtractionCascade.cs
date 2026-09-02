using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public sealed record AdaptiveExtractionResult(
    IReadOnlyList<AutomationObservation> Controls,
    AdaptiveExtractionSnapshot Snapshot,
    bool TimedOut,
    string Status);

public sealed record AdaptiveProbeRequest(
    string GapId,
    string SurfaceId,
    string Probe,
    RectI Bounds,
    double Potential,
    int EstimatedCostMs);

public sealed record AdaptiveEvidenceRequest(
    WindowTarget Surface,
    string SurfaceId,
    TimeSpan Timeout,
    int MaxNodes,
    RectI? ProbeBounds = null);

public enum ProviderCompatibilityStatus
{
    Healthy,
    ClientBridgeRegression,
    ProviderOpaque,
    TimedOut
}

public static class ProviderCompatibilityClassifier
{
    public static ProviderCompatibilityStatus Classify(
        IReadOnlyList<AutomationObservation> managed,
        IReadOnlyList<AutomationObservation> native,
        bool nativeTimedOut,
        bool hasOpaqueContainerGap)
    {
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(native);
        if (nativeTimedOut) return ProviderCompatibilityStatus.TimedOut;

        var managedShapes = managed.Where(IsUseful).Select(Shape).ToHashSet(StringComparer.Ordinal);
        var nativeUseful = native.Where(IsUseful).ToArray();
        var nativeAdditions = nativeUseful.Count(control => !managedShapes.Contains(Shape(control)));
        if (nativeAdditions >= Math.Max(3, managedShapes.Count / 5))
            return ProviderCompatibilityStatus.ClientBridgeRegression;
        if (hasOpaqueContainerGap && nativeAdditions < 3)
            return ProviderCompatibilityStatus.ProviderOpaque;
        return ProviderCompatibilityStatus.Healthy;
    }

    private static bool IsUseful(AutomationObservation control) =>
        !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
        Normalize(control.ControlType) is not ("Window" or "Pane" or "Group" or "Custom" or "Text");

    private static string Shape(AutomationObservation control) => string.Join('|',
        Normalize(control.ControlType), control.AutomationId.Trim().ToLowerInvariant(),
        control.ClassName.Trim().ToLowerInvariant(), control.Name.Trim().ToLowerInvariant(),
        control.Bounds.X / 6, control.Bounds.Y / 6, control.Bounds.Width / 6, control.Bounds.Height / 6);

    private static string Normalize(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
}

public interface IAdaptiveEvidenceCollector
{
    ControlEvidenceSource Source { get; }
    Task<ExtractionSourceResult> CollectAsync(AdaptiveEvidenceRequest request, CancellationToken cancellationToken);
}

public static class AdaptiveProbeScheduler
{
    private const double Decay = 0.82;
    private const double Threshold = 0.58;

    public static IReadOnlyList<AdaptiveProbeRequest> Select(
        IEnumerable<CoverageGapObservation> gaps,
        int remainingBudgetMs,
        int maximumProbes = 3)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var result = new List<AdaptiveProbeRequest>();
        var remaining = Math.Max(0, remainingBudgetMs);
        foreach (var gap in gaps
                     .Select(gap => (Gap: gap, FiredPotential: Decay * gap.Potential))
                     .Where(item => item.FiredPotential >= Threshold)
                     .OrderByDescending(item => item.FiredPotential - ProbeCost(item.Gap.NextProbe) / 3000d)
                     .ThenBy(item => item.Gap.GapId, StringComparer.Ordinal))
        {
            var cost = ProbeCost(gap.Gap.NextProbe);
            if (cost > remaining || result.Count >= maximumProbes) continue;
            result.Add(new(gap.Gap.GapId, gap.Gap.SurfaceId, gap.Gap.NextProbe,
                gap.Gap.Bounds, gap.FiredPotential, cost));
            remaining -= cost;
        }
        return result;
    }

    private static int ProbeCost(string probe) => probe switch
    {
        "msaa" => 700,
        "from-point" => 450,
        "subtree-point" => 650,
        "child-hwnd" => 650,
        _ => 800
    };
}

public static class CoverageGapDetector
{
    private static readonly HashSet<string> ContainerTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Pane", "Group", "Custom", "List", "Tree", "Menu", "Window" };

    public static IReadOnlyList<CoverageGapObservation> Detect(
        IReadOnlyList<WindowTarget> surfaces,
        IReadOnlyList<ExtractionSourceResult> sources,
        RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(sources);
        var result = new Dictionary<string, CoverageGapObservation>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Status == "timeout")
                Add(result, source.SurfaceId, CoverageGapKind.Timeout, rootBounds, 1.0, "msaa", diagnostic: "source-timeout");
            else if (source.Status is "node-limit" or "response-limit")
                Add(result, source.SurfaceId, CoverageGapKind.NodeLimit, rootBounds, .96, "msaa", diagnostic: source.Status);
        }

        var all = sources.SelectMany(source => source.Evidence).ToArray();
        foreach (var surface in surfaces)
        {
            var surfaceId = AdaptiveExtractionCascade.SurfaceId(surface, rootBounds);
            var controls = all.Where(item => item.SurfaceId == surfaceId && item.Source != ControlEvidenceSource.Win32).ToArray();
            if (controls.Length == 0)
            {
                var isRoot = surface.Hwnd == surface.RootOwnerHwnd;
                Add(result, surfaceId, isRoot ? CoverageGapKind.EmptyContainer : CoverageGapKind.ChildWindowUncovered,
                    surface.Bounds, .94, isRoot ? "msaa" : "child-hwnd", diagnostic: "surface-without-controls");
                continue;
            }

            var childParents = controls.Select(item => item.Control.ParentRuntimeId)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal);
            foreach (var evidence in controls)
            {
                var control = evidence.Control;
                if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                    Add(result, surfaceId, CoverageGapKind.EmptyBounds, control.Bounds, .55, "from-point",
                        control.RuntimeId, "empty-bounds");

                var type = NormalizeType(control.ControlType);
                var area = (long)Math.Max(0, control.Bounds.Width) * Math.Max(0, control.Bounds.Height);
                var rootArea = Math.Max(1L, (long)Math.Max(1, rootBounds.Width) * Math.Max(1, rootBounds.Height));
                if (ContainerTypes.Contains(type) && !childParents.Contains(control.RuntimeId) && area >= rootArea / 25)
                    Add(result, surfaceId, CoverageGapKind.LargeContainer, control.Bounds, .76, "from-point",
                        control.RuntimeId, "large-container-without-children");
            }

            foreach (var band in InferSparseCommandBand(surface.Bounds, controls.Select(item => item.Control)))
                Add(result, surfaceId, CoverageGapKind.LargeContainer, band, .99, "subtree-point",
                    diagnostic: "sparse-command-band");

            var reliableViews = controls.Where(item => item.Source is ControlEvidenceSource.UiaRaw or
                    ControlEvidenceSource.UiaControl or ControlEvidenceSource.UiaContent)
                .GroupBy(item => CandidateShape(item.Control), StringComparer.Ordinal);
            foreach (var group in reliableViews)
            {
                var distinct = group.Select(item => item.Source).Distinct().Count();
                if (distinct == 1 && sources.Count(source => source.SurfaceId == surfaceId &&
                        source.Source is ControlEvidenceSource.UiaRaw or ControlEvidenceSource.UiaControl or ControlEvidenceSource.UiaContent &&
                        source.Status is "ok" or "node-limit") >= 2)
                {
                    var sample = group.First();
                    Add(result, surfaceId, CoverageGapKind.ViewDivergence, sample.Control.Bounds, .67, "from-point",
                        sample.Control.RuntimeId, "uia-view-divergence");
                }
            }
        }
        return result.Values.OrderByDescending(gap => gap.Potential).ThenBy(gap => gap.GapId, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<RectI> InferSparseCommandBand(
        RectI surfaceBounds,
        IEnumerable<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (surfaceBounds.Width < 240 || surfaceBounds.Height < 240) return [];
        var visible = controls.Where(control => !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .ToArray();
        if (visible.Length is < 3 or >= 24) return [];

        var navigationBottom = surfaceBounds.Y + Math.Max(72, surfaceBounds.Height * 12 / 100);
        var topControls = visible.Where(control => control.Bounds.Y < navigationBottom).ToArray();
        if (topControls.Length < 3 || topControls.Length * 4 < visible.Length * 3) return [];

        var bandHeight = Math.Clamp(surfaceBounds.Height * 23 / 100, 150, 320);
        var columnWidth = Math.Max(1, surfaceBounds.Width / 3);
        return Enumerable.Range(0, 3)
            .Select(index => new RectI(
                surfaceBounds.X + index * columnWidth,
                surfaceBounds.Y,
                index == 2 ? surfaceBounds.Width - columnWidth * 2 : columnWidth,
                Math.Min(surfaceBounds.Height, bandHeight)))
            .ToArray();
    }

    internal static IReadOnlyList<CoverageGapObservation> FromCachedHints(
        string surfaceId,
        IReadOnlyList<AutomationObservation> observed,
        IEnumerable<AutomationObservation> cached,
        RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(cached);
        return cached
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                              IsContained(control.Bounds, rootBounds) &&
                              !observed.Any(actual => SameStableControl(actual, control)))
            .GroupBy(control => $"{control.Bounds.X / 48}:{control.Bounds.Y / 48}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(control =>
                    (long)control.Bounds.Width * control.Bounds.Height).First())
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .Take(6)
            .Select((control, index) => new CoverageGapObservation(
                "gap-cache-" + AdaptiveExtractionCascade.Hash(
                    $"{surfaceId}|{control.AutomationId}|{control.ControlType}|{control.ClassName}|{control.Bounds}", 20),
                surfaceId,
                CoverageGapKind.ViewDivergence,
                control.Bounds,
                Math.Max(.84, .98 - index * .02),
                "subtree-point",
                control.RuntimeId,
                "cached-control-not-observed"))
            .ToArray();
    }

    private static bool SameStableControl(AutomationObservation left, AutomationObservation right)
    {
        var sameType = NormalizeType(left.ControlType).Equals(NormalizeType(right.ControlType), StringComparison.OrdinalIgnoreCase);
        if (!sameType) return false;
        if (!string.IsNullOrWhiteSpace(left.AutomationId) &&
            left.AutomationId.Equals(right.AutomationId, StringComparison.OrdinalIgnoreCase) &&
            left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(left.Name) &&
               left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
               left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContained(RectI candidate, RectI root) =>
        candidate.X >= root.X && candidate.Y >= root.Y &&
        (long)candidate.X + candidate.Width <= (long)root.X + root.Width &&
        (long)candidate.Y + candidate.Height <= (long)root.Y + root.Height;

    private static void Add(IDictionary<string, CoverageGapObservation> values, string surfaceId,
        CoverageGapKind kind, RectI bounds, double potential, string probe, string runtime = "", string diagnostic = "")
    {
        var key = $"{surfaceId}|{kind}|{runtime}|{bounds.X / 8}:{bounds.Y / 8}:{bounds.Width / 8}:{bounds.Height / 8}";
        var id = "gap-" + AdaptiveExtractionCascade.Hash(key, 20);
        values.TryAdd(id, new(id, surfaceId, kind, bounds, potential, probe, runtime, diagnostic));
    }

    private static string CandidateShape(AutomationObservation control) => string.Join('|',
        NormalizeType(control.ControlType), control.AutomationId.Trim().ToLowerInvariant(),
        control.Name.Trim().ToLowerInvariant(), control.Bounds.X / 8, control.Bounds.Y / 8,
        control.Bounds.Width / 8, control.Bounds.Height / 8);

    private static string NormalizeType(string value) => value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
}

public static class ControlEvidenceMerger
{
    public static IReadOnlyList<MergedControlCandidate> Merge(IEnumerable<ExtractionSourceResult> sourceResults)
    {
        ArgumentNullException.ThrowIfNull(sourceResults);
        var groups = new List<List<ControlEvidenceObservation>>();
        foreach (var evidence in sourceResults.SelectMany(source => source.Evidence)
                     .Where(item => item.Source != ControlEvidenceSource.Win32)
                     .OrderBy(item => item.SurfaceId, StringComparer.Ordinal)
                     .ThenBy(item => item.EvidenceId, StringComparer.Ordinal))
        {
            var match = groups.FirstOrDefault(group => CanMerge(group[0], evidence));
            if (match is null) groups.Add([evidence]); else match.Add(evidence);
        }

        return groups.Select(group =>
        {
            var ordered = group.OrderByDescending(item => Reliability(item.Source) * item.Confidence)
                .ThenBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray();
            var chosen = ordered[0];
            var sources = ordered.Select(item => item.Source).Distinct().Order().ToArray();
            var confidence = 1d - ordered.Aggregate(1d, (value, item) => value * (1d - Math.Clamp(item.Confidence * Reliability(item.Source), 0, .99)));
            var conflict = ordered.Any(item => Conflicts(chosen.Control, item.Control));
            var status = conflict ? ExtractionCoverageStatus.Partial : sources.Length >= 2
                ? ExtractionCoverageStatus.Confirmed : ExtractionCoverageStatus.Observed;
            var idMaterial = chosen.SurfaceId + "|" + StableShape(chosen.Control);
            return new MergedControlCandidate("candidate-" + AdaptiveExtractionCascade.Hash(idMaterial, 24),
                chosen.SurfaceId, chosen.Control, ordered.Select(item => item.EvidenceId).ToArray(), sources,
                Math.Round(confidence, 4), status, conflict);
        }).OrderBy(item => item.SurfaceId, StringComparer.Ordinal).ThenBy(item => item.CandidateId, StringComparer.Ordinal).ToArray();
    }

    private static bool CanMerge(ControlEvidenceObservation left, ControlEvidenceObservation right)
    {
        if (left.SurfaceId != right.SurfaceId) return false;
        var a = left.Control;
        var b = right.Control;
        if (!string.IsNullOrWhiteSpace(a.AutomationId) && a.AutomationId.Equals(b.AutomationId, StringComparison.OrdinalIgnoreCase) &&
            Compatible(a, b)) return true;
        if (!string.IsNullOrWhiteSpace(a.RuntimeId) && a.RuntimeId == b.RuntimeId && Compatible(a, b)) return true;
        return IoU(a.Bounds, b.Bounds) >= .72 && Compatible(a, b) &&
               (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(b.Name) ||
                a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Compatible(AutomationObservation a, AutomationObservation b) =>
        NormalizeType(a.ControlType).Equals(NormalizeType(b.ControlType), StringComparison.OrdinalIgnoreCase) ||
        a.ControlType.Length == 0 || b.ControlType.Length == 0;

    private static bool Conflicts(AutomationObservation a, AutomationObservation b) =>
        !Compatible(a, b) ||
        !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(b.Name) &&
        !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(a.AutomationId) && !string.IsNullOrWhiteSpace(b.AutomationId) &&
        !a.AutomationId.Equals(b.AutomationId, StringComparison.OrdinalIgnoreCase);

    private static double IoU(RectI a, RectI b)
    {
        var x1 = Math.Max(a.X, b.X); var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width); var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0, x2 - x1) * (long)Math.Max(0, y2 - y1);
        var union = Math.Max(1L, (long)a.Width * a.Height + (long)b.Width * b.Height - intersection);
        return intersection / (double)union;
    }

    private static string StableShape(AutomationObservation value) => string.Join('|',
        NormalizeType(value.ControlType), value.AutomationId.Trim().ToLowerInvariant(),
        value.ClassName.Trim().ToLowerInvariant(), value.Name.Trim().ToLowerInvariant(),
        value.Bounds.X / 8, value.Bounds.Y / 8, value.Bounds.Width / 8, value.Bounds.Height / 8);

    private static string NormalizeType(string value) => value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
    private static double Reliability(ControlEvidenceSource source) => source switch
    {
        ControlEvidenceSource.UiaRaw => .97,
        ControlEvidenceSource.UiaControl => .95,
        ControlEvidenceSource.UiaContent => .93,
        ControlEvidenceSource.UiaFromPoint => .9,
        ControlEvidenceSource.Msaa => .84,
        ControlEvidenceSource.ChildWindow => .82,
        ControlEvidenceSource.Visual => .55,
        _ => .98
    };
}

public static class AdaptiveExtractionCascade
{
    public static async Task<AdaptiveExtractionResult> EnrichAsync(
        ManualRecordingSession session,
        WindowTarget target,
        IReadOnlyList<AutomationObservation> rawControls,
        bool rawTimedOut,
        string rawStatus,
        TimeSpan remainingBudget,
        int maxNodes,
        CancellationToken cancellationToken,
        IReadOnlyList<AutomationObservation>? probeHints = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        var timer = Stopwatch.StartNew();
        var surfaces = WindowCatalog.ListScopedWindows(target).Take(8).ToArray();
        if (surfaces.Length == 0) surfaces = [target];
        var surfaceMap = surfaces.ToDictionary(surface => surface.Hwnd,
            surface => SurfaceId(surface, target.Bounds));
        var results = new List<ExtractionSourceResult>();

        foreach (var surface in surfaces)
        {
            var surfaceId = surfaceMap[surface.Hwnd];
            var windowControl = new AutomationObservation(
                $"win32:{surface.Hwnd:x}", "", "", surface.Title, "ControlType.Window", surface.ClassName,
                surface.Bounds, true, false, "Win32", surface.Hwnd);
            results.Add(SourceResult(ControlEvidenceSource.Win32, surfaceId, [windowControl], "ok", 0));
        }

        foreach (var group in rawControls.GroupBy(item => item.WindowHwnd == 0 ? target.RootOwnerHwnd : item.WindowHwnd))
        {
            var surfaceId = surfaceMap.GetValueOrDefault(group.Key) ?? surfaceMap[target.RootOwnerHwnd];
            results.Add(SourceResult(ControlEvidenceSource.UiaRaw, surfaceId, group, rawStatus, 0));
        }
        if (rawControls.Count == 0)
            results.Add(SourceResult(ControlEvidenceSource.UiaRaw, surfaceMap[target.RootOwnerHwnd], [], rawStatus, 0));

        var executedGapIds = new HashSet<string>(StringComparer.Ordinal);
        var rootSurfaceId = surfaceMap[target.RootOwnerHwnd];
        var initialGaps = CoverageGapDetector.Detect(surfaces, results, target.Bounds)
            .Concat(CoverageGapDetector.FromCachedHints(
                rootSurfaceId, rawControls, probeHints ?? [], target.Bounds))
            .GroupBy(gap => gap.GapId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var opaqueCandidate = initialGaps.Any(gap => gap.Kind is CoverageGapKind.EmptyContainer or
            CoverageGapKind.LargeContainer or CoverageGapKind.ViewDivergence);
        var providerCompatibility = ProviderCompatibilityStatus.Healthy;
        if (opaqueCandidate && Remaining(remainingBudget, timer) >= TimeSpan.FromMilliseconds(1_600))
        {
            var nativeTimer = Stopwatch.StartNew();
            var nativeGap = initialGaps
                .Where(gap => gap.Kind is CoverageGapKind.EmptyContainer or CoverageGapKind.LargeContainer or
                              CoverageGapKind.ViewDivergence)
                .OrderByDescending(gap => gap.Potential)
                .ThenBy(gap => gap.Bounds.Y)
                .First();
            var nativePoint = new RectI(
                nativeGap.Bounds.X + Math.Max(1, nativeGap.Bounds.Width / 2),
                nativeGap.Bounds.Y + Math.Max(1, nativeGap.Bounds.Height / 2), 1, 1);
            var native = await session.CollectNativePointAutomationAsync(
                target.RootOwnerHwnd,
                nativePoint,
                TimeSpan.FromMilliseconds(1_400),
                Math.Min(maxNodes, 64),
                cancellationToken).ConfigureAwait(false);
            var managedAtPoint = rawControls.Where(control =>
                control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                nativePoint.X >= control.Bounds.X && nativePoint.Y >= control.Bounds.Y &&
                nativePoint.X < control.Bounds.X + control.Bounds.Width &&
                nativePoint.Y < control.Bounds.Y + control.Bounds.Height).ToArray();
            providerCompatibility = ProviderCompatibilityClassifier.Classify(
                managedAtPoint, native.Items, native.TimedOut, opaqueCandidate);
            results.Add(SourceResult(
                ControlEvidenceSource.UiaRaw,
                rootSurfaceId,
                native.Items,
                "provider-" + ToDiagnosticName(providerCompatibility),
                (int)nativeTimer.ElapsedMilliseconds,
                "native-uia3"));
        }
        var hotRequests = AdaptiveProbeScheduler.Select(
                initialGaps.Where(gap => gap.NextProbe == "subtree-point"),
                (int)Math.Max(0, Remaining(remainingBudget, timer).TotalMilliseconds),
                3)
            .ToArray();
        foreach (var request in providerCompatibility == ProviderCompatibilityStatus.ProviderOpaque
                     ? Array.Empty<AdaptiveProbeRequest>()
                     : hotRequests)
        {
            if (Remaining(remainingBudget, timer) < TimeSpan.FromMilliseconds(2_350)) break;
            results.Add(await CollectProbeAsync(
                session, target, surfaces, request, remainingBudget, timer, maxNodes, cancellationToken)
                .ConfigureAwait(false));
            executedGapIds.Add(request.GapId);
        }

        // A mature application cache already identifies the likely blind spots.
        // Prefer its narrow probes over repeating two whole-window UIA walks.
        // Cached controls remain unverified and disabled, so this optimization
        // cannot authorize an automatic click by itself.
        var hasStableProbeCoverage = (probeHints?.Count ?? 0) >= 24;
        var needsAlternateViews = providerCompatibility != ProviderCompatibilityStatus.ProviderOpaque &&
                                  !hasStableProbeCoverage &&
                                  (rawTimedOut || rawStatus is not "ok" ||
                                   rawControls.Count(control => !control.IsOffscreen &&
                                       control.Bounds.Width > 0 && control.Bounds.Height > 0) < 24);
        foreach (var view in needsAlternateViews
                     ? new[] { AutomationTreeView.Control, AutomationTreeView.Content }
                     : Array.Empty<AutomationTreeView>())
        {
            // A killed provider gets a bounded cleanup window in UiaWorkerClient.
            // Do not begin a probe if that cleanup cannot fit in the public budget.
            if (Remaining(remainingBudget, timer) < TimeSpan.FromMilliseconds(2_350)) break;
            var viewTimer = Stopwatch.StartNew();
            var budget = Min(Remaining(remainingBudget, timer) - TimeSpan.FromMilliseconds(2_050), TimeSpan.FromMilliseconds(1_100));
            var read = await session.CollectAutomationViewAsync(target.RootOwnerHwnd, view, budget,
                Math.Min(maxNodes, 1_500), cancellationToken).ConfigureAwait(false);
            var source = view == AutomationTreeView.Control ? ControlEvidenceSource.UiaControl : ControlEvidenceSource.UiaContent;
            foreach (var group in read.Items.GroupBy(item => item.WindowHwnd == 0 ? target.RootOwnerHwnd : item.WindowHwnd))
            {
                var surfaceId = surfaceMap.GetValueOrDefault(group.Key) ?? surfaceMap[target.RootOwnerHwnd];
                results.Add(SourceResult(source, surfaceId, group, read.Status, (int)viewTimer.ElapsedMilliseconds));
            }
            if (read.Items.Count == 0)
                results.Add(SourceResult(source, surfaceMap[target.RootOwnerHwnd], [], read.Status, (int)viewTimer.ElapsedMilliseconds));
        }

        var gaps = CoverageGapDetector.Detect(surfaces, results, target.Bounds);
        var requests = AdaptiveProbeScheduler.Select(
            gaps.Where(gap => !executedGapIds.Contains(gap.GapId)),
            (int)Math.Max(0, Remaining(remainingBudget, timer).TotalMilliseconds), 3);
        foreach (var request in providerCompatibility == ProviderCompatibilityStatus.ProviderOpaque
                     ? Array.Empty<AdaptiveProbeRequest>()
                     : requests)
        {
            if (Remaining(remainingBudget, timer) < TimeSpan.FromMilliseconds(2_350)) break;
            results.Add(await CollectProbeAsync(
                session, target, surfaces, request, remainingBudget, timer, maxNodes, cancellationToken)
                .ConfigureAwait(false));
            executedGapIds.Add(request.GapId);
        }

        var candidates = ControlEvidenceMerger.Merge(results);
        var finalGaps = CoverageGapDetector.Detect(surfaces, results, target.Bounds);
        if (providerCompatibility == ProviderCompatibilityStatus.ProviderOpaque)
        {
            finalGaps = finalGaps.Concat(initialGaps.Where(gap => gap.Kind is
                    CoverageGapKind.EmptyContainer or CoverageGapKind.LargeContainer or CoverageGapKind.ViewDivergence))
                .GroupBy(gap => gap.GapId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(gap => gap.Potential)
                .ThenBy(gap => gap.GapId, StringComparer.Ordinal)
                .ToArray();
        }
        var limitReached = rawTimedOut || results.Any(result => result.Status is
            "timeout" or "node-limit" or "response-limit" or "provider-timed-out");
        var status = candidates.Count == 0 ? ExtractionCoverageStatus.Unavailable
            : limitReached ? ExtractionCoverageStatus.LimitReached
            : finalGaps.Count > 0 ? ExtractionCoverageStatus.Partial
            : candidates.Any(candidate => candidate.CoverageStatus == ExtractionCoverageStatus.Confirmed)
                ? ExtractionCoverageStatus.Confirmed : ExtractionCoverageStatus.Observed;
        var stopReason = Remaining(remainingBudget, timer) <= TimeSpan.Zero ? "time-limit"
            : limitReached ? "source-limit" : finalGaps.Count == 0 ? "coverage-complete" : "probe-budget-exhausted";
        var snapshot = new AdaptiveExtractionSnapshot("adaptive-extraction/1", results, candidates, finalGaps,
            status, stopReason, (int)timer.ElapsedMilliseconds, executedGapIds.Count);
        var controls = candidates.Select(candidate => candidate.Control).ToArray();
        var aggregateStatus = status switch
        {
            ExtractionCoverageStatus.Confirmed or ExtractionCoverageStatus.Observed => "ok",
            ExtractionCoverageStatus.LimitReached => "node-limit",
            ExtractionCoverageStatus.Partial => "partial",
            _ => rawStatus
        };
        return new(controls, snapshot, limitReached, aggregateStatus);
    }

    private static async Task<ExtractionSourceResult> CollectProbeAsync(
        ManualRecordingSession session,
        WindowTarget target,
        IReadOnlyList<WindowTarget> surfaces,
        AdaptiveProbeRequest request,
        TimeSpan remainingBudget,
        Stopwatch timer,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var surface = surfaces.FirstOrDefault(value => SurfaceId(value, target.Bounds) == request.SurfaceId) ?? target;
        var probeTimer = Stopwatch.StartNew();
        var timeout = Min(Remaining(remainingBudget, timer) - TimeSpan.FromMilliseconds(2_050),
            TimeSpan.FromMilliseconds(request.EstimatedCostMs));
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) read;
        ControlEvidenceSource source;
        if (request.Probe == "msaa")
        {
            source = ControlEvidenceSource.Msaa;
            read = await session.CollectLegacyAutomationAsync(surface.Hwnd, timeout, Math.Min(maxNodes, 800), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var bounds = request.Bounds.Width > 0 && request.Bounds.Height > 0 ? request.Bounds : surface.Bounds;
            var point = new RectI(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, 1, 1);
            if (request.Probe == "subtree-point")
            {
                source = ControlEvidenceSource.UiaFromPoint;
                read = await session.CollectLocalSubtreeAutomationAsync(
                    point, timeout, Math.Min(maxNodes, 512), cancellationToken, surface.Hwnd).ConfigureAwait(false);
            }
            else if (request.Probe == "from-point")
            {
                source = ControlEvidenceSource.UiaFromPoint;
                read = await session.CollectPointAutomationAsync(
                    point, timeout, Math.Min(maxNodes, 64), cancellationToken, surface.Hwnd).ConfigureAwait(false);
            }
            else
            {
                source = ControlEvidenceSource.ChildWindow;
                read = await session.CollectAutomationViewAsync(surface.Hwnd, AutomationTreeView.Raw, timeout,
                    Math.Min(maxNodes, 800), cancellationToken).ConfigureAwait(false);
            }
        }
        return SourceResult(source, request.SurfaceId, read.Items, read.Status, (int)probeTimer.ElapsedMilliseconds);
    }

    internal static string SurfaceId(WindowTarget surface, RectI rootBounds)
    {
        var role = surface.Hwnd == surface.RootOwnerHwnd ? "root" : surface.OwnerHwnd != 0 ? "owned" : "child";
        var width = Math.Max(1, rootBounds.Width); var height = Math.Max(1, rootBounds.Height);
        var normalized = string.Join(':',
            (surface.Bounds.X - rootBounds.X) * 20 / width,
            (surface.Bounds.Y - rootBounds.Y) * 20 / height,
            surface.Bounds.Width * 20 / width,
            surface.Bounds.Height * 20 / height);
        return "surface-" + Hash($"{role}|{surface.ClassName.Trim().ToLowerInvariant()}|{normalized}", 24);
    }

    internal static string Hash(string value, int length) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..length];

    private static ExtractionSourceResult SourceResult(ControlEvidenceSource source, string surfaceId,
        IEnumerable<AutomationObservation> controls, string status, int durationMs, string technique = "")
    {
        var evidence = controls.Select((control, index) => new ControlEvidenceObservation(
            "evidence-" + Hash($"{source}|{surfaceId}|{control.RuntimeId}|{control.AutomationId}|{control.Bounds}|{index}", 24),
            source, surfaceId, control, SourceConfidence(source),
            string.Join(';', new[] { technique, status == "ok" ? "" : status }
                .Where(value => !string.IsNullOrWhiteSpace(value))))).ToArray();
        return new(source, surfaceId, evidence, status, durationMs);
    }

    private static string ToDiagnosticName(ProviderCompatibilityStatus status) => status switch
    {
        ProviderCompatibilityStatus.ClientBridgeRegression => "client-bridge-regression",
        ProviderCompatibilityStatus.ProviderOpaque => "opaque",
        ProviderCompatibilityStatus.TimedOut => "timed-out",
        _ => "healthy"
    };

    private static double SourceConfidence(ControlEvidenceSource source) => source switch
    {
        ControlEvidenceSource.UiaRaw => .96,
        ControlEvidenceSource.UiaControl => .94,
        ControlEvidenceSource.UiaContent => .92,
        ControlEvidenceSource.UiaFromPoint => .88,
        ControlEvidenceSource.Msaa => .82,
        ControlEvidenceSource.Visual => .52,
        _ => .9
    };

    private static TimeSpan Remaining(TimeSpan budget, Stopwatch timer) => budget - timer.Elapsed;
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}

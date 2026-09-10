using System.Text.Json;
using System.Globalization;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal sealed record RecordedHighlight(
    RectI CapturedRootBounds,
    string LayerKey,
    RectI Bounds);

internal sealed record MappedSurfaceHighlightSnapshot(
    string Id,
    string BundleId,
    long FrameSequence,
    string ClassName,
    string Title,
    string Role,
    RectI CapturedSurfaceBounds,
    IReadOnlyList<RectI> RelativeHighlightBounds,
    IReadOnlyList<AutomationObservation> IdentityControls)
{
    public bool IsPrimarySurface => Role is "root" or "root-owner";
}

internal static class RecordingHighlightHistory
{
    private const string RawDataStreamsLayer = "raw-data-streams";

    public static IReadOnlyList<RecordedHighlight> Load(IEnumerable<string> recordingPaths)
    {
        ArgumentNullException.ThrowIfNull(recordingPaths);
        var highlights = new List<RecordedHighlight>();
        foreach (var path in recordingPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            try
            {
                LoadBundle(path, highlights);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // A damaged historical bundle must not prevent the user from resuming
                // the healthy part of the logical map.
            }
        }

        return highlights
            .DistinctBy(highlight => RelativeIdentity(highlight))
            .ToArray();
    }

    public static IReadOnlyList<MappedSurfaceHighlightSnapshot> LoadMappedSurfaces(string mapPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        var nodes = SqliteGraphStore.ReadLayerNodes(mapPath, RawDataStreamsLayer);
        var surfaceNodes = nodes
            .Where(node => node.Kind == GraphNodeKind.Window)
            .ToArray();
        var controlsBySurface = nodes
            .Where(node => node.Kind == GraphNodeKind.Control)
            .Where(node => !IsTrue(Property(node, "curationHidden")))
            .GroupBy(node => Property(node, "rawDataStreamSurfaceId"), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var snapshots = new List<MappedSurfaceHighlightSnapshot>();

        foreach (var surfaceNode in surfaceNodes)
        {
            if (!controlsBySurface.TryGetValue(surfaceNode.Id, out var surfaceControlNodes))
                continue;

            foreach (var evidenceGroup in surfaceNode.Evidence
                         .Where(evidence => evidence.FrameSequence > 0 && evidence.Bounds is { IsValid: true })
                         .GroupBy(evidence => (evidence.BundleId, evidence.FrameSequence)))
            {
                var surfaceEvidence = evidenceGroup.First();
                var surfaceBounds = surfaceEvidence.Bounds!;
                var controls = surfaceControlNodes
                    .Select(node => ToControlView(node, surfaceNode.Id, evidenceGroup.Key.BundleId,
                        evidenceGroup.Key.FrameSequence))
                    .Where(control => control is not null)
                    .Cast<UiMapControlView>()
                    .Where(IsPresentableRawControl)
                    .ToArray();
                if (controls.Length == 0)
                    continue;

                var surfaceKind = Property(surfaceNode, "nativeWindowType") is { Length: > 0 } nativeKind
                    ? nativeKind
                    : Property(surfaceNode, "surfaceClass") is { Length: > 0 } surfaceClass
                        ? surfaceClass
                        : "RawWindow";
                var surface = new UiMapSurfaceView(
                    surfaceNode.Id,
                    UiUnderstandingLevel.RawDataStreams,
                    surfaceNode.Label,
                    surfaceKind,
                    surfaceNode.ParentId,
                    surfaceBounds,
                    controls.Length,
                    [],
                    [surfaceEvidence],
                    surfaceNode);
                var rendered = controls
                    .Where(control => !UiMapPresentation.IsStaleCachedControlForFrame(
                        control, evidenceGroup.Key.FrameSequence, evidenceGroup.Key.BundleId, controls))
                    .Where(control => !UiMapPresentation.IsRedundantCaptionButton(
                        control, evidenceGroup.Key.FrameSequence, evidenceGroup.Key.BundleId, controls))
                    .Where(control => !UiMapPresentation.IsRedundantCompositeBoundary(
                        control, evidenceGroup.Key.FrameSequence, evidenceGroup.Key.BundleId, controls))
                    .Where(control => !UiMapPresentation.IsRedundantPopupEditor(
                        control, surface, evidenceGroup.Key.FrameSequence, evidenceGroup.Key.BundleId, controls))
                    .Where(control => UiMapPresentation.ShouldRenderControl(
                        control, surface, UiMapProjectionMode.Overlay))
                    .Select(control => UiMapPresentation.ProjectToSurface(
                        UiMapPresentation.ResolveControlBounds(
                            control, evidenceGroup.Key.FrameSequence, evidenceGroup.Key.BundleId, controls),
                        surfaceBounds))
                    .Where(bounds => bounds is not null)
                    .Cast<RectI>()
                    .Distinct()
                    .Take(5_000)
                    .ToArray();
                if (rendered.Length == 0)
                    continue;

                snapshots.Add(new(
                    $"{surfaceNode.Id}:{evidenceGroup.Key.BundleId}:{evidenceGroup.Key.FrameSequence}",
                    evidenceGroup.Key.BundleId,
                    evidenceGroup.Key.FrameSequence,
                    Property(surfaceNode, "className"),
                    Property(surfaceNode, "title") is { Length: > 0 } title ? title : surfaceNode.Label,
                    Property(surfaceNode, "role"),
                    surfaceBounds,
                    rendered,
                    controls.Select(ToAutomationObservation).ToArray()));
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.BundleId, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.FrameSequence)
            .ThenBy(snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static MappedSurfaceHighlightSnapshot? SelectBestMappedSurface(
        IReadOnlyList<MappedSurfaceHighlightSnapshot> snapshots,
        WindowObservation currentWindow,
        IReadOnlyList<AutomationObservation> currentControls,
        bool currentIsPrimarySurface)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(currentWindow);
        ArgumentNullException.ThrowIfNull(currentControls);
        var candidates = snapshots
            .Where(snapshot => snapshot.IsPrimarySurface == currentIsPrimarySurface)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var exactClass = candidates.Where(snapshot => SameText(snapshot.ClassName, currentWindow.ClassName)).ToArray();
        if (exactClass.Length > 0)
            candidates = exactClass;
        else if (!currentIsPrimarySurface)
            return null;

        var exactTitle = candidates.Where(snapshot => SameText(snapshot.Title, currentWindow.Title)).ToArray();
        if (exactTitle.Length > 0)
            candidates = exactTitle;

        var currentKeys = currentControls
            .Where(control => control.IsEnabled && !control.IsOffscreen && control.Bounds.IsValid)
            .Select(ControlIdentityKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentSelection = SelectedNavigationKey(currentControls);
        var candidateSelections = candidates
            .Select(snapshot => SelectedNavigationKey(snapshot.IdentityControls))
            .Where(selection => selection.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (currentSelection.Length == 0 && candidateSelections.Length > 1)
            return null;
        if (currentSelection.Length > 0)
        {
            var snapshotsWithSelection = candidates
                .Select(snapshot => new
                {
                    Snapshot = snapshot,
                    Selection = SelectedNavigationKey(snapshot.IdentityControls)
                })
                .Where(item => item.Selection.Length > 0)
                .ToArray();
            var exactSelection = snapshotsWithSelection
                .Where(item => SameText(item.Selection, currentSelection))
                .Select(item => item.Snapshot)
                .ToArray();
            if (exactSelection.Length > 0)
                candidates = exactSelection;
            else if (snapshotsWithSelection.Length > 0)
                return null;
        }
        var scored = candidates
            .Select(snapshot =>
            {
                var snapshotKeys = snapshot.IdentityControls
                    .Select(ControlIdentityKey)
                    .Where(key => key.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shared = currentKeys.Count == 0 ? 0 : currentKeys.Count(snapshotKeys.Contains);
                var candidateSelection = SelectedNavigationKey(snapshot.IdentityControls);
                var selectionScore = currentSelection.Length == 0 || candidateSelection.Length == 0
                    ? 0
                    : SameText(currentSelection, candidateSelection) ? 1_000 : -500;
                var classScore = SameText(snapshot.ClassName, currentWindow.ClassName) ? 500 : 0;
                var titleScore = SameText(snapshot.Title, currentWindow.Title) ? 300 : 0;
                var sizeScore = SurfaceSizeScore(snapshot.CapturedSurfaceBounds, currentWindow.Bounds);
                var similarityScore = shared * 12 +
                                      (currentKeys.Count == 0 ? 0 : (int)Math.Round(shared * 500d / currentKeys.Count));
                return new { Snapshot = snapshot, Shared = shared, Score = selectionScore + classScore + titleScore + sizeScore + similarityScore };
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Shared)
            .ThenByDescending(item => item.Snapshot.RelativeHighlightBounds.Count)
            .ThenByDescending(item => item.Snapshot.FrameSequence)
            .ToArray();
        var best = scored.FirstOrDefault();
        if (best is null)
            return null;

        // A generic owned popup class and title can be reused for unrelated menus.
        // Require at least one live identity match before declaring such a surface
        // already mapped; otherwise an unseen dialog would be painted purple.
        if (!currentIsPrimarySurface && currentKeys.Count > 0 && best.Shared == 0)
            return null;
        return best.Snapshot;
    }

    internal static IReadOnlyList<RectI> ProjectMappedHighlights(
        MappedSurfaceHighlightSnapshot snapshot,
        RectI currentRootBounds,
        RectI currentSurfaceBounds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!currentRootBounds.IsValid || !currentSurfaceBounds.IsValid || !snapshot.CapturedSurfaceBounds.IsValid)
            return [];
        var scaleX = currentSurfaceBounds.Width / (double)snapshot.CapturedSurfaceBounds.Width;
        var scaleY = currentSurfaceBounds.Height / (double)snapshot.CapturedSurfaceBounds.Height;
        return snapshot.RelativeHighlightBounds
            .Select(bounds => new RectI(
                currentSurfaceBounds.X - currentRootBounds.X + (int)Math.Round(bounds.X * scaleX),
                currentSurfaceBounds.Y - currentRootBounds.Y + (int)Math.Round(bounds.Y * scaleY),
                Math.Max(1, (int)Math.Round(bounds.Width * scaleX)),
                Math.Max(1, (int)Math.Round(bounds.Height * scaleY))))
            .Distinct()
            .ToArray();
    }

    internal static IReadOnlyList<AutomationObservation> ProjectMappedControls(
        MappedSurfaceHighlightSnapshot snapshot,
        RectI currentSurfaceBounds,
        long currentWindowHwnd)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.CapturedSurfaceBounds.IsValid || !currentSurfaceBounds.IsValid)
            return [];

        var scaleX = currentSurfaceBounds.Width / (double)snapshot.CapturedSurfaceBounds.Width;
        var scaleY = currentSurfaceBounds.Height / (double)snapshot.CapturedSurfaceBounds.Height;
        return snapshot.IdentityControls
            .Where(control => control.Bounds.IsValid)
            .Take(RecordingContractLimits.MaxControlsPerFrame)
            .Select(control =>
            {
                var relativeX = control.Bounds.X - snapshot.CapturedSurfaceBounds.X;
                var relativeY = control.Bounds.Y - snapshot.CapturedSurfaceBounds.Y;
                var projected = new RectI(
                    currentSurfaceBounds.X + (int)Math.Round(relativeX * scaleX),
                    currentSurfaceBounds.Y + (int)Math.Round(relativeY * scaleY),
                    Math.Max(1, (int)Math.Round(control.Bounds.Width * scaleX)),
                    Math.Max(1, (int)Math.Round(control.Bounds.Height * scaleY)));
                return control with { Bounds = projected, WindowHwnd = currentWindowHwnd };
            })
            .ToArray();
    }

    private static UiMapControlView? ToControlView(
        GraphNode node,
        string surfaceId,
        string bundleId,
        long frameSequence)
    {
        var evidence = node.Evidence.FirstOrDefault(item =>
            item.FrameSequence == frameSequence &&
            string.Equals(item.BundleId, bundleId, StringComparison.Ordinal) &&
            item.Bounds is { IsValid: true });
        if (evidence?.Bounds is not { } bounds)
            return null;
        return new(
            node.Id,
            UiUnderstandingLevel.RawDataStreams,
            node.Label,
            Property(node, "controlType") is { Length: > 0 } controlType ? controlType : "Control",
            surfaceId,
            string.Empty,
            bounds,
            node.Evidence,
            node);
    }

    private static AutomationObservation ToAutomationObservation(UiMapControlView control) => new(
        Property(control.Source, "runtimeId"),
        Property(control.Source, "parentRuntimeId"),
        Property(control.Source, "automationId"),
        Property(control.Source, "name") is { Length: > 0 } name ? name : control.DisplayName,
        control.CanonicalKind,
        Property(control.Source, "className"),
        control.Bounds,
        !IsFalse(Property(control.Source, "enabled")),
        IsTrue(Property(control.Source, "offscreen")),
        Property(control.Source, "frameworkId"),
        SupportedPatterns: Properties(control.Source, "supportedPattern"),
        HasKeyboardFocus: IsTrue(Property(control.Source, "focused")),
        IsSelected: IsTrue(Property(control.Source, "selected")),
        ToggleState: Property(control.Source, "toggleState"),
        ExpandCollapseState: Property(control.Source, "expandCollapseState"));

    private static bool IsPresentableRawControl(UiMapControlView control)
    {
        var framework = Property(control.Source, "frameworkId");
        var className = Property(control.Source, "className");
        var visual = className is "UiAtlas.VisualControlRegion" or "UiAtlas.HoverRegion";
        var cached = framework.Equals("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase);
        if (visual || cached)
            return control.Bounds.IsValid;
        if (IsFalse(Property(control.Source, "effectivelyVisible")))
            return false;
        return !IsTrue(Property(control.Source, "offscreen"));
    }

    private static int SurfaceSizeScore(RectI recorded, RectI current)
    {
        if (!recorded.IsValid || !current.IsValid)
            return 0;
        var widthRatio = Math.Min(recorded.Width, current.Width) / (double)Math.Max(recorded.Width, current.Width);
        var heightRatio = Math.Min(recorded.Height, current.Height) / (double)Math.Max(recorded.Height, current.Height);
        return (int)Math.Round((widthRatio + heightRatio) * 100);
    }

    private static string SelectedNavigationKey(IEnumerable<AutomationObservation> controls)
    {
        foreach (var control in controls
                     .Where(control => control.IsSelected)
                     .Concat(controls.Where(control => !control.IsSelected && control.HasKeyboardFocus)))
        {
            var type = NormalizeControlType(control.ControlType);
            if (type is not ("TabItem" or "MenuItem" or "ListItem"))
                continue;
            var label = string.IsNullOrWhiteSpace(control.Name) ? control.AutomationId : control.Name;
            if (!string.IsNullOrWhiteSpace(label))
                return $"{type}|{label.Trim()}";
        }
        return string.Empty;
    }

    private static string ControlIdentityKey(AutomationObservation control)
    {
        var type = NormalizeControlType(control.ControlType);
        var automationId = control.AutomationId?.Trim() ?? string.Empty;
        var name = control.Name?.Trim() ?? string.Empty;
        var className = control.ClassName?.Trim() ?? string.Empty;
        if (automationId.Length == 0 && name.Length == 0)
            return string.Empty;
        return $"{type}|{automationId}|{name}|{className}";
    }

    private static string NormalizeControlType(string value)
    {
        const string prefix = "ControlType.";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }

    private static bool SameText(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Property(GraphNode node, string name) => node.Properties
        .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static IReadOnlyList<string> Properties(GraphNode node, string name) => node.Properties
        .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
        .Select(property => property.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    private static bool IsTrue(string value) => bool.TryParse(value, out var parsed) && parsed;
    private static bool IsFalse(string value) => bool.TryParse(value, out var parsed) && !parsed;

    private static void LoadBundle(string path, List<RecordedHighlight> output)
    {
        using var bundle = RecordingBundle.Open(path);
        var frames = bundle.Entries
            .Where(IsObservationEntry)
            .Select(bundle.ReadJson<FrameObservation>)
            .OrderBy(frame => frame.Sequence)
            .ToArray();
        if (frames.Length == 0)
            return;

        var events = ReadEvents(bundle);
        var statebook = bundle.ReadJson<DerivedStatebook>("derived/statebook.json");
        RestoreManualEpisodes(frames, events, statebook, output);
        RestoreAutomaticVisits(frames, events, output);
    }

    private static InputEvent[] ReadEvents(RecordingBundle bundle) =>
        bundle.ReadText("raw/input-events.jsonl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<InputEvent>(line, JsonDefaults.Options))
            .Where(item => item is not null)
            .Cast<InputEvent>()
            .OrderBy(item => item.Sequence)
            .ToArray();

    private static void RestoreManualEpisodes(
        IReadOnlyList<FrameObservation> frames,
        IReadOnlyList<InputEvent> events,
        DerivedStatebook statebook,
        List<RecordedHighlight> output)
    {
        var eventsBySequence = events.ToDictionary(item => item.Sequence);
        var framesBySequence = frames.ToDictionary(item => item.Sequence);
        foreach (var episode in statebook.Episodes ?? [])
        {
            if (episode.InputSequence is not long inputSequence ||
                !eventsBySequence.TryGetValue(inputSequence, out var input) ||
                input.Kind != InputEventKind.PointerUp ||
                !framesBySequence.TryGetValue(episode.EndFrameSequence, out var frame))
            {
                continue;
            }

            foreach (var bounds in ManualRecordingHighlightResolver.Resolve(frame, [input]))
                Add(output, frame, TabHighlightLayerResolver.ResolveLayerKey(frame, [bounds]), bounds);
        }
    }

    private static void RestoreAutomaticVisits(
        IReadOnlyList<FrameObservation> frames,
        IReadOnlyList<InputEvent> events,
        List<RecordedHighlight> output)
    {
        var markers = events
            .Where(item => item.Kind == InputEventKind.Marker && !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();
        var markerTexts = markers
            .Select(item => item.Text)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var marker in markers)
        {
            const string targetSeparator = ":target:";
            var targetIndex = marker.Text.IndexOf(targetSeparator, StringComparison.Ordinal);
            if (targetIndex < 0)
                continue;

            var markerPrefix = marker.Text[..targetIndex];
            if (!IsSuccessfulAutomaticVisit(markerPrefix, markerTexts) ||
                !TryParseBounds(marker.Text[(targetIndex + targetSeparator.Length)..], out var bounds))
            {
                continue;
            }

            var frame = NearestFullRootFrame(frames, marker.TimestampUtc);
            if (frame is null)
                continue;
            var layerKey = markerPrefix.StartsWith("auto-tabs:tab:", StringComparison.Ordinal)
                ? TabHighlightLayerResolver.GlobalLayerKey
                : TabHighlightLayerResolver.ResolveLayerKey(frame, [bounds]);
            Add(output, frame, layerKey, bounds);
        }

        // Older bundles did not persist tab target rectangles. Recover them by
        // matching the stable tab key against their recorded full-root frames.
        foreach (var marker in markers.Where(item =>
                     item.Text.StartsWith("auto-tabs:tab:", StringComparison.Ordinal) &&
                     item.Text.EndsWith(":clicked", StringComparison.Ordinal)))
        {
            var stableKey = marker.Text["auto-tabs:tab:".Length..^":clicked".Length];
            var match = frames
                .Where(IsFullRootFrame)
                .OrderBy(frame => Math.Abs((frame.TimestampUtc - marker.TimestampUtc).Ticks))
                .SelectMany(frame => AutoTabDiscovery.Discover(frame).Select(tab => (Frame: frame, Tab: tab)))
                .FirstOrDefault(item => string.Equals(item.Tab.StableKey, stableKey, StringComparison.Ordinal));
            if (match.Tab is not null)
                Add(output, match.Frame, TabHighlightLayerResolver.GlobalLayerKey, match.Tab.Observation.Bounds);
        }
    }

    private static bool IsSuccessfulAutomaticVisit(string markerPrefix, IReadOnlySet<string> markerTexts) =>
        markerTexts.Contains(markerPrefix + ":opened") ||
        markerTexts.Contains(markerPrefix + ":clicked");

    private static bool TryParseBounds(string value, out RectI bounds)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 4 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            bounds = new(x, y, width, height);
            return width > 0 && height > 0;
        }

        bounds = new(0, 0, 0, 0);
        return false;
    }

    private static FrameObservation? NearestFullRootFrame(
        IReadOnlyList<FrameObservation> frames,
        DateTimeOffset timestamp) =>
        frames
            .Where(IsFullRootFrame)
            .MinBy(frame => Math.Abs((frame.TimestampUtc - timestamp).Ticks));

    private static bool IsFullRootFrame(FrameObservation frame) =>
        string.IsNullOrWhiteSpace(frame.ObservationScope) ||
        string.Equals(frame.ObservationScope, "full-root", StringComparison.OrdinalIgnoreCase);

    private static void Add(List<RecordedHighlight> output, FrameObservation frame, string layerKey, RectI bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || frame.Window.Bounds.Width <= 0 || frame.Window.Bounds.Height <= 0)
            return;
        output.Add(new(frame.Window.Bounds, layerKey, bounds));
    }

    private static bool IsObservationEntry(string entry) =>
        entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
        entry.EndsWith(".json", StringComparison.Ordinal);

    private static string RelativeIdentity(RecordedHighlight highlight) =>
        string.Join('|',
            highlight.LayerKey,
            highlight.Bounds.X - highlight.CapturedRootBounds.X,
            highlight.Bounds.Y - highlight.CapturedRootBounds.Y,
            highlight.Bounds.Width,
            highlight.Bounds.Height);
}

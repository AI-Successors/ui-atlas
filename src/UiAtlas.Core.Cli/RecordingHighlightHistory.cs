using System.Text.Json;
using System.Globalization;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal sealed record RecordedHighlight(
    RectI CapturedRootBounds,
    string LayerKey,
    RectI Bounds);

internal static class RecordingHighlightHistory
{
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

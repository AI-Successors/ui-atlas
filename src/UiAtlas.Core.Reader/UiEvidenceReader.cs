using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

public sealed record UiEvidenceImage(
    byte[] Png,
    RectI? Highlight,
    string Entry,
    RectI ScreenshotBounds,
    AutomationObservation? InteractionSource,
    InteractionObservation? Interaction,
    FrameObservation Observation,
    long ScreenshotFrameSequence = 0,
    DateTimeOffset? ScreenshotTimestampUtc = null)
{
    public bool InheritedPixels => ScreenshotFrameSequence != 0 && ScreenshotFrameSequence != Observation.Sequence;
}

public sealed class UiEvidenceReader : IDisposable
{
    private readonly IReadOnlyDictionary<string, RecordingBundle> _bundles;
    public IReadOnlyList<string> SessionIds { get; }

    private UiEvidenceReader(IReadOnlyDictionary<string, RecordingBundle> bundles)
    {
        _bundles = bundles;
        SessionIds = bundles.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    public static UiEvidenceReader Open(string path)
        => Open([path]);

    public static UiEvidenceReader Open(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var bundles = new Dictionary<string, RecordingBundle>(StringComparer.Ordinal);
        try
        {
            foreach (var path in paths.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bundle = RecordingBundle.Open(path);
                try
                {
                    var report = RecordingBundleValidator.Validate(bundle);
                    if (!report.IsValid)
                        throw new InvalidDataException("The selected evidence bundle did not pass validation.");
                    var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                    if (bundles.ContainsKey(manifest.SessionId))
                        throw new InvalidDataException("Duplicate recording session evidence was selected.");
                    bundles.Add(manifest.SessionId, bundle);
                }
                catch
                {
                    bundle.Dispose();
                    throw;
                }
            }

            if (bundles.Count == 0)
                throw new InvalidDataException("No evidence bundles were selected.");
            return new(bundles);
        }
        catch
        {
            foreach (var bundle in bundles.Values)
                bundle.Dispose();
            throw;
        }
    }

    public UiEvidenceImage? Read(EvidenceRef evidence, bool allowInheritedPixels = false)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!_bundles.TryGetValue(evidence.BundleId, out var bundle))
            throw new InvalidDataException("Graph evidence does not identify the selected recording bundle.");
        if (evidence.FrameSequence <= 0 ||
            !string.Equals(evidence.ObservationEntry, $"raw/observations/frame-{evidence.FrameSequence:D6}.json", StringComparison.Ordinal))
            throw new InvalidDataException("Graph evidence does not use the canonical frame namespace.");
        var frame = bundle.ReadJson<FrameObservation>(evidence.ObservationEntry);
        if (frame.Sequence != evidence.FrameSequence)
            throw new InvalidDataException("Graph evidence does not match the selected recording frame.");

        var screenshotFrame = frame;
        var screenshotEntry = evidence.ScreenshotEntry;
        if (!string.IsNullOrEmpty(screenshotEntry) &&
            (!string.Equals(screenshotEntry, $"raw/frames/frame-{frame.Sequence:D6}.png", StringComparison.Ordinal) ||
             !string.Equals(frame.FrameEntry, screenshotEntry, StringComparison.Ordinal)))
            throw new InvalidDataException("Graph evidence does not match the selected recording frame.");

        if (string.IsNullOrEmpty(screenshotEntry))
        {
            if (!allowInheritedPixels)
            {
                screenshotEntry = frame.FrameEntry;
                if (string.IsNullOrEmpty(screenshotEntry)) return null;
            }
            else
            {
                var resolved = ObservationSceneReducer.ResolveScreenshot(frame, sequence =>
                {
                    var entry = $"raw/observations/frame-{sequence:D6}.json";
                    return bundle.Entries.Contains(entry) ? bundle.ReadJson<FrameObservation>(entry) : null;
                });
                if (resolved is null) return null;
                screenshotFrame = resolved;
                screenshotEntry = screenshotFrame.FrameEntry;
            }
        }

        if (string.IsNullOrEmpty(screenshotEntry)) return null;
        if (!string.Equals(screenshotEntry, $"raw/frames/frame-{screenshotFrame.Sequence:D6}.png", StringComparison.Ordinal) ||
            !string.Equals(screenshotFrame.FrameEntry, screenshotEntry, StringComparison.Ordinal))
            throw new InvalidDataException("Delta evidence screenshot is not canonical.");

        var windows = screenshotFrame.ScopedWindows is { Count: > 0 } ? screenshotFrame.ScopedWindows : [screenshotFrame.Window];
        var screenshotBounds = screenshotFrame.ScreenshotBounds ?? new RectI(
            windows.Min(x => x.Bounds.X),
            windows.Min(x => x.Bounds.Y),
            windows.Max(x => x.Bounds.X + x.Bounds.Width) - windows.Min(x => x.Bounds.X),
            windows.Max(x => x.Bounds.Y + x.Bounds.Height) - windows.Min(x => x.Bounds.Y));
        var bounds = evidence.Bounds;
        var relative = bounds is null ? null : bounds with { X = bounds.X - screenshotBounds.X, Y = bounds.Y - screenshotBounds.Y };
        InteractionObservation? interaction = null;
        if (bundle.Entries.Contains("raw/interactions.jsonl"))
        {
            interaction = bundle.ReadText("raw/interactions.jsonl")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => System.Text.Json.JsonSerializer.Deserialize<InteractionObservation>(line, JsonDefaults.Options))
                .FirstOrDefault(item => item is not null &&
                    (item.InteractionId == frame.InteractionId || item.ResultFrameSequences.Contains(frame.Sequence)));
        }
        return new(bundle.ReadBytes(screenshotEntry, 16 * 1024 * 1024), relative,
            screenshotEntry, screenshotBounds,
            frame.InteractionSource ?? interaction?.SourceControl, interaction, frame, screenshotFrame.Sequence, screenshotFrame.TimestampUtc);
    }

    public FrameObservation ReadObservation(string bundleId, long frameSequence)
    {
        if (!_bundles.TryGetValue(bundleId, out var bundle) || frameSequence <= 0)
            throw new InvalidDataException("Unknown observation.");
        return bundle.ReadJson<FrameObservation>($"raw/observations/frame-{frameSequence:D6}.json");
    }

    public ObservationScene ReadScene(string bundleId, long frameSequence)
    {
        if (!_bundles.TryGetValue(bundleId, out var bundle)) throw new InvalidDataException("Unknown recording.");
        var reducer = new ObservationSceneReducer();
        ObservationScene? result = null;
        foreach (var frame in bundle.Entries.Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
            .Select(bundle.ReadJson<FrameObservation>).Where(frame => frame.Sequence <= frameSequence).OrderBy(frame => frame.Sequence))
            result = reducer.Reduce(frame);
        return result?.FrameSequence == frameSequence ? result : throw new InvalidDataException("Unknown observation.");
    }

    public IReadOnlyList<ProviderObservation> ReadProviderObservations(string bundleId)
    {
        if (!_bundles.TryGetValue(bundleId, out var bundle)) throw new InvalidDataException("Unknown recording.");
        return bundle.Entries.Where(entry => entry.StartsWith("raw/providers/attempt-", StringComparison.Ordinal))
            .OrderBy(entry => entry, StringComparer.Ordinal).Select(bundle.ReadJson<ProviderObservation>).ToArray();
    }

    public void Dispose()
    {
        foreach (var bundle in _bundles.Values)
            bundle.Dispose();
    }
}

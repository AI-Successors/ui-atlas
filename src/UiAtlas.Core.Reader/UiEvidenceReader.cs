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
    DateTimeOffset? ScreenshotTimestampUtc = null,
    string PixelProvenance = "frame image",
    IReadOnlyList<RectI>? OccludedRegions = null,
    RectI? DisplayBounds = null,
    IReadOnlyList<RectI>? VisibleRegions = null)
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
        var relativeX = bounds is null ? 0L : (long)bounds.X - screenshotBounds.X;
        var relativeY = bounds is null ? 0L : (long)bounds.Y - screenshotBounds.Y;
        var relative = !ControlGeometry.IsRenderable(bounds) ||
            relativeX is < int.MinValue or > int.MaxValue || relativeY is < int.MinValue or > int.MaxValue
                ? null : bounds! with { X = (int)relativeX, Y = (int)relativeY };
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

    public UiEvidenceImage? ReadWindow(EvidenceRef evidence, long hwnd)
    {
        // Validate the canonical frame reference even when a separate HWND image exists.
        var original = Read(evidence);
        var frame = ReadObservation(evidence.BundleId, evidence.FrameSequence);
        var window = (frame.ScopedWindows ?? [frame.Window]).SingleOrDefault(item => item.Hwnd == hwnd)
            ?? throw new InvalidDataException("Selected HWND is not in this observation.");
        var context = RawSurfaceImageLayout.WindowContext(frame, window);
        if (original is not null && context.Count > 1)
        {
            // Display only a crop of this frame's already recorded pixels. Do not
            // compose independent HWND images or import any popup control records.
            return original with {
                PixelProvenance = $"captured frame crop with {context.Count - 1} owned popup(s)",
                DisplayBounds = RawSurfaceImageLayout.ContextBounds(context),
                VisibleRegions = RawSurfaceImageLayout.VisibleContextRegions(frame, context) };
        }
        var plane = frame.WindowImages?.SingleOrDefault(item => item.Hwnd == hwnd);
        if (plane is { Status: "ok", Entry: not null })
        {
            var canonical = $"raw/frames/frame-{frame.Sequence:D6}-window-{hwnd:X}.png";
            if (plane.Entry != canonical) throw new InvalidDataException("Window image reference is not canonical.");
            return new(_bundles[evidence.BundleId].ReadBytes(plane.Entry, 16 * 1024 * 1024), null,
                plane.Entry, plane.Bounds, frame.InteractionSource, null, frame, frame.Sequence,
                plane.CompletedUtc, "independent HWND image (" + plane.Method + ")");
        }
        if (original is null) return null;
        var occluders = RawSurfaceImageLayout.Occluders(frame, window);
        return original with { PixelProvenance = plane is null
            ? "legacy screen/composite crop; isolation unverified"
            : $"screen crop; isolated capture {plane.Status}", OccludedRegions = occluders };
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

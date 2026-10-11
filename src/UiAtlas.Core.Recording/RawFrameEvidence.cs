using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

/// <summary>Frame-local evidence only. This type deliberately has no scene or history input.</summary>
public static class RawFrameEvidence
{
    public const int MaxStreams = 256;
    public const int MaxControls = 40_000;

    public static IReadOnlyList<RawDataStreamObservation> Seal(
        FrameObservation frame, DateTimeOffset started, DateTimeOffset pixelsStarted,
        DateTimeOffset pixelsCompleted, string pixelStatus,
        IEnumerable<RawDataStreamObservation> providers)
    {
        var completed = frame.TimestampUtc;
        var windows = frame.ScopedWindows ?? [frame.Window];
        var streams = new List<RawDataStreamObservation>
        {
            new("pixels", "pixels", pixelsStarted, pixelsCompleted, pixelStatus, [],
                ScreenshotEntry: string.IsNullOrEmpty(frame.FrameEntry) ? null : frame.FrameEntry),
            new("win32", "win32", started, completed, "ok", [], windows.Count, Windows: windows)
        };
        var remaining = MaxControls;
        foreach (var provider in providers.Take(MaxStreams - 2))
        {
            var retained = provider.Controls.Take(remaining).Select(ProviderEvidence.Redact).ToArray();
            remaining -= retained.Length;
            streams.Add(provider with
            {
                StreamId = $"{provider.Source}:{streams.Count:D3}", Controls = retained,
                Truncated = provider.Truncated || retained.Length < provider.Controls.Count
            });
        }
        return streams;
    }

    public static IEnumerable<(string StreamId, string Source, AutomationObservation Control)> Controls(FrameObservation frame) =>
        frame.RawStreams is null
            ? frame.Automation.Select(control => ("legacy", "legacy", control))
            : frame.RawStreams.SelectMany(stream => stream.Controls.Select(control => (stream.StreamId, stream.Source, control)));
}

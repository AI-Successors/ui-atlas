using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public sealed partial class ManualRecordingSession
{
    /// <summary>Attended recording uses full observations; active exploration retains its explicit probes.</summary>
    public bool FullRawObservations { get; set; }
    public Action<string>? RawCaptureProgress { get; set; }

    internal static bool HasIncompleteRawStreams(IReadOnlyList<RawDataStreamObservation> streams) =>
        streams.Count > UiAtlas.Core.Recording.RawFrameEvidence.MaxStreams - 2 ||
        streams.Sum(stream => (long)stream.Controls.Count) > UiAtlas.Core.Recording.RawFrameEvidence.MaxControls ||
        streams.Any(stream => stream.Truncated || stream.Status is not ("ok" or "not-configured" or "not-applicable") ||
            stream.Controls.Any(control => control.ScopeStatus is "outside-target" or "unverified"));

    public async Task<FrameObservation> CaptureFullObservationAsync(string trigger, CancellationToken cancellationToken,
        string? interactionId = null, AutomationObservation? interactionSource = null)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (DateTimeOffset.UtcNow - _started > MaxDuration) throw new InvalidOperationException("Recording quota reached.");
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FrameObservation? result = null;
            // An interrupted attempt remains immutable evidence. A single fresh retry
            // avoids declaring readiness for a tree collected while the user acted.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                RawCaptureProgress?.Invoke(attempt == 0 ? "Waiting for the screen to settle..." :
                    "The screen changed during capture. Recording a fresh observation...");
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                using var raw = BeginRawObservation();
                result = await ObserveFrameAsync(trigger, cancellationToken,
                    new FrameCaptureOptions(IncludeAutomation: false, InteractionId: interactionId,
                        InteractionSource: interactionSource, PreferScreenBoundsScreenshot: true,
                        ScreenshotTimeout: TimeSpan.FromSeconds(5), FullRawObservation: true),
                    persistFrame: true, captureScreenshot: true, RichAutomationTimeout).ConfigureAwait(false);
                if (result.Acquisition?.StabilityStatus != "changed") break;
            }
            RawCaptureProgress?.Invoke(result!.AutomationStatus == "ok"
                ? "Observation captured. Ready for the next action."
                : "Observation saved with incomplete evidence. Ready for the next action.");
            return result!;
        }
        finally { _captureGate.Release(); }
    }

    private async Task<string> CheckObservationStabilityAsync(IReadOnlyList<WindowObservation> before,
        RectI? imageBounds, long sequence, DateTimeOffset started, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return "interrupted";
        try
        {
            var targets = WindowCatalog.ListScopedWindows(_target).Take(WindowSnapshotCapture.MaxScopedWindows).ToArray();
            var after = targets.Select(WindowSnapshotCapture.Observe).ToArray();
            if (!SameNativeScope(before, after)) return "changed";
            lock (_stateGate)
            {
                DrainInputUnsafe();
                if (_events.Any(input => input.TimestampUtc > started &&
                    input.Kind is InputEventKind.PointerDown or InputEventKind.KeyDown or InputEventKind.Wheel)) return "changed";
            }
            if (imageBounds is null || !TryGetFrameScreenshot(sequence, out var first)) return "unverified";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var last = await CaptureScreenshotAsync(token => WindowSnapshotCapture.CapturePngAsync(
                targets.Where(WindowSnapshotCapture.IsCapturable).ToArray(), token, preferScreenBounds: true), deadline.Token)
                .ConfigureAwait(false);
            return WindowSnapshotCapture.AreVisuallyEquivalentPng(first, last.Png) ? "stable" : "changed";
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or
            ArgumentException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return cancellationToken.IsCancellationRequested ? "interrupted" : "unverified";
        }
    }

    internal static bool SameNativeScope(IReadOnlyList<WindowObservation> before, IReadOnlyList<WindowObservation> after) =>
        before.OrderBy(window => window.Hwnd).Select(window => (window.Hwnd, window.ClassName, window.Bounds,
            window.Dpi, window.IsVisible, window.IsMinimized, window.IsCloaked, window.ZOrder))
        .SequenceEqual(after.OrderBy(window => window.Hwnd).Select(window => (window.Hwnd, window.ClassName, window.Bounds,
            window.Dpi, window.IsVisible, window.IsMinimized, window.IsCloaked, window.ZOrder)));
}

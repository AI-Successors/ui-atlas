using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class RawObservationCaptureTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("cancelled")]
    [InlineData("unavailable")]
    [InlineData("full")]
    public async Task PixelsAndCollectorCompletionSealOneFrameEvenWhenInterrupted(string outcome)
    {
        var ready = new TaskCompletionSource<(Window Window, long Hwnd)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window { Title = "Raw observation test", Width = 300, Height = 180,
                Left = 120, Top = 120, ShowActivated = false };
            window.Show();
            ready.SetResult((window, new WindowInteropHelper(window).Handle.ToInt64()));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var host = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var output = Path.Combine(Path.GetTempPath(), "raw-observation-test-" + Guid.NewGuid().ToString("N") + ".mlrec");
        try
        {
            await using var session = new ManualRecordingSession(WindowCatalog.Resolve(host.Hwnd), output);
            session.FullRawObservations = outcome == "full";
            session.Start(explicitConsent: true);
            using var cancellation = new CancellationTokenSource();
            async Task<AdaptiveExtractionResult> Collect(CancellationToken token)
            {
                Assert.Equal(1, session.LatestFrameSequence);
                Assert.True(session.TryGetFrameScreenshot(1, out var pixels));
                Assert.NotEmpty(pixels);
                if (outcome == "cancelled") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (outcome == "unavailable") throw new InvalidOperationException("Provider unavailable");
                var result = await session.CollectWindowAutomationAsync(host.Hwnd, TimeSpan.FromSeconds(1), 64, token);
                return new(result.Items, new("adaptive-extraction/1", [], [], [],
                    ExtractionCoverageStatus.Observed, "complete", 0, 0), result.TimedOut, result.Status);
            }
            var capture = session.CaptureAsync("raw-test", cancellation.Token,
                new FrameCaptureOptions(IncludeAutomation: false, AutomationCollector: Collect,
                    PreferScreenBoundsScreenshot: true, ScreenshotTimeout: TimeSpan.FromSeconds(3)));
            if (outcome == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            else await capture;
            session.Complete();
            using var bundle = RecordingBundle.Open(output);
            var report = RecordingBundleValidator.Validate(bundle);
            Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
            var entries = bundle.Entries.Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal)).Order().ToArray();
            var observationEntry = entries[0];
            var observation = bundle.ReadJson<FrameObservation>(observationEntry);
            Assert.Equal(1, observation.Sequence);
            Assert.Equal("observation", observation.RecordKind);
            Assert.Equal(observation.FrameEntry, Assert.Single(observation.RawStreams!, stream => stream.Source == "pixels").ScreenshotEntry);
            Assert.NotEmpty(Assert.Single(observation.RawStreams!, stream => stream.Source == "win32").Windows!);
            if (outcome is "cancelled" or "unavailable") Assert.Equal(outcome, observation.AutomationStatus);
            else
            {
                Assert.Contains(observation.RawStreams!, stream => stream.Source.StartsWith("uia.raw/", StringComparison.Ordinal));
                Assert.Contains(observation.RawStreams!, stream => stream.Source == "ocr");
                Assert.Contains(observation.RawStreams!, stream => stream.Source == "visual.geometry");
                Assert.All(observation.RawStreams!, stream => Assert.True(stream.CompletedUtc <= observation.TimestampUtc));
                if (outcome == "full")
                {
                    Assert.NotNull(observation.Acquisition);
                    Assert.Equal(RawObservationSemantics.Version, observation.Acquisition.FormatVersion);
                    Assert.Equal("screen-bounds-explicit", observation.Acquisition.CaptureMethod);
                    Assert.NotEmpty(observation.WindowImages!);
                    Assert.All(observation.WindowImages!, plane => {
                        Assert.Equal(host.Hwnd, plane.Hwnd);
                        Assert.True(plane.CompletedUtc <= observation.Acquisition.SealedUtc);
                        if (plane.Status == "ok") Assert.Contains(plane.Entry!, bundle.Entries);
                        else Assert.Null(plane.Entry);
                    });
                    Assert.All(observation.RawStreams!.Where(stream => stream.Source.StartsWith("uia."))
                        .SelectMany(stream => stream.Controls), control => {
                            Assert.Equal(host.Hwnd, control.QueryWindowHwnd);
                            Assert.NotNull(control.NativeWindowHwnd);
                            Assert.NotNull(control.ScopeStatus);
                            if (control.ScopeStatus != "verified") Assert.Equal(0, control.WindowHwnd);
                        });
                    Assert.All(observation.RawStreams!.Where(stream => stream.Source.StartsWith("uia.") || stream.Source.StartsWith("msaa/")),
                        stream => Assert.Equal(host.Hwnd, stream.TargetWindowHwnd));
                    var frames = entries.Select(bundle.ReadJson<FrameObservation>).ToArray();
                    Assert.All(frames, frame => Assert.NotNull(frame.Acquisition));
                    for (var i = 1; i < frames.Length; i++)
                        Assert.True(frames[i].Acquisition!.StartedUtc >= frames[i - 1].Acquisition!.SealedUtc);
                }
            }
        }
        finally
        {
            host.Window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            if (File.Exists(output)) File.Delete(output);
        }
    }
}

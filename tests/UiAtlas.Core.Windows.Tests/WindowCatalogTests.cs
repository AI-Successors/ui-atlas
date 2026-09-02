using System.IO;
using System.Runtime.InteropServices;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Windows.Tests;

public sealed class WindowCatalogTests
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;

    [Fact]
    public void GraphicsArgumentFailuresAreRecoverable()
    {
        Assert.True(WindowSnapshotCapture.IsRecoverableCaptureFailure(new ArgumentException("Synthetic invalid graphics parameter.")));
        Assert.True(WindowSnapshotCapture.IsRecoverableCaptureFailure(new FileFormatException("Synthetic image failure.")));
        Assert.False(WindowSnapshotCapture.IsRecoverableCaptureFailure(new OutOfMemoryException("Synthetic fatal allocation failure.")));
    }

    [Fact]
    public void BlankFrameProbeRejectsTransparentPng()
        => Assert.True(WindowSnapshotCapture.IsVisuallyBlankPng(CreateSolidPng(System.Windows.Media.Color.FromArgb(0, 0, 0, 0))));

    [Fact]
    public void BlankFrameProbeRejectsWhitePng()
        => Assert.True(WindowSnapshotCapture.IsVisuallyBlankPng(CreateSolidPng(System.Windows.Media.Colors.White)));

    [Fact]
    public void BlankFrameProbeAcceptsVisiblePixels()
        => Assert.False(WindowSnapshotCapture.IsVisuallyBlankPng(CreateSolidPng(System.Windows.Media.Color.FromArgb(255, 56, 128, 214))));

    [Fact]
    public void GraphicsCapturePixelsAreMadeOpaqueBeforeEncoding()
    {
        var pixels = new byte[]
        {
            215, 120, 0, 0,
            0, 0, 0, 0,
            255, 255, 255, 255
        };

        WindowsGraphicsCapture.ForceOpaqueAlpha(pixels, width: 3, height: 1, stride: 12);

        Assert.Equal([255, 255, 255], new[] { pixels[3], pixels[7], pixels[11] });
        Assert.Equal([215, 120, 0], new[] { pixels[0], pixels[1], pixels[2] });
    }

    [Fact]
    public void EvidenceDecoderRecoversRgbHiddenByBrokenAlpha()
    {
        var png = CreateSolidPng(System.Windows.Media.Color.FromArgb(0, 0, 120, 215));

        var bitmap = WindowSnapshotCapture.DecodeOpaquePng(png);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);

        Assert.Equal(System.Windows.Media.PixelFormats.Bgr32, bitmap.Format);
        Assert.Equal([215, 120, 0], new[] { pixels[0], pixels[1], pixels[2] });
    }

    [Theory]
    [InlineData("EXCEL", "bosa_sdm_XL9", true)]
    [InlineData("EXCEL", "BOSA_SDM_XL9", true)]
    [InlineData("EXCEL", "NUIDialog", true)]
    [InlineData("WINWORD", "NUIDialog", true)]
    [InlineData("other", "NUIDialog", false)]
    [InlineData("EXCEL", "NetUIHWND", false)]
    public void OfficeOwnerDrawnDialogsPreferScreenBoundsCapture(
        string processName,
        string className,
        bool expected)
    {
        var target = new WindowTarget(
            1, 1, 7, processName, DateTimeOffset.UnixEpoch, "Format Cells", className,
            new RectI(100, 100, 692, 636));

        Assert.Equal(expected, WindowSnapshotCapture.RequiresScreenBoundsCaptureForDialog(target));
    }

    [Fact]
    public void VisualReadinessTreatsIdenticalFramesAsStable()
    {
        var frame = CreateSolidPng(System.Windows.Media.Color.FromArgb(255, 56, 128, 214));

        Assert.True(WindowSnapshotCapture.AreVisuallyEquivalentPng(frame, frame));
    }

    [Fact]
    public void VisualReadinessRejectsMateriallyDifferentFrames()
    {
        var loading = CreateSolidPng(System.Windows.Media.Colors.White);
        var loaded = CreateSolidPng(System.Windows.Media.Color.FromArgb(255, 56, 128, 214));

        Assert.False(WindowSnapshotCapture.AreVisuallyEquivalentPng(loading, loaded));
    }

    [Fact]
    public void DialogReadinessRejectsStableBackgroundAndButtonBordersWithoutLabels()
    {
        var blank = CreateDialogPng(rendered: false);

        Assert.False(WindowSnapshotCapture.HasRenderedAutomationContentPng(
            blank, new RectI(100, 100, 220, 140), DialogAutomation()));
    }

    [Fact]
    public async Task DeferredDialogCaptureKeepsWaitingUntilLabelsArePainted()
    {
        var blank = CreateDialogPng(rendered: false);
        var rendered = CreateDialogPng(rendered: true);
        var captures = new Queue<byte[]>([blank, rendered]);
        var screenshotBounds = new RectI(100, 100, 220, 140);
        var automation = DialogAutomation();

        var selected = await ManualRecordingSession.WaitForStableScreenshotAsync(
            _ => Task.FromResult(new WindowSnapshotCapture.CaptureResult(
                captures.Count > 1 ? captures.Dequeue() : rendered,
                "test", UsedFallback: false)),
            waitForDeferredVisualContent: true,
            png => WindowSnapshotCapture.HasRenderedAutomationContentPng(png, screenshotBounds, automation),
            sampleInterval: TimeSpan.Zero,
            minimumObservation: TimeSpan.Zero,
            quietWindow: TimeSpan.Zero,
            maximumObservation: TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(rendered, selected.Png);
        Assert.Single(captures);
    }

    [Fact]
    public void DialogReadinessRequiresSeveralVisibleTabCaptions()
    {
        var blankTabs = CreateDialogPng(rendered: true, includeTabs: true, renderedTabs: false);
        var renderedTabs = CreateDialogPng(rendered: true, includeTabs: true, renderedTabs: true);
        var automation = DialogAutomation().Concat(DialogTabAutomation()).ToArray();
        var bounds = new RectI(100, 100, 220, 140);

        Assert.False(WindowSnapshotCapture.HasRenderedAutomationContentPng(blankTabs, bounds, automation));
        Assert.True(WindowSnapshotCapture.HasRenderedAutomationContentPng(renderedTabs, bounds, automation));
    }

    [Theory]
    [InlineData("Revit", "", "", true)]
    [InlineData("revit", "", "", true)]
    [InlineData("Host", "Revit.exe", "", true)]
    [InlineData("Host", "", "Autodesk Revit 2027", true)]
    [InlineData("EXCEL", "EXCEL.EXE", "Microsoft Excel", false)]
    public void LiveScreenFallbackIsUsedForRevitSurfaces(
        string processName,
        string originalFilename,
        string productName,
        bool expected)
    {
        var target = new WindowTarget(
            1, 1, 7, processName, DateTimeOffset.UnixEpoch, "Document", "Window", new RectI(0, 0, 800, 600),
            OriginalFilename: originalFilename,
            ProductName: productName);

        Assert.Equal(expected, WindowSnapshotCapture.RequiresLiveScreenFallback(target));
    }

    [Fact]
    public void TallPopupSamplingCoversRowsAndActionColumn()
    {
        var bounds = new RectI(159, 142, 376, 716);

        var points = BoundedAutomationCollector.PopupSamplingPoints(bounds);

        Assert.True(points.Count >= 70);
        Assert.Contains(points, point => point.X < bounds.X + bounds.Width / 2);
        Assert.Contains(points, point => point.X > bounds.X + bounds.Width * 3 / 4);
        var rows = points.Select(point => point.Y).Distinct().Order().ToArray();
        Assert.True(rows.Length >= 35);
        Assert.All(rows.Zip(rows.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 1, 26));
    }

    [Fact]
    public void PopupCoverageSelectsOnlyACloselyMatchingMenuTree()
    {
        var popup = new NativeMethods.Rect { Left = 100, Top = 200, Right = 400, Bottom = 600 };

        Assert.Equal(1d, BoundedAutomationCollector.PopupCoverage(
            new System.Windows.Rect(100, 200, 300, 400), popup));
        Assert.True(BoundedAutomationCollector.PopupCoverage(
            new System.Windows.Rect(110, 210, 280, 380), popup) > 0.8);
        Assert.True(BoundedAutomationCollector.PopupCoverage(
            new System.Windows.Rect(100, 200, 80, 40), popup) < 0.72);
    }

    [Fact]
    public void PopupSceneBoundsRetainTheFullOwnerWindow()
    {
        var started = DateTimeOffset.UnixEpoch;
        var root = new WindowTarget(1, 1, 7, "EXCEL", started, "Book1", "XLMAIN",
            new RectI(0, 0, 1200, 900));
        var popup = new WindowTarget(2, 1, 7, "EXCEL", started, "Fonts", "Net UI Tool Window",
            new RectI(160, 140, 376, 716), OwnerHwnd: 1);

        Assert.Equal(root.Bounds, ManualRecordingSession.CompositeBounds([root, popup]));
    }

    [Fact]
    public async Task RecorderRemembersControllerWindowIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Controller, long Target, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var controller = new System.Windows.Window { Title = "Synthetic command window", Width = 260, Height = 140, Left = 40, Top = 40, ShowActivated = false };
            controller.Show();
            var target = new System.Windows.Window { Title = "Synthetic recording target", Width = 260, Height = 140, Left = 340, Top = 40, ShowActivated = false };
            target.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(controller).Handle.ToInt64(),
                new System.Windows.Interop.WindowInteropHelper(target).Handle.ToInt64(), controller.Dispatcher));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-controller-test-" + Guid.NewGuid().ToString("N") + ".mlrec");
        try
        {
            await using var session = new ManualRecordingSession(WindowCatalog.Resolve(handles.Target), output);
            session.Start(explicitConsent: true);
            Assert.True(session.RememberControllerWindow((nint)handles.Controller));
            Assert.Equal((nint)handles.Controller, session.RememberedControllerWindow);
            Assert.False(session.RememberControllerWindow((nint)handles.Target));
            Assert.Equal(0, session.RememberedControllerWindow);
            session.Cancel(retain: false);
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void CatalogReturnsOneVisibleRepresentativePerRootOwnerFamily()
    {
        if (!OperatingSystem.IsWindows()) return;
        var windows = WindowCatalog.ListTopLevelWindows();
        Assert.Equal(windows.Count, windows.Select(x => x.RootOwnerHwnd).Distinct().Count());
        Assert.All(windows, x =>
        {
            Assert.True(NativeMethods.IsWindowVisible((nint)x.Hwnd));
            Assert.Equal(x.RootOwnerHwnd, WindowCatalog.GetRootOwnerHandle((nint)x.Hwnd).ToInt64());
        });
        Assert.All(windows, x => Assert.True(x.Bounds.Width > 0 && x.Bounds.Height > 0));
    }

    [Fact]
    public async Task CatalogUsesVisibleOwnedDialogWhenItsRootOwnerIsHidden()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, long Dialog, System.Windows.Threading.Dispatcher Dispatcher)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var title = "Synthetic legacy startup " + Guid.NewGuid().ToString("N");
        var thread = new Thread(() =>
        {
            var root = CreateWindowExW(0, "STATIC", "Synthetic hidden owner", WsPopup,
                -10_000, -10_000, 320, 220, 0, 0, 0, 0);
            var dialog = CreateWindowExW(0, "STATIC", title, WsPopup | WsVisible | WsCaption,
                120, 120, 360, 220, root, 0, 0, 0);
            ready.SetResult((root.ToInt64(), dialog.ToInt64(), System.Windows.Threading.Dispatcher.CurrentDispatcher));
            System.Windows.Threading.Dispatcher.Run();
            if (dialog != 0) DestroyWindow(dialog);
            if (root != 0) DestroyWindow(root);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.NotEqual(0, handles.Root);
            Assert.NotEqual(0, handles.Dialog);
            Assert.False(NativeMethods.IsWindowVisible((nint)handles.Root));
            var selected = Assert.Single(WindowCatalog.ListTopLevelWindows(), window => window.Hwnd == handles.Dialog);
            Assert.Equal(handles.Root, selected.RootOwnerHwnd);
            Assert.Equal(handles.Dialog, selected.Hwnd);
            Assert.True(ManualRecordingSession.IsActivationForegroundMatch(
                (nint)handles.Dialog,
                (nint)handles.Dialog));
            Assert.True(ManualRecordingSession.IsActivationForegroundMatch(
                (nint)handles.Dialog,
                (nint)handles.Root));

            var scoped = WindowCatalog.ListScopedWindows(selected);
            Assert.Contains(scoped, window => window.Hwnd == handles.Root);
            Assert.Contains(scoped, window => window.Hwnd == handles.Dialog);
            Assert.False(WindowSnapshotCapture.IsCapturable(scoped.Single(window => window.Hwnd == handles.Root)));
            Assert.True(WindowSnapshotCapture.IsCapturable(scoped.Single(window => window.Hwnd == handles.Dialog)));

            var capture = await WindowSnapshotCapture.CapturePngAsync(selected, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(capture.Png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task OwnedPopupKeepsItsOwnObservationIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, long Popup, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Synthetic root", Width = 320, Height = 220, Left = -10_000, Top = -10_000, ShowActivated = false };
            root.Show();
            var popup = new System.Windows.Window { Title = "Synthetic popup", Width = 180, Height = 120, Left = -9_900, Top = -9_900, Owner = root, ShowActivated = false,
                Content = new System.Windows.Controls.Button { Content = "Popup action" } };
            popup.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(),
                new System.Windows.Interop.WindowInteropHelper(popup).Handle.ToInt64(), root.Dispatcher));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var target = WindowCatalog.Resolve(handles.Root);
            var scoped = WindowCatalog.ListScopedWindows(target);
            var popup = Assert.Single(scoped, x => x.Hwnd == handles.Popup);
            Assert.Equal(handles.Popup, WindowSnapshotCapture.Observe(popup).Hwnd);
            var automation = BoundedAutomationCollector.Collect(handles.Root, 100);
            Assert.Contains(automation, item => item.WindowHwnd == handles.Popup);
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task NativeChildWindowResolvesToItsTopLevelRootOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, long Child, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Native child root", Width = 320, Height = 220, Left = -10_000, Top = -10_000, ShowActivated = false };
            root.Show();
            var rootHandle = new System.Windows.Interop.WindowInteropHelper(root).Handle;
            var child = CreateWindowExW(0, "STATIC", "Native child", WsChild | WsVisible,
                10, 10, 120, 40, rootHandle, 0, 0, 0);
            ready.SetResult((rootHandle.ToInt64(), child.ToInt64(), root.Dispatcher));
            System.Windows.Threading.Dispatcher.Run();
            if (child != 0) DestroyWindow(child);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.NotEqual(0, handles.Child);
            Assert.Equal(handles.Root, WindowCatalog.GetRootOwnerHandle((nint)handles.Child).ToInt64());
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ScopedCaptureProducesPngAndHonorsCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Capture root", Width = 260, Height = 180, Left = 80, Top = 80, ShowActivated = false,
                Content = new System.Windows.Controls.TextBlock { Text = "Synthetic capture pixels", Margin = new System.Windows.Thickness(20) } };
            root.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(), root.Dispatcher));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var target = WindowCatalog.Resolve(handles.Root);
            var capture = await WindowSnapshotCapture.CapturePngAsync(target, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(capture.Png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.False(WindowSnapshotCapture.IsVisuallyBlankPng(capture.Png));
            Assert.Contains(capture.Method, new[] { "windows-graphics-capture", "gdi-window-fallback", "screen-bounds-fallback" });
            if (string.Equals(Environment.GetEnvironmentVariable("UI-ATLAS_REQUIRE_NATIVE_CAPTURE"), "1", StringComparison.Ordinal))
                Assert.Equal("windows-graphics-capture", capture.Method);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                WindowSnapshotCapture.CapturePngAsync(target, new CancellationToken(canceled: true)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WindowSnapshotCapture.CapturePngAsync(Enumerable.Repeat(target, WindowSnapshotCapture.MaxScopedWindows + 1).ToArray(), CancellationToken.None));
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ScopedCaptureUsesTheCallerSealedWindowSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, System.Windows.Window Window)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Sealed root", Width = 240, Height = 160, Left = 160, Top = 160, ShowActivated = false };
            root.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(), root));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var target = WindowCatalog.Resolve(handles.Root);
            var sealedWindows = WindowCatalog.ListScopedWindows(target);
            await handles.Window.Dispatcher.InvokeAsync(() =>
            {
                var popup = new System.Windows.Window { Title = "Late popup", Width = 420, Height = 300, Left = 500, Top = 500, Owner = handles.Window, ShowActivated = false };
                popup.Show();
            });

            var capture = await WindowSnapshotCapture.CapturePngAsync(sealedWindows, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
            using var stream = new MemoryStream(capture.Png, writable: false);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(sealedWindows[0].Bounds.Width, decoder.Frames[0].PixelWidth);
            Assert.Equal(sealedWindows[0].Bounds.Height, decoder.Frames[0].PixelHeight);
        }
        finally
        {
            handles.Window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RecordingSessionSuppressesOverlayAroundEveryScreenshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window
            {
                Title = "Capture guard root",
                Width = 240,
                Height = 160,
                Left = 180,
                Top = 180,
                ShowActivated = false
            };
            root.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(), root.Dispatcher));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-capture-guard-test-" + Guid.NewGuid().ToString("N") + ".mlrec");
        var transitions = new List<string>();
        try
        {
            await using var session = new ManualRecordingSession(
                WindowCatalog.Resolve(handles.Root),
                output,
                _ =>
                {
                    transitions.Add("hidden");
                    return Task.CompletedTask;
                },
                () => transitions.Add("visible"));
            session.Start(explicitConsent: true);
            var frame = await session.CaptureAsync(
                "capture-guard",
                CancellationToken.None,
                new FrameCaptureOptions(IncludeAutomation: false, ScreenshotTimeout: TimeSpan.FromSeconds(5)));

            Assert.False(string.IsNullOrWhiteSpace(frame.FrameEntry));
            Assert.Equal(["hidden", "visible"], transitions);
            session.Cancel(retain: false);
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public async Task CancelRetentionPolicyIsAppliedAtomically()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Cancellation root", Width = 220, Height = 140, Left = 120, Top = 120, ShowActivated = false };
            root.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(), root.Dispatcher));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var directory = Path.Combine(Path.GetTempPath(), "ui-atlas-cancel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = WindowCatalog.Resolve(handles.Root);
            var discarded = Path.Combine(directory, "discarded.mlrec");
            await using (var session = new ManualRecordingSession(target, discarded))
            {
                session.Start(explicitConsent: true);
                session.Cancel(retain: false);
            }
            Assert.False(File.Exists(discarded));

            var retained = Path.Combine(directory, "retained.mlrec");
            await using (var session = new ManualRecordingSession(target, retained))
            {
                session.Start(explicitConsent: true);
                session.Cancel(retain: true);
            }
            Assert.True(RecordingBundleValidator.Validate(retained).IsValid);
            using var bundle = RecordingBundle.Open(retained);
            Assert.Equal(RecordingOutcome.Cancelled, bundle.ReadJson<RecordingManifest>("manifest.json").Outcome);
        }
        finally
        {
            handles.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ObservationReportsDpiAndMinimizedState()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, System.Windows.Window Window)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "DPI root", Width = 200, Height = 120, Left = 140, Top = 140, ShowActivated = false };
            root.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(), root));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var target = WindowCatalog.Resolve(handles.Root);
            Assert.InRange(WindowSnapshotCapture.Observe(target).Dpi, 48, 960);
            await handles.Window.Dispatcher.InvokeAsync(() => handles.Window.WindowState = System.Windows.WindowState.Minimized);
            await Task.Delay(100);
            Assert.True(WindowSnapshotCapture.Observe(target).IsMinimized);
        }
        finally
        {
            handles.Window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WindowMovementAcrossVirtualDesktopRefreshesBoundsAndDpi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(long Root, System.Windows.Window Window)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Virtual desktop root", Width = 180, Height = 100, Left = 30, Top = 30, ShowActivated = false };
            root.Show();
            ready.SetResult((new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(), root));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var handles = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var target = WindowCatalog.Resolve(handles.Root);
            var initial = WindowSnapshotCapture.Observe(target);
            var virtualLeft = System.Windows.SystemParameters.VirtualScreenLeft;
            var primaryWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
            var virtualWidth = System.Windows.SystemParameters.VirtualScreenWidth;
            if (virtualLeft >= 0 && virtualWidth <= primaryWidth)
            {
                Assert.False(string.Equals(Environment.GetEnvironmentVariable("UI-ATLAS_REQUIRE_MULTI_MONITOR"), "1", StringComparison.Ordinal),
                    "The qualification environment does not expose a secondary monitor.");
                return;
            }
            var destination = virtualLeft < 0 ? virtualLeft + 30 : primaryWidth + 30;
            await handles.Window.Dispatcher.InvokeAsync(() => handles.Window.Left = destination);
            await Task.Delay(300);
            var moved = WindowSnapshotCapture.Observe(target);
            Assert.NotEqual(initial.Bounds.X, moved.Bounds.X);
            Assert.InRange(moved.Dpi, 48, 960);
        }
        finally
        {
            handles.Window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    private static byte[] CreateSolidPng(System.Windows.Media.Color color)
    {
        const int width = 8;
        const int height = 8;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = color.A;
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateDialogPng(bool rendered, bool includeTabs = false, bool renderedTabs = false)
    {
        const int width = 220;
        const int height = 140;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 244;
            pixels[offset + 1] = 244;
            pixels[offset + 2] = 244;
            pixels[offset + 3] = 255;
        }

        DrawRectangle(120, 100, 70, 28, 70);
        if (rendered)
        {
            DrawGlyphRow(30, 31, 130, 6, 45);
            DrawGlyphRow(140, 109, 28, 5, 45);
        }
        if (includeTabs)
        {
            foreach (var x in new[] { 8, 58, 108, 158 })
            {
                DrawRectangle(x, 5, 45, 20, 90);
                if (renderedTabs)
                    DrawGlyphRow(x + 8, 12, 24, 5, 35);
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();

        void DrawRectangle(int x, int y, int rectangleWidth, int rectangleHeight, byte value)
        {
            for (var column = x; column < x + rectangleWidth; column++)
            {
                SetPixel(column, y, value);
                SetPixel(column, y + rectangleHeight - 1, value);
            }
            for (var row = y; row < y + rectangleHeight; row++)
            {
                SetPixel(x, row, value);
                SetPixel(x + rectangleWidth - 1, row, value);
            }
        }

        void DrawGlyphRow(int x, int y, int rowWidth, int glyphHeight, byte value)
        {
            for (var column = x; column < x + rowWidth; column += 8)
            for (var row = y; row < y + glyphHeight; row++)
                SetPixel(column, row, value);
        }

        void SetPixel(int x, int y, byte value)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
        }
    }

    private static AutomationObservation[] DialogAutomation() =>
    [
        new("message", "root", "4001",
            "A long message that must be visibly painted before capture.",
            "ControlType.Text", "NetUILabel", new RectI(130, 130, 140, 45),
            true, false, "Win32", 7),
        new("ok", "root", "2", "OK", "ControlType.Button", "NetUIButton",
            new RectI(220, 200, 70, 28), true, false, "Win32", 7,
            HasKeyboardFocus: true),
        new("close", "root", "Close", "Close", "ControlType.Button", "",
            new RectI(285, 105, 25, 25), true, false, "Win32", 7)
    ];

    private static AutomationObservation[] DialogTabAutomation() =>
    [
        new("tab-1", "root", "tab-1", "Number", "ControlType.TabItem", "OfficeDialogTab",
            new RectI(108, 105, 45, 20), true, false, "Win32", 7),
        new("tab-2", "root", "tab-2", "Alignment", "ControlType.TabItem", "OfficeDialogTab",
            new RectI(158, 105, 45, 20), true, false, "Win32", 7),
        new("tab-3", "root", "tab-3", "Font", "ControlType.TabItem", "OfficeDialogTab",
            new RectI(208, 105, 45, 20), true, false, "Win32", 7),
        new("tab-4", "root", "tab-4", "Border", "ControlType.TabItem", "OfficeDialogTab",
            new RectI(258, 105, 45, 20), true, false, "Win32", 7)
    ];
}

using System.IO;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AdaptiveCaptureCoordinatorTests
{
    [Fact]
    public void VisualPopupFallbackKeepsVisibleControlsConnectedToThePopup()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var popup = new WindowTarget(
            22, 11, process.Id, process.ProcessName, process.StartTime.ToUniversalTime(), "Menu", "#32768",
            new RectI(100, 80, 260, 320), OwnerHwnd: 11);
        var visualButton = new AutomationObservation(
            "visual:v3:button", "", "visual:v3:button", "Unlabelled button",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new RectI(120, 110, 180, 28),
            IsEnabled: false, IsOffscreen: true, FrameworkId: "UiAtlas.Visual.Geometry", WindowHwnd: 0);

        var controls = ManualRecordingSession.BuildVisualPopupFallbackAutomation(
            popup, [visualButton], [1, 2, 3, 4]);

        var root = Assert.Single(controls, control => string.IsNullOrWhiteSpace(control.ParentRuntimeId));
        var button = Assert.Single(controls, control => control.RuntimeId == visualButton.RuntimeId);
        Assert.Equal(popup.Hwnd, root.WindowHwnd);
        Assert.Equal(root.RuntimeId, button.ParentRuntimeId);
        Assert.Equal(popup.Hwnd, button.WindowHwnd);
        Assert.False(button.IsOffscreen);
    }

    [Fact]
    public void EmptyVisualPopupFallbackStillRetainsOneContentRegion()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var popup = new WindowTarget(
            22, 11, process.Id, process.ProcessName, process.StartTime.ToUniversalTime(), "Menu", "#32768",
            new RectI(100, 80, 260, 320), OwnerHwnd: 11);

        var controls = ManualRecordingSession.BuildVisualPopupFallbackAutomation(
            popup, [], [1, 2, 3, 4]);

        Assert.Equal(2, controls.Count);
        Assert.Equal("ControlType.Custom", controls[1].ControlType);
        Assert.Equal(controls[0].RuntimeId, controls[1].ParentRuntimeId);
        Assert.Equal("popup-surface", controls[1].VisualRole);
    }

    [Fact]
    public async Task BoundedWorkReturnsWhenTheUnderlyingOperationDoesNotFinish()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), AdaptiveCaptureCoordinator.ManualRootRefreshTimeout);
        Assert.True(AdaptiveCaptureCoordinator.CancelledRootDrainTimeout <= TimeSpan.FromSeconds(1));
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var result = await OpaqueSurfaceScanner.TryCompleteWithinAsync(
            pending.Task, TimeSpan.FromMilliseconds(60), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        pending.TrySetResult(1);
    }

    [Fact]
    public async Task TimedOutRootRefreshReturnsWithoutWaitingForHungWorkerCleanup()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var target = new WindowTarget(
            1, 1, process.Id, process.ProcessName, process.StartTime.ToUniversalTime(), "Root", "Window",
            new RectI(0, 0, 800, 600));
        var baseline = new FrameObservation(
            1, DateTimeOffset.UtcNow, "",
            new WindowObservation(1, 1, process.Id, "Window", "Root", target.Bounds,
                true, true, false, false, 96),
            [], false, "ok", "baseline");
        var release = new TaskCompletionSource<FrameObservation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-root-deadline-test-" + Guid.NewGuid().ToString("N") + ".mlrec");

        await using var session = new ManualRecordingSession(target, output);
        await using var coordinator = new AdaptiveCaptureCoordinator(
            session, target, null, null, null,
            async _ => await release.Task);
        coordinator.Start(baseline);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var refresh = coordinator.RefreshRootSurfaceAsync(
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        release.TrySetResult(null);
        await coordinator.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public void RootPageAnchorRejectsContradictionsButAcceptsAnInconclusiveRead()
    {
        var clients = new RootPageAnchor("clients", "", "");

        Assert.True(AdaptiveCaptureCoordinator.RootPageAnchorsMatch(clients, clients));
        Assert.False(AdaptiveCaptureCoordinator.RootPageAnchorsMatch(
            new RootPageAnchor("tables plan", "", ""), clients));
        Assert.True(AdaptiveCaptureCoordinator.RootPageAnchorsMatch(
            new RootPageAnchor("", "", ""), clients));
    }

    [Fact]
    public async Task CancelledRootRequestIsDrainedAndCannotSatisfyTheNextRequest()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var target = new WindowTarget(
            1, 1, process.Id, process.ProcessName, process.StartTime.ToUniversalTime(), "Root", "Window",
            new RectI(0, 0, 800, 600));
        var baseline = new FrameObservation(
            1, DateTimeOffset.UtcNow, "",
            new WindowObservation(1, 1, process.Id, "Window", "Root", target.Bounds,
                true, true, false, false, 96),
            [], false, "ok", "baseline");
        var replacement = baseline with { Sequence = 2, Trigger = "replacement" };
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-root-request-test-" + Guid.NewGuid().ToString("N") + ".mlrec");

        await using var session = new ManualRecordingSession(target, output);
        await using var coordinator = new AdaptiveCaptureCoordinator(
            session, target, null, null, null,
            async token =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return replacement;
            });
        coordinator.Start(baseline);

        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.RefreshRootSurfaceAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = await coordinator.RefreshRootSurfaceAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Same(replacement, next);
        Assert.Equal(2, attempts);
        await coordinator.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task RootCaptureRetriesWhenThePageAnchorAttemptIsRejected()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var target = new WindowTarget(
            1, 1, process.Id, process.ProcessName, process.StartTime.ToUniversalTime(), "Root", "Window",
            new RectI(0, 0, 800, 600));
        var baseline = new FrameObservation(
            1, DateTimeOffset.UtcNow, "",
            new WindowObservation(1, 1, process.Id, "Window", "Root", target.Bounds,
                true, true, false, false, 96),
            [], false, "ok", "baseline");
        var replacement = baseline with { Sequence = 2, Trigger = "stable" };
        var attempts = 0;
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-root-retry-test-" + Guid.NewGuid().ToString("N") + ".mlrec");

        await using var session = new ManualRecordingSession(target, output);
        await using var coordinator = new AdaptiveCaptureCoordinator(
            session, target, null, null, null,
            _ => Task.FromResult<FrameObservation?>(Interlocked.Increment(ref attempts) == 1
                ? null
                : replacement));
        coordinator.Start(baseline);

        var result = await coordinator.RefreshRootSurfaceAsync(TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Same(replacement, result);
        Assert.Equal(2, attempts);
        await coordinator.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task PreparedRootScreenshotIsPersistedWithoutTakingASecondPng()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(System.Windows.Window Window, long Hwnd)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new System.Windows.Window
            {
                Title = "Prepared screenshot root",
                Width = 320,
                Height = 220,
                Left = 100,
                Top = 100,
                ShowActivated = false
            };
            window.Show();
            ready.SetResult((window, new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64()));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var root = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-prepared-png-test-" + Guid.NewGuid().ToString("N") + ".mlrec");
        var beforeCaptureCalls = 0;
        var afterCaptureCalls = 0;
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nE0AAAAASUVORK5CYII=");

        try
        {
            var target = WindowCatalog.Resolve(root.Hwnd);
            await using var session = new ManualRecordingSession(
                target,
                output,
                _ =>
                {
                    Interlocked.Increment(ref beforeCaptureCalls);
                    return Task.CompletedTask;
                },
                () => Interlocked.Increment(ref afterCaptureCalls));
            session.Start(explicitConsent: true);

            var frame = await session.CaptureAsync(
                "prepared",
                CancellationToken.None,
                new FrameCaptureOptions(
                    IncludeAutomation: false,
                    PreparedScreenshot: new PreparedFrameScreenshot(
                        png, target.Bounds, "prepared-root", UsedFallback: false, IsPartial: false)));
            session.Complete();

            Assert.Equal(0, beforeCaptureCalls);
            Assert.Equal(0, afterCaptureCalls);
            Assert.Equal(target.Bounds, frame.ScreenshotBounds);
            using var bundle = RecordingBundle.Open(output);
            Assert.Equal(png, bundle.ReadBytes(frame.FrameEntry));
        }
        finally
        {
            root.Window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Theory]
    [InlineData("OUTLOOK", "Inbox - Outlook", true)]
    [InlineData("EXCEL", "Book1 - Excel", false)]
    public void OutlookUsesCompleteFramesForManualClicks(string processName, string title, bool expected)
    {
        var target = new WindowTarget(
            1, 1, 7, processName, DateTimeOffset.UnixEpoch, title, "Window",
            new RectI(0, 0, 1200, 800));

        Assert.Equal(expected, AdaptiveCaptureCoordinator.UsesFullControlFrames(target));
    }

    [Theory]
    [InlineData("EXCEL", "Window", "", true)]
    [InlineData("host", "XLMAIN", "Microsoft Excel", true)]
    [InlineData("Revit", "Window", "Autodesk Revit", false)]
    public void ChangedExcelSurfacesUseTheDedicatedWorksheetProvider(
        string processName,
        string className,
        string productName,
        bool expected)
    {
        var target = new WindowTarget(
            1, 1, 7, processName, DateTimeOffset.UnixEpoch, "Document", className,
            new RectI(0, 0, 1_600, 900), ProductName: productName);

        Assert.Equal(expected, AdaptiveCaptureCoordinator.IsExcelTarget(target));
    }

    [Theory]
    [InlineData("FullpageUIHost", true)]
    [InlineData("NetUIFullpageUIWindow", true)]
    [InlineData("XLSpreadsheet", false)]
    public void ExcelBackstageWindowClassesAreRecognized(string className, bool expected)
        => Assert.Equal(expected, AdaptiveCaptureCoordinator.IsBackstageWindowClass(className));

    [Fact]
    public void RootSurfaceIdentitySeparatesBackstageSectionsFromTheWorksheet()
    {
        AutomationObservation[] account =
        [
            new("root", "", "", "Backstage view", "ControlType.Pane", "FullpageUIHost",
                new RectI(0, 0, 1_600, 900), true, false, "Win32", 1),
            new("account", "root", "", "Account", "ControlType.ListItem", "NetUIRibbonTab",
                new RectI(0, 200, 200, 50), true, false, "Win32", 1, IsSelected: true)
        ];
        var worksheet = new[]
        {
            new AutomationObservation("grid", "", "Grid", "Grid", "ControlType.DataGrid", "XLSpreadsheetGrid",
                new RectI(0, 200, 1_600, 700), true, false, "Win32", 1)
        };

        Assert.Equal("excel-backstage:account", AdaptiveCaptureCoordinator.RootSurfaceIdentity(account));
        Assert.Equal("excel-worksheet", AdaptiveCaptureCoordinator.RootSurfaceIdentity(worksheet));
    }

    [Fact]
    public void PointerObservedCanvasTargetUsesAStableWindowRelativeMarker()
    {
        var target = new WindowTarget(
            1, 1, 7, "Revit", DateTimeOffset.UnixEpoch, "Model", "Window",
            new RectI(100, 200, 800, 600));

        var observed = AdaptiveCaptureCoordinator.CreateObservedCanvasTarget(
            target, new RectI(325, 460, 1, 1));

        Assert.Equal("CanvasItem", observed.ControlType);
        Assert.Equal("UiAtlas.Pointer", observed.FrameworkId);
        Assert.Equal("ui-atlas:pointer:225:260", observed.RuntimeId);
        Assert.Equal(new RectI(316, 451, 18, 18), observed.Bounds);
        Assert.Equal(target.RootOwnerHwnd, observed.WindowHwnd);
        Assert.Contains("SelectionItem", observed.SupportedPatterns!);
    }

    [Fact]
    public void RevitRibbonPanelClickBecomesAnIndividualCommand()
    {
        var target = new WindowTarget(
            1, 1, 7, "Revit", DateTimeOffset.UnixEpoch, "Model", "Window",
            new RectI(0, 0, 1600, 900));
        var panel = new AutomationObservation(
            "panel", "ribbon", "", "UIFramework.RvtRibbonPanel",
            "ControlType.DataItem", "ItemsControlItem", new RectI(60, 60, 420, 118),
            true, false, "WPF", target.RootOwnerHwnd,
            ["SelectionItemPatternIdentifiers.Pattern"]);

        var observed = AdaptiveCaptureCoordinator.NormalizeManualHighlightTarget(
            target, panel, new RectI(205, 102, 1, 1));

        Assert.NotNull(observed);
        Assert.Equal("ControlType.Button", observed.ControlType);
        Assert.Equal("UiAtlas.ObservedRibbonCommand", observed.ClassName);
        Assert.Equal("panel", observed.ParentRuntimeId);
        Assert.Equal(new RectI(193, 90, 24, 24), observed.Bounds);
        Assert.Contains("InvokePatternIdentifiers.Pattern", observed.SupportedPatterns!);
    }

    [Fact]
    public void NonRevitDataItemIsNotInventedAsARibbonCommand()
    {
        var target = new WindowTarget(
            1, 1, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Book", "Window",
            new RectI(0, 0, 1600, 900));
        var item = new AutomationObservation(
            "item", "root", "", "UIFramework.RvtRibbonPanel",
            "ControlType.DataItem", "ItemsControlItem", new RectI(60, 60, 420, 118),
            true, false, "WPF", target.RootOwnerHwnd);

        Assert.Same(item, AdaptiveCaptureCoordinator.NormalizeManualHighlightTarget(
            target, item, new RectI(205, 102, 1, 1)));
    }

    [Fact]
    public void CachedRevitRibbonSurfaceStillResolvesAQueuedClickToAnIndividualCommand()
    {
        var target = new WindowTarget(
            1, 1, 7, "Revit", DateTimeOffset.UnixEpoch, "Model", "Window",
            new RectI(0, 0, 1920, 1080), ProductName: "Autodesk Revit");
        var cachedPanel = new AutomationObservation(
            "cached-panel", "ribbon", "Home_Family_PanelBarScrollViewer", "Home_Family",
            "ControlType.Custom", "CachedRibbon", new RectI(0, 62, 1920, 122),
            true, false, "UiAtlas.Cached", target.RootOwnerHwnd);

        var observed = AdaptiveCaptureCoordinator.ResolveManualHighlightTarget(
            target, [cachedPanel], new RectI(657, 75, 1, 1));

        Assert.NotNull(observed);
        Assert.Equal("UiAtlas.ObservedRibbonCommand", observed.ClassName);
        Assert.Equal("cached-panel", observed.ParentRuntimeId);
        Assert.Equal(new RectI(645, 63, 24, 24), observed.Bounds);
    }

    [Fact]
    public void NativeMsaaDialogTabsUseDirectClickInsteadOfUiaSelection()
    {
        var nativeTab = new AutomationObservation("tab", "dialog", "msaa-tab", "Number",
            "ControlType.TabItem", "MSAA.Role37", new RectI(100, 100, 80, 24),
            true, false, "Win32", WindowHwnd: 2,
            SupportedPatterns: ["SelectionItemPatternIdentifiers.Pattern"]);
        var uiaTab = nativeTab with { ClassName = "NetUITab" };

        Assert.True(ManualRecordingSession.ShouldUseDirectClickForDialogTab(nativeTab));
        Assert.False(ManualRecordingSession.ShouldUseDirectClickForDialogTab(uiaTab));
    }

    [Fact]
    public void DialogDismissalPrefersCancelOverCloseAndOk()
    {
        var dialog = new RectI(100, 100, 600, 500);
        var controls = new[]
        {
            DialogButton("ok", "OK", new RectI(480, 540, 90, 30)),
            DialogButton("close", "Close", new RectI(650, 110, 30, 30)),
            DialogButton("cancel", "Cancel", new RectI(580, 540, 90, 30))
        };

        var selected = ManualRecordingSession.ResolveDialogDismissControl(controls, dialog);

        Assert.NotNull(selected);
        Assert.Equal("cancel", selected.AutomationId);
    }

    [Fact]
    public void DialogDismissalNeverUsesAffirmativeButton()
    {
        var dialog = new RectI(100, 100, 600, 500);
        var controls = new[] { DialogButton("ok", "OK", new RectI(480, 540, 90, 30)) };

        Assert.Null(ManualRecordingSession.ResolveDialogDismissControl(controls, dialog));
    }

    private static AutomationObservation DialogButton(string id, string name, RectI bounds) =>
        new(id, "dialog", id, name, "ControlType.Button", "Button", bounds,
            IsEnabled: true, IsOffscreen: false, FrameworkId: "Win32", WindowHwnd: 2,
            SupportedPatterns: ["Invoke"]);

    [Fact]
    public void OwnedDialogCandidateAcceptsOfficeDialogsAndRejectsTransientMenus()
    {
        var common = new WindowTarget(
            Hwnd: 2,
            RootOwnerHwnd: 1,
            ProcessId: 7,
            ProcessName: "EXCEL",
            ProcessStartedUtc: DateTimeOffset.UnixEpoch,
            Title: "Format Cells",
            ClassName: "#32770",
            Bounds: new RectI(100, 100, 560, 520),
            OwnerHwnd: 1);

        Assert.True(AdaptiveCaptureCoordinator.IsOwnedDialogCandidate(common));
        Assert.True(AdaptiveCaptureCoordinator.IsOwnedDialogCandidate(common with
        {
            ClassName = "bosa_sdm_XL9",
            OwnerHwnd = 0
        }));
        Assert.False(AdaptiveCaptureCoordinator.IsOwnedDialogCandidate(common with
        {
            Title = "",
            ClassName = "NetUIHWND",
            Bounds = new RectI(100, 100, 300, 200)
        }));
        Assert.False(AdaptiveCaptureCoordinator.IsOwnedDialogCandidate(common with
        {
            Title = "Book2 - Excel",
            ClassName = "XLMAIN",
            OwnerHwnd = 0
        }));
        Assert.False(AdaptiveCaptureCoordinator.IsOwnedDialogCandidate(common with
        {
            Title = "3c125013-b3f6-49d3-9a6e-c56fd9eb3906Monitor",
            ClassName = "#32770"
        }));
    }

    [Fact]
    public void DialogCheckpointDoesNotTreatPreExistingMainFormAsNewDialog()
    {
        var existing = new AdaptiveDialogCaptureCheckpoint(new HashSet<long> { 2 });

        Assert.False(AdaptiveCaptureCoordinator.ShouldCaptureDialogWindow(2, existing, alreadyCaptured: false));
        Assert.True(AdaptiveCaptureCoordinator.ShouldCaptureDialogWindow(3, existing, alreadyCaptured: false));
        Assert.False(AdaptiveCaptureCoordinator.ShouldCaptureDialogWindow(3, existing, alreadyCaptured: true));
    }

    [Fact]
    public void PeerRootCandidateAcceptsIndependentApplicationWindowButRejectsToolWindow()
    {
        var peer = new WindowTarget(
            Hwnd: 2,
            RootOwnerHwnd: 2,
            ProcessId: 7,
            ProcessName: "OUTLOOK",
            ProcessStartedUtc: DateTimeOffset.UnixEpoch,
            Title: "Untitled - Field Service Mission",
            ClassName: "rctrl_renwnd32",
            Bounds: new RectI(100, 100, 900, 700),
            OwnerHwnd: 0,
            ExStyle: 0x40100);

        Assert.True(AdaptiveCaptureCoordinator.IsPeerRootCaptureCandidate(peer));
        Assert.False(AdaptiveCaptureCoordinator.IsPeerRootCaptureCandidate(peer with
        {
            ExStyle = peer.ExStyle | NativeMethods.WsExToolWindow
        }));
        Assert.False(AdaptiveCaptureCoordinator.IsPeerRootCaptureCandidate(peer with
        {
            RootOwnerHwnd = 1,
            OwnerHwnd = 1
        }));
    }

    [Fact]
    public void OutlookInspectorUsesApplicationRootEvidenceInsteadOfNativeMsaa()
    {
        AutomationObservation[] controls =
        [
            DialogObservation("root", "", "Window", "Field Service Mission"),
            DialogObservation("mission", "root", "Edit", "Mission ID")
        ];

        Assert.False(BoundedAutomationCollector.ShouldPreferNativeDialogEvidence(
            "rctrl_renwnd32", controls));
        Assert.True(BoundedAutomationCollector.ShouldCollectOutlookPeerRootEvidence(
            "rctrl_renwnd32", isOutlook: true));
        Assert.False(BoundedAutomationCollector.ShouldCollectOutlookPeerRootEvidence(
            "rctrl_renwnd32", isOutlook: false));
    }

    [Fact]
    public void PointAutomationRejectsAncestryThatNeverReachedRequestedWindow()
    {
        Assert.True(BoundedAutomationCollector.IsPointAutomationChainScoped(reachedScopeRoot: true));
        Assert.False(BoundedAutomationCollector.IsPointAutomationChainScoped(reachedScopeRoot: false));
    }

    [Fact]
    public void MeaningfulDialogContentRequiresAnInteractiveDescendant()
    {
        var rootOnly = new[]
        {
            DialogObservation("root", "", "Window", "Format Cells")
        };
        var complete = rootOnly.Append(DialogObservation("ok", "root", "Button", "OK")).ToArray();

        Assert.False(ManualRecordingSession.HasMeaningfulDialogContent(rootOnly));
        Assert.True(ManualRecordingSession.HasMeaningfulDialogContent(complete));
    }

    private static AutomationObservation DialogObservation(
        string runtimeId,
        string parentRuntimeId,
        string type,
        string name) => new(
            runtimeId,
            parentRuntimeId,
            name,
            name,
            "ControlType." + type,
            "#32770",
            new RectI(100, 100, 120, 30),
            true,
            false,
            "Win32",
            WindowHwnd: 2,
            SupportedPatterns: type == "Button" ? ["Invoke"] : []);

    [Theory]
    [InlineData("MSO_BORDEREFFECT_WINDOW_CLASS", false)]
    [InlineData("SysShadow", false)]
    [InlineData("Net UI Tool Window", true)]
    public void PopupServiceWindowsDoNotEnterTheCaptureQueue(string className, bool expected)
    {
        Assert.Equal(expected, AdaptiveCaptureCoordinator.IsPopupCaptureCandidateClass(className));
    }

    [Fact]
    public void EmbeddedApplicationPanelsDoNotEnterThePopupCaptureQueue()
    {
        var panel = new WindowTarget(2, 1, 7, "Adobe Premiere Pro", DateTimeOffset.UnixEpoch,
            "Effects", "DroverLord - Window Class", new RectI(100, 100, 300, 500), Style: 0x40000000);
        var popup = panel with { Hwnd = 3, Style = unchecked((long)0x80000000) };

        Assert.True(AdaptiveCaptureCoordinator.IsEmbeddedChildWindow(panel));
        Assert.False(AdaptiveCaptureCoordinator.IsEmbeddedChildWindow(popup));
    }

    [Fact]
    public void ManualHighlightResolvesRibbonSourceBeforePopupCapture()
    {
        var point = new RectI(165, 25, 1, 1);
        AutomationObservation[] controls =
        [
            new("group", "", "", "Editing", "ControlType.Group", "NetUIGroup",
                new RectI(20, 0, 220, 100), true, false, "Win32"),
            new("split", "group", "", "Find & Select", "ControlType.SplitButton", "NetUISplitButton",
                new RectI(120, 10, 80, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
            new("button", "split", "", "Find", "ControlType.Button", "NetUIButton",
                new RectI(130, 15, 45, 45), true, false, "Win32", SupportedPatterns: ["Invoke"])
        ];

        var resolved = AdaptiveCaptureCoordinator.ResolveManualHighlightBounds(controls, point);

        Assert.Equal(new RectI(120, 10, 80, 60), resolved);
    }

    [Fact]
    public void ManualHighlightRejectsHugeContainerAtTheClickPoint()
    {
        var point = new RectI(760, 460, 1, 1);
        AutomationObservation[] controls =
        [
            new("window", "", "", "Dialog", "ControlType.Window", "Window",
                new RectI(100, 80, 1300, 800), true, false, "Win32", SupportedPatterns: ["Window"]),
            new("pane", "window", "", "Contact details", "ControlType.Pane", "Pane",
                new RectI(420, 180, 900, 650), true, false, "Win32")
        ];

        var resolved = AdaptiveCaptureCoordinator.ResolveManualHighlightBounds(controls, point);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task DialogCollectorReturnsAllVisibleTabsAndButtons()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(System.Windows.Window Root, System.Windows.Window Dialog, long RootHwnd, long DialogHwnd)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window { Title = "Dialog collector root", Width = 500, Height = 400 };
            root.Show();
            var tabs = new System.Windows.Controls.TabControl();
            foreach (var name in new[] { "Number", "Alignment", "Font", "Border", "Fill", "Protection" })
                tabs.Items.Add(new System.Windows.Controls.TabItem
                {
                    Header = name,
                    Content = new System.Windows.Controls.TextBox { Text = name + " value" }
                });
            var panel = new System.Windows.Controls.DockPanel();
            panel.Children.Add(new System.Windows.Controls.Button { Content = "Cancel", Width = 90, Height = 28 });
            panel.Children.Add(tabs);
            var dialog = new System.Windows.Window
            {
                Owner = root,
                Title = "Six tab dialog",
                Width = 440,
                Height = 320,
                Content = panel
            };
            dialog.Show();
            ready.SetResult((root, dialog,
                new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64(),
                new System.Windows.Interop.WindowInteropHelper(dialog).Handle.ToInt64()));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var windows = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var controls = await Task.Run(() =>
                BoundedAutomationCollector.CollectDialogWindow(windows.RootHwnd, windows.DialogHwnd, 500));

            Assert.Equal(6, controls.Count(control =>
                control.ControlType.EndsWith(".TabItem", StringComparison.Ordinal)));
            Assert.Contains(controls, control =>
                control.ControlType.EndsWith(".Button", StringComparison.Ordinal) && control.Name == "Cancel");
            Assert.All(controls, control => Assert.Equal(windows.DialogHwnd, control.WindowHwnd));
        }
        finally
        {
            windows.Root.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ManualAndAutomaticPathsPersistOwnedPopupsBeforeConfirming()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ready = new TaskCompletionSource<(System.Windows.Window Window, long Hwnd)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new System.Windows.Window
            {
                Title = "Adaptive capture root",
                Width = 360,
                Height = 240,
                Left = 80,
                Top = 80,
                ShowActivated = false
            };
            root.Show();
            ready.SetResult((root, new System.Windows.Interop.WindowInteropHelper(root).Handle.ToInt64()));
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var readyRoot = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var root = readyRoot.Window;
        var rootHwnd = readyRoot.Hwnd;
        var output = Path.Combine(Path.GetTempPath(), "ui-atlas-adaptive-test-" + Guid.NewGuid().ToString("N") + ".mlrec");

        try
        {
            var target = WindowCatalog.Resolve(rootHwnd);
            await using var session = new ManualRecordingSession(target, output);
            session.Start(explicitConsent: true);
            var baseline = await session.CaptureAsync("baseline", CancellationToken.None,
                new FrameCaptureOptions(IncludeAutomation: false, CapturePhase: "baseline"));
            var popupAttempts = new Dictionary<long, int>();
            var dialogAttempts = new Dictionary<long, int>();
            await using var coordinator = new AdaptiveCaptureCoordinator(
                session,
                target,
                status: null,
                (popupHwnd, _) =>
                {
                    var popupTarget = WindowCatalog.Resolve(popupHwnd);
                    var bounds = popupTarget.Bounds;
                    popupAttempts[popupHwnd] = popupAttempts.GetValueOrDefault(popupHwnd) + 1;
                    var attempt = popupAttempts[popupHwnd];
                    if (popupTarget.Title.StartsWith("Visual fallback", StringComparison.Ordinal))
                        return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], true, "timeout"));
                    var items = new List<AutomationObservation>
                    {
                        new($"{popupHwnd:x}.root", "", "", popupTarget.Title, "ControlType.Menu", "Popup",
                            bounds, true, false, "WPF", popupHwnd)
                    };
                    if (!popupTarget.Title.StartsWith("Automatic", StringComparison.Ordinal) || attempt >= 2)
                        items.Add(new($"{popupHwnd:x}.item", $"{popupHwnd:x}.root", "action", "Popup action",
                            "ControlType.MenuItem", "MenuItem", new RectI(bounds.X + 20, bounds.Y + 30,
                                Math.Max(20, bounds.Width - 40), 40), true, false, "WPF", popupHwnd));
                    if (popupTarget.Title.StartsWith("Automatic", StringComparison.Ordinal) && attempt >= 3)
                        items.Add(new($"{popupHwnd:x}.item2", $"{popupHwnd:x}.root", "action2", "Second popup action",
                            "ControlType.MenuItem", "MenuItem", new RectI(bounds.X + 20, bounds.Y + 75,
                                Math.Max(20, bounds.Width - 40), 30), true, false, "WPF", popupHwnd));
                    return Task.FromResult<(
                        IReadOnlyList<AutomationObservation> Items,
                        bool TimedOut,
                        string Status)>((
                        items,
                        false,
                        "ok"));
                },
                (dialogHwnd, _) =>
                {
                    var dialogTarget = WindowCatalog.Resolve(dialogHwnd);
                    var bounds = dialogTarget.Bounds;
                    dialogAttempts[dialogHwnd] = dialogAttempts.GetValueOrDefault(dialogHwnd) + 1;
                    if (dialogTarget.Title.Contains("Native only", StringComparison.Ordinal))
                        return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], true, "timeout"));
                    IReadOnlyList<AutomationObservation> items =
                    [
                        new($"{dialogHwnd:x}.root", "", "dialog", dialogTarget.Title,
                            "ControlType.Window", dialogTarget.ClassName, bounds,
                            true, false, "WPF", dialogHwnd),
                        new($"{dialogHwnd:x}.edit", $"{dialogHwnd:x}.root", "value", "[redacted]",
                            "ControlType.Edit", "TextBox",
                            new RectI(bounds.X + 30, bounds.Y + 50, 180, 28),
                            true, false, "WPF", dialogHwnd, ["Value"]),
                        new($"{dialogHwnd:x}.cancel", $"{dialogHwnd:x}.root", "cancel", "Cancel",
                            "ControlType.Button", "Button",
                            new RectI(bounds.X + 30, bounds.Y + 100, 90, 28),
                            true, false, "WPF", dialogHwnd, ["Invoke"])
                    ];
                    return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>((items, false, "ok"));
                });
            coordinator.Start(baseline);

            var manualCheckpoint = coordinator.CreateClickCheckpoint();
            var manualPopup = await ShowPopupAsync(root, "Manual popup", 150);
            var manualOutcome = await coordinator.CaptureClickAsync(
                new RectI(target.Bounds.X + 20, target.Bounds.Y + 20, 1, 1),
                manualCheckpoint,
                CancellationToken.None);
            Assert.Equal(AdaptiveClickCaptureOutcome.PopupCaptured, manualOutcome);
            await manualPopup.Dispatcher.InvokeAsync(manualPopup.Close);

            var automaticCheckpoint = coordinator.CreateClickCheckpoint();
            var automaticSource = new AutomationObservation(
                "root.source", "", "automatic", "Automatic source", "ControlType.Button", "Button",
                new RectI(target.Bounds.X + 10, target.Bounds.Y + 10, 90, 30), true, false, "WPF", rootHwnd,
                ["Invoke"]);
            coordinator.ArmPopupSource(automaticSource);
            var automaticPopupTask = ShowPopupAfterDelayAsync(
                root, "Automatic popup", 210, TimeSpan.FromMilliseconds(450));
            var automaticOutcome = await coordinator.WaitForPopupCapturesAsync(
                automaticCheckpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.Equal(AdaptivePopupCaptureOutcome.Captured, automaticOutcome);
            var automaticPopup = await automaticPopupTask;
            await automaticPopup.Dispatcher.InvokeAsync(automaticPopup.Close);

            var duplicateCheckpoint = coordinator.CreateClickCheckpoint();
            var duplicatePopupTask = ShowPopupAfterDelayAsync(
                root, "Automatic popup", 120, TimeSpan.FromMilliseconds(100));
            var duplicateOutcome = await coordinator.WaitForPopupCapturesAsync(
                duplicateCheckpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.Equal(AdaptivePopupCaptureOutcome.Captured, duplicateOutcome);
            var duplicatePopup = await duplicatePopupTask;
            await duplicatePopup.Dispatcher.InvokeAsync(duplicatePopup.Close);

            var visualFallbackCheckpoint = coordinator.CreateClickCheckpoint();
            var visualFallbackPopup = await ShowPopupAsync(root, "Visual fallback popup", 175);
            var visualFallbackOutcome = await coordinator.WaitForPopupCapturesAsync(
                visualFallbackCheckpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.Equal(AdaptivePopupCaptureOutcome.Captured, visualFallbackOutcome);
            await visualFallbackPopup.Dispatcher.InvokeAsync(visualFallbackPopup.Close);

            var dialogCheckpoint = coordinator.CreateDialogCheckpoint();
            var manualDialog = await ShowDialogAsync(root, "Manual dialog");
            var dialogResult = await coordinator.WaitForDialogCaptureAsync(
                dialogCheckpoint, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(AdaptiveDialogCaptureOutcome.Captured, dialogResult.Outcome);
            Assert.NotNull(dialogResult.Frame);
            Assert.Equal(dialogResult.Hwnd, dialogResult.Frame.Window.Hwnd);
            Assert.Equal(dialogResult.Frame.Window.Bounds, dialogResult.Frame.ScreenshotBounds);
            Assert.All(dialogResult.Frame.Automation, control => Assert.Equal(dialogResult.Hwnd, control.WindowHwnd));
            Assert.Contains(dialogResult.Frame.Automation, control =>
                control.WindowHwnd == dialogResult.Hwnd &&
                control.ControlType.EndsWith(".Button", StringComparison.Ordinal));
            var dismissStarted = DateTimeOffset.UtcNow;
            Assert.True(await session.DismissOwnedDialogAsync(dialogResult.Hwnd, CancellationToken.None));
            Assert.True(DateTimeOffset.UtcNow - dismissStarted < TimeSpan.FromSeconds(2));
            Assert.False(NativeMethods.IsWindow((nint)dialogResult.Hwnd));

            var peerCheckpoint = coordinator.CreateDialogCheckpoint();
            var peerWindow = await ShowDialogAsync(root, "Untitled - Field Service Mission", peerRoot: true);
            var peerResult = await coordinator.WaitForDialogCaptureAsync(
                peerCheckpoint, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(AdaptiveDialogCaptureOutcome.Captured, peerResult.Outcome);
            Assert.NotNull(peerResult.Frame);
            Assert.Contains(peerResult.Frame.ScopedWindows!, window =>
                window.Hwnd == peerResult.Hwnd && window.RootOwnerHwnd == peerResult.Hwnd && window.OwnerHwnd == 0);
            Assert.NotEmpty(peerResult.Frame.FrameEntry);
            Assert.Equal(1, dialogAttempts[peerResult.Hwnd]);
            await peerWindow.Dispatcher.InvokeAsync(peerWindow.Close);

            var nativeOnlyCheckpoint = coordinator.CreateDialogCheckpoint();
            var nativeOnlyWindow = await ShowDialogAsync(root, "Field Service Mission - Native only", peerRoot: true);
            var nativeOnlyResult = await coordinator.WaitForDialogCaptureAsync(
                nativeOnlyCheckpoint, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(AdaptiveDialogCaptureOutcome.Captured, nativeOnlyResult.Outcome);
            Assert.NotNull(nativeOnlyResult.Frame);
            Assert.True(nativeOnlyResult.Frame.AutomationTimedOut);
            Assert.Equal("timeout", nativeOnlyResult.Frame.AutomationStatus);
            Assert.NotEmpty(nativeOnlyResult.Frame.FrameEntry);
            Assert.Equal(1, dialogAttempts[nativeOnlyResult.Hwnd]);
            await nativeOnlyWindow.Dispatcher.InvokeAsync(nativeOnlyWindow.Close);

            await coordinator.DrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            session.Complete();

            using var bundle = RecordingBundle.Open(output);
            var popupFrames = bundle.Entries
                .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
                .Select(bundle.ReadJson<FrameObservation>)
                .Where(frame => frame.Trigger == "adaptive-popup")
                .ToArray();
            Assert.Equal(3, popupFrames.Length);
            Assert.Contains(popupFrames, frame => frame.Automation.Count == 3);
            Assert.Contains(popupFrames, frame => frame.InteractionSource?.Name == "Automatic source");
            Assert.Contains(popupFrames, frame =>
                frame.AutomationTimedOut && frame.AutomationStatus == "visual-only" &&
                frame.Automation.Any(control => control.ClassName == "UiAtlas.VisualControlRegion"));
            Assert.All(popupFrames, frame =>
            {
                Assert.Equal("popup-delta", frame.ObservationScope);
                Assert.Single(frame.ObservedWindowHwnds!);
                Assert.NotEmpty(frame.FrameEntry);
            });
            var dialogFrame = bundle.Entries
                .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
                .Select(bundle.ReadJson<FrameObservation>)
                .Single(frame => frame.Trigger == "adaptive-dialog:Manual dialog");
            Assert.Contains(dialogFrame.Automation, control =>
                control.WindowHwnd == dialogResult.Hwnd &&
                control.ControlType.EndsWith(".Button", StringComparison.Ordinal));
            var peerFrame = bundle.Entries
                .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
                .Select(bundle.ReadJson<FrameObservation>)
                .Single(frame => frame.Trigger == "adaptive-dialog:Untitled - Field Service Mission");
            Assert.Contains(peerFrame.Automation, control =>
                control.WindowHwnd == peerResult.Hwnd &&
                control.ControlType.EndsWith(".Edit", StringComparison.Ordinal));
            Assert.NotEmpty(peerFrame.FrameEntry);
            var nativeOnlyFrame = bundle.Entries
                .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
                .Select(bundle.ReadJson<FrameObservation>)
                .Single(frame => frame.Trigger == "adaptive-dialog:Field Service Mission - Native only");
            Assert.True(nativeOnlyFrame.AutomationTimedOut);
            Assert.NotEmpty(nativeOnlyFrame.FrameEntry);
            Assert.Contains("peer-root-controls-missed", bundle.ReadText("raw/capture-health.jsonl"));
        }
        finally
        {
            root.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static async Task<System.Windows.Window> ShowPopupAsync(
        System.Windows.Window owner,
        string title,
        double topOffset)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            var popup = new System.Windows.Window
            {
                Owner = owner,
                Title = title,
                Width = 200,
                Height = 130,
                Left = owner.Left + 80,
                Top = owner.Top + topOffset,
                ShowActivated = false,
                Content = new System.Windows.Controls.Button { Content = "Popup action" }
            };
            popup.Show();
            return popup;
        });
    }

    private static async Task<System.Windows.Window> ShowPopupAfterDelayAsync(
        System.Windows.Window owner,
        string title,
        double topOffset,
        TimeSpan delay)
    {
        await Task.Delay(delay);
        return await ShowPopupAsync(owner, title, topOffset);
    }

    private static async Task<System.Windows.Window> ShowDialogAsync(
        System.Windows.Window owner,
        string title,
        bool peerRoot = false)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            var content = new System.Windows.Controls.StackPanel();
            content.Children.Add(new System.Windows.Controls.TextBox { Text = "Dialog value", Width = 180 });
            content.Children.Add(new System.Windows.Controls.Button { Content = "Cancel", Width = 90 });
            var dialog = new System.Windows.Window
            {
                Title = title,
                Width = 320,
                Height = 220,
                Left = owner.Left + 20,
                Top = owner.Top + 20,
                ShowActivated = false,
                Content = content
            };
            if (!peerRoot)
                dialog.Owner = owner;
            dialog.Show();
            return dialog;
        });
    }

    [Theory]
    [InlineData(11, 4, true, AdaptivePopupCaptureOutcome.Captured)]
    [InlineData(11, 4, false, AdaptivePopupCaptureOutcome.Failed)]
    [InlineData(10, 5, true, AdaptivePopupCaptureOutcome.Failed)]
    [InlineData(10, 4, false, AdaptivePopupCaptureOutcome.Failed)]
    [InlineData(10, 4, true, AdaptivePopupCaptureOutcome.NotObserved)]
    public void PopupOutcomeRequiresAConfirmedCapture(
        long captures,
        long failures,
        bool queueDrained,
        AdaptivePopupCaptureOutcome expected)
    {
        var checkpoint = new AdaptiveCaptureCheckpoint(PopupCaptures: 10, PopupFailures: 4);

        Assert.Equal(expected, AdaptiveCaptureCoordinator.ResolvePopupCaptureOutcome(
            checkpoint, captures, failures, queueDrained));
    }

    [Fact]
    public void PopupIsConfirmedOnlyWhenItContainsChildControls()
    {
        var rootOnly = new AutomationObservation("2", "", "", "Menu", "ControlType.Menu", "Net UI Tool Window",
            new RectI(10, 10, 200, 300), true, false, "Win32", 2);
        var item = new AutomationObservation("2.1", "2", "choice", "Choice", "ControlType.MenuItem", "NetUITWBtnMenuItem",
            new RectI(10, 40, 200, 30), true, false, "Win32", 2);

        Assert.False(AdaptiveCaptureCoordinator.HasPopupContent([rootOnly]));
        Assert.True(AdaptiveCaptureCoordinator.HasPopupContent([rootOnly, item]));
    }

    [Fact]
    public void WorksheetCellInsidePopupBoundsIsRejected()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var root = new AutomationObservation("2", "", "", "Scatter", "ControlType.Menu", "Net UI Tool Window",
            popup.Bounds, true, false, "Win32", popup.Hwnd);
        var worksheetCell = new AutomationObservation("2.cell", "2", "L3", "L3", "ControlType.DataItem",
            "XLSpreadsheetCell", new RectI(130, 240, 80, 24), true, false, "Win32", popup.Hwnd);

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, [root, worksheetCell]);

        Assert.Empty(normalized);
    }

    [Fact]
    public void SingleConnectedMenuItemIsValidPopupContent()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var root = new AutomationObservation("2", "", "", "Menu", "ControlType.Menu", "Net UI Tool Window",
            popup.Bounds, true, false, "Win32", popup.Hwnd);
        var item = new AutomationObservation("2.1", "2", "choice", "Choice", "ControlType.MenuItem",
            "NetUITWBtnMenuItem", new RectI(120, 240, 180, 30), true, false, "Win32", popup.Hwnd);

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, [root, item]);

        Assert.Equal(2, normalized.Count);
        Assert.True(AdaptiveCaptureCoordinator.HasPopupContent(normalized));
    }

    [Fact]
    public void ListPopupValuesAndScrollbarAreValidControls()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        AutomationObservation[] controls =
        [
            new("2", "", "font-values", "Font size", "ControlType.List", "NetUIList",
                popup.Bounds, true, false, "Win32", popup.Hwnd),
            new("2.value", "2", "size-11", "11", "ControlType.Text", "NetUIValue",
                new RectI(120, 230, 150, 26), true, false, "Win32", popup.Hwnd),
            new("2.scroll", "2", "scroll", "Vertical", "ControlType.ScrollBar", "ScrollBar",
                new RectI(310, 220, 20, 260), true, false, "Win32", popup.Hwnd, ["RangeValue"]),
            new("2.up", "2.scroll", "up", "Scroll up", "ControlType.Button", "ScrollBarButton",
                new RectI(310, 220, 20, 20), true, false, "Win32", popup.Hwnd, ["Invoke"]),
            new("2.thumb", "2.scroll", "thumb", "Position", "ControlType.Thumb", "ScrollBarThumb",
                new RectI(310, 270, 20, 50), true, false, "Win32", popup.Hwnd, ["Transform"])
        ];

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, controls);

        Assert.Equal(controls.Length, normalized.Count);
        Assert.True(AdaptiveCaptureCoordinator.HasPopupContent(normalized));
        Assert.Contains(normalized, control => control.ControlType == "ControlType.Text" && control.Name == "11");
        Assert.Contains(normalized, control => control.ControlType == "ControlType.ScrollBar");
        Assert.Contains(normalized, control => control.ControlType == "ControlType.Thumb");
    }

    [Fact]
    public void NonWorksheetDataItemInsidePopupIsAValidValueControl()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var root = new AutomationObservation("2", "", "values", "Values", "ControlType.List", "NetUIList",
            popup.Bounds, true, false, "Win32", popup.Hwnd);
        var value = new AutomationObservation("2.value", "2", "value-1", "1 page", "ControlType.DataItem",
            "NetUIValue", new RectI(120, 240, 180, 30), true, false, "Win32", popup.Hwnd, ["SelectionItem"]);

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, [root, value]);

        Assert.Equal(2, normalized.Count);
        Assert.True(AdaptiveCaptureCoordinator.HasPopupContent(normalized));
    }

    [Fact]
    public void ConnectedPopupButtonIsRetainedWithoutClassNameBlacklisting()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var root = new AutomationObservation("2", "", "", "Copy", "ControlType.Menu", "Net UI Tool Window",
            popup.Bounds, true, false, "Win32", popup.Hwnd);
        var unrelatedRibbonButton = new AutomationObservation("2.1", "2", "clipboard", "Office Clipboard",
            "ControlType.Button", "NetUIRibbonButton", new RectI(120, 240, 180, 30), true, false, "Win32", popup.Hwnd);

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, [root, unrelatedRibbonButton]);

        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void NetUiMenuItemIsNotRejectedByItsRibbonClassName()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var root = new AutomationObservation("2", "", "", "Menu", "ControlType.Menu", "Net UI Tool Window",
            popup.Bounds, true, false, "Win32", popup.Hwnd);
        var item = new AutomationObservation("2.1", "2", "choice", "Choice", "ControlType.MenuItem",
            "NetUIRibbonButton", new RectI(120, 240, 180, 30), true, false, "Win32", popup.Hwnd);

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, [root, item]);

        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void PointDerivedMenuKeepsItsProviderRootAndItems()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var hwndRoot = new AutomationObservation("2", "", "", "Menu", "ControlType.Menu", "Net UI Tool Window",
            popup.Bounds, true, false, "Win32", popup.Hwnd);
        var providerRoot = new AutomationObservation("2.provider", "2", "", "Get Data", "ControlType.Menu",
            "NetUIMenu", popup.Bounds, true, false, "Win32", popup.Hwnd);
        var item = new AutomationObservation("2.1", "2.provider", "from-file", "From File", "ControlType.MenuItem",
            "NetUIAnchor", new RectI(120, 240, 180, 30), true, false, "Win32", popup.Hwnd);

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, [hwndRoot, providerRoot, item]);

        Assert.Equal(3, normalized.Count);
        Assert.Contains(normalized, control => control.RuntimeId == providerRoot.RuntimeId);
        Assert.True(AdaptiveCaptureCoordinator.HasPopupContent(normalized));
    }

    [Fact]
    public void RichOfficePopupPreservesStructuralContainersAndControls()
    {
        var popup = Popup(new RectI(100, 200, 300, 400));
        AutomationObservation[] controls =
        [
            new("root", "", "", "Get Data", "ControlType.Menu", "Net UI Tool Window",
                popup.Bounds, true, false, "Win32", popup.Hwnd),
            new("provider", "root", "", "Get Data", "ControlType.Menu", "NetUIMenu",
                popup.Bounds, true, false, "Win32", popup.Hwnd),
            new("pane", "provider", "", "", "ControlType.Pane", "NetUIPane",
                popup.Bounds, true, false, "Win32", popup.Hwnd),
            new("group", "pane", "", "Sources", "ControlType.Group", "NetUIGroup",
                new RectI(110, 230, 280, 300), true, false, "Win32", popup.Hwnd),
            new("file", "group", "from-file", "From File", "ControlType.MenuItem", "NetUIAnchor",
                new RectI(120, 250, 250, 30), true, false, "Win32", popup.Hwnd),
            new("database", "group", "from-database", "From Database", "ControlType.MenuItem", "NetUIAnchor",
                new RectI(120, 285, 250, 30), true, false, "Win32", popup.Hwnd)
        ];

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, controls);

        Assert.Equal(controls.Length, normalized.Count);
        Assert.Contains(normalized, control => control.ControlType == "ControlType.Pane");
        Assert.Contains(normalized, control => control.ControlType == "ControlType.Group");
    }

    [Fact]
    public void PopupSnapshotsMustHaveTheSameMaterializedStructure()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var first = PopupControls(popup, "Choice");
        var changed = PopupControls(popup, "Another choice");

        Assert.True(AdaptiveCaptureCoordinator.PopupSnapshotsMatch(popup, first, first));
        Assert.False(AdaptiveCaptureCoordinator.PopupSnapshotsMatch(popup, first, changed));
    }

    [Fact]
    public void PopupNormalizationRemovesEquivalentProviderDuplicates()
    {
        AutomationObservation[] controls =
        [
            new("root", "", "", "Arrange By", "ControlType.Menu", "Net UI Tool Window",
                new RectI(100, 100, 360, 240), true, false, "Win32", 2),
            new("provider-date", "root", "", "Date", "ControlType.ListItem", "NetUIGalleryButton",
                new RectI(104, 104, 107, 30), true, false, "Win32", 2),
            new("application-date", "root", "", "Date", "ListItem", "NetUIGalleryButton",
                new RectI(105, 105, 107, 30), true, false, "Win32", 2)
        ];

        var deduplicated = AdaptiveCaptureCoordinator.DeduplicatePopupControls(controls);

        Assert.Equal(2, deduplicated.Count);
        Assert.Single(deduplicated, item => item.Name == "Date");
    }

    [Fact]
    public void RibbonFrameSelectsVisibleOwnedPopupWindows()
    {
        WindowTarget[] windows =
        [
            new(10, 10, 20, "OUTLOOK", DateTimeOffset.UnixEpoch, "Inbox", "rctrl_renwnd32",
                new RectI(0, 0, 1400, 900)),
            new(11, 10, 20, "OUTLOOK", DateTimeOffset.UnixEpoch, "", "Net UI Tool Window",
                new RectI(300, 100, 330, 140), OwnerHwnd: 10, ZOrder: 1),
            new(12, 10, 20, "OUTLOOK", DateTimeOffset.UnixEpoch, "", "SysShadow",
                new RectI(304, 104, 330, 140), OwnerHwnd: 10, ZOrder: 2)
        ];

        var selected = AdaptiveCaptureCoordinator.SelectVisiblePopupTargets(10, windows);

        Assert.Single(selected);
        Assert.Equal(11, selected[0].Hwnd);
    }

    [Fact]
    public void LargeShapesGalleryRetainsAllConnectedItems()
    {
        var popup = Popup(new RectI(100, 100, 420, 900));
        var controls = new List<AutomationObservation>
        {
            new("2", "", "", "Shapes", "ControlType.Menu", "Net UI Tool Window", popup.Bounds,
                true, false, "Win32", popup.Hwnd)
        };
        controls.AddRange(Enumerable.Range(1, 87).Select(index =>
            new AutomationObservation($"2.{index}", "2", "", $"Shape {index}", "ControlType.ListItem",
                "NetUIGalleryButton", new RectI(110 + index % 8 * 40, 140 + index / 8 * 40, 30, 30),
                true, false, "Win32", popup.Hwnd)));

        var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, controls);

        Assert.Equal(88, normalized.Count);
    }

    [Fact]
    public void PopupFingerprintDeduplicatesEquivalentStructure()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));
        var first = Controls(100, 200, "Choice");
        var moved = Controls(108, 208, "Choice");

        Assert.Equal(
            AdaptiveCaptureCoordinator.FingerprintPopup(popup, first),
            AdaptiveCaptureCoordinator.FingerprintPopup(popup with { Bounds = new RectI(108, 208, 240, 300) }, moved));
    }

    [Fact]
    public void PopupFingerprintChangesWithStructure()
    {
        var popup = Popup(new RectI(100, 200, 240, 300));

        Assert.NotEqual(
            AdaptiveCaptureCoordinator.FingerprintPopup(popup, Controls(100, 200, "Choice")),
            AdaptiveCaptureCoordinator.FingerprintPopup(popup, Controls(100, 200, "Another choice")));
    }

    [Fact]
    public void ControlFingerprintChangesWithInteractiveState()
    {
        var off = new AutomationObservation("1.1", "1", "toggle", "Option", "CheckBox", "Button",
            new RectI(10, 10, 80, 24), true, false, "Win32", 1, ["Toggle"], ToggleState: "Off");
        var on = off with { ToggleState = "On" };

        Assert.NotEqual(
            AdaptiveCaptureCoordinator.FingerprintControlDelta([off]),
            AdaptiveCaptureCoordinator.FingerprintControlDelta([on]));
    }

    private static WindowTarget Popup(RectI bounds) =>
        new(2, 1, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Popup", "#32768", bounds, OwnerHwnd: 1);

    private static AutomationObservation[] Controls(int left, int top, string name) =>
        [new("2.1", "2", "choice", name, "ListItem", "ListItem", new RectI(left + 30, top + 60, 180, 36), true, false, "Win32", 2)];

    private static AutomationObservation[] PopupControls(WindowTarget popup, string name) =>
    [
        new("2", "", "", "Popup", "ControlType.Menu", "Net UI Tool Window", popup.Bounds,
            true, false, "Win32", popup.Hwnd),
        new("2.1", "2", "choice", name, "ControlType.ListItem", "NetUIGalleryButton",
            new RectI(popup.Bounds.X + 30, popup.Bounds.Y + 60, 180, 36), true, false, "Win32", popup.Hwnd)
    ];
}

using UiAtlas.Core.Contracts;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RecorderWorkflowCommandTests
{
    [Theory]
    [InlineData("Stage 1 of 5: capturing the current screen before scanning controls.", "Capturing screen...", "Stage 1 of 5")]
    [InlineData("Stage 2 of 5: scanning visible controls and tables. Complex applications can take several minutes.", "Scanning controls & tables...", "several minutes")]
    [InlineData("Stage 3 of 5: verifying 219 discovered controls and attaching them to this screen.", "Verifying discovered controls...", "219")]
    public void RecordingProgressExplainsCurrentStage(string message, string headline, string detailFragment)
    {
        Assert.Equal(headline, RecordingControlPanel.ActiveBarText(message));
        Assert.Contains(detailFragment, RecordingControlPanel.ActiveDetailText(message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CachedControlsRemainUnverifiedAndDoNotDuplicateObservedControls()
    {
        var observed = new AutomationObservation(
            "live", "", "save", "Save", "ControlType.Button", "Button",
            new RectI(100, 20, 80, 30), true, false, "WPF", 10, ["Invoke"]);
        var duplicate = observed with { RuntimeId = "cache:save", IsEnabled = false, IsOffscreen = true, FrameworkId = "UiAtlas.Cached" };
        var cachedOnly = new AutomationObservation(
            "cache:print", "", "print", "Print", "ControlType.Button", "Button",
            new RectI(200, 20, 80, 30), false, true, "UiAtlas.Cached", 10, ["Invoke"]);

        var merged = QuickSurfaceScanner.MergeCachedControls([observed], [duplicate, cachedOnly], 10);

        Assert.Equal(2, merged.Count);
        Assert.Contains(observed, merged);
        var unverified = Assert.Single(merged, control => control.AutomationId == "print");
        Assert.True(unverified.IsOffscreen);
        Assert.False(unverified.IsEnabled);
    }

    [Fact]
    public void NativeTreeDoesNotRestoreHistoricalVisualOcrCandidatesFromCache()
    {
        var native = new AutomationObservation(
            "live", "", "save", "Save", "ControlType.Button", "TAbacreButton",
            new RectI(100, 20, 80, 30), true, false, "Win32", 10, ["Invoke"]);
        var cachedVisual = new AutomationObservation(
            "cache:visual", "", "visual:old", "False OCR button", "ControlType.Button",
            "UiAtlas.VisualControlRegion", new RectI(200, 20, 80, 30),
            false, true, "UiAtlas.Cached", 10, ["Invoke"]);

        var merged = QuickSurfaceScanner.MergeCachedControls([native], [cachedVisual], 10);

        Assert.Equal([native], merged);
    }

    [Fact]
    public void QuickMapUsesEightSecondCaptureBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), QuickSurfaceScanner.CaptureBudget);
        Assert.Equal(2_500, QuickSurfaceScanner.MaximumControlCount);
    }

    [Fact]
    public void ControlOnlyClickDoesNotPretendThatNavigationSucceeded()
    {
        Assert.Equal(
            InteractionOutcome.Unobserved,
            Program.ResolveManualInteractionOutcome(
                AdaptiveClickCaptureOutcome.ControlCaptured,
                hasControl: true,
                hasResultFrame: true));
        Assert.Equal(
            InteractionOutcome.Succeeded,
            Program.ResolveManualInteractionOutcome(
                AdaptiveClickCaptureOutcome.RootCaptured,
                hasControl: true,
                hasResultFrame: true));
        Assert.Equal(
            InteractionOutcome.Failed,
            Program.ResolveManualInteractionOutcome(
                AdaptiveClickCaptureOutcome.RootCaptured,
                hasControl: false,
                hasResultFrame: true));
    }

    [Fact]
    public void EmptyAccessibilityTreeTriggersOpaqueSurfaceFallback()
    {
        var snapshot = new AdaptiveExtractionSnapshot(
            "adaptive-extraction/1",
            [new ExtractionSourceResult(ControlEvidenceSource.UiaRaw, "surface", [], "ok", 1)],
            [],
            [],
            ExtractionCoverageStatus.Unavailable,
            "coverage-complete",
            1,
            0);

        Assert.True(QuickSurfaceScanner.NeedsOpaqueSurfaceScan(snapshot));
    }

    [Fact]
    public void PartialInteractiveTreeWithOpaqueRegionTriggersVisualFallback()
    {
        var button = new AutomationObservation(
            "save", "", "save", "Save", "ControlType.Button", "Button",
            new RectI(20, 20, 80, 30), true, false, "Win32", 10, ["Invoke"]);
        var candidate = new MergedControlCandidate(
            "candidate", "surface", button, [], [ControlEvidenceSource.UiaRaw], .96,
            ExtractionCoverageStatus.Observed);
        var snapshot = new AdaptiveExtractionSnapshot(
            "adaptive-extraction/1",
            [new ExtractionSourceResult(ControlEvidenceSource.UiaRaw, "surface", [], "ok", 1)],
            [candidate],
            [new CoverageGapObservation("gap", "surface", CoverageGapKind.LargeContainer,
                new RectI(0, 100, 1000, 600), .8, "from-point")],
            ExtractionCoverageStatus.Partial,
            "probe-budget-exhausted",
            1,
            0);

        Assert.True(QuickSurfaceScanner.NeedsOpaqueSurfaceScan(snapshot));
    }

    [Fact]
    public void CompleteInteractiveTreeWithoutGapsDoesNotTriggerVisualFallback()
    {
        var button = new AutomationObservation(
            "save", "", "save", "Save", "ControlType.Button", "Button",
            new RectI(20, 20, 80, 30), true, false, "Win32", 10, ["Invoke"]);
        var candidate = new MergedControlCandidate(
            "candidate", "surface", button, [], [ControlEvidenceSource.UiaRaw], .96,
            ExtractionCoverageStatus.Confirmed);
        var snapshot = new AdaptiveExtractionSnapshot(
            "adaptive-extraction/1",
            [new ExtractionSourceResult(ControlEvidenceSource.UiaRaw, "surface", [], "ok", 1)],
            [candidate],
            [],
            ExtractionCoverageStatus.Confirmed,
            "coverage-complete",
            1,
            0);

        Assert.False(QuickSurfaceScanner.NeedsOpaqueSurfaceScan(snapshot));
    }

    [Fact]
    public void TimedOutNativePointVerificationMarksQuickMapPartial()
    {
        var bounds = new RectI(0, 0, 800, 600);
        var target = new WindowTarget(10, 10, 7, "LegacyApp", DateTimeOffset.UnixEpoch,
            "Legacy", "TfrmMain", bounds);
        var snapshot = new AdaptiveExtractionSnapshot(
            "adaptive-extraction/1", [], [], [], ExtractionCoverageStatus.Unavailable,
            "coverage-complete", 1, 0);
        var cascade = new AdaptiveExtractionResult([], snapshot, false, "visual-only");
        var visual = new AutomationObservation(
            "visual:save", "", "visual:save", "Save", "ControlType.Button",
            "UiAtlas.VisualControlRegion", new RectI(20, 20, 80, 30),
            false, true, "UiAtlas.Visual.Ocr", 10, VisualRole: "button", OcrText: "Save");
        var shadow = new OpaqueSurfaceScanResult(
            [visual], false, true, 0, 0, ["native-point-verification-partial"]);

        var merged = QuickSurfaceScanner.MergeShadowEvidence(cascade, target, shadow);

        Assert.True(merged.TimedOut);
        Assert.Equal("partial", merged.Status);
        Assert.Contains(merged.Controls, control => control.RuntimeId == visual.RuntimeId);
    }

    [Fact]
    public void HistoricalSurfaceCacheMatchesApplicationAcrossProcessesButNotMajorVersions()
    {
        var target = new TargetScope(
            10, 10, 7, "Revit", DateTimeOffset.UnixEpoch,
            ProductVersion: "27.2.10.0", ProductName: "Autodesk Revit");
        var sameApplication = new ApplicationPlanningProfileKey(
            "revit", "Autodesk Revit", "27", "MainWindow");
        var newerApplication = sameApplication with { MajorVersion = "28" };

        Assert.True(QuickSurfaceScanner.MatchesApplication(target, sameApplication));
        Assert.False(QuickSurfaceScanner.MatchesApplication(target, newerApplication));
    }

    [Fact]
    public void SharedInitialScanReportsVisibleAndUnverifiedControls()
    {
        var bounds = new RectI(0, 0, 1200, 800);
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "raw/frames/frame-000001.png",
            new WindowObservation(1, 1, 7, "XLMAIN", "Book1 - Excel", bounds, true, true, false, false, 96),
            [
                new AutomationObservation("visible", "root", "Visible", "Visible", "Button", "Button",
                    new RectI(20, 20, 80, 30), true, false, "Win32", 1, ["Invoke"]),
                new AutomationObservation("hidden", "root", "Hidden", "Hidden", "Button", "Button",
                    new RectI(120, 20, 80, 30), true, true, "Win32", 1, ["Invoke"])
            ],
            true,
            "partial",
            "quick-map:manual-initial-surface");

        var scan = QuickSurfaceScanner.Describe(frame);

        Assert.True(scan.HasUsableControls);
        Assert.Equal(QuickMapCaptureStatus.Partial, scan.Status);
        Assert.Equal(1, scan.VisibleControlCount);
        Assert.Equal(1, scan.UnverifiedControlCount);
        Assert.Contains("uia-timeout", scan.DiagnosticCodes);
    }

    [Theory]
    [InlineData("Revit")]
    [InlineData("EXCEL")]
    [InlineData("WINWORD")]
    [InlineData("POWERPNT")]
    [InlineData("OUTLOOK")]
    public void QuickMapUsesBoundedRibbonScanForKnownRibbonApplications(string processName)
    {
        var target = new WindowTarget(
            1, 1, 7, processName, DateTimeOffset.UnixEpoch, "Document", "Window",
            new RectI(0, 0, 1_600, 900));

        Assert.True(QuickSurfaceScanner.IsRibbonTarget(target));
    }

    [Theory]
    [InlineData("EXCEL", "Window", "", true)]
    [InlineData("host", "XLMAIN", "Microsoft Excel", true)]
    [InlineData("WINWORD", "OpusApp", "Microsoft Word", false)]
    public void QuickMapRoutesOnlyExcelThroughTheWorksheetProvider(
        string processName,
        string className,
        string productName,
        bool expected)
    {
        var target = new WindowTarget(
            1, 1, 7, processName, DateTimeOffset.UnixEpoch, "Document", className,
            new RectI(0, 0, 1_600, 900), ProductName: productName);

        Assert.Equal(expected, QuickSurfaceScanner.IsExcelTarget(target));
    }

    [Fact]
    public void RevitWindowIsRecognizedForInlineRibbonFlyoutCapture()
    {
        var window = new WindowObservation(10, 10, 20, "Window", "Autodesk Revit 2027 - Project",
            new RectI(0, 0, 1900, 1000), true, true, false, false, 96);

        Assert.True(Program.LooksLikeRevitWindow(window));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Adobe\\Adobe Premiere Pro 2022\\Adobe Premiere Pro.exe\"", "C:\\Program Files\\Adobe\\Adobe Premiere Pro 2022\\Adobe Premiere Pro.exe")]
    [InlineData("  C:\\Apps\\Tool.exe  ", "C:\\Apps\\Tool.exe")]
    public void RegisteredExecutablePathRemovesRegistryQuoting(string value, string expected)
    {
        Assert.Equal(expected, RecordingControlPanel.NormalizeRegisteredExecutablePath(value));
    }

    [Fact]
    public void RecordedRevitTargetUsesAutodeskInstallFolderFallback()
    {
        var target = new TargetScope(
            10, 10, 20, "Revit", DateTimeOffset.UnixEpoch,
            ProductVersion: "20260716_1515(x64)",
            OriginalFilename: "Revit.EXE",
            CompanyName: "Autodesk, Inc.",
            ProductName: "Autodesk Revit");

        Assert.True(RecordingControlPanel.IsAutodeskRevitExecutableCandidate(target, "Revit.exe"));
        Assert.False(RecordingControlPanel.IsAutodeskRevitExecutableCandidate(target, "AutoCAD.exe"));
    }

    [Fact]
    public void ResumeHistoryUsesLilacInsteadOfCurrentSessionBlue()
    {
        var historical = RecordingHighlightOverlay.ResolveHighlightColors(isHistorical: true);
        var current = RecordingHighlightOverlay.ResolveHighlightColors(isHistorical: false);
        var observed = RecordingHighlightOverlay.ResolveObservedHighlightColors();

        Assert.NotEqual(current.Fill, historical.Fill);
        Assert.NotEqual(current.Stroke, historical.Stroke);
        Assert.NotEqual(current.Stroke, observed.Stroke);
        Assert.True(observed.Stroke.G > observed.Stroke.B);
        Assert.True(historical.Stroke.R > current.Stroke.R);
    }

    [Theory]
    [InlineData(100, 100, 40, 30, 100, 100, 40, 30, true)]
    [InlineData(100, 100, 40, 30, 102, 99, 42, 31, true)]
    [InlineData(100, 100, 40, 30, 105, 100, 40, 30, true)]
    [InlineData(100, 100, 120, 90, 130, 120, 28, 28, false)]
    [InlineData(100, 100, 40, 30, 180, 100, 40, 30, false)]
    public void ConfirmedHighlightReplacesOnlyTheSameObservedElement(
        int x1, int y1, int width1, int height1,
        int x2, int y2, int width2, int height2,
        bool expected)
    {
        Assert.Equal(expected, RecordingHighlightOverlay.AreEquivalentHighlightBounds(
            new RectI(x1, y1, width1, height1),
            new RectI(x2, y2, width2, height2)));
    }

    [Fact]
    public void SurfaceRefreshKeepsConfirmedClicksAndReplacesOnlyObservedPageContent()
    {
        var firstClickedButton = new RectI(10, 10, 90, 40);
        var secondClickedButton = new RectI(110, 10, 90, 40);
        var persistentToolbarButton = new RectI(210, 10, 90, 40);
        var oldPageField = new RectI(10, 100, 180, 30);
        var newPageField = new RectI(10, 150, 180, 30);
        var confirmed = new Dictionary<string, List<RectI>>(StringComparer.Ordinal)
        {
            [TabHighlightLayerResolver.GlobalLayerKey] = [firstClickedButton]
        };
        var observed = new Dictionary<string, List<RectI>>(StringComparer.Ordinal)
        {
            [TabHighlightLayerResolver.GlobalLayerKey] = [persistentToolbarButton, oldPageField]
        };
        var historical = new Dictionary<string, List<RectI>>(StringComparer.Ordinal)
        {
            [TabHighlightLayerResolver.GlobalLayerKey] = [new RectI(310, 10, 90, 40)]
        };
        var replacement = new Dictionary<string, IReadOnlyList<RectI>>(StringComparer.Ordinal)
        {
            [TabHighlightLayerResolver.GlobalLayerKey] = [persistentToolbarButton, newPageField]
        };

        RecordingHighlightOverlay.RefreshObservedSurfaceLayers(
            confirmed, observed, historical, replacement);
        confirmed[TabHighlightLayerResolver.GlobalLayerKey].Add(secondClickedButton);
        RecordingHighlightOverlay.RefreshObservedSurfaceLayers(
            confirmed, observed, historical, replacement);

        Assert.Equal(
            [firstClickedButton, secondClickedButton],
            confirmed[TabHighlightLayerResolver.GlobalLayerKey]);
        Assert.Contains(persistentToolbarButton, observed[TabHighlightLayerResolver.GlobalLayerKey]);
        Assert.Contains(newPageField, observed[TabHighlightLayerResolver.GlobalLayerKey]);
        Assert.DoesNotContain(oldPageField, observed[TabHighlightLayerResolver.GlobalLayerKey]);
        Assert.Single(historical[TabHighlightLayerResolver.GlobalLayerKey]);
    }

    [Fact]
    public void DetachedOverlayVisualIsNotProjectableDuringShutdown()
    {
        var detached = new System.Windows.Media.DrawingVisual();

        Assert.False(RecordingHighlightOverlay.IsConnectedToPresentationSource(detached));
    }

    [Fact]
    public void HighlightFollowsStableControlIdentityInsteadOfAFormerNeighborPosition()
    {
        var recordedNotes = Control("ReviewNoteSplit", "Notes", new RectI(1000, 100, 52, 96));
        AutomationObservation[] current =
        [
            Control("ShowThreadedComments", "Show Comments", new RectI(1000, 100, 52, 96)),
            Control("ReviewNoteSplit", "Notes", new RectI(1120, 100, 52, 96))
        ];

        var resolved = RecordingHighlightOverlay.ResolveCurrentHighlightControl(
            recordedNotes, current, recordedNotes.Bounds);

        Assert.NotNull(resolved);
        Assert.Equal("ReviewNoteSplit", resolved.AutomationId);
        Assert.Equal(new RectI(1120, 100, 52, 96), resolved.Bounds);
    }

    [Fact]
    public void FailedAutoCaptureGetsExactlyOneRetryBeforeThePassCanSkipIt()
    {
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);

        Assert.True(Program.ShouldRetryAutoCapture(attempts, "home:font-color"));
        Assert.False(Program.ShouldRetryAutoCapture(attempts, "home:font-color"));
        Assert.Equal(2, attempts["home:font-color"]);
    }

    [Fact]
    public void VerificationScanKeepsOldTargetsAndAddsLateMaterializedTargets()
    {
        var merged = Program.MergeAutoTargets(
            new[] { (Key: "paste", Value: 1) },
            new[] { (Key: "paste", Value: 2), (Key: "font", Value: 3) },
            item => item.Key);

        Assert.Equal(2, merged.Length);
        Assert.Contains(merged, item => item == ("paste", 2));
        Assert.Contains(merged, item => item == ("font", 3));
    }

    [Theory]
    [InlineData(AdaptivePopupCaptureOutcome.Captured, true)]
    [InlineData(AdaptivePopupCaptureOutcome.NotObserved, false)]
    [InlineData(AdaptivePopupCaptureOutcome.Failed, false)]
    public void AutoPopupCommandCompletesOnlyAfterConfirmedGraphCapture(
        AdaptivePopupCaptureOutcome outcome,
        bool expected)
    {
        Assert.Equal(expected, Program.IsAutoPopupCaptureConfirmed(outcome));
    }

    [Fact]
    public void RevitInlineFlyoutRequiresNewVisibleMenuContent()
    {
        var window = new WindowObservation(10, 10, 20, "Window", "Autodesk Revit 2027",
            new RectI(0, 0, 1920, 1040), true, true, false, false, 96);
        var set = new AutomationObservation(
            "set", "work-plane", "ID_SKETCH_PLANE_TOOL_RibbonListButton_FlyoutButtonShowFlyout", "Set",
            "ControlType.Button", "Button", new RectI(1455, 78, 48, 43), true, false, "WPF", 10,
            ["InvokePatternIdentifiers.Pattern"]);
        var before = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window, [set],
            false, "ok", "initial");

        Assert.False(Program.HasMaterializedInlineFlyout(before, [set], set));

        var menuItem = new AutomationObservation(
            "set-work-plane", "set-menu", "ID_SET_WORK_PLANE", "Set Work Plane",
            "ControlType.MenuItem", "MenuItem", new RectI(1455, 125, 250, 40), true, false, "WPF", 10,
            ["InvokePatternIdentifiers.Pattern"]);

        Assert.True(Program.HasMaterializedInlineFlyout(before, [set, menuItem], set));
    }

    [Fact]
    public void UnrelatedScreenChangeDoesNotConfirmRequestedTab()
    {
        var bounds = new RectI(0, 0, 1200, 800);
        var window = new WindowObservation(10, 10, 20, "XLMAIN", "Book1 - Excel",
            bounds, true, true, false, false, 96);
        var requested = new AutomationObservation(
            "home", "tabs", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab",
            new RectI(100, 30, 70, 28), true, false, "Win32", 10, ["SelectionItem"]);
        var unrelatedSelected = new AutomationObservation(
            "insert", "tabs", "TabInsert", "Insert", "ControlType.TabItem", "NetUIRibbonTab",
            new RectI(175, 30, 70, 28), true, false, "Win32", 10, ["SelectionItem"], IsSelected: true);
        var captured = new FrameObservation(
            2, DateTimeOffset.UtcNow, "", window, [requested, unrelatedSelected], false, "ok", "auto-tabs");

        Assert.False(Program.IsRequestedTabSelected(requested, bounds, captured));
        Assert.True(Program.IsRequestedTabSelected(unrelatedSelected, bounds, captured));
    }

    [Fact]
    public void RevitMaterializedPanelConfirmsInvokeOnlyRibbonTab()
    {
        var bounds = new RectI(0, 0, 1920, 1040);
        var window = new WindowObservation(10, 10, 20, "HwndWrapper", "Autodesk Revit 2027",
            bounds, true, true, false, false, 120);
        var requested = new AutomationObservation(
            "create", "tabs", "Home_Family", "Create", "ControlType.Button", "Button",
            new RectI(59, 35, 68, 25), true, false, "WPF", 10, ["InvokePatternIdentifiers.Pattern"]);
        var selectedPanel = new AutomationObservation(
            "create-panel", "ribbon", "Home_Family_PanelBarScrollViewer", "Home_Family",
            "ControlType.Custom", "", new RectI(0, 62, 1920, 122), true, false, "WPF", 10);
        var captured = new FrameObservation(
            2, DateTimeOffset.UtcNow, "", window, [requested, selectedPanel], false, "ok", "auto-tabs");

        Assert.True(Program.IsRequestedTabSelected(requested, bounds, captured));
    }

    [Fact]
    public void WpfRibbonTabUsesBoundedPhysicalActivation()
    {
        var tab = new AutomationObservation(
            "modify", "tabs", "Modify", "Modify", "ControlType.Button", "Button",
            new RectI(483, 35, 72, 25), true, false, "WPF", 10,
            ["InvokePatternIdentifiers.Pattern"]);

        Assert.False(Program.ShouldInvokeAutoTabBeforeClick(tab, attempt: 1));
        Assert.False(Program.ShouldInvokeAutoTabBeforeClick(tab, attempt: 2));
    }

    [Theory]
    [InlineData("RESUME_MANUAL")]
    [InlineData("RESUME_AUTO")]
    [InlineData("RESUME_QUICK")]
    [InlineData("START_MANUAL")]
    [InlineData("START_AUTO")]
    public void MapReadyAcceptsManualAutomaticAndRescanCommands(string command)
    {
        Assert.True(Program.IsMapReadyLaunchCommand(command));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("FOCUS_READY")]
    [InlineData("P")]
    [InlineData("START_QUICK")]
    public void MapReadyRejectsNonLaunchCommands(string command)
    {
        Assert.False(Program.IsMapReadyLaunchCommand(command));
    }

    [Theory]
    [InlineData("START_MANUAL", true)]
    [InlineData("START_AUTO", true)]
    [InlineData("START_QUICK", false)]
    [InlineData("RESUME_MANUAL", false)]
    [InlineData("RESUME_AUTO", false)]
    [InlineData("RESUME_QUICK", false)]
    public void OnlyStartCommandsCreateANewMap(string command, bool expected)
    {
        Assert.Equal(expected, Program.IsNewMapLaunchCommand(command));
    }

    [Theory]
    [InlineData(false, false, "START_MANUAL")]
    [InlineData(false, true, "START_AUTO")]
    [InlineData(true, false, "RESUME_MANUAL")]
    [InlineData(true, true, "RESUME_AUTO")]
    public void SessionModeCommandPreservesNewMapVersusExplicitResume(
        bool resumeMode,
        bool autoTabs,
        string expected)
    {
        Assert.Equal(expected, Program.SessionModeLaunchCommand(resumeMode, autoTabs));
    }

    [Fact]
    public void CurrentScreenRescanIsAvailableOnlyForAnExistingMap()
    {
        Assert.Equal("RESUME_QUICK", Program.RescanLaunchCommand(resumeMode: true));
        Assert.Throws<InvalidOperationException>(() => Program.RescanLaunchCommand(resumeMode: false));
        Assert.True(RecordingControlPanel.ShouldShowRescanAction(resumeMode: true));
        Assert.False(RecordingControlPanel.ShouldShowRescanAction(resumeMode: false));
    }

    [Theory]
    [InlineData(true, "PreStart", true)]
    [InlineData(true, "MapReady", true)]
    [InlineData(true, "Active", false)]
    [InlineData(true, "Paused", false)]
    [InlineData(false, "MapReady", false)]
    public void WindowSelectionUnlocksAfterMapIsReadyButNotDuringRecording(
        bool supported,
        string modeName,
        bool expected)
    {
        var mode = Enum.Parse<RecordingControlPanel.RecordingPanelMode>(modeName);
        Assert.Equal(expected, RecordingControlPanel.AllowsTargetSelection(supported, mode));
    }

    [Fact]
    public void ExpandedApplicationMenuRequiresNewMenuContentBelowTheMenuBar()
    {
        var window = new WindowObservation(100, 100, 7, "Premiere Pro", "Adobe Premiere Pro",
            new RectI(0, 0, 1920, 1040), true, true, false, false, 96);
        var file = new AutomationObservation("menu.file", "menubar", "Item 1", "File",
            "ControlType.MenuItem", "", new RectI(0, 29, 40, 24), true, false, "Win32", 100);
        var edit = new AutomationObservation("menu.edit", "menubar", "Item 2", "Edit",
            "ControlType.MenuItem", "", new RectI(40, 29, 40, 24), true, false, "Win32", 100);
        var before = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window, [file, edit],
            false, "ok", "initial");
        var menu = AutoTabDiscovery.Discover(before).Single(item => item.DisplayName == "Edit");
        var after = before with
        {
            Sequence = 2,
            Automation = [file, edit, new AutomationObservation("menu.edit.undo", "menu.edit", "Item 1", "Undo",
                "ControlType.MenuItem", "", new RectI(40, 53, 390, 24), true, false, "Win32", 100)]
        };

        Assert.True(Program.HasExpandedApplicationMenu(before, after, menu));
        Assert.False(Program.HasExpandedApplicationMenu(before, before, menu));
    }

    private static AutomationObservation Control(string automationId, string name, RectI bounds) =>
        new(
            automationId,
            "parent",
            automationId,
            name,
            "ControlType.SplitButton",
            "NetUIRibbonButton",
            bounds,
            true,
            false,
            "Win32",
            1,
            ["ExpandCollapse"]);
}

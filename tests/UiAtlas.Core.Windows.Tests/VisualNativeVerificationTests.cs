using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class VisualNativeVerificationTests
{
    [Fact]
    public void OcrFallbackRequiresAbsenceOfUsableNativeApplicationControls()
    {
        var window = new AutomationObservation(
            "window", "", "", "Application", "ControlType.Window", "TfrmMain",
            new RectI(0, 0, 900, 700), true, false, "Win32", 1);
        var pane = new AutomationObservation(
            "pane", "window", "", "Opaque surface", "ControlType.Pane", "TAbacreDBGrid",
            new RectI(0, 50, 900, 650), true, false, "Win32", 1);
        var titleBar = new AutomationObservation(
            "title", "window", "", "Title", "ControlType.TitleBar", "TitleBar",
            new RectI(0, 0, 900, 30), true, false, "Win32", 1);
        var close = new AutomationObservation(
            "close", "title", "close", "Close", "ControlType.Button", "Button",
            new RectI(870, 0, 30, 30), true, false, "Win32", 1, ["Invoke"]);
        var applicationButton = new AutomationObservation(
            "save", "pane", "save", "Save", "ControlType.Button", "TAbacreButton",
            new RectI(20, 60, 90, 28), true, false, "Win32", 1, ["Invoke"]);
        var ownerDrawnAction = applicationButton with
        {
            RuntimeId = "custom-action",
            ControlType = "ControlType.Custom",
            ClassName = "OwnerDrawnAction"
        };

        Assert.True(VisualFallbackPolicy.ShouldUseOcrFallback([window, pane, titleBar, close]));
        Assert.False(VisualFallbackPolicy.ShouldUseOcrFallback(
            [window, pane, titleBar, close, applicationButton]));
        Assert.False(VisualFallbackPolicy.ShouldUseOcrFallback(
            [window, pane, titleBar, close, ownerDrawnAction]));
    }

    [Fact]
    public void VisualOrCachedCandidatesNeverCountAsANativeTree()
    {
        var visual = Visual("save", "ControlType.Button", new RectI(20, 10, 90, 28));
        var cached = visual with { FrameworkId = "UiAtlas.Cached", IsOffscreen = false };

        Assert.True(VisualFallbackPolicy.ShouldUseOcrFallback([visual, cached]));
    }

    [Fact]
    public void OfflineRepairReclassifiesPreviouslyCapturedVisualFallbacks()
    {
        var visual = Visual("legacy-cell", "ControlType.Button", new RectI(20, 10, 90, 28)) with
        {
            FrameworkId = "UiAtlas.Visual.Geometry"
        };
        var native = visual with { RuntimeId = "native", FrameworkId = "Win32" };

        Assert.True(OfflineRecordingEnricher.RequiresVisualReclassification([visual]));
        Assert.False(OfflineRecordingEnricher.RequiresVisualReclassification([native]));
    }

    [Fact]
    public void OfflineRepairRecoversLargeNativeDatabaseGridWithoutVisualPlaceholders()
    {
        var grid = new AutomationObservation(
            "grid", "root", "", "Orders", "ControlType.Pane", "TAbacreDBGrid",
            new RectI(20, 80, 900, 500), true, false, "Win32", 1);
        var table = grid with { RuntimeId = "table", ControlType = "ControlType.Table" };

        Assert.True(OfflineRecordingEnricher.RequiresLegacyStructureRecovery([grid]));
        Assert.False(OfflineRecordingEnricher.RequiresLegacyStructureRecovery([grid, table]));
    }

    [Fact]
    public void PartialRootSnapshotAlwaysRequestsOfflineVisualRecovery()
    {
        var window = new WindowObservation(
            1, 1, 7, "TfrmMain", "Order", new RectI(0, 0, 1_200, 800),
            true, true, false, false, 96);
        var root = new FrameObservation(
            1, DateTimeOffset.UnixEpoch, "raw/frames/frame-000001.png", window, [],
            true, "partial", "adaptive-root-change", [window]);
        var delta = root with
        {
            AutomationTimedOut = false,
            AutomationStatus = "ok",
            Trigger = "adaptive-control",
            ObservationScope = "control-delta"
        };

        Assert.True(OfflineRecordingEnricher.RequiresIncompleteRootRecovery(root));
        Assert.False(OfflineRecordingEnricher.RequiresIncompleteRootRecovery(delta));
    }

    [Fact]
    public void LegacyDelphiRootUsesFastSnapshotAndCarriesOnlyStableChromeHints()
    {
        var target = new WindowTarget(
            1, 1, 7, "ahms", DateTimeOffset.UnixEpoch, "Orders", "TfrmMain",
            new RectI(0, 0, 1_200, 800));
        AutomationObservation[] controls =
        [
            new("root", "", "", "Application", "ControlType.Window", "TfrmMain",
                target.Bounds, true, false, "Win32", 1),
            new("toolbar", "root", "", "", "ControlType.Pane", "TAbacrePanel",
                new RectI(0, 80, 1_200, 70), true, false, "Win32", 1),
            new("new-order", "toolbar", "", "New Order", "ControlType.Pane", "TAbacreButton",
                new RectI(200, 90, 100, 56), true, false, "Win32", 1),
            new("grid", "root", "", "Orders", "ControlType.Pane", "TAbacreDBGrid",
                new RectI(20, 220, 1_160, 500), true, false, "Win32", 1)
        ];

        Assert.True(AdaptiveCaptureCoordinator.PreferFastVisualRootCapture(target, controls));
        var hints = AdaptiveCaptureCoordinator.SelectFastRootSnapshotHints(controls, target.Bounds);
        Assert.Contains(hints, control => control.RuntimeId == "root");
        Assert.Contains(hints, control => control.RuntimeId == "toolbar");
        Assert.Contains(hints, control => control.RuntimeId == "new-order");
        Assert.DoesNotContain(hints, control => control.RuntimeId == "grid");
    }

    [Fact]
    public void PageControlWithOnlyTabHeadersLeavesItsBodyOpaque()
    {
        var root = new RectI(0, 0, 1_200, 800);
        var page = new AutomationObservation(
            "page", "root", "", "", "ControlType.Tab", "TPageControl",
            new RectI(0, 500, 1_200, 240), true, false, "Win32", 1);
        var tabs = new[] { "General", "Additional", "Orders", "Account", "Files", "Loyalty Points" }
            .Select((name, index) => new AutomationObservation(
                $"tab-{index}", page.RuntimeId, "", name, "ControlType.TabItem", "",
                new RectI(index * 90, 502, 86, 22), true, false, "Win32", 1))
            .ToArray();
        var toolbarButton = new AutomationObservation(
            "toolbar", "root", "", "Clients", "ControlType.Pane", "TAbacreButton",
            new RectI(400, 80, 100, 56), true, false, "Win32", 1);

        var region = Assert.Single(VisualFallbackPolicy.FindOpaqueRegions(
            [page, toolbarButton, .. tabs], root));

        Assert.Equal(page.Bounds, region);
    }

    [Fact]
    public void OfficeGalleryWithOnlyItsChevronLeavesPaintedCommandsOpaque()
    {
        var root = new RectI(0, 0, 1_920, 1_040);
        var gallery = new AutomationObservation(
            "gallery", "ribbon", "OfficeScriptsGallery", "", "ControlType.MenuItem", "NetUIAnchor",
            new RectI(169, 112, 659, 78), true, false, "Win32", 1,
            ["ExpandCollapsePatternIdentifiers.Pattern"]);
        var chevron = new AutomationObservation(
            "gallery-chevron", gallery.RuntimeId, "", "Office Scripts", "ControlType.Button", "NetUISimpleButton",
            new RectI(794, 112, 30, 78), true, false, "Win32", 1,
            ["InvokePatternIdentifiers.Pattern"]);

        var region = Assert.Single(VisualFallbackPolicy.FindOpaqueRegions([gallery, chevron], root));

        Assert.Equal(gallery.Bounds, region);
    }

    [Fact]
    public void ExcelTemplateListWithoutNativeItemsLeavesPaintedCardsOpaque()
    {
        var root = new RectI(0, 0, 1_920, 1_040);
        var templates = new AutomationObservation(
            "templates", "new", "", "Templates", "ControlType.List", "NetUIListView",
            new RectI(260, 126, 1_579, 190), true, false, "Win32", 1);

        var region = Assert.Single(VisualFallbackPolicy.FindOpaqueRegions([templates], root));

        Assert.Equal(templates.Bounds, region);
        Assert.True(OfflineRecordingEnricher.RequiresOpaqueGalleryRecovery([templates]));
    }

    [Fact]
    public void ExcelBackstageButtonsAreAlignedToTheirPaintedLabels()
    {
        var root = new RectI(0, 0, 1_000, 700);
        var slab = new AutomationObservation(
            "open", "root", "", "Open", "ControlType.Group", "NetUISlabContainer",
            new RectI(100, 80, 800, 500), true, false, "Win32", 1);
        var favorites = new AutomationObservation(
            "favorites", slab.RuntimeId, "", "Favorites", "ControlType.Button", "NetUIButton",
            new RectI(250, 200, 103, 38), true, false, "Win32", 1, ["Invoke"]);
        VisualTextObservation[] words =
        [
            new("Favorites", new RectI(269, 260, 65, 14), 0)
        ];

        var aligned = VisualSurfaceScanner.RealignOfficeBackstageControls(
            root, root.Width, root.Height, [slab, favorites], words);

        var button = Assert.Single(aligned, control => control.RuntimeId == favorites.RuntimeId);
        Assert.Equal(new RectI(250, 248, 103, 38), button.Bounds);
    }

    [Fact]
    public void OfflineRepairDetectsControlsWhoseTextBelongsToAnotherScreen()
    {
        var window = new RectI(0, 0, 1_000, 700);
        var stale = Enumerable.Range(0, 8).Select(index => new AutomationObservation(
            $"stale-{index}", "", "", $"Shift value {index}", "ControlType.Button", "TButton",
            new RectI(300 + index % 2 * 180, 120 + index / 2 * 70, 150, 32),
            true, false, "Win32", 1)).ToArray();
        var reportWords = Enumerable.Range(0, 10).Select(index => new VisualTextObservation(
            $"Report{index}", new RectI(320 + index % 2 * 180, 125 + index / 2 * 55, 90, 16), index)).ToArray();
        var matchingWords = stale.Select((control, index) => new VisualTextObservation(
            control.Name, new RectI(control.Bounds.X + 8, control.Bounds.Y + 7, 100, 16), index)).ToArray();

        Assert.True(OfflineRecordingEnricher.LooksStale(stale, reportWords, window, 1_000, 700));
        Assert.False(OfflineRecordingEnricher.LooksStale(stale, matchingWords, window, 1_000, 700));
    }

    [Fact]
    public void OfflineRepairDetectsLegacyPageTitleMismatchWithoutBodyLeaves()
    {
        var window = new RectI(0, 0, 1_000, 700);
        AutomationObservation[] stale =
        [
            new("page-title", "root", "", "Tables Plan", "ControlType.Pane", "TAbacrePanel",
                new RectI(0, 110, 1_000, 52), true, false, "Win32", 1),
            new("chair", "root", "", "1", "ControlType.Pane", "TAbacrePanel",
                new RectI(120, 260, 90, 90), true, false, "Win32", 1)
        ];
        var sharedWords = Enumerable.Range(0, 8)
            .Select(index => new VisualTextObservation($"Toolbar{index}",
                new RectI(20 + index * 90, 50, 70, 14), 0))
            .ToArray();
        var clientsWords = sharedWords.Append(
            new VisualTextObservation("Clients", new RectI(470, 126, 60, 16), 1)).ToArray();
        var matchingWords = sharedWords.Concat(
        [
            new VisualTextObservation("Tables", new RectI(450, 126, 48, 16), 1),
            new VisualTextObservation("Plan", new RectI(502, 126, 34, 16), 1)
        ]).ToArray();

        Assert.True(OfflineRecordingEnricher.LooksStale(stale, clientsWords, window, 1_000, 700));
        Assert.False(OfflineRecordingEnricher.LooksStale(stale, matchingWords, window, 1_000, 700));
    }

    [Fact]
    public void DenseGridProbesInteractiveControlsWithoutRecheckingEveryCell()
    {
        var candidates = Enumerable.Range(0, 320)
            .Select(index => Visual($"cell-{index}", "ControlType.DataItem",
                new RectI(20 + index % 20 * 30, 100 + index / 20 * 24, 28, 22)))
            .Append(Visual("header", "ControlType.HeaderItem", new RectI(120, 40, 100, 26)))
            .Append(Visual("button", "ControlType.Button", new RectI(20, 10, 90, 28)))
            .ToArray();
        var opaquePane = new AutomationObservation(
            "pane", "", "", "Opaque provider", "ControlType.Pane", "TAbacreDBGrid",
            new RectI(0, 0, 900, 700), true, false, "Win32", 1);

        var points = VisualNativeVerification.Plan(candidates, [opaquePane]);

        Assert.Equal(2, points.Count);
        Assert.Equal(new RectI(65, 24, 1, 1), points[0]);
        Assert.Equal(new RectI(170, 53, 1, 1), points[1]);
    }

    [Fact]
    public void DenseCandidateSetCanBeProcessedBeyondTheFirstProbeBatch()
    {
        var candidates = Enumerable.Range(0, 240)
            .Select(index => Visual($"button-{index}", "ControlType.Button",
                new RectI(10 + index % 24 * 34, 10 + index / 24 * 30, 30, 24)))
            .ToArray();

        var points = VisualNativeVerification.PlanAll(candidates, []);

        Assert.Equal(candidates.Length, points.Count);
        Assert.True(points.Count > VisualNativeVerification.MaximumProbePoints);
        Assert.Equal(3, points.Chunk(VisualNativeVerification.MaximumProbePoints).Count());
    }

    [Fact]
    public void AlreadyConfirmedNativeCandidateIsNotProbedAgain()
    {
        var first = Visual("save", "ControlType.Button", new RectI(20, 10, 90, 28));
        var second = Visual("cancel", "ControlType.Button", new RectI(120, 10, 90, 28));
        var native = new AutomationObservation(
            "native-save", "", "save", "Save", "ControlType.Button", "TButton",
            first.Bounds, true, false, "Win32", 1, ["Invoke"]);

        var point = Assert.Single(VisualNativeVerification.Plan([first, second], [native]));

        Assert.Equal(new RectI(165, 24, 1, 1), point);
    }

    [Fact]
    public void NativeLeafReplacesVisualCandidateButContainerDoesNot()
    {
        var candidate = Visual("room", "ControlType.Button", new RectI(20, 10, 90, 28));
        var pane = new AutomationObservation(
            "pane", "", "", "Surface", "ControlType.Pane", "TAbacreDBGrid",
            new RectI(0, 0, 900, 700), true, false, "Win32", 1);
        var button = new AutomationObservation(
            "button", "pane", "room", "Room", "ControlType.Pane", "TAbacreButton",
            candidate.Bounds, true, false, "Win32", 1, ["Invoke"]);
        var text = new AutomationObservation(
            "text", "button", "", "Room", "ControlType.Text", "Static",
            candidate.Bounds, true, false, "Win32", 1);

        Assert.Single(VisualNativeVerification.RetainUnconfirmedVisuals([candidate], [pane, text]));
        Assert.Empty(VisualNativeVerification.RetainUnconfirmedVisuals([candidate], [pane, button]));
    }

    [Fact]
    public void NativeTreeKeepsOnlyUnconfirmedVisualStructures()
    {
        var table = Visual("table", "ControlType.Table", new RectI(10, 80, 700, 400)) with
        {
            FrameworkId = "UiAtlas.Visual.Geometry",
            VisualRole = "table",
            OcrText = null
        };
        var cell = Visual("cell", "ControlType.DataItem", new RectI(10, 110, 100, 24)) with
        {
            FrameworkId = "UiAtlas.Visual.Geometry",
            ParentRuntimeId = table.RuntimeId,
            VisualRole = "table-cell",
            OcrText = null
        };
        var falseButton = Visual("glyph", "ControlType.Button", new RectI(300, 200, 30, 30)) with
        {
            FrameworkId = "UiAtlas.Visual.Geometry",
            OcrText = null
        };

        var retained = VisualNativeVerification.RetainUnconfirmedStructures(
            [table, cell, falseButton], []);

        Assert.Equal([table, cell], retained);
    }

    private static AutomationObservation Visual(string id, string type, RectI bounds) =>
        new($"visual:{id}", "", $"visual:{id}", id, type, "UiAtlas.VisualControlRegion",
            bounds, false, true, "UiAtlas.Visual.Ocr", 1, VisualRole: "button", OcrText: id);
}

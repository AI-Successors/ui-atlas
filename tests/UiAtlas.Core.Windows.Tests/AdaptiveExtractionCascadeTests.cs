using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AdaptiveExtractionCascadeTests
{
    [Fact]
    public void PointCollectorContractAcceptsCascadeNodeLimit()
    {
        Assert.True(BoundedAutomationCollector.IsSupportedNodeLimit(64));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedAutomationCollector.CollectPointWindow(1, 0, 0, 65));
    }

    [Fact]
    public void MergerCombinesTheSameControlAndRetainsEveryEvidenceSource()
    {
        var raw = Source(ControlEvidenceSource.UiaRaw, Control("raw-1", "Save", "save", new(100, 40, 80, 30)));
        var control = Source(ControlEvidenceSource.UiaControl, Control("control-9", "Save", "save", new(100, 40, 80, 30)));
        var msaa = Source(ControlEvidenceSource.Msaa, Control("msaa-4", "Save", "save", new(101, 40, 79, 30)));

        var result = ControlEvidenceMerger.Merge([raw, control, msaa]);

        var candidate = Assert.Single(result);
        Assert.Equal(3, candidate.EvidenceIds.Count);
        Assert.Equal([ControlEvidenceSource.UiaRaw, ControlEvidenceSource.UiaControl, ControlEvidenceSource.Msaa], candidate.Sources);
        Assert.Equal(ExtractionCoverageStatus.Confirmed, candidate.CoverageStatus);
        Assert.False(candidate.HasConflict);
    }

    [Fact]
    public void MergerMarksConflictingNamesWithoutDiscardingEvidence()
    {
        var raw = Source(ControlEvidenceSource.UiaRaw, Control("raw", "Save", "command", new(10, 10, 80, 30)));
        var control = Source(ControlEvidenceSource.UiaControl, Control("control", "Delete", "command", new(10, 10, 80, 30)));

        var candidate = Assert.Single(ControlEvidenceMerger.Merge([raw, control]));

        Assert.True(candidate.HasConflict);
        Assert.Equal(2, candidate.EvidenceIds.Count);
        Assert.Equal(ExtractionCoverageStatus.Partial, candidate.CoverageStatus);
    }

    [Fact]
    public void GapDetectorFindsLargeEmptyContainerAndViewDisagreement()
    {
        var root = new RectI(0, 0, 1000, 800);
        var target = new WindowTarget(10, 10, 1, "app", DateTimeOffset.UnixEpoch, "App", "Root", root);
        var surface = AdaptiveExtractionCascade.SurfaceId(target, root);
        var pane = Control("pane", "Canvas", "", new(0, 100, 1000, 700), "ControlType.Pane");
        var raw = Source(ControlEvidenceSource.UiaRaw, pane, surface);
        var control = new ExtractionSourceResult(ControlEvidenceSource.UiaControl, surface, [], "ok", 4);

        var gaps = CoverageGapDetector.Detect([target], [raw, control], root);

        Assert.Contains(gaps, gap => gap.Kind == CoverageGapKind.LargeContainer);
        Assert.Contains(gaps, gap => gap.Kind == CoverageGapKind.ViewDivergence);
    }

    [Fact]
    public void SchedulerIsDeterministicAndHonorsThresholdAndBudget()
    {
        var gaps = new[]
        {
            new CoverageGapObservation("b", "s", CoverageGapKind.Timeout, new(0, 0, 1, 1), 1, "msaa"),
            new CoverageGapObservation("a", "s", CoverageGapKind.LargeContainer, new(0, 0, 1, 1), .9, "from-point"),
            new CoverageGapObservation("low", "s", CoverageGapKind.EmptyBounds, new(0, 0, 1, 1), .4, "from-point")
        };

        var first = AdaptiveProbeScheduler.Select(gaps, 1_200, 3);
        var second = AdaptiveProbeScheduler.Select(gaps.Reverse(), 1_200, 3);

        Assert.Equal(first, second);
        Assert.Equal(2, first.Count);
        Assert.DoesNotContain(first, probe => probe.GapId == "low");
        Assert.True(first.Sum(probe => probe.EstimatedCostMs) <= 1_200);
    }

    [Fact]
    public void SparseTopNavigationCreatesThreeLocalCommandBandProbes()
    {
        var bounds = new RectI(100, 50, 1500, 900);
        var controls = Enumerable.Range(0, 7)
            .Select(index => Control($"tab-{index}", $"Tab {index}", $"tab-{index}",
                new RectI(120 + index * 90, 78, 80, 26)))
            .ToArray();

        var bands = CoverageGapDetector.InferSparseCommandBand(bounds, controls);

        Assert.Equal(3, bands.Count);
        Assert.Equal(bounds.X, bands[0].X);
        Assert.Equal(bounds.Width, bands.Sum(band => band.Width));
        Assert.All(bands, band => Assert.True(band.Height >= 150));
    }

    [Fact]
    public void DenseSurfaceDoesNotScheduleRedundantCommandBandProbes()
    {
        var bounds = new RectI(0, 0, 1200, 800);
        var controls = Enumerable.Range(0, 30)
            .Select(index => Control($"control-{index}", $"Control {index}", $"control-{index}",
                new RectI(10 + index * 10, 20 + index * 12, 60, 24)))
            .ToArray();

        Assert.Empty(CoverageGapDetector.InferSparseCommandBand(bounds, controls));
    }

    [Fact]
    public void CachedMissingControlCreatesLocalProbeButObservedMatchDoesNot()
    {
        var bounds = new RectI(0, 0, 1200, 800);
        var cached = Control("cache:save", "Save", "save", new RectI(900, 30, 80, 30));
        var missing = CoverageGapDetector.FromCachedHints("surface", [], [cached], bounds);
        var observed = CoverageGapDetector.FromCachedHints(
            "surface", [Control("live:save", "Save", "save", new RectI(902, 31, 80, 30))], [cached], bounds);

        Assert.Single(missing);
        Assert.Equal("subtree-point", missing[0].NextProbe);
        Assert.Empty(observed);
    }

    [Fact]
    public void NativeUiaAdditionsIdentifyManagedClientBridgeRegression()
    {
        var managed = new[] { Control("panel", "Modify", "panel", new(0, 30, 500, 120), "ControlType.Custom") };
        var native = Enumerable.Range(0, 5)
            .Select(index => Control($"button-{index}", $"Command {index}", $"command-{index}",
                new RectI(20 + index * 40, 50, 32, 32)))
            .ToArray();

        Assert.Equal(ProviderCompatibilityStatus.ClientBridgeRegression,
            ProviderCompatibilityClassifier.Classify(managed, native, false, true));
    }

    [Fact]
    public void EmptyNativeResultIdentifiesOpaqueProviderInsteadOfRetryingTree()
    {
        var managed = new[] { Control("panel", "Modify", "panel", new(0, 30, 500, 120), "ControlType.Custom") };

        Assert.Equal(ProviderCompatibilityStatus.ProviderOpaque,
            ProviderCompatibilityClassifier.Classify(managed, [], false, true));
        Assert.Equal(ProviderCompatibilityStatus.TimedOut,
            ProviderCompatibilityClassifier.Classify(managed, [], true, true));
    }

    [Fact]
    public void OpaqueSurfaceProbeIsBoundedAndPrioritizesTopCommandBand()
    {
        var root = new RectI(0, 0, 1200, 800);
        var gaps = new[]
        {
            new CoverageGapObservation("body", "s", CoverageGapKind.LargeContainer, new(0, 300, 1200, 400), .8, "from-point"),
            new CoverageGapObservation("ribbon", "s", CoverageGapKind.LargeContainer, new(0, 30, 900, 130), .9, "from-point")
        };

        var regions = OpaqueSurfaceScanner.SelectRegions(gaps, root);
        var points = OpaqueSurfaceScanner.ProbePoints(regions);

        Assert.Equal(new RectI(0, 30, 900, 130), regions[0]);
        Assert.InRange(points.Count, 1, 42);
        Assert.All(points, point => Assert.True(point.X >= root.X && point.X < root.X + root.Width));
    }

    [Fact]
    public void OpaqueSurfaceProbeKeepsLargeDatabaseGridInsteadOfThreeSmallGaps()
    {
        var root = new RectI(0, 0, 1920, 1020);
        var grid = new RectI(2, 236, 1916, 767);
        var gaps = new[]
        {
            new CoverageGapObservation("tiny-1", "s", CoverageGapKind.ViewDivergence, new(1778, 0, 70, 31), .7, "from-point"),
            new CoverageGapObservation("tiny-2", "s", CoverageGapKind.ViewDivergence, new(245, 247, 244, 32), .7, "from-point"),
            new CoverageGapObservation("tiny-3", "s", CoverageGapKind.ViewDivergence, new(1889, 239, 31, 198), .7, "from-point"),
            new CoverageGapObservation("grid", "s", CoverageGapKind.ViewDivergence, grid, .9, "from-point")
        };

        var regions = OpaqueSurfaceScanner.SelectRegions(gaps, root);

        Assert.Contains(grid, regions);
    }

    [Fact]
    public void EmptyProviderStillGetsCommandBandAndBodyProbeRegions()
    {
        var root = new RectI(100, 50, 1200, 800);

        var regions = OpaqueSurfaceScanner.SelectRegions([], root);
        var points = OpaqueSurfaceScanner.ProbePoints(regions);

        Assert.Equal(2, regions.Count);
        Assert.Equal(new RectI(100, 50, 1200, 200), regions[0]);
        Assert.Contains(points, point => point.Y < 250);
        Assert.Contains(points, point => point.Y >= 250);
        Assert.InRange(points.Count, 1, 42);
    }

    [Fact]
    public void ProbeBudgetIsSharedAcrossOpaqueRegions()
    {
        RectI[] regions = [new(0, 0, 1200, 180), new(0, 180, 1200, 620)];

        var points = OpaqueSurfaceScanner.ProbePoints(regions);

        Assert.Contains(points, point => point.Y < 180);
        Assert.Contains(points, point => point.Y >= 180);
        Assert.Equal(42, points.Count);
    }

    [Fact]
    public void HoverDifferenceReturnsOnlyTheChangedLocalControl()
    {
        const int width = 120;
        const int height = 80;
        var before = new byte[width * height * 4];
        var after = new byte[before.Length];
        for (var y = 28; y < 52; y++)
        for (var x = 42; x < 74; x++)
        {
            var offset = (y * width + x) * 4;
            after[offset] = 80;
            after[offset + 1] = 120;
            after[offset + 2] = 220;
            after[offset + 3] = 255;
        }

        var detected = OpaqueSurfaceScanner.DetectHoverBounds(
            new(width, height, before), new(width, height, after),
            new RectI(100, 200, width, height), new RectI(160, 240, 1, 1));

        Assert.Equal(new RectI(142, 228, 32, 24), detected);
    }

    [Theory]
    [InlineData(100, 30, false)]
    [InlineData(100, 56, true)]
    [InlineData(180, 40, true)]
    public void OnlyLargeHoverChangesMaterializeAsSeparateStates(int width, int height, bool expected)
    {
        Assert.Equal(expected, OpaqueSurfaceScanner.IsMaterializedHoverState(new RectI(10, 10, width, height)));
    }

    [Fact]
    public void VisualFallbackFindsAButtonWhenAccessibilityReturnsNothing()
    {
        const int width = 180;
        const int height = 100;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        DrawBorder(pixels, width, new RectI(30, 24, 90, 34), 25);
        var target = new WindowTarget(
            44, 22, 7, "opaque", DateTimeOffset.UnixEpoch, "Opaque", "Canvas",
            new RectI(400, 300, width, height));

        var controls = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            []);

        Assert.Contains(controls, control =>
            control.ClassName == "UiAtlas.VisualControlRegion" &&
            control.WindowHwnd == target.Hwnd &&
            Math.Abs(control.Bounds.X - 430) <= 2 &&
            Math.Abs(control.Bounds.Y - 324) <= 2 &&
            Math.Abs(control.Bounds.Width - 90) <= 2 &&
            Math.Abs(control.Bounds.Height - 34) <= 2);
    }

    [Fact]
    public void VisualFallbackDoesNotInventControlsOnAFlatSurface()
    {
        const int width = 180;
        const int height = 100;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        var target = new WindowTarget(
            44, 22, 7, "opaque", DateTimeOffset.UnixEpoch, "Opaque", "Canvas",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            []);

        Assert.Empty(controls);
    }

    [Fact]
    public void VisualFallbackFillsPartialTreeWithoutDuplicatingKnownButton()
    {
        const int width = 180;
        const int height = 100;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var buttonBounds = new RectI(430, 324, 90, 34);
        DrawBorder(pixels, width, new RectI(30, 24, 90, 34), 25);
        var target = new WindowTarget(
            44, 22, 7, "revit", DateTimeOffset.UnixEpoch, "Revit", "HwndWrapper",
            new RectI(400, 300, width, height));
        var container = new AutomationObservation(
            "root", "", "", "Ribbon", "ControlType.Pane", "HwndWrapper", target.Bounds,
            true, false, "WPF", target.Hwnd);

        var discovered = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [container]);
        Assert.Contains(discovered, control => IntersectionOverUnion(control.Bounds, buttonBounds) >= .72);

        var knownButton = new AutomationObservation(
            "known", "root", "save", "Save", "ControlType.Button", "Button", buttonBounds,
            true, false, "WPF", target.Hwnd, ["Invoke"]);
        var deduplicated = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [container, knownButton]);
        Assert.DoesNotContain(deduplicated, control => IntersectionOverUnion(control.Bounds, buttonBounds) >= .72);
    }

    [Fact]
    public void VisualFallbackDoesNotTurnTextCrossingANativeMenuItemIntoAButton()
    {
        const int width = 180;
        const int height = 100;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var fragment = new RectI(30, 34, 30, 30);
        DrawBorder(pixels, width, fragment, 25);
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Legacy", "TfrmMain",
            new RectI(0, 0, width, height));
        var menuItem = new AutomationObservation(
            "menu", "root", "", "Hotel", "ControlType.MenuItem", "",
            new RectI(20, 20, 70, 24), true, false, "Win32", target.Hwnd);

        var controls = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [menuItem]);

        Assert.DoesNotContain(controls, control => IntersectionOverUnion(control.Bounds, fragment) >= .72);
    }

    [Fact]
    public void VisualFallbackSplitsOpaqueOfficeGalleryIntoNamedButtons()
    {
        const int width = 900;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var target = new WindowTarget(
            44, 22, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Book1 - Excel", "XLMAIN",
            new RectI(0, 0, width, height));
        var gallery = new AutomationObservation(
            "gallery", "ribbon", "OfficeScriptsGallery", "", "ControlType.MenuItem", "NetUIAnchor",
            new RectI(100, 80, 660, 80), true, false, "Win32", target.Hwnd,
            ["ExpandCollapsePatternIdentifiers.Pattern"]);
        var chevron = new AutomationObservation(
            "gallery-chevron", gallery.RuntimeId, "", "Office Scripts", "ControlType.Button", "NetUISimpleButton",
            new RectI(726, 80, 30, 80), true, false, "Win32", target.Hwnd,
            ["InvokePatternIdentifiers.Pattern"]);
        VisualTextObservation[] words =
        [
            new("Unhide All Rows and Columns", new RectI(134, 92, 174, 14), 0),
            new("Freeze Selection", new RectI(350, 92, 112, 14), 0),
            new("Make a Subtable from Selection", new RectI(548, 92, 168, 14), 0),
            new("Remove Hyperlinks", new RectI(134, 128, 128, 14), 1),
            new("Count Empty Rows", new RectI(350, 128, 126, 14), 1),
            new("Return Table Data as JSON", new RectI(548, 128, 164, 14), 1)
        ];

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [gallery.Bounds],
            [gallery, chevron],
            words);

        var buttons = controls.Where(control => control.ParentRuntimeId == gallery.RuntimeId).ToArray();
        Assert.Equal(6, buttons.Length);
        Assert.All(buttons, button => Assert.Equal("ControlType.Button", button.ControlType));
        Assert.Contains(buttons, button => button.Name == "Freeze Selection");
        Assert.Contains(buttons, button => button.Name == "Remove Hyperlinks");
        Assert.Contains(buttons, button => button.Name == "Return Table Data as JSON");
    }

    [Fact]
    public void VisualFallbackSplitsExcelTemplateGalleryIntoWholeCards()
    {
        const int width = 1_100;
        const int height = 420;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var target = new WindowTarget(
            44, 22, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Book1 - Excel", "XLMAIN",
            new RectI(0, 0, width, height));
        var gallery = new AutomationObservation(
            "templates", "new", "", "Templates", "ControlType.List", "NetUIListView",
            new RectI(100, 80, 900, 180), true, false, "Win32", target.Hwnd);
        var cardBounds = new[]
        {
            new RectI(120, 135, 150, 100),
            new RectI(340, 135, 150, 100),
            new RectI(560, 135, 150, 100),
            new RectI(780, 135, 150, 100)
        };
        foreach (var card in cardBounds) DrawBorder(pixels, width, card, 90);
        VisualTextObservation[] words =
        [
            new("Blank workbook", new RectI(140, 274, 110, 14), 4),
            new("Welcome to Excel", new RectI(355, 274, 120, 14), 4),
            new("Formula tutorial", new RectI(575, 274, 116, 14), 4),
            new("PivotTable tutorial", new RectI(790, 274, 130, 14), 4)
        ];

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [gallery.Bounds],
            [gallery],
            words);

        var buttons = controls.Where(control => control.ParentRuntimeId == gallery.RuntimeId).ToArray();
        Assert.Equal(4, buttons.Length);
        Assert.Contains(buttons, button => button.Name == "Blank workbook");
        Assert.Contains(buttons, button => button.Name == "PivotTable tutorial");
        Assert.All(buttons, button =>
        {
            Assert.True(button.Bounds.Y <= 135);
            Assert.True(button.Bounds.Y + button.Bounds.Height >= 288);
        });
    }

    [Fact]
    public void VisualFallbackTreatsEachExcelTableStylePreviewAsOneButton()
    {
        const int width = 500;
        const int height = 330;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var target = new WindowTarget(
            44, 22, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Book1 - Excel", "Net UI Tool Window",
            new RectI(0, 0, width, height));
        int[] columns = [20, 130, 240, 350];
        int[] rows = [42, 132, 222];
        foreach (var top in rows)
        foreach (var left in columns)
        {
            var card = new RectI(left, top, 90, 56);
            FillRect(pixels, width, card, 230, 240, 250);
            DrawBorder(pixels, width, card, 45);
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 4; column++)
                DrawBorder(pixels, width,
                    new RectI(left + column * 22, top + row * 18, 23, 19), 80);
        }
        VisualTextObservation[] words =
        [
            new("Light", new RectI(20, 10, 42, 14), 0),
            new("Medium", new RectI(20, 100, 58, 14), 1),
            new("Dark", new RectI(20, 190, 36, 14), 2)
        ];

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        var buttons = controls.Where(control =>
            control.ControlType == "ControlType.Button" &&
            control.Name.Contains("table style", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(12, buttons.Length);
        Assert.Contains(buttons, button => button.Name == "Light table style 1");
        Assert.Contains(buttons, button => button.Name == "Medium table style 4");
        Assert.Contains(buttons, button => button.Name == "Dark table style 4");
        Assert.All(buttons, button => Assert.Contains("Invoke", button.SupportedPatterns ?? []));
        Assert.DoesNotContain(controls, control =>
            control.ControlType is "ControlType.Table" or "ControlType.DataItem" or "ControlType.List");
    }

    [Fact]
    public async Task VisualFallbackTreatsTextOnlyExcelHeadingStylesAsSeparateButtons()
    {
        const int width = 820;
        const int height = 260;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var target = new WindowTarget(
            44, 22, 7, "recorded-process", DateTimeOffset.UnixEpoch, "", "Net UI Tool Window",
            new RectI(0, 0, width, height));
        VisualTextObservation[] words =
        [
            new("Titles", new RectI(18, 20, 42, 16), 0),
            new("and", new RectI(66, 20, 25, 16), 0),
            new("Headings", new RectI(97, 20, 68, 16), 0),
            new("Heading 1", new RectI(10, 58, 98, 22), 1),
            new("Heading 2", new RectI(144, 58, 94, 22), 1),
            new("Heading 3", new RectI(275, 58, 91, 22), 1),
            new("Heading 4", new RectI(408, 60, 82, 18), 1),
            new("Title", new RectI(543, 54, 50, 28), 1),
            new("Total", new RectI(676, 58, 49, 22), 1),
            new("Themed", new RectI(18, 112, 57, 16), 2),
            new("Cell", new RectI(81, 112, 28, 16), 2),
            new("Styles", new RectI(115, 112, 42, 16), 2)
        ];

        var controls = await VisualSurfaceScanner.DiscoverWithWordsAsync(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words,
            CancellationToken.None);

        var buttons = controls.Where(control => control.VisualRole == "cell-style-button").ToArray();
        Assert.Equal(6, buttons.Length);
        Assert.Equal(
            ["Heading 1", "Heading 2", "Heading 3", "Heading 4", "Title", "Total"],
            buttons.OrderBy(button => button.Bounds.X).Select(button => button.Name));
        Assert.All(buttons, button =>
        {
            Assert.Equal("ControlType.Button", button.ControlType);
            Assert.Contains("Invoke", button.SupportedPatterns ?? []);
            Assert.True(button.Bounds.Width >= 80);
            Assert.True(button.Bounds.Height >= 18);
        });
    }

    [Fact]
    public void VisualFallbackDoesNotTreatOrdinaryDocumentHeadingsAsCellStyleButtons()
    {
        const int width = 820;
        const int height = 260;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var target = new WindowTarget(
            44, 22, 7, "recorded-process", DateTimeOffset.UnixEpoch, "", "Net UI Tool Window",
            new RectI(0, 0, width, height));
        VisualTextObservation[] words =
        [
            new("Heading 1", new RectI(10, 58, 98, 22), 1),
            new("Heading 2", new RectI(144, 58, 94, 22), 1),
            new("Heading 3", new RectI(275, 58, 91, 22), 1),
            new("Heading 4", new RectI(408, 60, 82, 18), 1),
            new("Title", new RectI(543, 54, 50, 28), 1),
            new("Total", new RectI(676, 58, 49, 22), 1)
        ];

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        Assert.DoesNotContain(controls, control => control.VisualRole == "cell-style-button");
    }

    [Fact]
    public void VisualFallbackSnapsCachedExcelWorksheetCellsToPaintedGridLines()
    {
        const int width = 500;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        int[] verticalEdges = [0, 32, 112, 192, 272, 352, 432, 480];
        int[] horizontalEdges = [40, 64, 88, 112, 136, 160, 184, 208, 232, 260];
        foreach (var x in verticalEdges.Where(x => x > 0 && x < width))
            FillRect(pixels, width, new RectI(x, 40, 2, 220), 205, 205, 205);
        foreach (var y in horizontalEdges.Where(y => y > 0 && y < height))
            FillRect(pixels, width, new RectI(0, y, 480, 2), 205, 205, 205);

        var target = new WindowTarget(
            44, 22, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Book1 - Excel", "XLMAIN",
            new RectI(0, 0, width, height));
        var staleGrid = new AutomationObservation(
            "cache:grid", "", "", "Grid", "ControlType.DataGrid", "XLSpreadsheetGrid",
            new RectI(0, 40, 480, 220), false, true, "", target.Hwnd);
        var cachedCell = new AutomationObservation(
            "cache:cell", "", "", "DataItem", "ControlType.DataItem", "XLSpreadsheetCell",
            new RectI(32, 64, 76, 23), false, true, "UiAtlas.Cached", target.Hwnd);

        Assert.True(OfflineRecordingEnricher.RequiresCachedExcelGridRecovery([staleGrid, cachedCell]));

        var controls = VisualSurfaceScanner.DiscoverLegacySurfaceControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [staleGrid, cachedCell]);

        var table = Assert.Single(controls, control => control.VisualRole == "table");
        Assert.Equal("Worksheet grid", table.Name);
        var headerB = Assert.Single(controls, control =>
            control.VisualRole == "spreadsheet-column-header" && control.Name == "B");
        Assert.Equal(new RectI(112, 40, 80, 24), headerB.Bounds);
        var cellA1 = Assert.Single(controls, control =>
            control.VisualRole == "spreadsheet-cell" && control.Name == "A1");
        Assert.Equal(new RectI(32, 64, 80, 24), cellA1.Bounds);
    }

    [Fact]
    public void HealthyVisibleExcelWorksheetDoesNotNeedPixelGridRepair()
    {
        var visibleBounds = new RectI(0, 0, 1000, 700);
        var controls = new List<AutomationObservation>();
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 5; column++)
        {
            controls.Add(new AutomationObservation(
                $"cell-{row}-{column}", "grid", $"{column}:{row}", $"Cell {column},{row}",
                "ControlType.DataItem", "XLSpreadsheetCell",
                new RectI(40 + column * 80, 120 + row * 24, 80, 24),
                true, false, "Win32", 22));
        }
        controls.Add(new AutomationObservation(
            "cached-hidden", "grid", "Z99", "Z99", "ControlType.DataItem", "XLSpreadsheetCell",
            new RectI(900, 680, 80, 24), false, true, "UiAtlas.Cached", 22));
        controls.Add(new AutomationObservation(
            "grid", "", "Grid", "Grid", "ControlType.DataGrid", "XLSpreadsheetGrid",
            new RectI(0, 100, 1_000, 580), true, false, "Win32", 22));

        Assert.True(VisualSurfaceScanner.HasReliableVisibleExcelWorksheet(controls, visibleBounds));
        Assert.False(OfflineRecordingEnricher.RequiresCachedExcelGridRecovery(controls));
        Assert.False(VisualSurfaceScanner.HasReliableVisibleExcelWorksheet(
            controls.Where(control => control.IsOffscreen).ToArray(), visibleBounds));
    }

    [Fact]
    public void LegacyRecoveryFindsPaintedWorksheetCellsWithoutNativeGridEvidence()
    {
        const int width = 420;
        const int height = 250;
        var pixels = Enumerable.Repeat((byte)245, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < 7; row++)
        for (var column = 0; column < 5; column++)
            DrawBorder(pixels, width,
                new RectI(20 + column * 72, 36 + row * 24, 73, 25), 190);
        var target = new WindowTarget(
            44, 22, 7, "EXCEL", DateTimeOffset.UnixEpoch, "Book1 - Excel", "XLMAIN",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverLegacySurfaceControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            []);

        var table = Assert.Single(controls, control => control.ControlType == "ControlType.Table");
        var cells = controls.Where(control =>
            control.ParentRuntimeId == table.RuntimeId &&
            control.ControlType == "ControlType.DataItem").ToArray();
        Assert.True(cells.Length >= 20);
    }

    [Fact]
    public void VisualFallbackRejectsNarrowUnlabelledArtworkFragment()
    {
        const int width = 180;
        const int height = 100;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var fragment = new RectI(80, 24, 25, 40);
        DrawBorder(pixels, width, fragment, 25);
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Legacy", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            []);

        Assert.DoesNotContain(controls, control => IntersectionOverUnion(control.Bounds, fragment) >= .72);
    }

    [Fact]
    public void VisualFallbackDoesNotDuplicateLegacyPaneButtonWithButtonClass()
    {
        const int width = 260;
        const int height = 120;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var button = new RectI(30, 24, 160, 56);
        var textFragment = new RectI(82, 42, 54, 22);
        DrawBorder(pixels, width, button, 25);
        DrawBorder(pixels, width, textFragment, 25);
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Reservations", "TfrmMain",
            new RectI(0, 0, width, height));
        var nativeButton = new AutomationObservation(
            "native", "", "", "Rooms Calendar", "ControlType.Pane", "TAbacreButton",
            button, true, false, "Win32", target.Hwnd);

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [nativeButton],
            [new VisualTextObservation("Rooms", new RectI(84, 45, 50, 16), 0)]);

        Assert.Empty(controls);
    }

    [Fact]
    public void VisualFallbackKeepsOuterMultilineButtonAndRejectsTightTextFrame()
    {
        const int width = 260;
        const int height = 130;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var button = new RectI(30, 20, 180, 70);
        var tightTextFrame = new RectI(76, 54, 70, 24);
        FillRect(pixels, width, button, 244, 224, 224);
        DrawBorder(pixels, width, button, 25);
        DrawBorder(pixels, width, tightTextFrame, 25);
        VisualTextObservation[] words =
        [
            new("Previous", new RectI(70, 34, 72, 16), 0),
            new("Month", new RectI(80, 58, 62, 16), 1)
        ];
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Calendar", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        var detected = Assert.Single(controls, control => control.ControlType == "ControlType.Button");
        Assert.Equal("Previous Month", detected.Name);
        Assert.True(IntersectionOverUnion(detected.Bounds, button) >= .72);
        Assert.DoesNotContain(controls, control => IntersectionOverUnion(control.Bounds, tightTextFrame) >= .72);
    }

    [Fact]
    public void VisualFallbackDoesNotEmitGenericControlInsideDetectedTable()
    {
        const int width = 320;
        const int height = 220;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 3; column++)
            DrawBorder(pixels, width, new RectI(20 + column * 70, 30 + row * 32, 71, 33), 30);
        var nested = new RectI(35, 65, 30, 62);
        DrawBorder(pixels, width, nested, 30);
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Calendar", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            [
                new VisualTextObservation("203", new RectI(38, 70, 24, 14), 2),
                new VisualTextObservation("204", new RectI(38, 103, 24, 14), 3)
            ]);

        Assert.Contains(controls, control => control.ControlType == "ControlType.Table");
        Assert.Contains(controls, control => control.ControlType == "ControlType.DataItem");
        Assert.DoesNotContain(controls, control =>
            (control.ControlType is "ControlType.Button" or "ControlType.Edit" or "ControlType.List") &&
            IntersectionOverUnion(control.Bounds, nested) >= .20);
    }

    [Fact]
    public async Task RootNativeCollectionRetriesAfterEveryTimeoutAndUsesBoundedRecovery()
    {
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Calendar", "TfrmMain",
            new RectI(100, 200, 1_000, 800));
        var recovered = Control("legacy-button", "Previous Month", "previous", new RectI(120, 240, 180, 60));
        var fullCalls = 0;
        var legacyCalls = 0;
        var bandCalls = 0;

        async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> Run()
            => await AdaptiveCaptureCoordinator.CollectRootNativeAutomationAsync(
                target,
                _ =>
                {
                    fullCalls++;
                    return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], true, "timeout"));
                },
                _ =>
                {
                    legacyCalls++;
                    return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([recovered], false, "ok"));
                },
                (band, _) =>
                {
                    bandCalls++;
                    Assert.Equal(new RectI(100, 200, 1_000, 280), band);
                    return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], false, "ok"));
                },
                CancellationToken.None);

        var first = await Run();
        var second = await Run();

        Assert.Equal(2, fullCalls);
        Assert.Equal(2, legacyCalls);
        Assert.Equal(2, bandCalls);
        Assert.True(first.TimedOut);
        Assert.Equal("partial", first.Status);
        Assert.Contains(first.Items, item => item.Name == "Previous Month");
        Assert.True(second.TimedOut);
        Assert.Equal("partial", second.Status);
        Assert.Contains(second.Items, item => item.Name == "Previous Month");
    }

    [Fact]
    public async Task RootNativeCollectionEnrichesUnnamedClassicMenuThroughRecovery()
    {
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Hotel", "TfrmMain",
            new RectI(0, 0, 1_200, 800));
        var unnamed = new AutomationObservation("uia-menu-1", "uia-bar", "Item 1", "",
            "ControlType.MenuItem", "", new RectI(0, 29, 61, 24), true, false, "Win32", 44);
        var named = unnamed with { RuntimeId = "msaa-menu-1", Name = "Action" };
        var legacyCalls = 0;
        var bandCalls = 0;

        var result = await AdaptiveCaptureCoordinator.CollectRootNativeAutomationAsync(
            target,
            _ => Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([unnamed], false, "ok")),
            _ =>
            {
                legacyCalls++;
                return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([named], false, "ok"));
            },
            (_, _) =>
            {
                bandCalls++;
                return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], false, "ok"));
            },
            CancellationToken.None);

        Assert.Equal(1, legacyCalls);
        Assert.Equal(1, bandCalls);
        Assert.False(result.TimedOut);
        Assert.Equal("ok", result.Status);
        Assert.Equal("Action", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task RootNativeNodeLimitContinuesThroughEveryRegionalBatch()
    {
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Shifts", "TfrmMain",
            new RectI(100, 200, 1_000, 800));
        var visited = new List<RectI>();

        var result = await AdaptiveCaptureCoordinator.CollectRootNativeAutomationAsync(
            target,
            _ => Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(
                ([Control("root", "Shifts", "root", target.Bounds)], false, "node-limit")),
            _ => Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], false, "ok")),
            (band, _) =>
            {
                visited.Add(band);
                var row = Control($"row-{band.Y}", $"Row {band.Y}", $"row-{band.Y}",
                    new RectI(band.X + 20, band.Y + 10, 200, 30));
                return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([row], false, "ok"));
            },
            CancellationToken.None);

        Assert.Equal(AdaptiveCaptureCoordinator.RootRecoveryBands(target.Bounds), visited);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal("partial", result.Status);
    }

    [Fact]
    public async Task RegionalNodeLimitIsSplitUntilTheSubregionsComplete()
    {
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Reports", "TfrmMain",
            new RectI(0, 0, 800, 400));
        var calls = 0;
        var visited = new List<RectI>();

        var result = await AdaptiveCaptureCoordinator.CollectRootNativeAutomationAsync(
            target,
            _ => Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(
                ([Control("root", "Reports", "root", target.Bounds)], false, "node-limit")),
            _ => Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([], false, "ok")),
            (band, _) =>
            {
                calls++;
                visited.Add(band);
                var status = band.Y == 0 && band.Height == 100 ? "node-limit" : "ok";
                var item = Control($"item-{band.Y}-{band.Height}", "Report option",
                    $"item-{band.Y}-{band.Height}", new RectI(10, band.Y + 2, 120, 20));
                return Task.FromResult<(IReadOnlyList<AutomationObservation>, bool, string)>(([item], false, status));
            },
            CancellationToken.None);

        Assert.Equal(6, calls);
        Assert.Contains(new RectI(0, 0, 800, 50), visited);
        Assert.Contains(new RectI(0, 50, 800, 50), visited);
        Assert.Contains(result.Items, item => item.RuntimeId == "item-50-50");
    }

    [Fact]
    public void VisualFallbackIdentityIgnoresCoordinatesAndUniformScale()
    {
        var firstPixels = Enumerable.Repeat((byte)235, 180 * 100 * 4).ToArray();
        var secondPixels = Enumerable.Repeat((byte)235, 360 * 200 * 4).ToArray();
        for (var index = 3; index < firstPixels.Length; index += 4) firstPixels[index] = 255;
        for (var index = 3; index < secondPixels.Length; index += 4) secondPixels[index] = 255;
        DrawBorder(firstPixels, 180, new RectI(30, 24, 90, 34), 25);
        DrawBorderWithThickness(secondPixels, 360, new RectI(120, 80, 180, 68), 25, 2);
        var firstTarget = new WindowTarget(
            44, 22, 7, "opaque", DateTimeOffset.UnixEpoch, "Opaque", "Canvas",
            new RectI(400, 300, 180, 100));
        var secondTarget = firstTarget with { Bounds = new RectI(900, 500, 180, 100) };

        var first = VisualSurfaceScanner.Discover(
            firstTarget,
            new OpaqueSurfaceScanner.PixelFrame(180, 100, firstPixels),
            [firstTarget.Bounds],
            []).Single(control => Math.Abs(control.Bounds.Width - 90) <= 2);
        var second = VisualSurfaceScanner.Discover(
            secondTarget,
            new OpaqueSurfaceScanner.PixelFrame(360, 200, secondPixels),
            [secondTarget.Bounds],
            []).Single(control => Math.Abs(control.Bounds.Width - 90) <= 2);

        Assert.NotEqual(first.Bounds.X - firstTarget.Bounds.X, second.Bounds.X - secondTarget.Bounds.X);
        Assert.Equal(first.AutomationId, second.AutomationId);
        Assert.StartsWith("visual:v3:", first.AutomationId, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualFallbackUsesOcrToClassifyButtonAndLabelField()
    {
        const int width = 360;
        const int height = 140;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        DrawBorder(pixels, width, new RectI(30, 24, 92, 34), 25);
        DrawBorder(pixels, width, new RectI(170, 82, 150, 28), 25);
        var target = new WindowTarget(
            44, 22, 7, "opaque", DateTimeOffset.UnixEpoch, "Opaque", "Canvas",
            new RectI(400, 300, width, height));
        var words = new VisualTextObservation[]
        {
            new("Save", new RectI(55, 32, 42, 16), 0),
            new("Name", new RectI(118, 87, 42, 16), 1)
        };

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        var button = Assert.Single(controls, control => control.OcrText == "Save");
        Assert.Equal("ControlType.Button", button.ControlType);
        Assert.Equal("button", button.VisualRole);
        var field = Assert.Single(controls, control => control.OcrText == "Name");
        Assert.Equal("ControlType.Edit", field.ControlType);
        Assert.Equal("field", field.VisualRole);
    }

    [Fact]
    public void VisualFallbackDoesNotReplaceButtonTextWithNearbyCalendarLabel()
    {
        const int width = 360;
        const int height = 140;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var buttonBounds = new RectI(170, 64, 100, 44);
        DrawBorder(pixels, width, buttonBounds, 25);
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Calendar", "TfrmMain",
            new RectI(0, 0, width, height));
        VisualTextObservation[] words =
        [
            new("2026", new RectI(120, 78, 36, 14), 0),
            new("Next", new RectI(184, 78, 34, 14), 1),
            new("Month", new RectI(222, 78, 38, 14), 1)
        ];

        var control = Assert.Single(VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words));

        Assert.Equal("ControlType.Button", control.ControlType);
        Assert.Equal("Next Month", control.Name);
        Assert.Equal("Next Month", control.OcrText);
    }

    [Theory]
    [InlineData("Mon fr", "Previous Mondi", "Previous Month")]
    [InlineData("Next Month", "Next Mon&l", "Next Month")]
    [InlineData("Tcday", "Today", "Today")]
    public void LocalizedButtonOcrPrefersCompleteCleanLabel(
        string current,
        string localized,
        string expected)
    {
        Assert.Equal(expected,
            VisualSurfaceScanner.SelectLocalizedLabel(current, localized, ["Month", "Calendar"]));
    }

    [Fact]
    public void VisualFallbackMergesGridCellsUnderOneTable()
    {
        const int width = 320;
        const int height = 180;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            DrawBorder(pixels, width, new RectI(30 + column * 70, 28 + row * 30, 71, 31), 30);
        var target = new WindowTarget(
            44, 22, 7, "opaque", DateTimeOffset.UnixEpoch, "Opaque", "Canvas",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            []);

        var table = Assert.Single(controls, control => control.ControlType == "ControlType.Table");
        var cells = controls.Where(control => control.ParentRuntimeId == table.RuntimeId).ToArray();
        Assert.True(cells.Length >= 4);
        Assert.All(cells, cell => Assert.Equal("ControlType.DataItem", cell.ControlType));
        Assert.All(cells, cell => Assert.Equal(table.VisualGroupId, cell.VisualGroupId));
        Assert.All(cells, cell => Assert.NotNull(cell.TableRow));
        Assert.All(cells, cell => Assert.NotNull(cell.TableColumn));
    }

    [Fact]
    public void VisualFallbackSplitsWideLegacyGridAndKeepsOnlyHeaderText()
    {
        const int columns = 12;
        const int rows = 20;
        const int cellWidth = 120;
        const int cellHeight = 24;
        const int left = 20;
        const int top = 16;
        const int width = 1_500;
        const int height = 520;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            DrawBorder(pixels, width,
                new RectI(left + column * cellWidth, top + row * cellHeight, cellWidth + 1, cellHeight + 1), 30);
        var target = new WindowTarget(
            44, 22, 7, "ahms", DateTimeOffset.UnixEpoch, "Orders", "TfrmMain",
            new RectI(0, 0, width, height));
        var words = Enumerable.Range(0, columns)
            .Select(column => new VisualTextObservation(
                "Header " + column,
                new RectI(left + column * cellWidth + 8, top + 4, 74, 14),
                0))
            .Append(new VisualTextObservation(
                "James Marcony",
                new RectI(left + 5 * cellWidth + 8, top + cellHeight + 4, 90, 14),
                1))
            .ToArray();

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        var table = Assert.Single(controls, control => control.ControlType == "ControlType.Table");
        var tableControls = controls.Where(control => control.ParentRuntimeId == table.RuntimeId).ToArray();
        Assert.Equal(columns, tableControls.Count(control => control.ControlType == "ControlType.HeaderItem"));
        Assert.True(tableControls.Count(control => control.ControlType == "ControlType.DataItem") >= 200);
        Assert.Contains(tableControls, control => control.Name == "Header 5");
        Assert.DoesNotContain(tableControls, control => control.Name.Contains("James", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeDatabaseGridIsMappedAsWholeCellsBeforeGlyphRectangles()
    {
        const int columns = 12;
        const int rows = 21;
        const int cellWidth = 120;
        const int cellHeight = 24;
        const int left = 20;
        const int top = 32;
        const int width = 1_500;
        const int height = 560;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var cell = new RectI(
                left + column * cellWidth,
                top + row * cellHeight,
                cellWidth + 1,
                cellHeight + 1);
            DrawBorder(pixels, width, cell, 30);
            // Reproduce the small, high-contrast text shapes which used to
            // exhaust the 300-candidate budget and become fake buttons.
            DrawBorder(pixels, width,
                new RectI(cell.X + 10, cell.Y + 6, 26, 12), 45);
        }
        var target = new WindowTarget(
            44, 22, 7, "ahms", DateTimeOffset.UnixEpoch, "Orders", "TfrmMain",
            new RectI(0, 0, width, height));
        var gridBounds = new RectI(left, top, columns * cellWidth + 1, rows * cellHeight + 1);
        var nativeGrid = new AutomationObservation(
            "native-grid", "root", "", "", "ControlType.Pane", "TAbacreDBGrid",
            gridBounds, true, false, "Win32", target.Hwnd);
        var words = Enumerable.Range(0, columns)
            .Select(column => new VisualTextObservation(
                "Header " + column,
                new RectI(left + column * cellWidth + 42, top + 5, 68, 14),
                0))
            .Append(new VisualTextObservation(
                "James Marcony",
                new RectI(left + 5 * cellWidth + 42, top + cellHeight + 5, 70, 14),
                1))
            .ToArray();

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [nativeGrid],
            words);

        var table = Assert.Single(controls, control => control.ControlType == "ControlType.Table");
        var tableControls = controls.Where(control => control.ParentRuntimeId == table.RuntimeId).ToArray();
        Assert.Equal(columns, tableControls.Count(control => control.ControlType == "ControlType.HeaderItem"));
        Assert.Equal((rows - 1) * columns,
            tableControls.Count(control => control.ControlType == "ControlType.DataItem"));
        Assert.DoesNotContain(controls, control =>
            control.ControlType == "ControlType.Button" &&
            gridBounds.X <= control.Bounds.X + control.Bounds.Width / 2 &&
            gridBounds.Y <= control.Bounds.Y + control.Bounds.Height / 2 &&
            gridBounds.X + gridBounds.Width > control.Bounds.X + control.Bounds.Width / 2 &&
            gridBounds.Y + gridBounds.Height > control.Bounds.Y + control.Bounds.Height / 2);
        Assert.DoesNotContain(tableControls,
            control => control.Name.Contains("James", StringComparison.OrdinalIgnoreCase));

        var repairedEvidenceControls = VisualSurfaceScanner.DiscoverDatabaseGridControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [nativeGrid]);
        var repairedTable = Assert.Single(repairedEvidenceControls,
            control => control.ControlType == "ControlType.Table");
        var repairedChildren = repairedEvidenceControls
            .Where(control => control.ParentRuntimeId == repairedTable.RuntimeId)
            .ToArray();
        Assert.Equal(columns,
            repairedChildren.Count(control => control.ControlType == "ControlType.HeaderItem"));
        Assert.Equal((rows - 1) * columns,
            repairedChildren.Count(control => control.ControlType == "ControlType.DataItem"));
    }

    [Fact]
    public void NativeDatabaseGridKeepsItsShortHeaderRow()
    {
        const int width = 700;
        const int height = 260;
        const int left = 20;
        const int top = 40;
        int[] rowEdges = [top, top + 22, top + 49, top + 76, top + 103, top + 130, top + 157];
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < rowEdges.Length - 1; row++)
        for (var column = 0; column < 3; column++)
            DrawBorder(pixels, width, new RectI(
                left + column * 200,
                rowEdges[row],
                201,
                rowEdges[row + 1] - rowEdges[row] + 1), 30);
        var target = new WindowTarget(
            44, 22, 7, "ahms", DateTimeOffset.UnixEpoch, "Orders", "TfrmMain",
            new RectI(0, 0, width, height));
        var nativeGrid = new AutomationObservation(
            "native-grid", "root", "", "", "ControlType.Pane", "TAbacreDBGrid",
            new RectI(left, top, 601, rowEdges[^1] - top + 1),
            true, false, "Win32", target.Hwnd);

        var controls = VisualSurfaceScanner.DiscoverDatabaseGridControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [nativeGrid]);

        var headers = controls.Where(control => control.ControlType == "ControlType.HeaderItem").ToArray();
        Assert.Equal(3, headers.Length);
        Assert.All(headers, header => Assert.InRange(header.Bounds.Y, top, top + 1));
        Assert.All(headers, header => Assert.Equal(23, header.Bounds.Height));
    }

    [Fact]
    public void HeaderAndAlignedTextRowsAreRecoveredAsSparseTable()
    {
        const int width = 640;
        const int height = 300;
        const int left = 20;
        const int top = 40;
        int[] columnWidths = [260, 100, 80];
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var x = left;
        foreach (var columnWidth in columnWidths)
        {
            DrawBorder(pixels, width, new RectI(x, top, columnWidth + 1, 23), 30);
            x += columnWidth;
        }

        var words = new List<VisualTextObservation>
        {
            new("Name", new RectI(left + 6, top + 4, 50, 14), 0),
            new("Price", new RectI(left + 270, top + 4, 44, 14), 0),
            new("Qty", new RectI(left + 372, top + 4, 30, 14), 0)
        };
        for (var row = 0; row < 4; row++)
        {
            var y = top + 27 + row * 20;
            // Real Windows OCR often numbers vertically aligned column text
            // as separate lines, even though the words share one visual row.
            words.Add(new($"Product {row}", new RectI(left + 6, y, 80, 14), row * 3 + 1));
            words.Add(new($"{row + 1}.50", new RectI(left + 270, y + 1, 38, 14), row * 3 + 2));
            words.Add(new("1", new RectI(left + 372, y + 1, 10, 14), row * 3 + 3));
        }
        var target = new WindowTarget(
            44, 22, 7, "ahms", DateTimeOffset.UnixEpoch, "Order", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        var table = Assert.Single(controls, control => control.ControlType == "ControlType.Table");
        var tableControls = controls.Where(control => control.ParentRuntimeId == table.RuntimeId).ToArray();
        Assert.Equal(3, tableControls.Count(control => control.ControlType == "ControlType.HeaderItem"));
        Assert.Equal(12, tableControls.Count(control => control.ControlType == "ControlType.DataItem"));
        Assert.DoesNotContain(controls, control =>
            control.ControlType == "ControlType.Button" &&
            control.Bounds.X >= table.Bounds.X && control.Bounds.Y >= table.Bounds.Y &&
            control.Bounds.X < table.Bounds.X + table.Bounds.Width &&
            control.Bounds.Y < table.Bounds.Y + table.Bounds.Height);
    }

    [Fact]
    public void OwnerDrawnViewFilterIsRecoveredAsOneComboBox()
    {
        const int width = 1_500;
        const int height = 440;
        const int left = 20;
        const int top = 100;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < 10; row++)
        for (var column = 0; column < 5; column++)
            DrawBorder(pixels, width,
                new RectI(left + column * 250, top + row * 24, 251, 25), 30);
        DrawBorder(pixels, width, new RectI(40, 60, 800, 28), 30);

        var target = new WindowTarget(
            44, 22, 7, "ahms", DateTimeOffset.UnixEpoch, "Orders", "TfrmMain",
            new RectI(0, 0, width, height));
        var nativeGrid = new AutomationObservation(
            "native-grid", "root", "", "", "ControlType.Pane", "TAbacreDBGrid",
            new RectI(left, top, 1_251, 241), true, false, "Win32", target.Hwnd);
        var nativeSearch = new AutomationObservation(
            "native-search", "root", "", "", "ControlType.ComboBox", "TRVComboBox",
            new RectI(1_100, 60, 200, 28), true, false, "Win32", target.Hwnd);

        var controls = VisualSurfaceScanner.DiscoverDatabaseGridControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [nativeGrid, nativeSearch]);

        var combo = Assert.Single(controls, control => control.ControlType == "ControlType.ComboBox");
        Assert.Equal("View filter", combo.Name);
        Assert.Equal(40, combo.Bounds.X);
        Assert.Equal(60, combo.Bounds.Y);
        Assert.True(combo.Bounds.Width >= 800);
        Assert.Contains("ExpandCollapse", combo.SupportedPatterns ?? []);
    }

    [Fact]
    public void ClassicReportTreeIsRecoveredAsWholeRowsWithExpandableParents()
    {
        const int width = 500;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var words = new List<VisualTextObservation>();
        for (var row = 0; row < 10; row++)
        {
            var parent = row is 0 or 5;
            var accentX = parent ? 20 : 44;
            var top = 40 + row * 20;
            FillRect(pixels, width, new RectI(accentX, top + 2, 10, 10), 192, 128, 128);
            words.Add(new(parent ? $"Group {row / 5 + 1}" : $"Report {row}",
                new RectI(accentX + 18, top + 1, 80, 14), row));
        }
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Reports", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        Assert.Single(controls, control => control.ControlType == "ControlType.Tree");
        var items = controls.Where(control => control.ControlType == "ControlType.TreeItem").ToArray();
        Assert.Equal(10, items.Length);
        Assert.All(items, item => Assert.True(item.Bounds.Width >= 90));
        Assert.Equal(2, items.Count(item => item.SupportedPatterns?.Contains("ExpandCollapse") == true));
    }

    [Fact]
    public void ClassicReportOptionsAreRecoveredAsRadioButtonsFromIndependentIndicators()
    {
        const int width = 600;
        const int height = 260;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        DrawBorder(pixels, width, new RectI(328, 72, 9, 9), 70);
        DrawBorder(pixels, width, new RectI(328, 102, 9, 9), 70);
        VisualTextObservation[] words =
        [
            new("All", new RectI(344, 69, 22, 14), 2),
            new("Dates", new RectI(370, 69, 38, 14), 2),
            new("Between", new RectI(344, 99, 58, 14), 3)
        ];
        var target = new WindowTarget(44, 22, 7, "legacy", DateTimeOffset.UnixEpoch,
            "Reports", "TfrmMain", new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels), [target.Bounds], [], words);

        Assert.Contains(controls, control => control.ControlType == "ControlType.RadioButton" && control.Name == "All Dates");
        Assert.Contains(controls, control => control.ControlType == "ControlType.RadioButton" && control.Name == "Between");
    }

    [Fact]
    public void VisualRadioCandidateInsideKnownRibbonCommandIsSuppressed()
    {
        const int width = 600;
        const int height = 180;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        DrawBorder(pixels, width, new RectI(328, 72, 9, 9), 70);
        VisualTextObservation[] words =
        [
            new("Format", new RectI(344, 69, 48, 14), 2)
        ];
        var target = new WindowTarget(44, 22, 7, "EXCEL", DateTimeOffset.UnixEpoch,
            "Book1 - Excel", "XLMAIN", new RectI(0, 0, width, height));
        var ribbonCommand = new AutomationObservation(
            "native:format", "FormatCellsMenu", "Format", "MenuItem", "ControlType.MenuItem",
            "NetUIAnchor", new RectI(310, 54, 100, 54), false, true, "Win32", target.Hwnd);

        var controls = VisualSurfaceScanner.DiscoverCore(target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels), [target.Bounds], [ribbonCommand], words);

        Assert.DoesNotContain(controls, control =>
            control.ControlType == "ControlType.RadioButton" && control.Name == "Format");
    }

    [Fact]
    public void LargeButtonRowsAreNotStarvedByHundredsOfTextSizedRectangles()
    {
        const int width = 1_000;
        const int height = 520;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        for (var row = 0; row < 16; row++)
        for (var column = 0; column < 20; column++)
            DrawBorder(pixels, width, new RectI(5 + column * 48, 10 + row * 20, 25, 17), 30);
        var expected = new List<RectI>();
        for (var column = 0; column < 8; column++)
        {
            var button = new RectI(20 + column * 120, 430, 90, 45);
            FillRect(pixels, width, button, 244, 224, 224);
            DrawBorder(pixels, width, button, 30);
            expected.Add(button);
        }
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.Discover(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            []);

        Assert.All(expected, button => Assert.Contains(controls,
            control => control.ControlType == "ControlType.Button" &&
                       IntersectionOverUnion(control.Bounds, button) >= .72));
    }

    [Fact]
    public void ClientDetailTabsAreMappedAsTabItemsInsteadOfLooseWords()
    {
        const int width = 1_000;
        const int height = 400;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        VisualTextObservation[] words =
        [
            new("General", new RectI(20, 250, 52, 14), 7),
            new("Additional", new RectI(92, 250, 68, 14), 7),
            new("Orders", new RectI(180, 250, 48, 14), 7),
            new("Files", new RectI(250, 250, 34, 14), 7),
            new("Loyalty", new RectI(306, 250, 46, 14), 7),
            new("Points", new RectI(357, 250, 42, 14), 7)
        ];
        DrawBorder(pixels, width, new RectI(10, 239, 405, 36), 30);
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        Assert.Single(controls, control => control.ControlType == "ControlType.Tab");
        var tabs = controls.Where(control => control.ControlType == "ControlType.TabItem").ToArray();
        Assert.Equal(5, tabs.Length);
        Assert.Contains(tabs, tab => tab.Name == "Loyalty Points");
        Assert.All(tabs, tab => Assert.Contains("SelectionItem", tab.SupportedPatterns ?? []));
    }

    [Fact]
    public void PopulatedClientFormFieldsUseTheirLabelsInsteadOfTheirValues()
    {
        const int width = 640;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var nameField = new RectI(100, 42, 170, 24);
        var jobField = new RectI(100, 82, 170, 24);
        var stateField = new RectI(100, 122, 170, 24);
        var noteField = new RectI(360, 42, 180, 170);
        foreach (var bounds in new[] { nameField, jobField, stateField, noteField })
            DrawBorder(pixels, width, bounds, 30);
        FillRect(pixels, width, new RectI(stateField.X + stateField.Width - 18, stateField.Y + 1, 1,
            stateField.Height - 2), 30, 30, 30);
        VisualTextObservation[] words =
        [
            new("Name:", new RectI(35, 47, 42, 14), 0),
            new("James", new RectI(102, 47, 42, 14), 0),
            new("Job", new RectI(18, 87, 23, 14), 1),
            new("Title:", new RectI(44, 87, 34, 14), 1),
            new("Manager", new RectI(102, 87, 58, 14), 1),
            new("State:", new RectI(30, 127, 48, 14), 2),
            new("Note:", new RectI(360, 20, 38, 14), 3)
        ];
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        var name = Assert.Single(controls, control => control.Name == "Name");
        Assert.Equal("ControlType.Edit", name.ControlType);
        Assert.Equal("James", name.OcrText);
        var job = Assert.Single(controls, control => control.Name == "Job Title");
        Assert.Equal("ControlType.Edit", job.ControlType);
        Assert.Equal("Manager", job.OcrText);
        var state = Assert.Single(controls, control => control.Name == "State");
        Assert.Equal("ControlType.ComboBox", state.ControlType);
        Assert.Contains("ExpandCollapse", state.SupportedPatterns ?? []);
        var note = Assert.Single(controls, control => control.Name == "Note");
        Assert.Equal("ControlType.Edit", note.ControlType);
        Assert.True(IntersectionOverUnion(note.Bounds, noteField) >= .72);
        Assert.DoesNotContain(controls, control => control.Name is "James" or "Manager");
    }

    [Theory]
    [InlineData("Surnamet:", "Surname")]
    [InlineData("Cojntryt", "Country")]
    [InlineData("Job Tide:", "Job Title")]
    [InlineData("hb Tlde", "Job Title")]
    [InlineData("3treet•.", "Street")]
    [InlineData("31reeu:", "Street1")]
    [InlineData("ZIPS.", "ZIP")]
    [InlineData("'lote.i", "Note")]
    [InlineData("Phonet", "Phone")]
    [InlineData("James", "James")]
    public void FieldLabelNormalizationCorrectsLocalOcrWithoutRewritingValues(string observed, string expected) =>
        Assert.Equal(expected, VisualSurfaceScanner.NormalizeFieldLabel(observed));

    [Fact]
    public void WideSingleLineAndMultilineFieldsAreRetainedWhenLabelled()
    {
        const int width = 1_700;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var street = new RectI(100, 60, 650, 28);
        var note = new RectI(850, 60, 800, 180);
        DrawBorder(pixels, width, street, 30);
        DrawBorder(pixels, width, note, 30);
        VisualTextObservation[] words =
        [
            new("Street:", new RectI(30, 67, 48, 12), 0),
            new("Note:", new RectI(850, 40, 38, 12), 1)
        ];
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target, new OpaqueSurfaceScanner.PixelFrame(width, height, pixels), [target.Bounds], [], words);

        var streetControl = Assert.Single(controls, control => control.Name == "Street");
        Assert.Equal("ControlType.Edit", streetControl.ControlType);
        var noteControl = Assert.Single(controls, control => control.Name == "Note");
        Assert.Equal("ControlType.Edit", noteControl.ControlType);
        Assert.True(IntersectionOverUnion(noteControl.Bounds, note) >= .72);
    }

    [Fact]
    public void OcrFrameArtifactDoesNotHidePopulatedField()
    {
        const int width = 400;
        const int height = 140;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var field = new RectI(100, 40, 220, 32);
        DrawBorder(pixels, width, field, 30);
        VisualTextObservation[] words =
        [
            new("Name:", new RectI(30, 49, 42, 12), 0),
            new("15Ä¯", new RectI(98, 40, 224, 27), 1)
        ];
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target, new OpaqueSurfaceScanner.PixelFrame(width, height, pixels), [target.Bounds], [], words);

        var control = Assert.Single(controls, item => item.Name == "Name");
        Assert.Equal("ControlType.Edit", control.ControlType);
        Assert.DoesNotContain("15Ä", control.OcrText ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialHeightDropDownDividerStillIdentifiesComboBox()
    {
        const int width = 400;
        const int height = 140;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var field = new RectI(100, 40, 220, 34);
        DrawBorder(pixels, width, field, 30);
        FillRect(pixels, width, new RectI(field.X + field.Width - 22, field.Y + 5, 1, 22), 30, 30, 30);
        VisualTextObservation[] words = [new("State:", new RectI(30, 50, 42, 12), 0)];
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target, new OpaqueSurfaceScanner.PixelFrame(width, height, pixels), [target.Bounds], [], words);

        var control = Assert.Single(controls, item => item.Name == "State");
        Assert.Equal("ControlType.ComboBox", control.ControlType);
        Assert.Contains("ExpandCollapse", control.SupportedPatterns ?? []);
    }

    [Fact]
    public void OcrWordsWithoutTabGeometryDoNotCreateControls()
    {
        const int width = 600;
        const int height = 320;
        var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        VisualTextObservation[] words =
        [
            new("General", new RectI(20, 250, 52, 14), 7),
            new("Additional", new RectI(92, 250, 68, 14), 7),
            new("Orders", new RectI(180, 250, 48, 14), 7),
            new("Files", new RectI(250, 250, 34, 14), 7)
        ];
        var target = new WindowTarget(
            44, 22, 7, "legacy", DateTimeOffset.UnixEpoch, "Clients", "TfrmMain",
            new RectI(0, 0, width, height));

        var controls = VisualSurfaceScanner.DiscoverCore(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            [target.Bounds],
            [],
            words);

        Assert.Empty(controls);
    }

    [Fact]
    public void OcrIdentityMatchesAcrossFiveMovedAndScaledFrames()
    {
        var identities = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var scale = index + 1;
            var width = 240 * scale;
            var height = 120 * scale;
            var pixels = Enumerable.Repeat((byte)235, width * height * 4).ToArray();
            for (var alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
            var button = new RectI((20 + index * 7) * scale, (18 + index * 3) * scale, 60 * scale, 22 * scale);
            DrawBorderWithThickness(pixels, width, button, 25, scale);
            var target = new WindowTarget(
                44, 22, 7, "opaque", DateTimeOffset.UnixEpoch, "Opaque", "Canvas",
                new RectI(300 + index * 100, 200 + index * 50, width, height));
            var words = new[]
            {
                new VisualTextObservation("Save", new RectI(button.X + 10 * scale, button.Y + 4 * scale, 38 * scale, 13 * scale), 0)
            };

            var control = VisualSurfaceScanner.DiscoverCore(
                    target,
                    new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
                    [target.Bounds],
                    [],
                    words)
                .Single(item => item.OcrText == "Save");
            identities.Add(control.AutomationId);
        }

        Assert.Single(identities.Distinct(StringComparer.Ordinal));
        Assert.StartsWith("visual:v3:", identities[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ManualClickPromotesTheSameHoverShadowNode()
    {
        var target = new WindowTarget(10, 10, 20, "Revit", DateTimeOffset.UnixEpoch,
            "Autodesk Revit", "HwndWrapper", new RectI(0, 0, 1200, 800));
        var shadow = new AutomationObservation(
            "shadow-hover:save", "", "shadow:save", "Save", "ControlType.Custom",
            "UiAtlas.HoverRegion", new RectI(400, 60, 32, 28), false, true,
            "UiAtlas.Shadow.Hover", 10, []);

        var promoted = AdaptiveCaptureCoordinator.ResolveManualHighlightTarget(
            target, [shadow], new RectI(415, 72, 1, 1));

        Assert.NotNull(promoted);
        Assert.Equal(shadow.RuntimeId, promoted.RuntimeId);
        Assert.Equal(shadow.Bounds, promoted.Bounds);
        Assert.Equal("UiAtlas.Pointer", promoted.FrameworkId);
        Assert.True(promoted.IsEnabled);
        Assert.False(promoted.IsOffscreen);
    }

    [Fact]
    public void PopupTextListCreatesOneControlPerVisibleRowAndKeepsSelection()
    {
        const int width = 80;
        const int height = 170;
        var pixels = new byte[width * height * 4];
        FillRect(pixels, width, new RectI(0, 0, width, height), 255, 255, 255);
        FillRect(pixels, width, new RectI(1, 71, width - 2, 30), 238, 238, 238);
        var target = new WindowTarget(10, 10, 20, "EXCEL", DateTimeOffset.UnixEpoch,
            "", "Net UI Tool Window", new RectI(100, 200, width, height));
        VisualTextObservation[] words =
        [
            new("8", new RectI(12, 20, 8, 12), 0),
            new("9", new RectI(12, 50, 8, 12), 1),
            new("11", new RectI(12, 80, 16, 12), 2),
            new("12", new RectI(12, 110, 16, 12), 3),
            new("14", new RectI(12, 140, 16, 12), 4)
        ];

        var controls = VisualSurfaceScanner.DiscoverPopupTextListControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            words);

        var list = Assert.Single(controls, control => control.ControlType == "ControlType.List");
        var items = controls.Where(control => control.ControlType == "ControlType.ListItem").ToArray();
        Assert.Equal(["8", "9", "11", "12", "14"], items.Select(item => item.Name));
        Assert.All(items, item => Assert.Equal(list.RuntimeId, item.ParentRuntimeId));
        Assert.True(Assert.Single(items, item => item.Name == "11").IsSelected);
        Assert.All(items.Where(item => item.Name != "11"), item => Assert.False(item.IsSelected));
    }

    [Fact]
    public void PopupTextListKeepsPaintedRowsWhoseShortLabelsOcrMissed()
    {
        const int width = 80;
        const int height = 200;
        var pixels = new byte[width * height * 4];
        FillRect(pixels, width, new RectI(0, 0, width, height), 255, 255, 255);
        FillRect(pixels, width, new RectI(1, 66, width - 2, 28), 238, 238, 238);
        foreach (var center in new[] { 20, 50, 80, 110, 140, 170 })
            FillRect(pixels, width, new RectI(12, center - 5, 8, 10), 40, 40, 40);
        var target = new WindowTarget(10, 10, 20, "EXCEL", DateTimeOffset.UnixEpoch,
            "", "Net UI Tool Window", new RectI(100, 200, width, height));
        VisualTextObservation[] words =
        [
            new("9", new RectI(12, 44, 8, 12), 0),
            new("12", new RectI(12, 104, 16, 12), 1),
            new("14", new RectI(12, 134, 16, 12), 2),
            new("16", new RectI(12, 164, 16, 12), 3)
        ];

        var controls = VisualSurfaceScanner.DiscoverPopupTextListControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            words);

        var items = controls.Where(control => control.ControlType == "ControlType.ListItem").ToArray();
        Assert.Equal(6, items.Length);
        Assert.Equal(2, items.Count(item => item.Name == "List item"));
        Assert.True(items[2].IsSelected);
        Assert.Equal(items.Length, items.Select(item => item.RuntimeId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PopupFontListKeepsDecorativeRowsAcrossSmallPitchDrift()
    {
        const int width = 376;
        const int height = 716;
        var pixels = new byte[width * height * 4];
        FillRect(pixels, width, new RectI(0, 0, width, height), 255, 255, 255);
        var centers = new[]
        {
            20, 58, 91, 126, 161, 197, 230, 263, 296, 329, 362, 395, 426, 459, 492, 525, 558
        };
        foreach (var center in centers)
            FillRect(pixels, width, new RectI(25, center - 6, 72, 12), 40, 40, 40);
        var target = new WindowTarget(10, 10, 20, "EXCEL", DateTimeOffset.UnixEpoch,
            "", "Net UI Tool Window", new RectI(100, 200, width, height));
        var missedCenters = new HashSet<int> { 395, 525 };
        var words = centers
            .Where(center => !missedCenters.Contains(center))
            .Select((center, index) => new VisualTextObservation(
                $"Font {index + 1}", new RectI(25, center - 6, 72, 12), index))
            .ToArray();

        var controls = VisualSurfaceScanner.DiscoverPopupTextListControls(
            target,
            new OpaqueSurfaceScanner.PixelFrame(width, height, pixels),
            words);

        var items = controls.Where(control => control.ControlType == "ControlType.ListItem").ToArray();
        Assert.Equal(centers.Length, items.Length);
        Assert.Equal(2, items.Count(item => item.Name == "List item"));
        Assert.All(items, item => Assert.InRange(item.Bounds.Height, 29, 39));
    }

    [Theory]
    [InlineData("No specific format", "123 General No specific format", "General")]
    [InlineData("12", "12 Number", "Number")]
    [InlineData("Currency", "Currency", "Currency")]
    [InlineData("1/4 Fraction", "1/4 Fraction", "Fraction")]
    [InlineData("102", "102 Scientific", "Scientific")]
    [InlineData("More Number", "More Number Formats...", "More Number Formats...")]
    [InlineData("List item", "Agency FB", "Agency FB")]
    public void PopupActionLabelsPreferLocalizedRowTextOverIconFragments(
        string existing,
        string localized,
        string expected)
    {
        Assert.Equal(expected, VisualSurfaceScanner.SelectPopupActionLabel(existing, localized));
    }

    [Fact]
    public void ManualClickPromotesTheSameVisualFallbackNode()
    {
        var target = new WindowTarget(10, 10, 20, "opaque", DateTimeOffset.UnixEpoch,
            "Opaque app", "Canvas", new RectI(0, 0, 1200, 800));
        var visual = new AutomationObservation(
            "visual:save", "", "visual:save", "Visual control 1", "ControlType.Button",
            "UiAtlas.VisualControlRegion", new RectI(400, 60, 90, 30), false, true,
            "UiAtlas.Visual", 10, []);

        var promoted = AdaptiveCaptureCoordinator.ResolveManualHighlightTarget(
            target, [visual], new RectI(445, 75, 1, 1));

        Assert.NotNull(promoted);
        Assert.Equal(visual.RuntimeId, promoted.RuntimeId);
        Assert.Equal(visual.Bounds, promoted.Bounds);
        Assert.Equal("UiAtlas.Pointer", promoted.FrameworkId);
        Assert.True(promoted.IsEnabled);
        Assert.False(promoted.IsOffscreen);
    }

    private static ExtractionSourceResult Source(ControlEvidenceSource source, AutomationObservation control, string surface = "surface") =>
        new(source, surface,
            [new($"evidence-{source}", source, surface, control, .9)], "ok", 1);

    private static AutomationObservation Control(string runtime, string name, string automationId, RectI bounds,
        string type = "ControlType.Button") =>
        new(runtime, "", automationId, name, type, "Test", bounds, true, false, "Test", 10, ["Invoke"]);

    private static void DrawBorder(byte[] pixels, int strideWidth, RectI bounds, byte value)
    {
        for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
        {
            SetPixel(x, bounds.Y);
            SetPixel(x, bounds.Y + bounds.Height - 1);
        }
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            SetPixel(bounds.X, y);
            SetPixel(bounds.X + bounds.Width - 1, y);
        }
        return;

        void SetPixel(int x, int y)
        {
            var offset = (y * strideWidth + x) * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
    }

    private static void DrawBorderWithThickness(
        byte[] pixels,
        int strideWidth,
        RectI bounds,
        byte value,
        int thickness)
    {
        for (var inset = 0; inset < thickness; inset++)
            DrawBorder(pixels, strideWidth,
                new RectI(bounds.X + inset, bounds.Y + inset, bounds.Width - inset * 2, bounds.Height - inset * 2),
                value);
    }

    private static void FillRect(
        byte[] pixels,
        int strideWidth,
        RectI bounds,
        byte blue,
        byte green,
        byte red)
    {
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
        {
            var offset = (y * strideWidth + x) * 4;
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }
    }

    private static double IntersectionOverUnion(RectI first, RectI second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var union = Math.Max(1L,
            (long)first.Width * first.Height + (long)second.Width * second.Height - intersection);
        return intersection / (double)union;
    }
}

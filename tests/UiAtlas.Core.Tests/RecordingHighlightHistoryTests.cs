using UiAtlas.Core.Cli;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RecordingHighlightHistoryTests
{
    [Fact]
    public void RestoresOnlyTheUsersRecordedClick()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(temp.Path, manualPointerUp: true);

        var highlights = RecordingHighlightHistory.Load([recording]);

        var highlight = Assert.Single(highlights);
        Assert.Equal(new RectI(15, 15, 120, 36), highlight.Bounds);
    }

    [Fact]
    public void RestoresSuccessfulAutomaticRecorderClicks()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(
            temp.Path,
            markers:
            [
                "auto-tabs:command:tab-home:command-1:target:10,20,30,40",
                "auto-tabs:command:tab-home:command-1:opened"
            ]);

        var highlights = RecordingHighlightHistory.Load([recording]);

        var highlight = Assert.Single(highlights);
        Assert.Equal(new RectI(10, 20, 30, 40), highlight.Bounds);
    }

    [Fact]
    public void DoesNotRestoreAnAutomaticTargetThatWasNotOpened()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(
            temp.Path,
            markers:
            [
                "auto-tabs:command:tab-home:command-1:target:10,20,30,40",
                "auto-tabs:command:skipped:tab-home:command-1"
            ]);

        var highlights = RecordingHighlightHistory.Load([recording]);

        Assert.Empty(highlights);
    }

    [Fact]
    public void RecognizesOnlyRecorderControlPanelWindows()
    {
        Assert.True(RecordingPanelCoordinator.IsRecorderWindowTitle("UiAtlas recording - excel"));
        Assert.False(RecordingPanelCoordinator.IsRecorderWindowTitle("UiAtlas mapped controls overlay"));
        Assert.False(RecordingPanelCoordinator.IsRecorderWindowTitle("UiAtlas Core — UI Knowledge Graph Editor"));
    }

    [Fact]
    public void LoadsMappedControlsFromTheRawDataStreamsLayer()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(temp.Path, manualPointerUp: true, worksheetControls: true);
        var map = Path.Combine(temp.Path, "map.db");
        SqliteGraphStore.Save(new RecordingGraphBuilder().Build(recording), map);

        var surfaces = RecordingHighlightHistory.LoadMappedSurfaces(map);

        Assert.NotEmpty(surfaces);
        Assert.Contains(surfaces, surface =>
            surface.RelativeHighlightBounds.Contains(new RectI(10, 10, 80, 24)));
        Assert.True(surfaces.Sum(surface => surface.RelativeHighlightBounds.Count) >
                    RecordingHighlightHistory.Load([recording]).Count);
    }

    [Fact]
    public void LayerReaderReturnsOnlyRequestedLayerNodes()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(temp.Path);
        var map = Path.Combine(temp.Path, "map.db");
        var graph = new RecordingGraphBuilder().Build(recording);
        SqliteGraphStore.Save(graph, map);
        var expected = graph.Nodes
            .Where(node => node.Properties.Any(property =>
                property.Name == "layer" && property.Value == "raw-data-streams"))
            .Select(node => node.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var actual = SqliteGraphStore.ReadLayerNodes(map, "raw-data-streams");

        Assert.Equal(expected, actual.Select(node => node.Id));
        Assert.All(actual, node => Assert.Contains(node.Properties, property =>
            property.Name == "layer" && property.Value == "raw-data-streams"));
        Assert.Empty(SqliteGraphStore.ReadLayerNodes(map, "missing-layer"));
    }

    [Fact]
    public void SelectsTheMappedRootFrameForTheCurrentlySelectedTab()
    {
        var home = Surface("home", isPrimary: true,
            [Control("tab-home", "Home", "TabItem", selected: true)]);
        var insert = Surface("insert", isPrimary: true,
            [Control("tab-insert", "Insert", "TabItem", selected: true)]);
        var current = Window("XLMAIN", "Book1 - Excel", new RectI(0, 0, 1200, 900));

        var selected = RecordingHighlightHistory.SelectBestMappedSurface(
            [home, insert], current,
            [Control("tab-home", "Home", "TabItem"), Control("tab-insert", "Insert", "TabItem", selected: true)],
            currentIsPrimarySurface: true);

        Assert.Equal("insert", selected?.Id);
    }

    [Fact]
    public void DoesNotGuessBetweenMappedTabsWhenLiveSelectionIsUnavailable()
    {
        var home = Surface("home", isPrimary: true,
            [Control("tab-home", "Home", "TabItem", selected: true)]);
        var insert = Surface("insert", isPrimary: true,
            [Control("tab-insert", "Insert", "TabItem", selected: true)]);
        var current = Window("XLMAIN", "Book1 - Excel", new RectI(0, 0, 1200, 900));

        var selected = RecordingHighlightHistory.SelectBestMappedSurface(
            [home, insert], current, [], currentIsPrimarySurface: true);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectsTheMappedDialogFrameForTheCurrentlySelectedTab()
    {
        var page = Surface("page", isPrimary: false,
            [Control("tab-page", "Page", "TabItem", selected: true), Control("ok", "OK", "Button")]);
        var margins = Surface("margins", isPrimary: false,
            [Control("tab-margins", "Margins", "TabItem", selected: true), Control("ok", "OK", "Button")]);
        var current = Window("#32770", "Page Setup", new RectI(200, 100, 600, 500));

        var selected = RecordingHighlightHistory.SelectBestMappedSurface(
            [page, margins], current,
            [Control("tab-page", "Page", "TabItem"), Control("tab-margins", "Margins", "TabItem", selected: true),
                Control("ok", "OK", "Button")],
            currentIsPrimarySurface: false);

        Assert.Equal("margins", selected?.Id);
    }

    [Fact]
    public void DoesNotPaintAnUnseenDialogAsAlreadyMapped()
    {
        var mapped = Surface("mapped", isPrimary: false,
            [Control("number", "Number", "TabItem", selected: true), Control("ok", "OK", "Button")]);
        var current = Window("#32770", "New Dialog", new RectI(200, 100, 600, 500));

        var selected = RecordingHighlightHistory.SelectBestMappedSurface(
            [mapped], current,
            [Control("advanced", "Advanced", "TabItem", selected: true), Control("apply", "Apply", "Button")],
            currentIsPrimarySurface: false);

        Assert.Null(selected);
    }

    [Fact]
    public void DoesNotReuseAnotherRecordedTabForAnUnseenDialogTab()
    {
        var mapped = Surface("page", isPrimary: false,
            [Control("tab-page", "Page", "TabItem", selected: true), Control("ok", "OK", "Button")]);
        var current = Window("#32770", "Page Setup", new RectI(200, 100, 600, 500));

        var selected = RecordingHighlightHistory.SelectBestMappedSurface(
            [mapped], current,
            [Control("tab-page", "Page", "TabItem"), Control("tab-margins", "Margins", "TabItem", selected: true),
                Control("ok", "OK", "Button")],
            currentIsPrimarySurface: false);

        Assert.Null(selected);
    }

    [Fact]
    public void ProjectsMappedDialogControlsAfterTheDialogMovesAndResizes()
    {
        var snapshot = Surface("dialog", isPrimary: false, [],
            capturedBounds: new RectI(100, 100, 400, 200),
            highlights: [new RectI(20, 10, 100, 20)]);

        var projected = RecordingHighlightHistory.ProjectMappedHighlights(
            snapshot,
            currentRootBounds: new RectI(50, 25, 1400, 1000),
            currentSurfaceBounds: new RectI(250, 125, 800, 400));

        Assert.Equal([new RectI(240, 120, 200, 40)], projected);
    }

    [Fact]
    public void ProjectsMappedControlEvidenceOntoTheCurrentWindow()
    {
        var snapshot = Surface("dialog", isPrimary: false,
            [Control("ok", "OK", "Button") with
            {
                Bounds = new RectI(120, 110, 100, 20),
                WindowHwnd = 17
            }],
            capturedBounds: new RectI(100, 100, 400, 200));

        var projected = RecordingHighlightHistory.ProjectMappedControls(
            snapshot,
            currentSurfaceBounds: new RectI(250, 125, 800, 400),
            currentWindowHwnd: 42);

        var control = Assert.Single(projected);
        Assert.Equal(new RectI(290, 145, 200, 40), control.Bounds);
        Assert.Equal(42, control.WindowHwnd);
    }

    private static MappedSurfaceHighlightSnapshot Surface(
        string id,
        bool isPrimary,
        IReadOnlyList<AutomationObservation> controls,
        RectI? capturedBounds = null,
        IReadOnlyList<RectI>? highlights = null) => new(
        id,
        "bundle",
        1,
        isPrimary ? "XLMAIN" : "#32770",
        isPrimary ? "Book1 - Excel" : "Page Setup",
        isPrimary ? "root" : "owned-dialog",
        capturedBounds ?? new RectI(0, 0, 1200, 900),
        highlights ?? [new RectI(10, 10, 80, 24)],
        controls);

    private static WindowObservation Window(string className, string title, RectI bounds) =>
        new(1, 1, 7, className, title, bounds, true, true, false, false, 96);

    private static AutomationObservation Control(
        string automationId,
        string name,
        string controlType,
        bool selected = false) =>
        new(automationId, "", automationId, name, controlType, "TestControl",
            new RectI(10, 10, 80, 24), true, false, "Test", IsSelected: selected);
}

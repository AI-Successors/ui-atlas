using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class SavedDataGridCountsTests
{
    [Fact]
    public void CountsNativeSemanticGridsAndSavedReviewsWithoutCountingCandidatesCellsOrRawCopies()
    {
        var native = NativeGrid();
        var graph = Graph(native,
            native with { Id = "raw-grid", Properties = [new("layer", "raw-world"), new("supportedPattern", "Grid")] },
            native with { Id = "candidate", Properties = [new("layer", "semantic-world"), new("controlType", "DataGrid")] },
            native with { Id = "status-bar", Properties = [new("layer", "semantic-world"), new("controlType", "StatusBar"),
                new("frameworkId", "Win32"), new("supportedPattern", "GridPatternIdentifiers.Pattern")] },
            native with { Id = "visual", Properties = [.. native.Properties, new("frameworkId", "UiAtlas.Visual.Ocr")] },
            native with { Id = "cell", Properties = [.. native.Properties, new("tableRow", "0"), new("tableColumn", "0")] });
        var saved = Review("legacy-grid", null);

        var report = MapQualityInspector.Evaluate(graph, new([], [], []), [saved, saved]);

        Assert.Equal(new SavedDataGridCounts(1, 1), report.DataGrids);
        Assert.Equal(2, report.DataGrids.Total);
        Assert.Contains("Saved data grids: 2 (1 UIA-native, 1 non-UIA-native).", report.UserSummary());
    }

    [Fact]
    public void ReviewedNativeGridAlreadyInMapIsCountedOnceUsingWindowRelativeEvidence()
    {
        var grid = Review("reviewed-native", null) with
        {
            Context = Review("reviewed-native", null).Context with
            {
                Control = new("Grid", "orders", "DataGrid", "Orders")
            }
        };
        var graph = Graph(NativeGrid(), Surface());

        Assert.Equal(new SavedDataGridCounts(1, 0), SavedDataGridCounts.From(graph, [grid]));
        // An identically named grid on a different window must remain a separate saved grid.
        var otherWindow = grid with
        {
            GridId = "other-window",
            Context = grid.Context with { WindowLocator = grid.Context.WindowLocator with { WindowClassName = "OtherWindow" } }
        };
        Assert.Equal(new SavedDataGridCounts(1, 1), SavedDataGridCounts.From(graph, [grid, otherWindow]));
    }

    [Fact]
    public void SavedNativeClassificationAndCountsSurviveResumeAndReplacingAReview()
    {
        using var temp = new TempDirectory();
        var workspace = RecorderWorkspace.CreateStandaloneWorkspace(Path.Combine(temp.Path, "grids.mlrec"), "OrdersApp");
        var native = Review("native", true);
        var nonNative = Review("non-native", false);
        workspace.SaveDataGrid(native.DisplayName, native.Context, native.Review);
        workspace.SaveDataGrid(nonNative.DisplayName, nonNative.Context, nonNative.Review);
        workspace.SaveDataGrid("Updated name", native.Context, native.Review);
        workspace.AddCompletedSession("first", Path.Combine(temp.Path, "first.mlrec"));

        var manifest = LogicalMapSessionStore.Load(workspace.SessionManifestPath);
        var resumed = RecorderWorkspace.CreateExistingWorkspace(workspace.MapPath, workspace.DefaultExportPath,
            workspace.SessionManifestPath, manifest);

        Assert.Equal(2, resumed.DataGrids.Count);
        Assert.Equal(new SavedDataGridCounts(1, 1), SavedDataGridCounts.From(Graph(), resumed.DataGrids));
    }

    [Fact]
    public void MapWithoutGridsReportsZeroForBothKinds()
    {
        var report = MapQualityInspector.Evaluate(Graph(), new([], [], []));
        Assert.Equal(new SavedDataGridCounts(0, 0), report.DataGrids);
        Assert.Contains("Saved data grids: 0 (0 UIA-native, 0 non-UIA-native).", report.UserSummary());
    }

    private static UiKnowledgeGraph Graph(params GraphNode[] nodes) =>
        new(new("uikg/4", "test", "graph", DateTimeOffset.UnixEpoch, "bundle", "hash", "full-evidence/1"), nodes, []);

    private static GraphNode NativeGrid() => new("native-grid", GraphNodeKind.Control, "surface", "native-grid", "Orders",
        [new("layer", "semantic-world"), new("frameworkId", "WPF"), new("controlType", "DataGrid"),
            new("supportedPattern", "GridPatternIdentifiers.Pattern"), new("semanticSurfaceId", "surface"),
            new("className", "Grid"), new("automationId", "orders"), new("name", "Orders")],
        [new("bundle", 1, "frame.json", new(150, 250, 600, 300)),
            new("resume-bundle", 2, "frame.json", new(250, 350, 600, 300))]);

    private static GraphNode Surface() => new("surface", GraphNodeKind.Surface, "", "surface", "Orders",
        [new("layer", "semantic-world"), new("className", "OrdersWindow")],
        [new("bundle", 1, "frame.json", new(100, 200, 1000, 800)),
            new("resume-bundle", 2, "frame.json", new(200, 300, 1000, 800))]);

    private static SavedDataGridReview Review(string id, bool? native) => new(id, id,
        new(new("OrdersApp", "OrdersWindow", "Orders", [], new("Grid")), "orders-tab",
            new("Grid", id, "DataGrid", id), new(50, 50, 600, 300), native),
        new("acquisition", id, new("revision", true, 1, [new("column-1", 0, "Name", 0, 600)], 20, 20),
            new(0, 0, 600, 20), "image.png", "hash", "capture.json", GridCaptureStatus.Partial,
            new(true, true, true, false, true, false, false, [], ["bottom-not-reached"]),
            new(GridRestorationStatus.Succeeded, "restored", true, true), [], [], DateTimeOffset.UnixEpoch),
        DateTimeOffset.UnixEpoch);
}

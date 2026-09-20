using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mcp;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Windows.Tests;

public sealed class SavedGridMcpTests
{
    [Fact]
    public void SessionGridWithPartialSchemaIsListedWithoutOpeningItsOldImage()
    {
        using var fixture = new CatalogFixture();
        var response = fixture.Reader.List("map");
        Assert.True(response.ListingComplete);
        var entry = Assert.Single(response.Grids);
        Assert.Equal("Orders fixture", entry.DisplayName);
        Assert.False(entry.SchemaComplete);
        Assert.Equal("Amount", Assert.Single(entry.Columns).Label);
        Assert.False(File.Exists(fixture.Grid.Review.ImagePath));
        var loaded = fixture.Reader.Get("map", "grid");
        Assert.Equal(fixture.Grid.GridId, loaded.GridId);
        Assert.Equal(fixture.Grid.Context.RelativeBounds, loaded.Context.RelativeBounds);
        Assert.Equal(fixture.Grid.Review.Schema.Columns, loaded.Review.Schema.Columns);
    }

    [Fact]
    public void MissingManifestIsEmptyButMismatchedOrDuplicateDefinitionsAreIssues()
    {
        using var fixture = new CatalogFixture();
        fixture.Save(fixture.Session with { LogicalMapId = "different" });
        Assert.False(fixture.Reader.List("map").ListingComplete);
        fixture.Save(fixture.Session with { DataGrids = [fixture.Grid, fixture.Grid] });
        Assert.False(fixture.Reader.List("map").ListingComplete);
        fixture.Save(fixture.Session with { DataGrids = [fixture.Grid with { Context = fixture.Grid.Context with
        { WindowLocator = fixture.Grid.Context.WindowLocator with { ProcessName = "another-app" } } }] });
        Assert.False(fixture.Reader.List("map").ListingComplete);
        File.Delete(fixture.Catalog.MapSessionPath("map"));
        Assert.Empty(fixture.Reader.List("map").Grids);
        Assert.True(fixture.Reader.List("map").ListingComplete);
        Assert.False(fixture.Reader.List("../escape").ListingComplete);
    }

    [Fact]
    public void BindingUsesCurrentWindowPositionAndRejectsChangedHostGeometry()
    {
        using var fixture = new CatalogFixture();
        var target = new GridTargetIdentity(1, 1, 1, DateTimeOffset.UtcNow, 2,
            new(400, 300, 500, 500), new(408, 324, 200, 100));
        Assert.Equal(target.HostBounds, GridExplorationService.BoundsFor(fixture.Grid, target));
        Assert.Throws<InvalidOperationException>(() => GridExplorationService.BoundsFor(fixture.Grid,
            target with { HostBounds = target.HostBounds with { Width = 250 } }));
    }

    [Fact]
    public async Task PollReturnsActualImageBytesOnlyWhenRequestedAndPreservesPartialCoverage()
    {
        using var fixture = new CatalogFixture();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aKGkAAAAASUVORK5CYII=");
        await using var registry = new GridOperationRegistry((_, _, _, _) => Task.FromResult(
            GridExplorationResult.Failed(GridAcquisitionStage.Capture, "tile-limit") with { Status = GridCaptureStatus.Partial, Png = png }),
            (_, _, progress, error) => GridExplorationResult.Failed(progress.Stage, error.Message));
        var tools = new GridMcpTools(fixture.Reader, registry);
        var started = tools.StartGridExploration("map", "grid", "image-request");
        var id = started.Operation!.AcquisitionId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (registry.Get(id).FinishedUtc is null) await Task.Delay(10, timeout.Token);
        Assert.Single(tools.GetGridExploration(id).Content);
        var response = tools.GetGridExploration(id, includeImage: true);
        var wire = JsonSerializer.Serialize(response, McpJsonUtilities.DefaultOptions);
        var roundtrip = JsonSerializer.Deserialize<CallToolResult>(wire, McpJsonUtilities.DefaultOptions)!;
        Assert.Equal(png, Assert.IsType<ImageContentBlock>(roundtrip.Content[1]).DecodedData.ToArray());
        using var metadata = JsonDocument.Parse(Assert.IsType<TextContentBlock>(roundtrip.Content[0]).Text);
        Assert.Equal("Partial", metadata.RootElement.GetProperty("operation").GetProperty("result").GetProperty("status").GetString());
        Assert.DoesNotContain("\"png\"", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletedExplorationWireResultDistinguishesColumnsFromAllRows()
    {
        using var fixture = new CatalogFixture();
        await using var registry = new GridOperationRegistry((_, _, _, _) => Task.FromResult(
            GridExplorationResult.Failed(GridAcquisitionStage.Verify, "") with
            {
                Status = GridCaptureStatus.Complete, Reasons = [], Scope = GridCaptureScope.VisibleRowBand,
                Coverage = new(true, true, true, false, true, true, false, [], [])
            }), (_, _, progress, error) => GridExplorationResult.Failed(progress.Stage, error.Message));
        var tools = new GridMcpTools(fixture.Reader, registry);
        var id = tools.StartGridExploration("map", "grid", "columns-request").Operation!.AcquisitionId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (registry.Get(id).FinishedUtc is null) await Task.Delay(10, timeout.Token);
        using var metadata = JsonDocument.Parse(Assert.IsType<TextContentBlock>(tools.GetGridExploration(id).Content[0]).Text);
        var result = metadata.RootElement.GetProperty("operation").GetProperty("result");
        Assert.Equal("Complete", result.GetProperty("status").GetString());
        Assert.Equal("VisibleRowBand", result.GetProperty("scope").GetString());
        Assert.True(result.GetProperty("coverage").GetProperty("columnCoverageComplete").GetBoolean());
        Assert.False(result.GetProperty("coverage").GetProperty("rowCoverageComplete").GetBoolean());
    }

    [Fact]
    public void AppListingMatchesMapsWithoutGridsAndKeepsSeparateWindowInstances()
    {
        using var fixture = new CatalogFixture();
        var one = AppWindow();
        var two = one with { Hwnd = 12, RootOwnerHwnd = 12, Title = "Second instance" };
        var other = one with { Hwnd = 13, ProcessName = "unmapped" };
        var apps = new AppGridCatalog(fixture.Reader, () => [one, two, other], _ => one);
        var listed = apps.List();
        Assert.True(listed.ListingComplete);
        Assert.Equal(3, listed.Apps.Count);
        Assert.Equal(3, listed.Apps.Select(a => a.AppId).Distinct().Count());
        Assert.All(listed.Apps.Take(2), app => { Assert.True(app.HasMap); Assert.Equal(1, app.SavedGridCount); });
        Assert.False(listed.Apps[2].HasMap);
        Assert.Equal(listed.Apps.Select(a => a.AppId), apps.List().Apps.Select(a => a.AppId));
        fixture.Save(fixture.Session with { DataGrids = [] });
        Assert.True(apps.List().Apps[0].HasMap);
        Assert.Equal(0, apps.List().Apps[0].SavedGridCount);
    }

    [Fact]
    public void UnknownAndReusedAppHandlesCannotSelectAnotherProcess()
    {
        using var fixture = new CatalogFixture();
        var current = AppWindow();
        var apps = new AppGridCatalog(fixture.Reader, () => [current], _ => current);
        Assert.Equal("unknown_app", Assert.Throws<GridOperationException>(() => apps.Get("not-listed")).Code);
        var id = Assert.Single(apps.List().Apps).AppId;
        current = current with { ProcessStartedUtc = current.ProcessStartedUtc.AddSeconds(1) };
        Assert.Equal("app_changed", Assert.Throws<GridOperationException>(() => apps.Get(id)).Code);
        Assert.NotEqual(id, Assert.Single(apps.List().Apps).AppId);
        Assert.Equal("unknown_app", Assert.Throws<GridOperationException>(() => apps.Get(id)).Code);
    }

    [Fact]
    public void GridReferencesAreAppScopedAndChangedSavedDefinitionsFailBeforeBinding()
    {
        using var fixture = new CatalogFixture();
        var current = AppWindow();
        var apps = new AppGridCatalog(fixture.Reader, () => [current], _ => current);
        var id = Assert.Single(apps.List().Apps).AppId;
        var grid = Assert.Single(apps.ListGrids(id).Grids);
        Assert.False(grid.Available); // The synthetic HWND is not a live application.
        Assert.NotNull(grid.Reason);
        Assert.Equal("unknown_grid_ref", Assert.Throws<GridOperationException>(() => apps.ResolveGrid("another-app", grid.GridRef)).Code);
        fixture.Save(fixture.Session with { DataGrids = [fixture.Grid with { SavedUtc = fixture.Grid.SavedUtc.AddSeconds(1) }] });
        Assert.Equal("grid_changed", Assert.Throws<GridOperationException>(() => apps.ResolveGrid(id, grid.GridRef)).Code);
        var relisted = Assert.Single(apps.ListGrids(id).Grids);
        Assert.NotEqual(grid.GridRef, relisted.GridRef);
        Assert.Equal("unknown_grid_ref", Assert.Throws<GridOperationException>(() => apps.ResolveGrid(id, grid.GridRef)).Code);
    }

    [Fact]
    public async Task AppRequestReturnsGridColumnsAndDeduplicatesApprovalWork()
    {
        using var fixture = new CatalogFixture();
        var window = AppWindow();
        var apps = new AppGridCatalog(fixture.Reader, () => [window], _ => window);
        var calls = 0;
        var entered = new TaskCompletionSource<GridExplorationArguments>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registry = new GridOperationRegistry(async (args, _, _, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult(args);
            await Task.Delay(Timeout.Infinite, token);
            return GridExplorationResult.Failed(GridAcquisitionStage.Bind, "unused");
        }, (_, _, p, _) => GridExplorationResult.Failed(p.Stage, "cancelled"));
        var tools = new GridMcpTools(fixture.Reader, registry, apps);
        var appId = Assert.Single(tools.ListApps().Apps).AppId;
        var first = tools.ShowAppGrids(appId, "app-request");
        Assert.True(first.Ok);
        Assert.Equal("Orders fixture", Assert.Single(first.Grids).DisplayName);
        Assert.Equal("Amount", Assert.Single(first.Grids[0].Columns).Label);
        Assert.Equal(appId, first.Operation!.AppId);
        Assert.Equal(first.Operation.AcquisitionId, tools.ShowAppGrids(appId, "app-request").Operation!.AcquisitionId);
        var args = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(appId, args.AppId);
        Assert.Equal("", args.MapId);
        Assert.Equal(1, calls);
        Assert.True(tools.CancelGridExploration(first.Operation.AcquisitionId).Ok);
    }

    [Fact]
    public async Task MissingMapsGridsAndCorruptCatalogNeverStartApproval()
    {
        using var fixture = new CatalogFixture();
        var window = AppWindow();
        var apps = new AppGridCatalog(fixture.Reader, () => [window], _ => window);
        var calls = 0;
        await using var registry = new GridOperationRegistry((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(GridExplorationResult.Failed(GridAcquisitionStage.Bind, "unexpected"));
        }, (_, _, p, _) => GridExplorationResult.Failed(p.Stage, "unexpected"));
        var tools = new GridMcpTools(fixture.Reader, registry, apps);
        var id = Assert.Single(tools.ListApps().Apps).AppId;
        fixture.Save(fixture.Session with { DataGrids = [] });
        Assert.Equal("no_saved_grids", tools.ShowAppGrids(id, "empty").Error!.Code);
        window = window with { ProcessName = "unmapped" };
        id = Assert.Single(tools.ListApps().Apps).AppId;
        Assert.Equal("no_map", tools.ShowAppGrids(id, "unmapped").Error!.Code);
        fixture.Save(fixture.Session with { LogicalMapId = "wrong" });
        Assert.False(tools.ListApps().ListingComplete);
        Assert.Equal("catalog_incomplete", tools.ShowAppGrids(id, "corrupt").Error!.Code);
        Assert.Equal(0, calls);
    }

    private static WindowTarget AppWindow() => new(11, 11, int.MaxValue, "FIXTURE", DateTimeOffset.UnixEpoch,
        "Synthetic", "Main", new(0, 0, 500, 500));

    [Fact]
    public void AppMatchesAllItsMapsWithoutCollapsingRepeatedGridIdsOrIncludingOtherApps()
    {
        using var fixture = new CatalogFixture();
        var graph = SqliteGraphStore.Load(fixture.Catalog.MapPath("map"));
        SqliteGraphStore.Save(graph with { Metadata = graph.Metadata with { LogicalMapId = "second-map" } },
            fixture.Catalog.MapPath("second-map"));
        LogicalMapSessionStore.Save(fixture.Catalog.MapSessionPath("second-map"),
            fixture.Session with { LogicalMapId = "second-map" });
        SqliteGraphStore.Save(graph with { Metadata = graph.Metadata with { LogicalMapId = "other-app" } },
            fixture.Catalog.MapPath("other-app"));
        LogicalMapSessionStore.Save(fixture.Catalog.MapSessionPath("other-app"),
            fixture.Session with { LogicalMapId = "other-app", ProcessName = "other", DataGrids = [] });
        var window = AppWindow();
        var apps = new AppGridCatalog(fixture.Reader, () => [window], _ => window);
        var entry = Assert.Single(apps.List().Apps);
        Assert.Equal(2, entry.MapCount);
        Assert.Equal(2, entry.SavedGridCount);
        var selected = apps.Get(entry.AppId);
        Assert.Equal(["map", "second-map"], selected.Catalog.Grids.Select(g => g.MapId).Order().ToArray());
        Assert.All(selected.Catalog.Grids, g => Assert.Equal("grid", g.GridId));
    }

    internal sealed class CatalogFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ui-atlas-grid-catalog-" + Guid.NewGuid().ToString("N"));
        public LocalArtifactCatalog Catalog { get; }
        public SavedGridCatalog Reader { get; }
        public SavedDataGridReview Grid { get; }
        public LogicalMapSessionManifest Session { get; }
        public CatalogFixture()
        {
            Catalog = new(_root); Reader = new(Catalog);
            GraphNode[] nodes = [new("app", GraphNodeKind.Application, "", "app", "fixture", [new("layer", "shared"), new("processName", "fixture")], [])];
            var metadata = new GraphMetadata(FormatVersions.Graph, FormatVersions.Tool, "graph", DateTimeOffset.UtcNow,
                "map", GraphSemantics.ComputeHash(nodes, []), FormatVersions.FullEvidenceProfile, LogicalMapId: "map");
            SqliteGraphStore.Save(new(metadata, nodes, []), Catalog.MapPath("map"));
            var review = new GridSchemaReview("capture", "grid", new("partial", false, null,
                    [new("physical-0", 0, "Amount", 0, 100)], 0, 20), new(0, 0, 200, 20),
                Path.Combine(_root, "does-not-exist.png"), "unread", "unread", GridCaptureStatus.Partial,
                new(true, true, true, false, true, true, false, [], []),
                new(GridRestorationStatus.Succeeded, "fixture", true, true), [], [], DateTimeOffset.UtcNow);
            Grid = new("grid", "Orders fixture", new(new("fixture", "Main", "Synthetic", [], new("Grid")),
                "surface", new("Grid"), new(8, 24, 200, 100)), review, DateTimeOffset.UtcNow);
            Session = new(LogicalMapSessionStore.FormatVersion, "map", "fixture", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, [], DataGrids: [Grid]);
            Save(Session);
        }
        public void Save(LogicalMapSessionManifest session) => LogicalMapSessionStore.Save(Catalog.MapSessionPath("map"), session);
        public void Dispose() => Directory.Delete(_root, true);
    }
}

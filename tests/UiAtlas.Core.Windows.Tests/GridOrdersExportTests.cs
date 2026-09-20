using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mcp;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridOrdersExportTests
{
    private sealed class Headers(bool emptyFirst = false) : IGridSchemaHeaderReader
    {
        public string Name => "test-reader";
        public Task<IReadOnlyList<GridHeaderText>> ReadAsync(IReadOnlyList<GridHeaderImage> images, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<GridHeaderText>>(images.Select((i, n) => new GridHeaderText(i.ColumnKey,
                emptyFirst && n == 0 ? GridCellReadStatus.Empty : GridCellReadStatus.Text, emptyFirst && n == 0 ? "" : "Duplicate")).ToArray());
    }

    private static AzureOpenAiTableReader Reader(bool unreadable = false, bool wrongKey = false) => new(new Headers(), (images, _) =>
        Task.FromResult<IReadOnlyList<GridHeaderText>>(images.Select((i, index) => new GridHeaderText(
            wrongKey && index == 0 ? "invented" : i.ColumnKey,
            unreadable && index == 0 ? GridCellReadStatus.Unreadable : index == 1 ? GridCellReadStatus.Empty : GridCellReadStatus.Text,
            unreadable && index == 0 ? null : index == 1 ? "" : index == 2 ? "00123" : "=2+2")).ToArray()));

    [Fact]
    public async Task FixedCellsPreserveDuplicateColumnsLiteralsAndBlankValues()
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true);
        var data = await Reader().ExtractAsync(fixture.Source, "id", "grid", null, default);
        Assert.Equal(GridDataStatus.Complete, data.Status);
        Assert.Equal(4, data.Rows.Count); Assert.Equal(3, data.Schema.Columns.Count);
        Assert.Equal(3, data.Schema.Columns.Select(c => c.ColumnKey).Distinct().Count());
        Assert.All(data.Schema.Columns, c => Assert.Equal("Duplicate", c.Label));
        Assert.Equal(["=2+2", "", "00123"], data.Rows[0].Cells.Select(c => c.Text));
        Assert.All(data.Rows.SelectMany(r => r.Cells), c => Assert.Equal("tile", Assert.Single(c.Sources).TileId));
        Assert.Equal(fixture.Source.Capture.Restoration, data.Restoration);
    }

    [Fact]
    public async Task UnreadableCellsArePartialAndInventedKeysFailWithoutRows()
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true);
        var partial = await Reader(unreadable: true).ExtractAsync(fixture.Source, "id", "grid", null, default);
        Assert.Equal(GridDataStatus.Partial, partial.Status);
        Assert.Equal(GridExtractionStatus.Partial, partial.ExtractionStatus);
        Assert.Contains("unreadable-cells", partial.Reasons);
        var bad = await Reader(wrongKey: true).ExtractAsync(fixture.Source, "id", "grid", null, default);
        Assert.Equal(GridDataStatus.Failed, bad.Status); Assert.Empty(bad.Rows);
        Assert.Contains("azure-cell-cardinality-mismatch", bad.Reasons);
    }

    [Fact]
    public async Task NarrowBlankGutterRequiresMatchingReviewedFirstDataColumnBeforeExclusion()
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true, rowSelector: true);
        var reader = new AzureOpenAiTableReader(new Headers(true), (cells, _) => Task.FromResult<IReadOnlyList<GridHeaderText>>(
            cells.Select(c => new GridHeaderText(c.ColumnKey, GridCellReadStatus.Text, "Value")).ToArray()));
        var unknown = await reader.ExtractAsync(fixture.Source, "id", "grid", null, default);
        Assert.Equal(GridDataStatus.Failed, unknown.Status);
        var hint = fixture.Source.Capture.Schema with { Columns = [new("physical-x-8", 0, "Duplicate", 8, 60)] };
        var known = await reader.ExtractAsync(fixture.Source, "id", "grid", null, default, hint);
        Assert.Equal(GridDataStatus.Complete, known.Status);
        Assert.Equal(3, known.Schema.Columns.Count);
        Assert.Equal("physical-x-8", known.Schema.Columns[0].ColumnKey);
        Assert.All(known.Rows, r => Assert.Equal(3, r.Cells.Count));
    }

    [Theory]
    [InlineData(false, 97)]
    [InlineData(true, 106)]
    public void FooterIsTrimmedOnlyWhenRuledRowsEndInABlankShortStrip(bool text, int expectedHeight)
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true, footer: true, footerText: text);
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(File.ReadAllBytes(fixture.Source.ImagePath!));
        Assert.Equal(expectedHeight, GridImageLayout.TrimBlankFooter(frame, fixture.Source.Capture.Tiles[0].BodyBounds).Height);
    }

    [Fact]
    public void SharedTerminalBorderIsNotAnExtraPartialRowButClippedRowsRemainPartial()
    {
        var rules = Enumerable.Range(0, 38).Select(i => 15 + i * 21).ToArray();
        rules[0]++; rules[^1]--;
        var rows = TableViewportDescriber.DescribeRows(new(0, 15, 100, 778), rules,
            TableViewportDescriber.InferPitch(rules));
        Assert.Equal(37, rows.Count); Assert.All(rows, r => Assert.False(r.IsPartial));
        var clipped = TableViewportDescriber.DescribeRows(new(0, 15, 100, 783), rules, 21);
        Assert.True(clipped[^1].IsPartial);
    }

    [Fact]
    public async Task IncompleteColumnsAndCancellationNeverBecomeACompleteRead()
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true);
        var source = fixture.Source with { Capture = fixture.Source.Capture with {
            Coverage = fixture.Source.Capture.Coverage with { RightBoundary = false } } };
        var failed = await Reader().ExtractAsync(source, "id", "grid", null, default);
        Assert.Equal(GridDataStatus.Failed, failed.Status); Assert.Empty(failed.Rows);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await Reader().ExtractAsync(fixture.Source, "id", "grid", null, cancellation.Token);
        Assert.Contains("extraction-cancelled", cancelled.Reasons);
        Assert.Equal(fixture.Source.Capture.Restoration, cancelled.Restoration);
    }

    [Fact]
    public async Task WorkbookRoundTripsLiteralValuesAndNeverOverwritesOrSilentlyExportsPartial()
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true);
        var data = await Reader().ExtractAsync(fixture.Source, "id", "grid", null, default);
        var exporter = new GridExcelExporter(() => fixture.Directory);
        var first = exporter.Export("id", "Orders", data);
        var second = exporter.Export("id", "Orders", data);
        Assert.NotEqual(first.Path, second.Path); Assert.True(first.Verified);
        Assert.Equal(4, first.RowCount); Assert.Equal(3, first.ColumnCount);
        var qa = Environment.GetEnvironmentVariable("UIATLAS_EXPORT_QA_DIR");
        if (!string.IsNullOrWhiteSpace(qa))
        {
            Directory.CreateDirectory(Path.Combine(qa, "evidence"));
            var copy = Path.Combine(qa, "synthetic-export.xlsx"); File.Copy(first.Path, copy, true);
            File.WriteAllText(Path.Combine(qa, "export.json"), JsonSerializer.Serialize(new { file = first with { Path = copy } }, GridMcpTools.Json));
            File.WriteAllText(Path.Combine(qa, "evidence", "data.json"), JsonSerializer.Serialize(data, GridMcpTools.Json));
        }
        using var zip = ZipFile.OpenRead(first.Path);
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(sheet); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Empty(xml.Descendants(ns + "f"));
        Assert.Equal("00123", xml.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "C5").Value);
        var partial = data with { Status = GridDataStatus.Partial, Reasons = ["row-limit"] };
        Assert.Equal("partial_data", Assert.Throws<GridOperationException>(() => exporter.Export("id", "Orders", partial)).Code);
        Assert.Contains("PARTIAL", exporter.Export("id", "Orders", partial, true).Path);
        Assert.Equal("no_exportable_data", Assert.Throws<GridOperationException>(() => exporter.Export("id", "Orders", data with { Status = GridDataStatus.Failed })).Code);
        var bad = data with { Rows = [data.Rows[0] with { Cells = data.Rows[0].Cells.Select(c => c with { ColumnKey = "wrong" + c.ColumnKey }).ToArray() }] };
        Assert.Equal("invalid_dataset", Assert.Throws<GridOperationException>(() => exporter.Export("id", "Orders", bad)).Code);
    }

    [Fact]
    public async Task PollingPagesRetainedDatasetAndDoesNotRestartRead()
    {
        using var fixture = new GridSchemaExtractorTests.Fixture(ruledRows: true);
        var data = await Reader().ExtractAsync(fixture.Source, "id", "grid", null, default);
        var calls = 0;
        await using var registry = new GridOperationRegistry((args, id, progress, token) =>
        {
            calls++; return Task.FromResult(GridExplorationResult.Failed(GridAcquisitionStage.Extract, "fixture") with { Data = data });
        }, (_, _, progress, error) => GridExplorationResult.Failed(progress.Stage, error.Message));
        var tools = new GridMcpTools(new SavedGridCatalog(new LocalArtifactCatalog(fixture.Directory)), registry);
        var started = registry.Start(new("", "", "request", "app", "grid-ref"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (registry.Get(started.AcquisitionId).FinishedUtc is null) await Task.Delay(10, timeout.Token);
        using var page = JsonDocument.Parse(((TextContentBlock)tools.GetGridRead(started.AcquisitionId, 1, 2).Content[0]).Text);
        Assert.Equal(2, page.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(3, page.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.Equal(4, page.RootElement.GetProperty("totalRowCount").GetInt32());
        Assert.False(page.RootElement.GetProperty("operation").GetProperty("result").TryGetProperty("data", out _));
        Assert.True(tools.GetGridRead(started.AcquisitionId, 0, 201).IsError);
        Assert.True(tools.GetGridRead("unknown").IsError);
        Assert.Equal(1, calls);
    }
}

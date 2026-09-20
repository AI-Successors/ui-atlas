using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class DataGridExtractionTests
{
    [Fact]
    public async Task LiteralRepeatedWordsAndLineBreaksSurvive()
    {
        var reader = Reader("very very good\nNew York New York");
        var frame = Frame(20, 20);
        SetDark(frame, 5, 5);
        var result = await reader.ReadAsync(frame, "a", Source(), false, default);
        Assert.Equal(GridCellReadStatus.Text, result.Status);
        Assert.Equal("very very good\nNew York New York", result.Text);
    }

    [Fact]
    public async Task OcrSilenceAndFailureAreNotEmpty()
    {
        var inkFrame = Frame(20, 20);
        SetDark(inkFrame, 5, 5);
        var silent = await Reader("").ReadAsync(inkFrame, "a", Source(), false, default);
        var failed = await new TableCellOcrReader((_, _) => Task.FromResult(new LiteralOcrObservation(false, null, "runtime-failed")))
            .ReadAsync(Frame(20, 20), "a", Source(), true, default);
        Assert.Equal(GridCellReadStatus.Unreadable, silent.Status);
        Assert.Null(silent.Text);
        // Exact uniform qualified pixels prove blank before OCR; use a nonuniform cell for failure below.
        Assert.Equal(GridCellReadStatus.Empty, failed.Status);
        var frame = Frame(20, 20);
        frame.Pixels[(5 * 20 + 5) * 4] = 0;
        failed = await new TableCellOcrReader((_, _) => Task.FromResult(new LiteralOcrObservation(false, null, "runtime-failed")))
            .ReadAsync(frame, "a", Source(), true, default);
        Assert.Equal(GridCellReadStatus.Unreadable, failed.Status);
        Assert.Null(failed.Text);
        Assert.Equal("runtime-failed", failed.Reason);
    }

    [Fact]
    public async Task ExactUniformBlankRequiresExplicitQualification()
    {
        var unqualified = await Reader("").ReadAsync(Frame(20, 20), "a", Source(), false, default);
        Assert.Equal("uniform-blank-detector-unqualified", unqualified.Reason);
        var result = await Reader("").ReadAsync(Frame(20, 20), "a", Source(), true, default);
        Assert.Equal(GridCellReadStatus.Empty, result.Status);
        Assert.Equal("", result.Text);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task OutOfBoundsSourceDoesNotInvokeOcr()
    {
        var calls = 0;
        var reader = new TableCellOcrReader((_, _) => { calls++; return Task.FromResult(new LiteralOcrObservation(true, "wrong", null)); });
        var result = await reader.ReadAsync(Frame(20, 20), "a", Source() with { PixelBounds = new(18, 0, 5, 5) }, false, default);
        Assert.Equal(0, calls);
        Assert.Equal(GridCellReadStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task ClippedInkCannotBecomePlausibleShortenedText()
    {
        var frame = Frame(20, 20);
        for (var y = 5; y < 10; y++) SetDark(frame, 18, y);
        var result = await Reader("truncated").ReadAsync(frame, "a", Source(), false, default);
        Assert.Equal(GridCellReadStatus.Unreadable, result.Status);
        Assert.Null(result.Text);
        Assert.Equal("cell-ink-touches-horizontal-edge", result.Reason);
    }

    [Fact]
    public void ConflictingReadingsDoNotSelectPlausibleText()
    {
        var first = new GridCellValue("a", GridCellReadStatus.Text, "100", null, [Source()]);
        var second = first with { Text = "700", Sources = [Source() with { TileId = "tile2" }] };
        var result = TableGridReader.Combine("a", [first, second]);
        Assert.Equal(GridCellReadStatus.Unreadable, result.Status);
        Assert.Null(result.Text);
        Assert.Equal("conflicting-accepted-cell-readings", result.Reason);
        Assert.Equal(2, result.Sources.Count);
    }

    [Fact]
    public async Task DuplicateLabelsAndIdenticalLookingRowsRetainPhysicalIdentity()
    {
        using var fixture = new CaptureFixture(2, 2);
        var result = await new TableGridReader(Reader("same same")).ExtractAsync(fixture.Capture);
        Assert.Equal(GridDataStatus.Complete, result.Status);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows.Select(row => row.AcquisitionRowId).Distinct().Count());
        Assert.Equal(new[] { "column0", "column1" }, result.Rows[0].Cells.Select(cell => cell.ColumnKey));
        Assert.All(result.Rows.SelectMany(row => row.Cells), cell => Assert.Equal("same same", cell.Text));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.CaptureTileCount);
    }

    [Fact]
    public async Task RowBatchesDoNotLoseOrDuplicateBoundaryRows()
    {
        using var fixture = new CaptureFixture(17, 1);
        var result = await new TableGridReader(Reader("literal")).ExtractAsync(fixture.Capture, new(RowBatchSize: 8));
        Assert.Equal(GridDataStatus.Complete, result.Status);
        Assert.Equal(Enumerable.Range(0, 17), result.Rows.Select(row => row.RowIndex));
    }

    [Fact]
    public async Task CompleteColumnExplorationDoesNotMeanCompleteDataExtraction()
    {
        using var fixture = new CaptureFixture(2, 2);
        var capture = fixture.Capture with
        {
            Mode = GridAcquisitionMode.ImageExploration,
            Scope = GridCaptureScope.VisibleRowBand,
            Coverage = fixture.Capture.Coverage with { BottomBoundary = false }
        };
        Assert.Equal(GridCaptureStatus.Complete, capture.Status);
        Assert.True(capture.Coverage.ColumnCoverageComplete);
        var result = await new TableGridReader(Reader("sample")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Partial, result.Status);
        Assert.Equal(GridExtractionStatus.Complete, result.ExtractionStatus);
        Assert.Equal(2, result.Rows.Count);
        Assert.False(result.Coverage!.RowCoverageComplete);
    }

    [Fact]
    public async Task ExtractionLimitIsPartialAndRetainsRestoration()
    {
        using var fixture = new CaptureFixture(3, 1);
        var result = await new TableGridReader(Reader("value")).ExtractAsync(fixture.Capture, new(MaxRows: 2));
        Assert.Equal(GridDataStatus.Partial, result.Status);
        Assert.Equal(2, result.Rows.Count);
        Assert.Contains("extraction-row-limit", result.Reasons);
        Assert.Equal(fixture.Capture.Restoration, result.Restoration);
    }

    [Fact]
    public async Task SuccessfulSubsetTranscriptionPreservesEarlierJoinFailureStage()
    {
        using var fixture = new CaptureFixture(1, 1);
        var capture = fixture.Capture with
        {
            Status = GridCaptureStatus.Partial,
            Stage = GridAcquisitionStage.Join,
            Reasons = ["ambiguous-terminal-scroll"]
        };
        var result = await new TableGridReader(Reader("value")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Partial, result.Status);
        Assert.Equal(GridExtractionStatus.Complete, result.ExtractionStatus);
        Assert.Equal(GridAcquisitionStage.Join, result.Stage);
        Assert.Contains("ambiguous-terminal-scroll", result.Reasons);
    }

    [Fact]
    public async Task MissingRowsRemainExplicit()
    {
        using var fixture = new CaptureFixture(3, 1);
        var tile = fixture.Capture.Tiles[0];
        var capture = fixture.Capture with { Tiles = [tile with { Rows = tile.Rows.Where(row => row.RowIndex != 1).ToArray() }] };
        var result = await new TableGridReader(Reader("value")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Partial, result.Status);
        Assert.Equal(new[] { 0, 2 }, result.Rows.Select(row => row.RowIndex));
        Assert.Contains("missing-row-occurrences", result.Reasons);
    }

    [Fact]
    public async Task DuplicateRecordIdsAreReturnedWithoutDeduplication()
    {
        using var fixture = new CaptureFixture(2, 1);
        var result = await new TableGridReader(Reader("ID-1")).ExtractAsync(fixture.Capture, new(RecordIdColumnKey: "column0"));
        Assert.Equal(GridDataStatus.Partial, result.Status);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("ID-1", row.RecordId));
        Assert.Contains("duplicate-record-id-retained", result.Reasons);
    }

    [Fact]
    public async Task ChangedSourceHashCannotReadReplacementPixels()
    {
        using var fixture = new CaptureFixture(1, 1);
        var capture = fixture.Capture with { Tiles = [fixture.Capture.Tiles[0] with { Sha256 = new string('0', 64) }] };
        var result = await new TableGridReader(Reader("forged")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.All(result.Rows.SelectMany(row => row.Cells), cell => Assert.Null(cell.Text));
        Assert.Contains(result.Reasons, reason => reason.Contains("source-sha256-mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedTilesNeverReachOcr()
    {
        using var fixture = new CaptureFixture(1, 1);
        var calls = 0;
        var reader = new TableGridReader(new TableCellOcrReader((_, _) => { calls++; return Task.FromResult(new LiteralOcrObservation(true, "x", null)); }));
        var capture = fixture.Capture with { Tiles = [fixture.Capture.Tiles[0] with { Accepted = false }] };
        var result = await reader.ExtractAsync(capture);
        Assert.Equal(0, calls);
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task UncertainJoinCannotBeRepairedByConfidentTranscription(bool accepted, int candidates)
    {
        using var fixture = new CaptureFixture(1, 1);
        var tile = fixture.Capture.Tiles[0];
        var capture = fixture.Capture with
        {
            Tiles = [tile, tile with { TileId = "second" }],
            Joins = [new(tile.TileId, "second", GridScrollAxis.Vertical, accepted, 0, 0, candidates, 100, 1, "ambiguous")]
        };
        var result = await new TableGridReader(Reader("confident")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Contains("accepted-tiles-have-unverified-joins", result.Reasons);
    }

    [Fact]
    public async Task DuplicateSourceIdsFailBeforeReading()
    {
        using var fixture = new CaptureFixture(1, 1);
        var capture = fixture.Capture with { Tiles = [fixture.Capture.Tiles[0], fixture.Capture.Tiles[0]] };
        var result = await new TableGridReader(Reader("x")).ExtractAsync(capture);
        Assert.Contains("duplicate-or-missing-source-tile-id", result.Reasons);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task JoinDisplacementMustAgreeWithAcceptedPhysicalPlacement()
    {
        using var fixture = new CaptureFixture(1, 1);
        var tile = fixture.Capture.Tiles[0];
        var capture = fixture.Capture with
        {
            Tiles = [tile, tile with { TileId = "shifted", OffsetX = 10 }],
            Joins = [new("tile", "shifted", GridScrollAxis.Horizontal, true, 5, 0, 1, 100, 1, "wrong-offset")]
        };
        var result = await new TableGridReader(Reader("wrong column")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Contains("accepted-tiles-have-unverified-joins", result.Reasons);
    }

    [Fact]
    public async Task DuplicateRowRegionCannotSilentlyDisappearInMerge()
    {
        using var fixture = new CaptureFixture(1, 1);
        var tile = fixture.Capture.Tiles[0];
        var result = await new TableGridReader(Reader("x")).ExtractAsync(fixture.Capture with
            { Tiles = [tile with { Rows = [tile.Rows[0], tile.Rows[0]] }] });
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.Contains("duplicate-row-source-region", result.Reasons);
    }

    [Fact]
    public async Task ShiftedRowDoesNotAssignTextToAnotherOccurrence()
    {
        using var fixture = new CaptureFixture(2, 1);
        var tile = fixture.Capture.Tiles[0];
        var capture = fixture.Capture with { Tiles = [tile with { Rows = [tile.Rows[1] with { RowIndex = 0 }] }] };
        var result = await new TableGridReader(Reader("wrong row")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.All(result.Rows.SelectMany(row => row.Cells), cell => Assert.Null(cell.Text));
        Assert.Contains(result.Reasons, reason => reason.StartsWith("row-region-invalid:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialEdgeRowIsNotTranscribed()
    {
        using var fixture = new CaptureFixture(1, 1);
        var tile = fixture.Capture.Tiles[0];
        var capture = fixture.Capture with { Tiles = [tile with { Rows = [tile.Rows[0] with { IsComplete = false }] }] };
        var result = await new TableGridReader(Reader("not full")).ExtractAsync(capture);
        Assert.Empty(result.Rows);
        Assert.Equal(GridDataStatus.Failed, result.Status);
    }

    [Fact]
    public async Task MissingColumnIsUnreadableRatherThanShifted()
    {
        using var fixture = new CaptureFixture(1, 2);
        var tile = fixture.Capture.Tiles[0];
        var capture = fixture.Capture with
        {
            Tiles = [tile with { BodyBounds = tile.BodyBounds with { Width = 20 }, Rows = [tile.Rows[0] with { Bounds = tile.Rows[0].Bounds with { Width = 20 } }] }]
        };
        var result = await new TableGridReader(Reader("first")).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Partial, result.Status);
        Assert.Equal("first", result.Rows[0].Cells[0].Text);
        Assert.Equal(GridCellReadStatus.Unreadable, result.Rows[0].Cells[1].Status);
        Assert.Empty(result.Rows[0].Cells[1].Sources);
    }

    [Fact]
    public async Task AcceptedHorizontalViewsAssignByPhysicalOffsetAndMergeOverlap()
    {
        using var fixture = new CaptureFixture(1, 3);
        var tile = fixture.Capture.Tiles[0];
        var left = tile with
        {
            BodyBounds = new(0, 20, 40, 20),
            Rows = [new("row-0", 0, new(0, 20, 40, 20), true)]
        };
        var right = tile with
        {
            TileId = "right", OffsetX = 20, BodyBounds = new(20, 20, 40, 20),
            Rows = [new("row-0", 0, new(20, 20, 40, 20), true)]
        };
        var capture = fixture.Capture with
        {
            Tiles = [left, right],
            Joins = [new("tile", "right", GridScrollAxis.Horizontal, true, 20, 0, 1, 100, 1, "verified")]
        };
        var result = await new TableGridReader(new TableCellOcrReader((frame, _) =>
            Task.FromResult(new LiteralOcrObservation(true, frame.Pixels[0].ToString(), null)))).ExtractAsync(capture);
        Assert.Equal(GridDataStatus.Complete, result.Status);
        Assert.Single(result.Rows);
        Assert.Equal(new[] { "250", "200", "150" }, result.Rows[0].Cells.Select(cell => cell.Text));
        Assert.Equal(2, result.Rows[0].Cells[1].Sources.Count);
        Assert.Single(result.Rows[0].Cells[2].Sources);
        Assert.Equal("right", result.Rows[0].Cells[2].Sources[0].TileId);
    }

    [Fact]
    public async Task CancelledExtractionPreservesCaptureReceipt()
    {
        using var fixture = new CaptureFixture(1, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new TableGridReader(Reader("x")).ExtractAsync(fixture.Capture, cancellationToken: cancellation.Token);
        Assert.Equal(GridDataStatus.Failed, result.Status);
        Assert.Contains("extraction-cancelled", result.Reasons);
        Assert.Equal(fixture.Capture.ManifestPath, result.CaptureManifestPath);
        Assert.Equal(fixture.Capture.Restoration, result.Restoration);
    }

    [Fact]
    public async Task DescriberEstablishesGeometryBeforeLabelsAndPreservesDuplicateHeaders()
    {
        var frame = Frame(80, 80);
        SetDark(frame, 8, 8);
        SetDark(frame, 48, 8);
        for (var y = 0; y < 80; y++) SetDark(frame, 40, y);
        foreach (var y in new[] { 20, 40, 60 })
            for (var x = 0; x < 80; x++) SetDark(frame, x, y);
        var schema = new GridSchema("r1", true, 2,
            [new("a", 0, "Name", 0, 40), new("b", 1, "Name", 40, 80)], 20, 20);
        var request = new GridViewportRequest(Encode(frame), new(100, 100, 80, 80), new(0, 0, 80, 20), new(0, 20, 80, 60), schema);
        var result = await new TableViewportDescriber(Reader("Name")).DescribeAsync(request);
        Assert.True(result.StructureVerified, string.Join(';', result.Reasons));
        Assert.False(result.ReadingQualified);
        Assert.Equal(new[] { "a", "b" }, result.Columns.Select(column => column.ColumnKey));
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.False(row.IsPartial));
        Assert.False(result.SchemaFragment.IsComplete);
    }

    [Fact]
    public async Task DescriberRejectsIncompatibleHeaderWithoutChangingSavedSchema()
    {
        var frame = Frame(40, 80);
        SetDark(frame, 8, 8);
        foreach (var y in new[] { 20, 40, 60 }) for (var x = 0; x < 40; x++) SetDark(frame, x, y);
        var schema = new GridSchema("r1", true, 1, [new("a", 0, "Order Number", 0, 40)], 20, 20);
        var result = await new TableViewportDescriber(Reader("Different")).DescribeAsync(
            new(Encode(frame), new(0, 0, 40, 80), new(0, 0, 40, 20), new(0, 20, 40, 60), schema));
        Assert.False(result.StructureVerified);
        Assert.Contains("header-label-changed:a", result.Reasons);
        Assert.Equal("Order Number", schema.Columns[0].Label);
        Assert.Equal("Order Number", result.SchemaFragment.Columns[0].Label);
    }

    [Fact]
    public async Task DescriberDoesNotExtendRowsIntoUnruledCanvas()
    {
        var frame = Frame(40, 200);
        SetDark(frame, 8, 8);
        foreach (var y in new[] { 20, 40, 60, 80 }) for (var x = 0; x < 40; x++) SetDark(frame, x, y);
        var schema = new GridSchema("r1", true, 1, [new("a", 0, "ID", 0, 40)], 20, 20);
        var result = await new TableViewportDescriber(Reader("ID")).DescribeAsync(
            new(Encode(frame), new(0, 0, 40, 200), new(0, 0, 40, 20), new(0, 20, 40, 180), schema));
        Assert.True(result.StructureVerified);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(80, result.Rows[^1].Bounds.Y + result.Rows[^1].Bounds.Height);
    }

    [Fact]
    public async Task BlankCanvasDoesNotProveEmptyTableOrInventColumns()
    {
        var schema = new GridSchema("candidate", false, null, [], 0, 20);
        var result = await new TableViewportDescriber(Reader("caption")).DescribeAsync(
            new(Encode(Frame(80, 100)), new(0, 0, 80, 100), new(0, 0, 80, 20), new(0, 20, 80, 80), schema));
        Assert.False(result.StructureVerified);
        Assert.False(result.ReadingQualified);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
    }

    private static TableCellOcrReader Reader(string value) => new((_, _) => Task.FromResult(new LiteralOcrObservation(true, value, null)));
    private static GridCellSource Source() => new("tile", new(1, 1, 18, 18), "hash");
    private static OpaqueSurfaceScanner.PixelFrame Frame(int width, int height) => new(width, height, Enumerable.Repeat((byte)255, width * height * 4).ToArray());
    private static void SetDark(OpaqueSurfaceScanner.PixelFrame frame, int x, int y)
    {
        var offset = (y * frame.Width + x) * 4;
        frame.Pixels[offset] = frame.Pixels[offset + 1] = frame.Pixels[offset + 2] = 100;
    }
    private static byte[] Encode(OpaqueSurfaceScanner.PixelFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class CaptureFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "uiatlas-public-grid-tests", Guid.NewGuid().ToString("N"));
        public CapturedGrid Capture { get; }
        public CaptureFixture(int rows, int columns)
        {
            Directory.CreateDirectory(_directory);
            var frame = Frame(columns * 20, (rows + 1) * 20);
            for (var y = 20; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                frame.Pixels[(y * frame.Width + x) * 4] = (byte)(250 - (x / 20) * 50);
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                SetDark(frame, column * 20 + 8, (row + 1) * 20 + 8);
            var bytes = Encode(frame);
            var path = Path.Combine(_directory, "synthetic.png");
            File.WriteAllBytes(path, bytes);
            var body = new RectI(0, 20, columns * 20, rows * 20);
            var image = new RectI(0, 0, frame.Width, frame.Height);
            var now = DateTimeOffset.UtcNow;
            var schema = new GridSchema("schema1", true, columns,
                Enumerable.Range(0, columns).Select(index => new GridColumn($"column{index}", index, "Duplicate", index * 20, (index + 1) * 20)).ToArray(), 20, 20);
            var tile = new GridCapturedTile("tile", path, Convert.ToHexString(SHA256.HashData(bytes)), now,
                image, new(0, 0, columns * 20, 20), body, 0, 0, true,
                Enumerable.Range(0, rows).Select(index => new GridCapturedRowRegion($"row-{index}", index, new(0, (index + 1) * 20, columns * 20, 20), true)).ToArray());
            Capture = new("acquisition", "grid", schema, new(1, 1, 1, now, 2, image, image), now, now,
                GridCaptureStatus.Complete, GridAcquisitionStage.Capture, [], [tile], [], [],
                new(true, true, true, true, true, false, false, [body], []),
                new(GridRestorationStatus.Succeeded, "restored", true, true), Path.Combine(_directory, "manifest.json"));
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

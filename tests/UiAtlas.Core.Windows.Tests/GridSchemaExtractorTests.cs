using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridSchemaExtractorTests
{
    [Fact]
    public async Task CloudReaderReceivesOnlySelectedHeaderPixelsAndCannotChangeGeometry()
    {
        using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed);
        var reader = new HeaderReader();
        var header = new RectI(0, 0, 180, 24);
        var draft = await new GridSchemaExtractor(reader).ExtractAsync(fixture.Source, header, [50, 120]);
        Assert.Equal("test-cloud", draft.HeaderReader);
        Assert.Equal(new double[] { 0, 50, 120 }, draft.Schema.Columns.Select(c => c.StartX));
        Assert.Equal(new double[] { 50, 120, 180 }, draft.Schema.Columns.Select(c => c.EndX));
        Assert.Equal(new[] { "Exact Header", "Exact Header", "" }, draft.Schema.Columns.Select(c => c.Label));
        Assert.All(draft.HeaderReadings, r => Assert.Equal(24, r.Bounds.Height));
        Assert.Equal(new[] { 50, 70, 60 }, reader.Sizes.Select(s => s.Width));
        Assert.All(reader.Sizes, s => Assert.Equal(24, s.Height));
        Assert.False(GridSchemaExtractor.Confirm(draft, ["Exact Header", "Exact Header", ""]).Schema.IsComplete);
    }

    private sealed class HeaderReader : IGridSchemaHeaderReader
    {
        public string Name => "test-cloud";
        internal List<(int Width, int Height)> Sizes { get; } = [];
        public Task<IReadOnlyList<GridHeaderText>> ReadAsync(IReadOnlyList<GridHeaderImage> headers, CancellationToken cancellation)
        {
            foreach (var header in headers)
            {
                using var stream = new MemoryStream(header.Png);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Sizes.Add((frame.PixelWidth, frame.PixelHeight));
            }
            return Task.FromResult<IReadOnlyList<GridHeaderText>>(headers.Select((h, i) => new GridHeaderText(h.ColumnKey,
                i == 2 ? GridCellReadStatus.Unreadable : GridCellReadStatus.Text, i == 2 ? null : "Exact Header")).ToArray());
        }
    }

    [Theory]
    [InlineData(GridCaptureStatus.Complete, GridRestorationStatus.Succeeded)]
    [InlineData(GridCaptureStatus.Partial, GridRestorationStatus.Failed)]
    [InlineData(GridCaptureStatus.Partial, GridRestorationStatus.SafelySkipped)]
    [InlineData(GridCaptureStatus.Failed, GridRestorationStatus.Failed)]
    public async Task EveryRetainedCaptureCanProduceAReviewedSchemaWithoutPromotingCoverage(GridCaptureStatus status, GridRestorationStatus restoration)
    {
        using var fixture = new Fixture(status, restoration);
        var calls = 0;
        var extractor = new GridSchemaExtractor(new TableCellOcrReader((crop, _) =>
        {
            calls++;
            Assert.Equal(20, crop.Height); // Header only, never body cells.
            return Task.FromResult(new LiteralOcrObservation(true, "Code Code", null));
        }));
        var draft = await extractor.ExtractAsync(fixture.Source, fixture.Source.HeaderBounds);
        Assert.Equal(3, calls); Assert.Equal(3, draft.Schema.Columns.Count);
        Assert.Equal(3, draft.Schema.Columns.Select(c => c.ColumnKey).Distinct().Count());
        Assert.All(draft.Schema.Columns, c => Assert.Equal("Code Code", c.Label));
        Assert.False(draft.Schema.IsComplete); Assert.Null(draft.ReviewedUtc);
        var reviewed = GridSchemaExtractor.Confirm(draft, ["Code", "Code", ""]);
        var path = Path.Combine(fixture.Directory, "schema.json");
        await GridSchemaExtractor.SaveAsync(reviewed, path);
        var saved = JsonSerializer.Deserialize<GridSchemaReview>(await File.ReadAllTextAsync(path), JsonDefaults.Options)!;
        Assert.Equal(status, saved.CaptureStatus); Assert.Equal(restoration, saved.Restoration.Status);
        Assert.Equal(status == GridCaptureStatus.Complete, saved.Schema.IsComplete);
        Assert.False(saved.Schema.UniformBlankCellsQualified); Assert.Equal(0, saved.Schema.RowHeight);
        Assert.Equal(new[] { "Code", "Code", "" }, saved.Schema.Columns.Select(c => c.Label));
        Assert.NotNull(saved.ReviewedUtc); Assert.Equal(draft.ImageSha256, saved.ImageSha256);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Fact]
    public async Task SchemaCompletenessDependsOnColumnsNotBottomBoundaryOrRestoration()
    {
        using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed);
        var source = fixture.Source with { Capture = fixture.Source.Capture with { Coverage = fixture.Source.Capture.Coverage with { RightBoundary = true } } };
        var draft = await Reader().ExtractAsync(source, source.HeaderBounds);
        var reviewed = GridSchemaExtractor.Confirm(draft, ["One", "Two", "Three"]);
        Assert.True(reviewed.Schema.IsComplete); Assert.False(reviewed.Coverage.BottomBoundary);
        Assert.Equal(GridCaptureStatus.Partial, reviewed.CaptureStatus);
        Assert.Equal(GridRestorationStatus.Failed, reviewed.Restoration.Status);
    }

    [Fact]
    public async Task SavedColumnReviewRetainsTheExplorationScopeAndIncompleteRowCoverage()
    {
        using var fixture = new Fixture();
        var source = fixture.Source with { Capture = fixture.Source.Capture with
        {
            Mode = GridAcquisitionMode.ImageExploration, Scope = GridCaptureScope.VisibleRowBand,
            Coverage = fixture.Source.Capture.Coverage with { BottomBoundary = false }
        } };
        var draft = await Reader().ExtractAsync(source, source.HeaderBounds);
        var reviewed = GridSchemaExtractor.Confirm(draft, ["One", "Two", "Three"]);
        var path = Path.Combine(fixture.Directory, "column-review.json");
        await GridSchemaExtractor.SaveAsync(reviewed, path);
        var saved = JsonSerializer.Deserialize<GridSchemaReview>(await File.ReadAllTextAsync(path), JsonDefaults.Options)!;
        Assert.Equal(GridCaptureScope.VisibleRowBand, saved.CaptureScope);
        Assert.Equal(GridCaptureStatus.Complete, saved.CaptureStatus);
        Assert.True(saved.Schema.IsComplete);
        Assert.True(saved.Coverage.ColumnCoverageComplete);
        Assert.False(saved.Coverage.RowCoverageComplete);
        Assert.Equal(0, saved.Schema.RowHeight);
    }

    [Fact]
    public async Task UserCanSelectDifferentHeaderBandAndCorrectDetectedDividers()
    {
        using var fixture = new Fixture();
        var draft = await Reader().ExtractAsync(fixture.Source, new(0, 24, 180, 24), [60, 120]);
        Assert.Equal(24, draft.HeaderBounds.Y); Assert.Equal(3, draft.Schema.Columns.Count);
        Assert.All(draft.HeaderReadings, r => Assert.Equal(24, r.Bounds.Y));
        var merged = await Reader().ExtractAsync(fixture.Source, new(0, 24, 180, 24), [120]);
        Assert.Equal(2, merged.Schema.Columns.Count); Assert.Equal(120, merged.Schema.Columns[0].EndX);
    }

    [Fact]
    public async Task MissingOcrAndBlankHeaderStillAllowManualReview()
    {
        using var fixture = new Fixture();
        var extractor = new GridSchemaExtractor(new TableCellOcrReader((_, _) => Task.FromResult(new LiteralOcrObservation(false, null, "ocr-language-unavailable"))));
        var draft = await extractor.ExtractAsync(fixture.Source, fixture.Source.HeaderBounds);
        Assert.All(draft.HeaderReadings, r => Assert.Equal(GridCellReadStatus.Unreadable, r.Status));
        Assert.All(draft.Schema.Columns, c => Assert.Equal("", c.Label));
        var reviewed = GridSchemaExtractor.Confirm(draft, ["Manual", "Manual", ""]);
        Assert.Equal("Manual", reviewed.Schema.Columns[0].Label);
    }

    [Fact]
    public async Task NoDividersRetainsSingleColumnForManualCorrection()
    {
        using var fixture = new Fixture();
        var draft = await Reader().ExtractAsync(fixture.Source, fixture.Source.HeaderBounds, []);
        Assert.Single(draft.Schema.Columns); Assert.Contains("column-dividers-not-detected", draft.Reasons);
    }

    [Fact]
    public async Task HeaderGlyphStrokesAndStitchedViewportBordersAreNotColumns()
    {
        using var fixture = new Fixture(stitchedArtifacts: true);
        var draft = await Reader().ExtractAsync(fixture.Source, fixture.Source.HeaderBounds);
        Assert.Equal(new double[] { 0, 60, 120 }, draft.Schema.Columns.Select(c => c.StartX));
        Assert.Equal(new double[] { 60, 120, 180 }, draft.Schema.Columns.Select(c => c.EndX));
    }

    [Fact]
    public async Task RejectsInvalidGeometryDividersAndChangedSourceBeforeSave()
    {
        using var fixture = new Fixture();
        var extractor = Reader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(fixture.Source, new(0, 70, 180, 24)));
        foreach (var dividers in new int[][] { [60, 60], [1, 60], [-1], [181], [60, 61] })
            await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(fixture.Source, fixture.Source.HeaderBounds, dividers));
        var draft = await extractor.ExtractAsync(fixture.Source, fixture.Source.HeaderBounds);
        var path = Path.Combine(fixture.Directory, "schema.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => GridSchemaExtractor.SaveAsync(draft, path));
        var reviewed = GridSchemaExtractor.Confirm(draft, ["A", "B", "C"]);
        await File.WriteAllTextAsync(fixture.Source.ImagePath!, "changed source");
        await Assert.ThrowsAsync<InvalidOperationException>(() => GridSchemaExtractor.SaveAsync(reviewed, path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FailedJoinCanReviewOriginalTileWithoutInventingAnAssembledImage()
    {
        using var fixture = new Fixture(GridCaptureStatus.Failed);
        var source = fixture.Source with { ImagePath = null };
        var retained = GridSchemaExtractor.PrepareReviewSource(source);
        Assert.Equal(source.Capture.Tiles[0].PngPath, retained.ImagePath);
        Assert.Same(source.Capture, retained.Capture);
        var draft = await Reader().ExtractAsync(retained, retained.HeaderBounds);
        Assert.Contains("schema-single-viewport", draft.Reasons);
        Assert.False(GridSchemaExtractor.Confirm(draft, ["A", "B", "C"]).Schema.IsComplete);
        await File.WriteAllTextAsync(retained.ImagePath!, "modified");
        Assert.Throws<InvalidOperationException>(() => GridSchemaExtractor.PrepareReviewSource(source));
    }

    [Fact]
    public async Task CancellationDoesNotReturnAPartiallyReadSchema()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ExtractAsync(fixture.Source,
            fixture.Source.HeaderBounds, cancellationToken: cancellation.Token));
    }

    private static GridSchemaExtractor Reader() => new(new TableCellOcrReader((_, _) => Task.FromResult(new LiteralOcrObservation(true, "Header", null))));

    internal sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "schema-review-test-" + Guid.NewGuid().ToString("N"));
        internal GridImageExplorationResult Source { get; }
        internal Fixture(GridCaptureStatus status = GridCaptureStatus.Complete, GridRestorationStatus restoration = GridRestorationStatus.Succeeded,
            bool stitchedArtifacts = false, bool ruledRows = false, bool rowSelector = false, bool footer = false, bool footerText = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            const int width = 180;
            var height = footer ? 130 : ruledRows ? 120 : 72;
            var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
            void Dark(int x, int y) { var offset = (y * width + x) * 4; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 40; }
            foreach (var x in new[] { 60, 120 }) for (var y = 0; y < height; y++) Dark(x, y);
            if (rowSelector) for (var y = 0; y < height; y++) Dark(8, y);
            if (ruledRows) foreach (var y in new[] { 24, 48, 72, 96 }) for (var x = 0; x < width; x++) Dark(x, y);
            if (footer) foreach (var y in new[] { 120, 128 }) for (var x = 0; x < width; x++) Dark(x, y);
            if (footerText) for (var x = 15; x < 25; x++) Dark(x, 124);
            if (stitchedArtifacts) for (var y = 0; y < 24; y++) Dark(25, y); // A tall header glyph, absent from the body.
            foreach (var y in new[] { 8, 32 }) foreach (var x in new[] { 12, 72, 132 }) { Dark(x, y); Dark(x + 1, y + 1); }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
            var path = Path.Combine(Directory, "table.png"); File.WriteAllBytes(path, png);
            var assembledPath = path;
            if (stitchedArtifacts)
            {
                for (var y = 0; y < height; y++) Dark(90, y); // A copied viewport border, absent from the original tile.
                var assembled = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                var assembledEncoder = new PngBitmapEncoder(); assembledEncoder.Frames.Add(BitmapFrame.Create(assembled));
                assembledPath = Path.Combine(Directory, "assembled.png"); using var output = File.Create(assembledPath); assembledEncoder.Save(output);
            }
            var now = DateTimeOffset.UtcNow; var image = new RectI(0, 0, width, height); var header = new RectI(0, 0, width, 24);
            var body = new RectI(0, 24, width, height - 24);
            var tile = new GridCapturedTile("tile", path, Convert.ToHexString(SHA256.HashData(png)), now, image, header, body, 0, 0, status != GridCaptureStatus.Failed, []);
            var capture = new CapturedGrid("acquisition", "grid", new("image", false, null, [], 0, 24), new(1, 1, 1, now, 1, image, image),
                now, now, status, GridAcquisitionStage.Capture, status == GridCaptureStatus.Complete ? [] : ["tile-limit"],
                [tile], [], [], new(true, true, status == GridCaptureStatus.Complete, status == GridCaptureStatus.Complete, true, false, false, [body], []),
                new(restoration, "fixture", restoration == GridRestorationStatus.Succeeded, restoration == GridRestorationStatus.Succeeded), Path.Combine(Directory, "manifest.json"));
            Source = new(capture, assembledPath, image, header, capture.Reasons);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

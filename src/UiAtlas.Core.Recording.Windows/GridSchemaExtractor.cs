using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Offline header extraction and review. Never scrolls or reads business cells.</summary>
public sealed class GridSchemaExtractor
{
    private readonly TableCellOcrReader _reader;
    private readonly IGridSchemaHeaderReader? _headerReader;
    public GridSchemaExtractor() : this(new TableCellOcrReader()) { }
    internal GridSchemaExtractor(TableCellOcrReader reader) => _reader = reader;
    public GridSchemaExtractor(IGridSchemaHeaderReader headerReader) : this() => _headerReader = headerReader;

    public static GridImageExplorationResult PrepareReviewSource(GridImageExplorationResult source)
    {
        if (source.ImagePath is not null) return source;
        var tile = source.Capture.Tiles.FirstOrDefault() ?? throw new InvalidOperationException("schema-image-unavailable");
        var png = File.ReadAllBytes(tile.PngPath);
        if (!Convert.ToHexString(SHA256.HashData(png)).Equals(tile.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("source-image-hash-mismatch");
        return source with { ImagePath = tile.PngPath, TableBounds = new(0, 0, tile.ScreenshotBounds.Width, tile.ScreenshotBounds.Height),
            HeaderBounds = tile.HeaderBounds, Reasons = source.Reasons.Append("schema-single-viewport").ToArray() };
    }

    public async Task<GridSchemaReview> ExtractAsync(GridImageExplorationResult source, RectI header,
        IReadOnlyList<int>? dividers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ImagePath is null) throw new InvalidOperationException("schema-image-unavailable");
        var png = await File.ReadAllBytesAsync(source.ImagePath, cancellationToken).ConfigureAwait(false);
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(png);
        if ((long)frame.Width * frame.Height > 32_000_000 ||
            !TableCellOcrReader.Contains(frame, header) || header.Width < 8 || header.Height < 4)
            throw new InvalidOperationException("schema-header-outside-image");
        var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        var rules = dividers ?? FindDividers(source, frame, header, cancellationToken);
        if (dividers is not null && (rules.Any(x => x <= header.X || x >= header.X + header.Width) || rules.Distinct().Count() != rules.Count))
            throw new InvalidOperationException("schema-dividers-invalid");
        var edges = new[] { header.X }.Concat(dividers is null ? rules.Where(x => x > header.X + 3 && x < header.X + header.Width - 3) : rules)
            .Append(header.X + header.Width).Distinct().Order().ToArray();
        if (edges.Length > 129 || edges.Zip(edges.Skip(1)).Any(pair => pair.Second - pair.First < 4))
            throw new InvalidOperationException("schema-dividers-invalid");
        var reasons = new List<string>();
        if (source.Capture.Status != GridCaptureStatus.Complete) reasons.Add("source-capture-incomplete");
        if (rules.Count == 0) reasons.Add("column-dividers-not-detected");
        var columns = new List<GridColumn>();
        var readings = new List<GridSchemaHeaderReading>();
        var singleViewport = source.Reasons.Contains("schema-single-viewport", StringComparer.Ordinal);
        if (singleViewport) reasons.Add("schema-single-viewport");
        var xOrigin = singleViewport ? source.Capture.Tiles[0].OffsetX - source.Capture.Tiles[0].BodyBounds.X :
            source.Capture.Tiles.Where(t => t.Accepted).Select(t => t.OffsetX).DefaultIfEmpty(0).Min();
        var completeWidth = !singleViewport && source.Capture.Coverage.LeftBoundary && source.Capture.Coverage.RightBoundary &&
            source.Capture.Coverage.Continuous && source.Capture.Coverage.Gaps.Count == 0 &&
            header.X == source.HeaderBounds.X && header.Width == source.HeaderBounds.Width;
        IReadOnlyList<GridHeaderText>? cloudReadings = null;
        if (_headerReader is not null)
        {
            var images = edges.Zip(edges.Skip(1)).Select(pair => new GridHeaderImage($"physical-x-{xOrigin + pair.First}",
                HeaderPng(frame, new(pair.First, header.Y, pair.Second - pair.First, header.Height)))).ToArray();
            cloudReadings = await _headerReader.ReadAsync(images, cancellationToken).ConfigureAwait(false);
            if (cloudReadings.Count != images.Length || !cloudReadings.Select(r => r.ColumnKey).SequenceEqual(images.Select(i => i.ColumnKey)))
                throw new AzureHeaderException("The header reader returned mismatched columns. No schema was accepted.");
        }
        for (var i = 0; i < edges.Length - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = new RectI(edges[i], header.Y, edges[i + 1] - edges[i], header.Height);
            // Identity is physical, never derived from potentially duplicate or unreadable labels.
            var key = $"physical-x-{xOrigin + edges[i]}";
            var clipped = i == 0 && !source.Capture.Coverage.LeftBoundary ||
                i == edges.Length - 2 && !source.Capture.Coverage.RightBoundary;
            var cellSource = new GridCellSource("schema-image", TableViewportDescriber.Inset(bounds), hash);
            var value = cloudReadings is null
                ? await _reader.ReadAsync(frame, key, cellSource, false, cancellationToken).ConfigureAwait(false)
                : new GridCellValue(key, cloudReadings[i].Status, cloudReadings[i].Text,
                    cloudReadings[i].Status == GridCellReadStatus.Unreadable ? "azure-header-unreadable" : null, [cellSource]);
            if (clipped) reasons.Add($"column-edge-unverified:{key}");
            columns.Add(new(key, i, value.Text ?? "", xOrigin + bounds.X, xOrigin + bounds.X + bounds.Width));
            readings.Add(new(key, bounds, value.Status, value.Text, value.Reason));
            if (value.Status != GridCellReadStatus.Text) reasons.Add($"header-needs-review:{key}");
        }
        // A reviewed header does not establish uniform body rows or qualify text extraction.
        var schema = new GridSchema("draft-" + hash[..16], false, null, columns, 0, header.Height);
        if (!completeWidth) reasons.Add("schema-width-unverified");
        return new(source.Capture.AcquisitionId, source.Capture.GridId, schema, header, source.ImagePath,
            hash, source.Capture.ManifestPath, source.Capture.Status, source.Capture.Coverage,
            source.Capture.Restoration, readings, reasons, HeaderReader: _headerReader?.Name ?? "windows-ocr",
            CaptureScope: source.Capture.Scope);
    }

    private static byte[] HeaderPng(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        // Crop only the header; retain its edge pixels so narrow text is not clipped by the local OCR inset.
        var crop = TableCellOcrReader.Crop(frame, bounds);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(crop.Width, crop.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, crop.Pixels, crop.Width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    public static GridSchemaReview Confirm(GridSchemaReview draft, IReadOnlyList<string> labels)
    {
        if (labels.Count != draft.Schema.Columns.Count || labels.Any(label => label is null))
            throw new ArgumentException("Every captured column must have a reviewed label (which may be blank).", nameof(labels));
        var columns = draft.Schema.Columns.Select((column, index) => column with { Label = labels[index] }).ToArray();
        var complete = !draft.Reasons.Contains("schema-width-unverified", StringComparer.Ordinal);
        var schema = draft.Schema with { Revision = "review-" + Guid.NewGuid().ToString("N"), Columns = columns,
            IsComplete = complete, KnownColumnCount = complete ? columns.Length : null };
        return draft with { Schema = schema, ReviewedUtc = DateTimeOffset.UtcNow };
    }

    private static IReadOnlyList<int> FindDividers(GridImageExplorationResult source,
        OpaqueSurfaceScanner.PixelFrame image, RectI header, CancellationToken cancellation)
    {
        var candidates = CorroboratedRules(image, header);
        if (source.Reasons.Contains("schema-single-viewport", StringComparer.Ordinal) ||
            header.Y != source.HeaderBounds.Y || header.Height != source.HeaderBounds.Height) return candidates;
        var tiles = source.Capture.Tiles.Where(tile => tile.Accepted).ToArray();
        if (tiles.Length == 0) return candidates;
        var xOrigin = tiles.Min(tile => tile.OffsetX);
        var supported = new List<int>();
        foreach (var tile in tiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(tile.PngPath);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(tile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("source-image-hash-mismatch");
            var frame = OpaqueSurfaceScanner.PixelFrame.Decode(bytes);
            if (!TableCellOcrReader.Contains(frame, tile.HeaderBounds) || !TableCellOcrReader.Contains(frame, tile.BodyBounds))
                throw new InvalidOperationException("source-image-geometry-mismatch");
            foreach (var rule in CorroboratedRules(frame, tile.HeaderBounds))
            {
                // A viewport border copied into a mosaic is not evidence of a column divider.
                if (rule <= tile.HeaderBounds.X + 3 || rule >= tile.HeaderBounds.X + tile.HeaderBounds.Width - 3) continue;
                supported.Add(rule - tile.HeaderBounds.X + tile.OffsetX - xOrigin);
            }
        }
        return candidates.Where(x => supported.Any(rule => Math.Abs(rule - x) <= 2)).ToArray();
    }

    private static IReadOnlyList<int> CorroboratedRules(OpaqueSurfaceScanner.PixelFrame frame, RectI header)
    {
        var headerRules = TableViewportDescriber.FindRules(frame, header, vertical: true);
        var below = new RectI(header.X, header.Y + header.Height, header.Width,
            Math.Min(Math.Max(64, header.Height * 3), frame.Height - header.Y - header.Height));
        if (below.Height < 8) return headerRules;
        var bodyRules = TableViewportDescriber.FindRules(frame, below, vertical: true);
        return headerRules.Where(x => bodyRules.Any(rule => Math.Abs(rule - x) <= 2)).ToArray();
    }

    public static async Task SaveAsync(GridSchemaReview review, string path, CancellationToken cancellationToken = default)
    {
        if (review.ReviewedUtc is null) throw new InvalidOperationException("schema-review-required");
        var bytes = await File.ReadAllBytesAsync(review.ImagePath, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(review.ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("schema-image-changed");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(review, JsonDefaults.Options), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

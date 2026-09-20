using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Literal transcription of fixed cells from a newly verified mosaic. Never drives the desktop.</summary>
public sealed class AzureOpenAiTableReader
{
    private readonly IGridSchemaHeaderReader _headers;
    private readonly Func<IReadOnlyList<GridHeaderImage>, CancellationToken, Task<IReadOnlyList<GridHeaderText>>> _cells;
    public AzureOpenAiTableReader(AzureHeaderSettings settings, string key)
    {
        var reader = new AzureOpenAiHeaderReader(settings, key);
        _headers = reader; _cells = reader.ReadCellsAsync;
    }
    internal AzureOpenAiTableReader(IGridSchemaHeaderReader headers,
        Func<IReadOnlyList<GridHeaderImage>, CancellationToken, Task<IReadOnlyList<GridHeaderText>>> cells)
    { _headers = headers; _cells = cells; }

    public async Task<GridReadResult> ExtractAsync(GridImageExplorationResult image, string id, string gridId,
        IProgress<AcquisitionProgress>? progress, CancellationToken cancellation, GridSchema? savedHint = null)
    {
        var start = DateTimeOffset.UtcNow;
        var capture = image.Capture;
        var rows = new List<GridRowData>();
        var reasons = new List<string>(capture.Reasons);
        var schema = capture.Schema;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            if (image.ImagePath is null) throw new InvalidOperationException("no-captured-table-image");
            if (!capture.Coverage.ColumnCoverageComplete) throw new InvalidOperationException("column-coverage-incomplete");
            var bytes = await File.ReadAllBytesAsync(image.ImagePath, timeout.Token).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var frame = OpaqueSurfaceScanner.PixelFrame.Decode(bytes);
            Report("Reading column headers with Azure", 0);
            var draft = await new GridSchemaExtractor(_headers).ExtractAsync(image, image.HeaderBounds,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            string? selector = null;
            if (savedHint?.Columns.FirstOrDefault() is { } savedFirst && draft.Schema.Columns.Count > 1)
            {
                var first = draft.Schema.Columns[0]; var next = draft.Schema.Columns[1];
                if (first.EndX - first.StartX <= 16 && draft.HeaderReadings[0].Status == GridCellReadStatus.Empty &&
                    next.StartX == savedFirst.StartX && next.Label == savedFirst.Label)
                {
                    selector = first.ColumnKey;
                    draft = draft with { Schema = draft.Schema with { Columns = draft.Schema.Columns.Skip(1).Select((c, i) => c with { Ordinal = i }).ToArray() } };
                }
            }
            if (draft.Reasons.Any(r => r.StartsWith("header-needs-review", StringComparison.Ordinal) && r != "header-needs-review:" + selector ||
                r is "schema-width-unverified" or "column-dividers-not-detected"))
                throw new InvalidOperationException("fresh-column-schema-unproven");
            var bodyLeft = selector is null ? image.HeaderBounds.X : (int)draft.Schema.Columns[0].StartX;
            var body = new RectI(bodyLeft, image.HeaderBounds.Y + image.HeaderBounds.Height,
                image.HeaderBounds.Width - bodyLeft, image.TableBounds.Height - image.HeaderBounds.Height);
            if (!TableCellOcrReader.Contains(frame, body)) throw new InvalidOperationException("body-region-invalid");
            var rules = TableViewportDescriber.FindRules(frame, body, false).ToList();
            var gaps = rules.Zip(rules.Skip(1), (a, b) => b - a).Where(g => g >= 4).Order().ToArray();
            if (gaps.Length >= 3 && rules[^1] - rules[^2] < gaps[gaps.Length / 2] - 2)
            {
                // A short terminal strip may be blank grid/footer chrome. Text there remains partial.
                if (HasInk(frame, body with { Y = rules[^2] + 2, Height = body.Y + body.Height - rules[^2] - 2 }))
                    reasons.Add("partial-terminal-row");
                rules.RemoveAt(rules.Count - 1);
            }
            var pitch = TableViewportDescriber.InferPitch(rules);
            if (pitch < 4) throw new InvalidOperationException("uniform-row-pitch-unproven");
            var regions = TableViewportDescriber.DescribeRows(body, rules, pitch);
            if (regions.Count == 0) throw new InvalidOperationException("no-rows-empty-table-unproven");
            if (regions.Any(r => r.IsPartial)) reasons.Add("partial-row-regions");
            if (regions.Count > 1000) reasons.Add("extraction-row-limit");
            if (draft.Schema.Columns.Count > 64) throw new InvalidOperationException("extraction-column-limit");
            schema = draft.Schema with { Revision = "azure-" + hash[..16], IsComplete = true,
                KnownColumnCount = draft.Schema.Columns.Count, RowHeight = pitch };
            var accepted = capture.Tiles.Where(t => t.Accepted).ToArray();
            var xOrigin = accepted.Min(t => t.OffsetX);
            var yOrigin = accepted.Min(t => t.OffsetY);
            var pending = new List<(string Key, int Row, GridColumn Column, IReadOnlyList<GridCellSource> Sources, byte[] Png)>();
            var readings = new Dictionary<string, GridCellValue>(StringComparer.Ordinal);
            foreach (var region in regions.Where(r => !r.IsPartial).Take(1000))
            foreach (var column in schema.Columns)
            {
                var bounds = new RectI((int)Math.Round(column.StartX) - xOrigin, region.Bounds.Y,
                    (int)Math.Round(column.EndX - column.StartX), region.Bounds.Height);
                // Remove rules only; keep original cell pixels and exact spelling.
                bounds = new(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
                if (!TableCellOcrReader.Contains(frame, bounds)) throw new InvalidOperationException("cell-region-outside-image");
                var key = $"r{region.LocalOrdinal}-c{column.Ordinal}";
                var content = bounds with { X = bounds.X + xOrigin, Y = bounds.Y - image.HeaderBounds.Height + yOrigin };
                var sources = new List<GridCellSource>();
                foreach (var tile in accepted)
                {
                    var left = Math.Max(content.X, tile.OffsetX); var top = Math.Max(content.Y, tile.OffsetY);
                    var right = Math.Min(content.X + content.Width, tile.OffsetX + tile.BodyBounds.Width);
                    var bottom = Math.Min(content.Y + content.Height, tile.OffsetY + tile.BodyBounds.Height);
                    if (right > left && bottom > top) sources.Add(new(tile.TileId,
                        new(tile.BodyBounds.X + left - tile.OffsetX, tile.BodyBounds.Y + top - tile.OffsetY,
                            right - left, bottom - top), tile.Sha256));
                }
                if (sources.Count == 0) throw new InvalidOperationException("cell-source-unproven");
                pending.Add((key, region.LocalOrdinal, column, sources, CropPng(frame, bounds)));
            }
            foreach (var batch in pending.Chunk(32))
            {
                timeout.Token.ThrowIfCancellationRequested();
                var values = await _cells(batch.Select(c => new GridHeaderImage(c.Key, c.Png)).ToArray(), timeout.Token).ConfigureAwait(false);
                if (values.Count != batch.Length || values.Select(v => v.ColumnKey).Distinct().Count() != values.Count ||
                    values.Any(v => !batch.Any(c => c.Key == v.ColumnKey))) throw new InvalidOperationException("azure-cell-cardinality-mismatch");
                foreach (var cell in batch)
                {
                    var value = values.Single(v => v.ColumnKey == cell.Key);
                    if (value.Status == GridCellReadStatus.Text && string.IsNullOrWhiteSpace(value.Text) ||
                        value.Status == GridCellReadStatus.Empty && value.Text != "" ||
                        value.Status == GridCellReadStatus.Unreadable && value.Text is not null)
                        throw new InvalidOperationException("azure-cell-value-invalid");
                    readings.Add(cell.Key, new(cell.Column.ColumnKey, value.Status, value.Text,
                        value.Status == GridCellReadStatus.Unreadable ? "azure-cell-unreadable" : null, cell.Sources));
                }
                Report($"Read {readings.Count} of {pending.Count} cells with Azure", readings.Count / schema.Columns.Count);
            }
            foreach (var region in regions.Where(r => !r.IsPartial).Take(1000))
            {
                var values = schema.Columns.Select(c => readings[$"r{region.LocalOrdinal}-c{c.Ordinal}"]).ToArray();
                if (values.Any(v => v.Status == GridCellReadStatus.Unreadable)) reasons.Add("unreadable-cells");
                rows.Add(new($"row-{region.LocalOrdinal}", region.LocalOrdinal, null, values));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or AzureHeaderException or IOException or ArgumentException)
        {
            reasons.Add(error is OperationCanceledException ? cancellation.IsCancellationRequested ? "extraction-cancelled" : "azure-extraction-time-limit" : error.Message);
        }
        var distinct = reasons.Distinct(StringComparer.Ordinal).ToArray();
        var hasData = rows.Any(r => r.Cells.Any(c => c.Status == GridCellReadStatus.Text));
        var complete = hasData && schema.IsComplete && distinct.Length == 0 && capture.Status == GridCaptureStatus.Complete &&
            capture.Scope == GridCaptureScope.FullTable && capture.Coverage.ColumnCoverageComplete && capture.Coverage.RowCoverageComplete;
        return new(id, gridId, capture.Source, schema, rows, complete ? GridDataStatus.Complete : hasData ? GridDataStatus.Partial : GridDataStatus.Failed,
            distinct, capture.Status, hasData && !rows.Any(r => r.Cells.Any(c => c.Status == GridCellReadStatus.Unreadable))
                ? GridExtractionStatus.Complete : hasData ? GridExtractionStatus.Partial : GridExtractionStatus.Failed,
            capture.Restoration, GridAcquisitionStage.Extract, capture.StartedUtc, capture.EndedUtc, start, DateTimeOffset.UtcNow,
            capture.ManifestPath, _headers.Name, capture.Coverage, capture.Tiles.Count, capture.Movements.Count);

        void Report(string message, int count) => progress?.Report(new(id, GridAcquisitionLifecycle.Running,
            GridAcquisitionStage.Extract, (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds, capture.Tiles.Count,
            count, capture.Movements.Count, message));
    }

    private static byte[] CropPng(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        var crop = TableCellOcrReader.Crop(frame, bounds);
        var bitmap = BitmapSource.Create(crop.Width, crop.Height, 96, 96, PixelFormats.Bgra32, null, crop.Pixels, crop.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    internal static bool HasInk(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            var dark = 0; var valid = 0;
            for (var x = bounds.X + 2; x < bounds.X + bounds.Width - 2; x++)
            {
                var p = (y * frame.Width + x) * 4;
                if (frame.Pixels[p + 3] == 0) continue;
                valid++;
                if (frame.Pixels[p] < 128 && frame.Pixels[p + 1] < 128 && frame.Pixels[p + 2] < 128) dark++;
            }
            if (dark > 2 && dark < valid * .5) return true;
        }
        return false;
    }
}

using System.Security.Cryptography;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Transcribes only accepted positioned rows. Does not capture, scroll, join, or infer values.</summary>
public sealed class TableGridReader
{
    private readonly TableCellOcrReader _reader;

    public TableGridReader() : this(new TableCellOcrReader()) { }
    internal TableGridReader(TableCellOcrReader reader) => _reader = reader;

    public async Task<GridReadResult> ExtractAsync(CapturedGrid capture,
        GridExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        options ??= new(RecordIdColumnKey: capture.Schema.RecordIdColumnKey,
            UniformBlankCellsQualified: capture.Schema.UniformBlankCellsQualified);
        var started = DateTimeOffset.UtcNow;
        var reasons = new List<string>();
        var rows = new List<GridRowData>();
        if (options.MaxRows is < 1 or > 200 || options.MaxColumns is < 1 or > 32 ||
            options.MaxDurationMs is < 1 or > 30_000 || options.RowBatchSize is < 1 or > 32)
            return Result(capture, rows, ["invalid-extraction-limits"], started);
        var schemaError = ValidateSchema(capture.Schema, options.MaxColumns);
        if (schemaError is not null) return Result(capture, rows, [schemaError], started);
        if (options.RecordIdColumnKey is not null && !capture.Schema.Columns.Any(c => c.ColumnKey == options.RecordIdColumnKey))
            return Result(capture, rows, ["record-id-column-not-in-schema"], started);
        if (capture.Tiles.Select(tile => tile.TileId).Distinct(StringComparer.Ordinal).Count() != capture.Tiles.Count ||
            capture.Tiles.Any(tile => string.IsNullOrWhiteSpace(tile.TileId)))
            return Result(capture, rows, ["duplicate-or-missing-source-tile-id"], started);

        var accepted = capture.Tiles.Where(tile => tile.Accepted).ToArray();
        var connected = ConnectedTiles(accepted, capture.Joins);
        if (connected.Count != accepted.Length)
            return Result(capture, rows, ["accepted-tiles-have-unverified-joins"], started);
        var regions = accepted.SelectMany(tile => tile.Rows.Where(row => row.IsComplete).Select(row => (Tile: tile, Row: row))).ToArray();
        if (regions.GroupBy(item => (item.Tile.TileId, item.Row.RowOccurrenceId)).Any(group => group.Count() > 1))
            return Result(capture, rows, ["duplicate-row-source-region"], started);
        if (regions.Any(item => string.IsNullOrWhiteSpace(item.Row.RowOccurrenceId) || item.Row.RowIndex < 0) ||
            regions.GroupBy(item => item.Row.RowOccurrenceId, StringComparer.Ordinal).Any(group => group.Select(item => item.Row.RowIndex).Distinct().Count() != 1) ||
            regions.GroupBy(item => item.Row.RowIndex).Any(group => group.Select(item => item.Row.RowOccurrenceId).Distinct(StringComparer.Ordinal).Count() != 1))
            return Result(capture, rows, ["row-occurrence-identity-conflict"], started);
        var groups = regions.GroupBy(item => item.Row.RowOccurrenceId, StringComparer.Ordinal)
            .OrderBy(group => group.First().Row.RowIndex).ToArray();
        if (groups.Length > options.MaxRows) reasons.Add("extraction-row-limit");
        if (groups.Length == 0) reasons.Add("no-complete-rows-empty-table-unproven");
        if (!capture.Schema.IsComplete) reasons.Add("schema-incomplete");
        var indexes = groups.Select(group => group.First().Row.RowIndex).ToArray();
        if (indexes.Length > 0 && (indexes[0] != 0 || indexes.Zip(indexes.Skip(1)).Any(pair => pair.Second != pair.First + 1)))
            reasons.Add("missing-row-occurrences");

        using var deadline = new CancellationTokenSource(options.MaxDurationMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var frames = new Dictionary<string, OpaqueSurfaceScanner.PixelFrame>(StringComparer.Ordinal);
        var failedFrames = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var batch in groups.Take(options.MaxRows).Chunk(options.RowBatchSize))
            {
                linked.Token.ThrowIfCancellationRequested();
                foreach (var group in batch)
                {
                    var cells = new List<GridCellValue>();
                    foreach (var column in capture.Schema.Columns.OrderBy(column => column.Ordinal))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        var readings = new List<GridCellValue>();
                        foreach (var item in group)
                        {
                            if (!ValidRow(item.Tile, item.Row, capture.Schema.RowHeight))
                            {
                                reasons.Add($"row-region-invalid:{item.Tile.TileId}:{item.Row.RowOccurrenceId}");
                                continue;
                            }
                            var left = column.StartX - item.Tile.OffsetX + item.Tile.BodyBounds.X;
                            var right = column.EndX - item.Tile.OffsetX + item.Tile.BodyBounds.X;
                            // Partial edge columns are covered only by a later accepted image.
                            if (left < item.Tile.BodyBounds.X || right > item.Tile.BodyBounds.X + item.Tile.BodyBounds.Width) continue;
                            var bounds = TableViewportDescriber.Inset(new((int)Math.Round(left), item.Row.Bounds.Y,
                                (int)Math.Round(right - left), item.Row.Bounds.Height));
                            var source = new GridCellSource(item.Tile.TileId, bounds, item.Tile.Sha256);
                            if (!frames.TryGetValue(item.Tile.TileId, out var frame) && !failedFrames.Contains(item.Tile.TileId))
                            {
                                try
                                {
                                    var info = new FileInfo(item.Tile.PngPath);
                                    if (!info.Exists || info.Length is <= 0 or > 64L * 1024 * 1024)
                                        throw new InvalidDataException("source-file-missing-or-too-large");
                                    var bytes = await File.ReadAllBytesAsync(item.Tile.PngPath, linked.Token).ConfigureAwait(false);
                                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), item.Tile.Sha256, StringComparison.OrdinalIgnoreCase))
                                        throw new InvalidDataException("source-sha256-mismatch");
                                    frame = OpaqueSurfaceScanner.PixelFrame.Decode(bytes);
                                    if (frame.Width != item.Tile.ScreenshotBounds.Width || frame.Height != item.Tile.ScreenshotBounds.Height)
                                        throw new InvalidDataException("source-dimensions-mismatch");
                                    frames.Add(item.Tile.TileId, frame);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception exception)
                                {
                                    failedFrames.Add(item.Tile.TileId);
                                    reasons.Add($"source-unreadable:{item.Tile.TileId}:{exception.GetType().Name}:{exception.Message}");
                                }
                            }
                            if (frame is null)
                                readings.Add(TableCellOcrReader.Unreadable(column.ColumnKey, source, "source-unreadable"));
                            else
                                readings.Add(await _reader.ReadAsync(frame, column.ColumnKey, source,
                                    options.UniformBlankCellsQualified, linked.Token).ConfigureAwait(false));
                        }
                        cells.Add(Combine(column.ColumnKey, readings));
                    }
                    var id = options.RecordIdColumnKey is null ? null :
                        cells.Single(cell => cell.ColumnKey == options.RecordIdColumnKey) is { Status: GridCellReadStatus.Text } idCell
                            ? idCell.Text : null;
                    if (options.RecordIdColumnKey is not null && id is null) reasons.Add($"record-id-unreadable:{group.Key}");
                    rows.Add(new(group.Key, group.First().Row.RowIndex, id, cells));
                }
            }
        }
        catch (OperationCanceledException)
        {
            reasons.Add(cancellationToken.IsCancellationRequested ? "extraction-cancelled" : "extraction-time-limit");
        }
        if (rows.Any(row => row.Cells.Any(cell => cell.Status == GridCellReadStatus.Unreadable)))
            reasons.Add("unreadable-or-unassigned-cells");
        if (rows.Where(row => row.RecordId is not null).GroupBy(row => row.RecordId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            reasons.Add("duplicate-record-id-retained");
        return Result(capture, rows, reasons, started);
    }

    internal static GridCellValue Combine(string key, IReadOnlyList<GridCellValue> observations)
    {
        var sources = observations.SelectMany(value => value.Sources).Distinct().ToArray();
        var readable = observations.Where(value => value.Status != GridCellReadStatus.Unreadable).ToArray();
        if (readable.Length == 0)
            return new(key, GridCellReadStatus.Unreadable, null,
                observations.Count == 0 ? "missing-cell-source" : string.Join(';', observations.Select(value => value.Reason).Distinct()), sources);
        if (readable.Select(value => (value.Status, value.Text)).Distinct().Count() != 1)
            return new(key, GridCellReadStatus.Unreadable, null, "conflicting-accepted-cell-readings", sources);
        return readable[0] with { Sources = sources };
    }

    private static HashSet<string> ConnectedTiles(GridCapturedTile[] tiles, IReadOnlyList<GridJoinEvidence> joins)
    {
        var connected = new HashSet<string>(StringComparer.Ordinal);
        if (tiles.Length == 0) return connected;
        connected.Add(tiles[0].TileId);
        var byId = tiles.ToDictionary(tile => tile.TileId, StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var join in joins.Where(join => join.Accepted && join.CompatibleCandidates == 1 && join.ComparedPixels > 0 &&
                         double.IsFinite(join.MatchFraction) && join.MatchFraction is > 0 and <= 1))
                if (byId.TryGetValue(join.FromTileId, out var from) && byId.TryGetValue(join.ToTileId, out var to) &&
                    (long)to.OffsetX - from.OffsetX == join.DisplacementX &&
                    (long)to.OffsetY - from.OffsetY == join.DisplacementY && connected.Contains(join.FromTileId))
                    changed |= connected.Add(join.ToTileId);
        }
        return connected;
    }

    private static bool ValidRow(GridCapturedTile tile, GridCapturedRowRegion row, double pitch) =>
        row.Bounds.X >= tile.BodyBounds.X && row.Bounds.Y >= tile.BodyBounds.Y &&
        (long)row.Bounds.X + row.Bounds.Width <= (long)tile.BodyBounds.X + tile.BodyBounds.Width &&
        (long)row.Bounds.Y + row.Bounds.Height <= (long)tile.BodyBounds.Y + tile.BodyBounds.Height &&
        Math.Abs(row.Bounds.Height - pitch) <= 2 &&
        Math.Abs(tile.OffsetY + row.Bounds.Y - tile.BodyBounds.Y - row.RowIndex * pitch) <= 2;

    private static string? ValidateSchema(GridSchema schema, int maxColumns)
    {
        if (string.IsNullOrWhiteSpace(schema.Revision) || schema.Layout != "rectangular-single-header-uniform-rows" ||
            !double.IsFinite(schema.RowHeight) || schema.RowHeight < 4 ||
            !double.IsFinite(schema.HeaderHeight) || schema.HeaderHeight <= 0 || schema.Columns.Count == 0)
            return "schema-geometry-invalid";
        if (schema.Columns.Count > maxColumns) return "extraction-column-limit";
        if (schema.IsComplete && schema.KnownColumnCount != schema.Columns.Count) return "schema-column-count-mismatch";
        if (schema.Columns.Select(column => column.ColumnKey).Distinct(StringComparer.Ordinal).Count() != schema.Columns.Count ||
            schema.Columns.Any(column => string.IsNullOrWhiteSpace(column.ColumnKey))) return "schema-column-key-conflict";
        var ordered = schema.Columns.OrderBy(column => column.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var column = ordered[index];
            if (column.Ordinal != index || !double.IsFinite(column.StartX) || !double.IsFinite(column.EndX) ||
                column.StartX < 0 || column.EndX - column.StartX < 3 ||
                (index > 0 && Math.Abs(ordered[index - 1].EndX - column.StartX) > 1))
                return "schema-column-interval-invalid";
        }
        return null;
    }

    private static GridReadResult Result(CapturedGrid capture, IReadOnlyList<GridRowData> rows,
        IReadOnlyList<string> extractionReasons, DateTimeOffset started)
    {
        var hasData = rows.Any(row => row.Cells.Any(cell => cell.Status != GridCellReadStatus.Unreadable));
        var extractionComplete = extractionReasons.Count == 0 && rows.Count > 0;
        var extraction = extractionComplete ? GridExtractionStatus.Complete : hasData ? GridExtractionStatus.Partial : GridExtractionStatus.Failed;
        var complete = extractionComplete && capture.Status == GridCaptureStatus.Complete && capture.Schema.IsComplete &&
            capture.Coverage.Continuous && capture.Coverage.LeftBoundary && capture.Coverage.RightBoundary &&
            capture.Coverage.TopBoundary && capture.Coverage.BottomBoundary && capture.Coverage.Gaps.Count == 0;
        var reasons = capture.Reasons.Concat(extractionReasons).Distinct(StringComparer.Ordinal).ToList();
        if (!complete && extractionComplete) reasons.Add("capture-coverage-incomplete");
        return new(capture.AcquisitionId, capture.GridId, capture.Source, capture.Schema, rows,
            complete ? GridDataStatus.Complete : hasData ? GridDataStatus.Partial : GridDataStatus.Failed,
            reasons, capture.Status, extraction, capture.Restoration,
            capture.Status != GridCaptureStatus.Complete ? capture.Stage : GridAcquisitionStage.Extract,
            capture.StartedUtc, capture.EndedUtc, started, DateTimeOffset.UtcNow, capture.ManifestPath,
            "windows-ocr", capture.Coverage, capture.Tiles.Count, capture.Movements.Count);
    }
}

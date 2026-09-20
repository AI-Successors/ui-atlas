using System.Security.Cryptography;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Narrow rectangular ruled-table interpretation. Never scrolls or assigns body text.</summary>
public sealed class TableViewportDescriber
{
    private readonly TableCellOcrReader _reader;

    public TableViewportDescriber() : this(new TableCellOcrReader()) { }
    internal TableViewportDescriber(TableCellOcrReader reader) => _reader = reader;

    public async Task<GridViewportDescription> DescribeAsync(
        GridViewportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var reasons = new List<string>();
        OpaqueSurfaceScanner.PixelFrame frame;
        try { frame = OpaqueSurfaceScanner.PixelFrame.Decode(request.Png); }
        catch (Exception exception) { return Failed(request, $"viewport-png-invalid:{exception.GetType().Name}"); }
        if (!TableCellOcrReader.Contains(frame, request.HeaderBounds) ||
            !TableCellOcrReader.Contains(frame, request.BodyBounds) ||
            request.HeaderBounds.Y + request.HeaderBounds.Height > request.BodyBounds.Y ||
            request.HeaderBounds.X != request.BodyBounds.X ||
            request.HeaderBounds.Width != request.BodyBounds.Width ||
            request.ScreenshotBounds.Width != frame.Width || request.ScreenshotBounds.Height != frame.Height)
            return Failed(request, "viewport-region-geometry-invalid");
        if (request.Schema.Layout != "rectangular-single-header-uniform-rows")
            return Failed(request, "unsupported-table-layout");

        var vertical = FindRules(frame, request.HeaderBounds, vertical: true);
        var horizontal = FindRules(frame, request.BodyBounds, vertical: false);
        var pitch = request.Schema.RowHeight > 0 ? request.Schema.RowHeight : InferPitch(horizontal);
        if (!double.IsFinite(pitch) || pitch < 4 || pitch > request.BodyBounds.Height)
            return Failed(request, "uniform-row-pitch-unproven");
        if (horizontal.Count < 2 || !UniformRules(horizontal, pitch))
            reasons.Add("row-separators-unproven-or-nonuniform");
        if (request.Schema.HeaderHeight > 0 && Math.Abs(request.Schema.HeaderHeight - request.HeaderBounds.Height) > 2)
            reasons.Add("header-height-changed");

        var rows = DescribeRows(request.BodyBounds, horizontal, pitch);
        var columns = new List<GridViewportColumn>();
        var fragment = new List<GridColumn>();
        var expectedColumns = request.Schema.Columns.Count > 0;
        var intervals = expectedColumns
            ? request.Schema.Columns.Select(column => (column.ColumnKey, column.Ordinal, column.Label,
                Left: column.StartX + request.BodyBounds.X - request.OffsetX,
                Right: column.EndX + request.BodyBounds.X - request.OffsetX)).ToArray()
            : BuildIntervals(request, vertical);
        if (intervals.Length == 0) reasons.Add("physical-columns-unproven");
        var hash = Convert.ToHexString(SHA256.HashData(request.Png)).ToLowerInvariant();
        foreach (var interval in intervals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = (int)Math.Round(interval.Left);
            var right = (int)Math.Round(interval.Right);
            var visibleLeft = Math.Max(left, request.HeaderBounds.X);
            var visibleRight = Math.Min(right, request.HeaderBounds.X + request.HeaderBounds.Width);
            if (visibleRight <= visibleLeft) continue;
            var partial = left < visibleLeft || right > visibleRight ||
                (!expectedColumns &&
                 ((left <= request.HeaderBounds.X + 2 && !NearRule(vertical, left)) ||
                  (right >= request.HeaderBounds.X + request.HeaderBounds.Width - 2 && !NearRule(vertical, right))));
            if (!partial && expectedColumns)
            {
                if (left > request.HeaderBounds.X + 2 && !NearRule(vertical, left))
                    reasons.Add($"column-start-changed:{interval.ColumnKey}");
                if (right < request.HeaderBounds.X + request.HeaderBounds.Width - 2 && !NearRule(vertical, right))
                    reasons.Add($"column-end-changed:{interval.ColumnKey}");
            }
            var header = new RectI(visibleLeft, request.HeaderBounds.Y,
                visibleRight - visibleLeft, request.HeaderBounds.Height);
            var body = new RectI(visibleLeft, request.BodyBounds.Y,
                visibleRight - visibleLeft, request.BodyBounds.Height);
            GridCellValue label;
            if (partial)
                label = TableCellOcrReader.Unreadable(interval.ColumnKey,
                    new("viewport", header, hash), "partial-header");
            else
                label = await _reader.ReadAsync(frame, interval.ColumnKey,
                    new("viewport", new(header.X + 2, header.Y + 2,
                        Math.Max(0, header.Width - 4), Math.Max(0, header.Height - 4)), hash), false, cancellationToken).ConfigureAwait(false);
            if (!partial && label.Status != GridCellReadStatus.Text)
                reasons.Add($"header-unreadable:{interval.ColumnKey}");
            if (expectedColumns && request.VerifyHeaderLabels && !partial &&
                !string.Equals(label.Text, interval.Label, StringComparison.Ordinal))
                reasons.Add($"header-label-changed:{interval.ColumnKey}");
            columns.Add(new(interval.ColumnKey, interval.Ordinal, header, body, partial, label.Text, label.Status));
            fragment.Add(new(interval.ColumnKey, interval.Ordinal, expectedColumns ? interval.Label : label.Text ?? "",
                interval.Left - request.BodyBounds.X + request.OffsetX,
                interval.Right - request.BodyBounds.X + request.OffsetX));
        }
        if (columns.Count == 0) reasons.Add("no-visible-schema-columns");
        else if (columns.All(column => column.IsPartial)) reasons.Add("no-complete-physical-columns");
        var schema = request.Schema with
        {
            Columns = fragment,
            IsComplete = false,
            KnownColumnCount = null,
            RowHeight = pitch,
            HeaderHeight = request.HeaderBounds.Height,
            RecordIdColumnKey = fragment.Any(column => column.ColumnKey == request.Schema.RecordIdColumnKey)
                ? request.Schema.RecordIdColumnKey : null
        };
        var verified = reasons.Count == 0;
        return new(schema, columns, rows, pitch, verified, verified && request.LocalOcrQualified,
            "windows-ocr", request.NativeProbe, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<int> FindRules(OpaqueSurfaceScanner.PixelFrame frame, RectI region, bool vertical)
    {
        var start = vertical ? region.X : region.Y;
        var length = vertical ? region.Width : region.Height;
        var crossStart = vertical ? region.Y : region.X;
        var crossLength = vertical ? region.Height : region.Width;
        var candidates = new List<int>();
        for (var axis = start + 1; axis < start + length - 1; axis++)
        {
            var matching = 0;
            var sampled = 0;
            for (var cross = crossStart + 2; cross < crossStart + crossLength - 2; cross++)
            {
                var x = vertical ? axis : cross;
                var y = vertical ? cross : axis;
                var beforeX = vertical ? axis - 1 : cross;
                var beforeY = vertical ? cross : axis - 1;
                var afterX = vertical ? axis + 1 : cross;
                var afterY = vertical ? cross : axis + 1;
                var current = Luminance(frame, x, y);
                if (Math.Abs(current - Luminance(frame, beforeX, beforeY)) >= 18 ||
                    Math.Abs(current - Luminance(frame, afterX, afterY)) >= 18) matching++;
                sampled++;
            }
            if (sampled > 0 && matching >= sampled * .75) candidates.Add(axis);
        }
        var rules = new List<int>();
        for (var index = 0; index < candidates.Count;)
        {
            var first = candidates[index];
            var last = first;
            while (++index < candidates.Count && candidates[index] <= last + 1) last = candidates[index];
            rules.Add((first + last) / 2);
        }
        return rules;
    }

    private static (string ColumnKey, int Ordinal, string Label, double Left, double Right)[] BuildIntervals(
        GridViewportRequest request, IReadOnlyList<int> rules)
    {
        if (rules.Count == 0) return [];
        var boundaries = new[] { request.HeaderBounds.X }.Concat(rules)
            .Append(request.HeaderBounds.X + request.HeaderBounds.Width).Distinct().Order().ToArray();
        return boundaries.Zip(boundaries.Skip(1)).Where(pair => pair.Second - pair.First >= 8)
            .Select((pair, index) => ($"physical-x-{pair.First - request.BodyBounds.X + request.OffsetX}",
                index, "", (double)pair.First, (double)pair.Second)).ToArray();
    }

    internal static IReadOnlyList<GridViewportRow> DescribeRows(RectI body, IReadOnlyList<int> rules, double pitch)
    {
        var rows = new List<GridViewportRow>();
        if (rules.Count < 2) return rows;
        var boundaries = rules.ToList();
        if (rules[0] > body.Y + 1 && rules[0] - body.Y <= pitch + 2) boundaries.Insert(0, body.Y);
        var bottom = body.Y + body.Height;
        // An unruled remainder can be empty canvas. Never fabricate additional rows
        // by extrapolating the row pitch through it.
        // Rule detection returns the centre of a shared border; a one/two-pixel
        // remainder is that border, not a truncated business row.
        if (bottom - rules[^1] > 2 && bottom - rules[^1] <= pitch + 2) boundaries.Add(bottom);
        foreach (var pair in boundaries.Zip(boundaries.Skip(1)))
        {
            var start = Math.Max(body.Y, pair.First);
            var end = Math.Min(bottom, pair.Second);
            if (end <= start || end - start > pitch + 2) continue;
            rows.Add(new(rows.Count, new(body.X, start, body.Width, end - start),
                Math.Abs(end - start - pitch) > 2));
        }
        return rows;
    }

    internal static RectI Inset(RectI bounds) => new(bounds.X + 2, bounds.Y + 2,
        Math.Max(0, bounds.Width - 4), Math.Max(0, bounds.Height - 4));

    internal static double InferPitch(IReadOnlyList<int> rules)
    {
        var gaps = rules.Zip(rules.Skip(1), (first, second) => second - first).Where(gap => gap >= 4).Order().ToArray();
        if (gaps.Length < 2) return 0;
        var median = gaps[gaps.Length / 2];
        return gaps.All(gap => Math.Abs(gap - median) <= 2) ? gaps.Average() : 0;
    }

    private static bool UniformRules(IReadOnlyList<int> rules, double pitch) =>
        rules.Zip(rules.Skip(1), (first, second) => Math.Abs(second - first - pitch) <= 2).All(value => value);

    private static bool NearRule(IReadOnlyList<int> rules, int coordinate) => rules.Any(rule => Math.Abs(rule - coordinate) <= 2);

    private static int Luminance(OpaqueSurfaceScanner.PixelFrame frame, int x, int y)
    {
        var offset = (y * frame.Width + x) * 4;
        return (frame.Pixels[offset] * 29 + frame.Pixels[offset + 1] * 150 + frame.Pixels[offset + 2] * 77) >> 8;
    }

    private static GridViewportDescription Failed(GridViewportRequest request, string reason) =>
        new(request.Schema with { IsComplete = false }, [], [], 0, false, false,
            "windows-ocr", request.NativeProbe, [reason]);
}

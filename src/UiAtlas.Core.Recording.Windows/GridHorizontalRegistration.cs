namespace UiAtlas.Core.Recording.Windows;

/// <summary>Horizontal image alignment using shared header and body detail, including scaled text.</summary>
internal static class GridHorizontalRegistration
{
    internal static GridRegistrationResult Register(GridPixelBuffer beforeBody, GridPixelBuffer afterBody,
        GridPixelBuffer beforeHeader, GridPixelBuffer afterHeader, int direction,
        CancellationToken cancellationToken = default, int excludedLeadingBodyPixels = 0,
        (int Start, int End)? excludedBefore = null, (int Start, int End)? excludedAfter = null, int excludedTrailingBodyPixels = 0)
    {
        if (direction is not (-1 or 1)) return Reject("invalid-direction");
        if (!SameSize(beforeBody, afterBody) || !SameSize(beforeHeader, afterHeader) ||
            beforeBody.Width != beforeHeader.Width) return Reject("geometry-changed");
        if (DataGridRegistration.SameAnchor(beforeBody, afterBody) &&
            DataGridRegistration.SameAnchor(beforeHeader, afterHeader))
            return new(false, true, 0, 0, 0, beforeBody.Gray.Length, 1, "unchanged-pixels");

        var fixedLeft = FixedLeadingBand(beforeBody, afterBody, beforeHeader, afterHeader);
        var bodyLeft = Math.Max(fixedLeft, excludedLeadingBodyPixels);
        var body = (Before: new Plane(beforeBody, bodyLeft, excludedBefore, excludedTrailingBodyPixels), After: new Plane(afterBody, bodyLeft, excludedAfter, excludedTrailingBodyPixels));
        var header = (Before: new Plane(beforeHeader, fixedLeft), After: new Plane(afterHeader, fixedLeft));
        var maximum = beforeBody.Width - Math.Max(24, beforeBody.Width / 5);
        var candidates = new List<(double Shift, Comparison Score)>();
        for (var distance = 1; distance <= maximum; distance++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shift = distance * direction;
            var h = Compare(header.Before, header.After, shift, sampled: true);
            var b = Compare(body.Before, body.After, shift, sampled: true);
            if ((h + b).MeanError > 16 || (h + b).Count < 64) continue;
            // Sampling only shortlists offsets. Acceptance checks every informative pixel
            // from both observations, so new or changed content cannot hide in a blank area.
            for (var phase = -.5; phase <= .5; phase += .25)
            {
                var measured = shift + phase;
                if (measured * direction < 1 || Math.Abs(measured) > maximum) continue;
                h = Compare(header.Before, header.After, measured, sampled: true);
                b = Compare(body.Before, body.After, measured, sampled: true);
                if (!Compatible(h, b)) continue;
                h = Compare(header.Before, header.After, measured, sampled: false);
                b = Compare(body.Before, body.After, measured, sampled: false);
                if (Compatible(h, b)) candidates.Add((measured, h + b));
            }
        }
        if (candidates.Count == 0) return Reject("no-compatible-content-overlap");
        var best = candidates.OrderBy(c => c.Score.MeanError).First();
        // Neighbouring offsets can describe the same fractional-pixel raster phase.
        // Separate peaks remain ambiguous, even when one contains more repeated rows.
        if (candidates.Any(c => Math.Abs(c.Shift - best.Shift) > 2))
            return new(false, false, 0, 0, candidates.Count, best.Score.Count, best.Score.Fraction, "ambiguous-repeated-content");
        return new(true, false, (int)Math.Round(best.Shift), 0, 1, best.Score.Count, best.Score.Fraction,
            "unique-scaled-horizontal-overlap", best.Shift);
    }

    private static bool Compatible(Comparison header, Comparison body) =>
        (header + body) is { Count: >= 96, SpanX: >= 24, SpanY: >= 6 } total &&
        total.Fraction >= .95 && total.MeanError <= 7 &&
        (header.Count < 24 || header.Fraction >= .90 && header.MeanError <= 9) &&
        (body.Count < 24 || body.Fraction >= .90 && body.MeanError <= 9);

    private static Comparison Compare(Plane before, Plane after, double shift, bool sampled)
    {
        var count = 0; var matches = 0; long error = 0;
        var minX = int.MaxValue; var maxX = int.MinValue;
        var minY = int.MaxValue; var maxY = int.MinValue;
        Add(after, before, shift, false);
        Add(before, after, -shift, true);
        return new(count, matches, error, count == 0 ? 0 : maxX - minX, count == 0 ? 0 : maxY - minY);

        void Add(Plane source, Plane destination, double delta, bool reverse)
        {
            var stride = sampled ? Math.Max(1, source.Features.Count / 512) : 1;
            for (var i = 0; i < source.Features.Count; i += stride)
            {
                var point = source.Features[i];
                var x = point % source.Width;
                var y = point / source.Width;
                var otherX = x + delta;
                if (otherX < destination.Left + 2 || otherX >= destination.Right - 2) continue;
                var floor = (int)Math.Floor(otherX);
                if (destination.Excluded(floor) || destination.Excluded(floor + 1)) continue;
                var fraction = otherX - floor;
                var otherValue = destination.Gray[y * destination.Width + floor] * (1 - fraction) +
                    destination.Gray[y * destination.Width + floor + 1] * fraction;
                var difference = (int)Math.Round(Math.Abs(source.Gray[point] - otherValue));
                count++; error += difference;
                if (difference <= 18) matches++;
                var alignedX = reverse ? (int)Math.Round(otherX) : x;
                minX = Math.Min(minX, alignedX); maxX = Math.Max(maxX, alignedX);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
    }

    private sealed class Plane
    {
        internal int Width { get; }
        internal int Left { get; }
        internal int Right { get; }
        internal byte[] Gray { get; }
        internal List<int> Features { get; } = [];
        private readonly (int Start, int End)? _excluded;
        internal bool Excluded(int x) => _excluded is { } band && x >= band.Start - 2 && x < band.End + 2;
        internal Plane(GridPixelBuffer pixels, int left, (int Start, int End)? excluded = null, int trailing = 0)
        {
            _excluded = excluded;
            Width = pixels.Width;
            Left = left;
            Right = pixels.Width - trailing;
            Gray = new byte[pixels.Gray.Length];
            // A small comparison-only filter suppresses antialiasing phase differences.
            // Original PNG pixels are never rescaled or rewritten for the saved panorama.
            for (var y = 1; y < pixels.Height - 1; y++)
            for (var x = 1; x < Width - 1; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++) sum += pixels[x + dx, y + dy];
                Gray[y * Width + x] = (byte)((sum + 4) / 9);
            }
            for (var y = 2; y < pixels.Height - 2; y++)
            for (var x = left + 2; x < Right - 2; x++)
            {
                var p = y * Width + x;
                if (Excluded(x)) continue;
                // Flat fills and rules alone do not prove displacement.
                if (Math.Abs(Gray[p + 1] - Gray[p - 1]) > 12 &&
                    Math.Abs(Gray[p + Width] - Gray[p - Width]) > 12) Features.Add(p);
            }
        }
    }

    private static int FixedLeadingBand(GridPixelBuffer body, GridPixelBuffer nextBody,
        GridPixelBuffer header, GridPixelBuffer nextHeader)
    {
        // Ignore only a bounded strip proven stationary in BOTH observations. Retain
        // every original pixel in the output: first-observation overlap keeps this
        // strip once, without dropping a narrow or unlabelled business column.
        var width = 0;
        for (; width < Math.Min(32, body.Width / 10); width++)
        {
            if (!SameColumn(body, nextBody, width) || !SameColumn(header, nextHeader, width)) break;
        }
        return width >= 6 ? width : 0;

        static bool SameColumn(GridPixelBuffer a, GridPixelBuffer b, int x)
        {
            for (var y = 0; y < a.Height; y++) if (a[x, y] != b[x, y]) return false;
            return true;
        }
    }

    private readonly record struct Comparison(int Count, int Matches, long Error, int SpanX, int SpanY)
    {
        internal double Fraction => Count == 0 ? 0 : Matches / (double)Count;
        internal double MeanError => Count == 0 ? 0 : Error / (double)Count;
        public static Comparison operator +(Comparison a, Comparison b) =>
            new(a.Count + b.Count, a.Matches + b.Matches, a.Error + b.Error,
                Math.Max(a.SpanX, b.SpanX), Math.Max(a.SpanY, b.SpanY));
    }

    private static bool SameSize(GridPixelBuffer a, GridPixelBuffer b) => a.Width >= 5 && a.Height >= 5 &&
        a.Width == b.Width && a.Height == b.Height && (long)a.Width * a.Height == a.Gray.Length && a.Gray.Length == b.Gray.Length;
    private static GridRegistrationResult Reject(string reason) => new(false, false, 0, 0, 0, 0, 0, reason);
}

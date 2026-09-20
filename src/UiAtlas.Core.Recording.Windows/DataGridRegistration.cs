using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Unscaled body pixels. Headers and scrollbars are excluded by the caller.</summary>
internal sealed record GridPixelBuffer(int Width, int Height, byte[] Gray)
{
    public byte this[int x, int y] => Gray[y * Width + x];
}

internal sealed record GridRegistrationResult(
    bool Accepted, bool NoChange, int DisplacementX, int DisplacementY,
    int CompatibleCandidates, int ComparedPixels, double MatchFraction, string Reason,
    double? SubpixelDisplacementX = null, double? SubpixelDisplacementY = null);

/// <summary>
/// Conservative translation-only registration for the supported rectangular fixture.
/// Every qualifying displacement is retained: repeated patterns are ambiguity, not a score tie-break.
/// </summary>
internal static class DataGridRegistration
{
    internal static GridRegistrationResult Register(
        GridPixelBuffer before, GridPixelBuffer after, GridScrollAxis axis, int direction,
        int minimumOverlap = 24, CancellationToken cancellationToken = default)
    {
        if (!Valid(before) || !Valid(after) || before.Width != after.Width || before.Height != after.Height)
            return Reject("geometry-changed");
        if (direction is not (-1 or 1)) return Reject("invalid-direction");
        if (before.Gray.AsSpan().SequenceEqual(after.Gray))
            return new(false, true, 0, 0, 0, before.Gray.Length, 1, "unchanged-pixels");

        var extent = axis == GridScrollAxis.Horizontal ? before.Width : before.Height;
        var maxShift = extent - Math.Max(minimumOverlap, extent / 5);
        if (maxShift < 1) return Reject("insufficient-overlap-area");
        var candidates = new List<(int Shift, int Count, double Fraction)>();
        for (var magnitude = 1; magnitude <= maxShift; magnitude++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shift = magnitude * direction;
            var dx = axis == GridScrollAxis.Horizontal ? shift : 0;
            var dy = axis == GridScrollAxis.Vertical ? shift : 0;
            var comparison = Compare(before, after, dx, dy);
            if (comparison.Count >= 48 && comparison.Fraction >= .995 && comparison.Distributed)
                candidates.Add((shift, comparison.Count, comparison.Fraction));
        }

        if (candidates.Count != 1)
            return new(false, false, 0, 0, candidates.Count, 0, 0,
                candidates.Count == 0 ? "no-compatible-content-overlap" : "ambiguous-repeated-content");

        var accepted = candidates[0];
        return new(true, false,
            axis == GridScrollAxis.Horizontal ? accepted.Shift : 0,
            axis == GridScrollAxis.Vertical ? accepted.Shift : 0,
            1, accepted.Count, accepted.Fraction, "unique-content-translation");
    }

    // Placement convention: after[x,y] corresponds to before[x+dx,y+dy].
    // Both images contribute informative pixels, so newly introduced text cannot be ignored.
    private static (int Count, double Fraction, bool Distributed) Compare(
        GridPixelBuffer before, GridPixelBuffer after, int dx, int dy)
    {
        var left = Math.Max(1, 1 - dx);
        var top = Math.Max(1, 1 - dy);
        var right = Math.Min(after.Width - 1, before.Width - 1 - dx);
        var bottom = Math.Min(after.Height - 1, before.Height - 1 - dy);
        var count = 0;
        var matches = 0;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var bx = x + dx;
            var by = y + dy;
            if (!HasDetail(before, bx, by) && !HasDetail(after, x, y)) continue;
            count++;
            if (Math.Abs(before[bx, by] - after[x, y]) <= 2) matches++;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            // Quickly eliminate unrelated offsets without preferring any winning score.
            if (count >= 64 && matches < count * .97) return (count, 0, false);
        }
        return (count, count == 0 ? 0 : matches / (double)count,
            maxX - minX >= Math.Min(16, (right - left) / 3) &&
            maxY - minY >= Math.Min(16, (bottom - top) / 3));
    }

    private static bool HasDetail(GridPixelBuffer image, int x, int y)
    {
        var value = image[x, y];
        // A horizontal or vertical rule alone is not distinctive table content.
        return (Math.Abs(value - image[x - 1, y]) > 12 || Math.Abs(value - image[x + 1, y]) > 12) &&
               (Math.Abs(value - image[x, y - 1]) > 12 || Math.Abs(value - image[x, y + 1]) > 12);
    }

    internal static bool SameAnchor(GridPixelBuffer before, GridPixelBuffer after) =>
        Valid(before) && Valid(after) && before.Width == after.Width && before.Height == after.Height &&
        before.Gray.AsSpan().SequenceEqual(after.Gray);

    private static bool Valid(GridPixelBuffer image) => image.Width >= 4 && image.Height >= 4 &&
        (long)image.Width * image.Height == image.Gray.LongLength;

    private static GridRegistrationResult Reject(string reason) => new(false, false, 0, 0, 0, 0, 0, reason);
}

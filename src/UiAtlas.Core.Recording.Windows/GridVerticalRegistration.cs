namespace UiAtlas.Core.Recording.Windows;

/// <summary>Image-only vertical overlap with the same raster-phase verification as horizontal exploration.</summary>
internal static class GridVerticalRegistration
{
    internal static GridRegistrationResult Register(GridPixelBuffer before, GridPixelBuffer after, int direction,
        CancellationToken cancellation = default)
    {
        if (before.Width != after.Width || before.Height != after.Height || before.Width < 5 || before.Height < 5 ||
            (long)before.Width * before.Height != before.Gray.Length || before.Gray.Length != after.Gray.Length)
            return new(false, false, 0, 0, 0, 0, 0, "geometry-changed");
        // Transpose comparison buffers only. The fixed header is checked separately by the coordinator;
        // it must never provide false evidence for vertical motion. Saved pixels remain untouched.
        var empty = new GridPixelBuffer(before.Height, 5, new byte[before.Height * 5]);
        var firstSelection = LeadingSelection(before); var nextSelection = LeadingSelection(after);
        var selectionHeight = firstSelection.Height > 0 && Math.Abs(firstSelection.Height - nextSelection.Height) <= 1 &&
            Math.Abs(firstSelection.Shade - nextSelection.Shade) <= 2 ? Math.Max(firstSelection.Height, nextSelection.Height) : 0;
        var firstBand = SelectionBand(before); var nextBand = SelectionBand(after);
        var sameSelection = firstBand is { } a && nextBand is { } b && Math.Abs((a.End - a.Start) - (b.End - b.Start)) <= 2;
        var result = GridHorizontalRegistration.Register(Transpose(before), Transpose(after), empty, empty, direction, cancellation, selectionHeight,
            sameSelection ? firstBand : null, sameSelection ? nextBand : null, FixedFooter(before, after));
        return result with { DisplacementX = 0, DisplacementY = result.DisplacementX,
            SubpixelDisplacementX = null, SubpixelDisplacementY = result.SubpixelDisplacementX,
            Reason = result.Accepted ? "unique-scaled-vertical-overlap" : result.Reason };
    }

    private static (int Start, int End)? SelectionBand(GridPixelBuffer image)
    {
        var rows = new List<int>();
        for (var y = 0; y < image.Height; y++)
        {
            var histogram = new int[256];
            for (var x = 0; x < image.Width; x++) histogram[image[x, y]]++;
            var shade = Enumerable.Range(64, 128).MaxBy(v => histogram[v]);
            if (histogram[shade] >= image.Width * .75) rows.Add(y);
        }
        if (rows.Count < 8 || rows.Count > Math.Min(64, image.Height / 6) || rows[^1] - rows[0] + 1 != rows.Count) return null;
        return (rows[0], rows[^1] + 1);
    }

    private static int FixedFooter(GridPixelBuffer before, GridPixelBuffer after)
    {
        var height = 0;
        for (; height < Math.Min(32, before.Height / 10); height++)
        {
            var y = before.Height - 1 - height;
            if (!before.Gray.AsSpan(y * before.Width, before.Width).SequenceEqual(after.Gray.AsSpan(y * after.Width, after.Width))) break;
        }
        // A bounded footer proven pixel-identical in both observations is viewport chrome.
        // This excludes comparison evidence only; every original pixel remains in capture output.
        return height >= 6 ? height : 0;
    }

    private static (int Height, int Shade) LeadingSelection(GridPixelBuffer image)
    {
        // A dark, full-width selection at the top is viewport chrome: its old record changes
        // colour and font when it moves away. Exclude that bounded band from BOTH sides of
        // the comparison only when the same fill and extent occur in both observations.
        // No source pixels are removed from the image and remaining body detail must still
        // establish a unique overlap. This path never qualifies transcribed business data.
        if (image.Height < 80) return default;
        var histogram = new int[256];
        for (var x = 0; x < image.Width; x++) histogram[image[x, 3]]++;
        var shade = Enumerable.Range(64, 128).MaxBy(value => histogram[value]);
        if (histogram[shade] < image.Width * .75) return default;
        var limit = Math.Min(64, image.Height / 8);
        for (var y = 4; y <= limit; y++)
        {
            var filled = 0;
            for (var x = 0; x < image.Width; x++) if (Math.Abs(image[x, y] - shade) <= 2) filled++;
            if (filled < image.Width * .6) return y >= 8 ? (y, shade) : default;
        }
        return default;
    }

    private static GridPixelBuffer Transpose(GridPixelBuffer source)
    {
        var pixels = new byte[source.Gray.Length];
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++) pixels[x * source.Height + y] = source[x, y];
        return new(source.Height, source.Width, pixels);
    }
}

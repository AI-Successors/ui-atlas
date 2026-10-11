using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

public sealed record RawSurfaceImageLayout(RectI SourcePixels, RectI WindowLocalBounds, string Coverage)
{
    public static RawSurfaceImageLayout? Create(RectI window, RectI image, int pixelWidth, int pixelHeight)
    {
        var overlap = RawObservationSemantics.Intersection(window, image);
        if (overlap is null || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var scaleX = pixelWidth / (double)image.Width;
        var scaleY = pixelHeight / (double)image.Height;
        var left = Math.Clamp((int)Math.Floor(((long)overlap.X - image.X) * scaleX), 0, pixelWidth);
        var top = Math.Clamp((int)Math.Floor(((long)overlap.Y - image.Y) * scaleY), 0, pixelHeight);
        var right = Math.Clamp((int)Math.Ceiling(((long)overlap.X + overlap.Width - image.X) * scaleX), 0, pixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(((long)overlap.Y + overlap.Height - image.Y) * scaleY), 0, pixelHeight);
        if (right <= left || bottom <= top) return null;
        return new(new(left, top, right - left, bottom - top),
            overlap with { X = overlap.X - window.X, Y = overlap.Y - window.Y },
            RawObservationSemantics.Coverage(window, image));
    }
}

public static class RawVariantPresentation
{
    public static IReadOnlyList<UiMapVariantView> Filter(IEnumerable<UiMapVariantView> variants,
        bool skipEmpty, bool includeSupplementary) => variants
        .Where(variant => includeSupplementary || variant.RecordKind != "supplementary")
        // Failed/unknown coverage is never hidden by the convenience empty filter.
        .Where(variant => !skipEmpty || variant.ControlCount > 0 || variant.CaptureStatus != "ok")
        .ToArray();
}

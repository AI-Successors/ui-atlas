namespace UiAtlas.Core.Contracts;

public sealed record RectI(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width >= 0 && Height >= 0;
}

/// <summary>Geometry usable by derived controls and screen drawing, distinct from raw provider payloads.</summary>
public static class ControlGeometry
{
    public static bool IsBounded(RectI? bounds) => bounds is { Width: >= 0, Height: >= 0 } &&
        (long)bounds.Width * bounds.Height <= 16_000_000;

    public static bool IsRenderable(RectI? bounds) => IsBounded(bounds) &&
        bounds is { Width: > 0, Height: > 0 } &&
        (long)bounds.X + bounds.Width <= int.MaxValue &&
        (long)bounds.Y + bounds.Height <= int.MaxValue;
}

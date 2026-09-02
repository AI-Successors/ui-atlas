namespace UiAtlas.Core.Contracts;

public sealed record RectI(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width >= 0 && Height >= 0;
}

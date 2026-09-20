using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class DataGridRegistrationTests
{
    [Theory]
    [InlineData(GridScrollAxis.Horizontal, 31)]
    [InlineData(GridScrollAxis.Horizontal, 7)]
    [InlineData(GridScrollAxis.Vertical, 29)]
    [InlineData(GridScrollAxis.Vertical, 5)]
    [InlineData(GridScrollAxis.Vertical, -5)]
    public void DistinctiveOverlapMeasuresActualIncludingShortTerminalDisplacement(GridScrollAxis axis, int displacement)
    {
        var before = View(40, 40);
        var after = View(40 + (axis == GridScrollAxis.Horizontal ? displacement : 0),
            40 + (axis == GridScrollAxis.Vertical ? displacement : 0));
        var result = DataGridRegistration.Register(before, after, axis, Math.Sign(displacement));
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(axis == GridScrollAxis.Horizontal ? displacement : 0, result.DisplacementX);
        Assert.Equal(axis == GridScrollAxis.Vertical ? displacement : 0, result.DisplacementY);
    }

    [Fact]
    public void MultipleRepetitiveContentAlignmentsAreRejected()
    {
        var before = View(0, 0, period: 16);
        var after = View(0, 5, period: 16);
        var result = DataGridRegistration.Register(before, after, GridScrollAxis.Vertical, 1);
        Assert.False(result.Accepted);
        Assert.True(result.CompatibleCandidates > 1);
        Assert.Equal("ambiguous-repeated-content", result.Reason);
    }

    [Fact]
    public void GridRulesAloneNeverEstablishMovement()
    {
        var before = View(0, 0, rulesOnly: true);
        var after = View(0, 5, rulesOnly: true);
        var result = DataGridRegistration.Register(before, after, GridScrollAxis.Vertical, 1);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void WrongAxisAndGapsAreRejected()
    {
        Assert.False(DataGridRegistration.Register(View(0, 0), View(4, 20), GridScrollAxis.Vertical, 1).Accepted);
        Assert.False(DataGridRegistration.Register(View(0, 0), View(0, 90), GridScrollAxis.Vertical, 1).Accepted);
    }

    [Fact]
    public void IdenticalPixelsDoNotByThemselvesProveTerminalBoundary()
    {
        var result = DataGridRegistration.Register(View(0, 0), View(0, 0), GridScrollAxis.Vertical, 1);
        Assert.True(result.NoChange);
        Assert.False(result.Accepted);
    }

    private static GridPixelBuffer View(int x, int y, int period = 0, bool rulesOnly = false)
    {
        var pixels = new byte[96 * 80];
        for (var py = 0; py < 80; py++)
        for (var px = 0; px < 96; px++)
        {
            var row = period == 0 ? py + y : (py + y) % period;
            pixels[py * 96 + px] = rulesOnly ? (byte)(row % 16 == 0 ? 10 : 250) : Pixel(px + x, row);
        }
        return new(96, 80, pixels);
    }

    internal static byte Pixel(int x, int y)
    {
        var hash = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
        hash ^= hash >> 13;
        return (byte)(hash % 256);
    }
}

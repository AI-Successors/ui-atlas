using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridHorizontalRegistrationTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void ScaledTextCanAdvanceAndReturnWithTheSameMeasuredOverlap(double scale) => Sta(() =>
    {
        var before = Render(0, scale);
        var after = Render(91, scale);
        var forward = GridHorizontalRegistration.Register(before.Body, after.Body, before.Header, after.Header, 1);
        Assert.True(forward.Accepted, forward.ToString());
        Assert.InRange(forward.DisplacementX, (int)Math.Round(91 * scale) - 1, (int)Math.Round(91 * scale) + 1);
        var backward = GridHorizontalRegistration.Register(after.Body, before.Body, after.Header, before.Header, -1);
        Assert.True(backward.Accepted, backward.ToString());
        Assert.Equal(-forward.DisplacementX, backward.DisplacementX);
    });

    [Fact]
    public void StationaryRowSelectorIsExcludedOnlyFromComparison() => Sta(() =>
    {
        var before = Render(0, 1.25); var after = Render(91, 1.25);
        var body = Gutter(before.Body); var header = Gutter(before.Header);
        var original = body.Gray.ToArray();
        var result = GridHorizontalRegistration.Register(body, Gutter(after.Body), header, Gutter(after.Header), 1);
        Assert.True(result.Accepted, result.ToString());
        Assert.Equal(original, body.Gray);
        Assert.Equal(before.Body.Width + 16, body.Width);

        static GridPixelBuffer Gutter(GridPixelBuffer source)
        {
            var width = source.Width + 16;
            var gray = new byte[width * source.Height];
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < 16; x++) gray[y * width + x] = DataGridRegistrationTests.Pixel(x, y);
                Buffer.BlockCopy(source.Gray, y * source.Width, gray, y * width + 16, source.Width);
            }
            return new(width, source.Height, gray);
        }
    });

    [Fact]
    public void HeaderCannotOverrideChangedBodyContent() => Sta(() =>
    {
        var before = Render(0, 1.25);
        var changed = Render(91, 1.25, changed: true);
        var result = GridHorizontalRegistration.Register(before.Body, changed.Body, before.Header, changed.Header, 1);
        Assert.False(result.Accepted);
    });

    [Fact]
    public void RepeatedColumnsDoNotChooseAnArbitraryDisplacement() => Sta(() =>
    {
        var before = Render(0, 1, repeated: true);
        var after = Render(16, 1, repeated: true);
        var result = GridHorizontalRegistration.Register(before.Body, after.Body, before.Header, after.Header, 1);
        Assert.False(result.Accepted);
        Assert.Equal("ambiguous-repeated-content", result.Reason);
    });

    [Fact]
    public void BlankBodyCanUseDistinctiveHeadersButRulesAloneCannot() => Sta(() =>
    {
        var before = Render(0, 1.25);
        var after = Render(91, 1.25);
        var blank = before.Body with { Gray = Enumerable.Repeat((byte)255, before.Body.Gray.Length).ToArray() };
        Assert.True(GridHorizontalRegistration.Register(blank, blank, before.Header, after.Header, 1).Accepted);
        var rules = before.Header with { Gray = Enumerable.Range(0, before.Header.Gray.Length)
            .Select(i => (byte)(i % before.Header.Width % 80 == 0 ? 0 : 255)).ToArray() };
        var shifted = rules with { Gray = Enumerable.Range(0, rules.Gray.Length)
            .Select(i => (byte)((i % rules.Width + 16) % 80 == 0 ? 0 : 255)).ToArray() };
        Assert.False(GridHorizontalRegistration.Register(blank, blank, rules, shifted, 1).Accepted);
    });

    [Fact]
    public void WrongDirectionAndMissingOverlapAreRejected() => Sta(() =>
    {
        var before = Render(0, 1.25);
        var after = Render(91, 1.25);
        var gap = Render(600, 1.25);
        Assert.False(GridHorizontalRegistration.Register(before.Body, after.Body, before.Header, after.Header, -1).Accepted);
        Assert.False(GridHorizontalRegistration.Register(before.Body, gap.Body, before.Header, gap.Header, 1).Accepted);
    });

    private static (GridPixelBuffer Header, GridPixelBuffer Body) Render(int offset, double scale,
        bool changed = false, bool repeated = false)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 420, 110));
            var labels = new[] { "Record", "Reference", "Status", "Person", "Location", "Workstation", "Payment" };
            for (var column = 0; column < 16; column++)
            {
                var x = column * 91 - offset;
                dc.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Gray, 1), new Rect(x, 0, 91, 22));
                Text(repeated ? "Column" : labels[column % labels.Length] + column, x + 4, 3);
                for (var row = 0; row < 3; row++)
                {
                    var y = 22 + row * 22;
                    dc.DrawRectangle(row == 0 ? Brushes.LightGray : Brushes.White, new Pen(Brushes.Gray, 1), new Rect(x, y, 91, 22));
                    Text(changed ? "REPLACED" : repeated ? "Same" : $"Item {column * 37 + row * 13:D3}", x + 4, y + 3);
                }
            }
            void Text(string text, int x, int y) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Black, 1), new Point(x, y));
        }
        var bitmap = new RenderTargetBitmap(420, 110, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        var pixels = new byte[scaled.PixelWidth * scaled.PixelHeight * 4];
        scaled.CopyPixels(pixels, scaled.PixelWidth * 4, 0);
        var headerHeight = (int)Math.Round(22 * scale);
        return (Crop(0, headerHeight), Crop(headerHeight, scaled.PixelHeight - headerHeight));

        GridPixelBuffer Crop(int top, int height)
        {
            var gray = new byte[scaled.PixelWidth * height];
            for (var i = 0; i < gray.Length; i++)
            {
                var p = (top * scaled.PixelWidth + i) * 4;
                gray[i] = (byte)((pixels[p] * 29 + pixels[p + 1] * 150 + pixels[p + 2] * 77) >> 8);
            }
            return new(scaled.PixelWidth, height, gray);
        }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

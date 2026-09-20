using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridVerticalRegistrationTests
{
    [Theory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(1.75)]
    public void ScaledRowsAdvanceAndReturnAcrossSelectionRepaint(double scale) => Sta(() =>
    {
        var before = Render(4, scale); var after = Render(5, scale); var original = before.Gray.ToArray();
        var forward = GridVerticalRegistration.Register(before, after, 1);
        Assert.True(forward.Accepted, forward.ToString());
        Assert.InRange(forward.SubpixelDisplacementY!.Value, 21 * scale - .5, 21 * scale + .5);
        Assert.Equal(0, forward.DisplacementX); Assert.Null(forward.SubpixelDisplacementX);
        var backward = GridVerticalRegistration.Register(after, before, -1);
        Assert.True(backward.Accepted, backward.ToString());
        Assert.Equal(-forward.DisplacementY, backward.DisplacementY);
        Assert.Equal(original, before.Gray);
    });

    [Fact]
    public void SelectionRepaintDoesNotConsumeTheOverlapToleranceOfAShortViewport() => Sta(() =>
    {
        var before = Render(4, 1.25, visibleRows: 10); var after = Render(5, 1.25, visibleRows: 10);
        var result = GridVerticalRegistration.Register(before, after, 1);
        Assert.True(result.Accepted, result.ToString()); Assert.Equal(26.25, result.SubpixelDisplacementY);
        Assert.False(GridVerticalRegistration.Register(before, Render(5, 1.25, changed: true, visibleRows: 10), 1).Accepted);
    });

    [Fact]
    public void ChangedRowsWrongDirectionAndMissingOverlapAreRejected() => Sta(() =>
    {
        var before = Render(4, 1.25);
        Assert.False(GridVerticalRegistration.Register(before, Render(5, 1.25, changed: true), 1).Accepted);
        Assert.False(GridVerticalRegistration.Register(before, Render(5, 1.25), -1).Accepted);
        Assert.False(GridVerticalRegistration.Register(before, Render(40, 1.25), 1).Accepted);
    });

    [Fact]
    public void RepeatedRowsDoNotInventAUniqueDisplacement() => Sta(() =>
    {
        var before = Render(4, 1.25, repeated: true); var after = Render(5, 1.25, repeated: true);
        var result = GridVerticalRegistration.Register(before, after, 1);
        Assert.False(result.Accepted);
    });

    [Fact]
    public void IdenticalPixelsAreNoChangeAndBlankRowsProvideNoMotionEvidence()
    {
        var blank = new GridPixelBuffer(200, 160, Enumerable.Repeat((byte)255, 200 * 160).ToArray());
        var result = GridVerticalRegistration.Register(blank, blank, 1);
        Assert.True(result.NoChange); Assert.False(result.Accepted);
        Assert.False(GridVerticalRegistration.Register(blank, blank with { Width = 201 }, 1).Accepted);
    }

    [Fact]
    public void BottomSelectionRepaintAllowsLargeVerifiedStepsWithoutAcceptingChangedRows() => Sta(() =>
    {
        var before = Render(4, 1, bottomSelection: true);
        var after = Render(12, 1, bottomSelection: true);
        var result = GridVerticalRegistration.Register(before, after, 1);
        Assert.True(result.Accepted, result.ToString()); Assert.Equal(168, result.DisplacementY);
        Assert.False(GridVerticalRegistration.Register(before, Render(12, 1, changed: true, bottomSelection: true), 1).Accepted);
        var directory = Environment.GetEnvironmentVariable("UIATLAS_VERTICAL_REPLAY_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            GridPixelBuffer Load(string name)
            {
                var frame = OpaqueSurfaceScanner.PixelFrame.Decode(System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory, name)));
                var trimmed = GridImageLayout.TrimBlankFooter(frame, new(3, 19, 1143, 437));
                Assert.Equal(421, trimmed.Height);
                const int width = 1143, height = 437; var gray = new byte[width * height];
                for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                {
                    var p = ((y + 19) * frame.Width + x + 3) * 4;
                    gray[y * width + x] = (byte)((frame.Pixels[p] * 29 + frame.Pixels[p + 1] * 150 + frame.Pixels[p + 2] * 77) >> 8);
                }
                return new(width, height, gray);
            }
            var firstName = Environment.GetEnvironmentVariable("UIATLAS_VERTICAL_REPLAY_FIRST") ?? "tile-0018.png";
            var secondName = Environment.GetEnvironmentVariable("UIATLAS_VERTICAL_REPLAY_SECOND") ?? "tile-0019.png";
            var replay = GridVerticalRegistration.Register(Load(firstName), Load(secondName), 1);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "vertical-replay.json"), System.Text.Json.JsonSerializer.Serialize(replay));
            Assert.True(replay.Accepted, replay.ToString()); Assert.Equal(168, replay.DisplacementY);
        }
    });

    private static GridPixelBuffer Render(int first, double scale, bool changed = false, bool repeated = false, int visibleRows = 22, bool bottomSelection = false)
    {
        const int width = 540, pitch = 21;
        var height = visibleRows * pitch;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (var row = first - 1; row < first + 24; row++)
            {
                var y = (row - first) * pitch; var selected = row == first + (bottomSelection ? visibleRows - 1 : 0);
                dc.DrawRectangle(selected ? Brushes.Gray : Brushes.WhiteSmoke, new Pen(Brushes.Silver, 1), new Rect(0, y, width, pitch));
                for (var column = 0; column < 5; column++)
                {
                    dc.DrawLine(new Pen(Brushes.Silver, 1), new Point(column * 108, y), new Point(column * 108, y + pitch));
                    var label = changed ? "REPLACED" : repeated ? "Same row" : $"{column * 113 + row * 37} ref {row:D2}";
                    dc.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, selected ? FontWeights.Normal : FontWeights.SemiBold, FontStretches.Normal),
                        11, selected ? Brushes.White : Brushes.Black, 1), new Point(column * 108 + 4, y + 2));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        var rgba = new byte[scaled.PixelWidth * scaled.PixelHeight * 4]; scaled.CopyPixels(rgba, scaled.PixelWidth * 4, 0);
        var gray = new byte[rgba.Length / 4];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)((rgba[i * 4] * 29 + rgba[i * 4 + 1] * 150 + rgba[i * 4 + 2] * 77) >> 8);
        return new(scaled.PixelWidth, scaled.PixelHeight, gray);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

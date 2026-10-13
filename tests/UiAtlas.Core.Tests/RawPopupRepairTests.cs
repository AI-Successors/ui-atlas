using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class RawPopupRepairTests
{
    [Fact]
    public void ContextLayersKeepExactBundleFrameWindowAndAttemptEvenWhenAnotherCaptureReusesTheHwnd()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var frame = WithPopup(bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json"));
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], [])));
        var raw = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        var layer = Assert.Single(RawPopupLayer.ForContext(raw, frame, "golden", frame.Window.Hwnd));
        Assert.Equal(2, layer.Window.Hwnd);
        Assert.NotNull(layer.Surface);
        Assert.All(layer.Controls, control => Assert.Equal(layer.Surface.Id, control.OwnerSurfaceId));
        Assert.All(layer.Controls, control => Assert.Equal("popup", UiMappingReadModel.Property(control.Source, "rawStreamId")));
        Assert.Equal("popup", RawPopupLayer.PreferredAttempt(layer.Streams, layer.Controls)!.StreamId);
        Assert.Contains(layer.Streams, stream => stream.Status == "timeout" && stream.Controls.Count == 0);
        Assert.Empty(Assert.Single(RawPopupLayer.ForContext(raw, frame, "resumed", 1)).Controls);
        Assert.Empty(Assert.Single(RawPopupLayer.ForContext(raw, frame with { Sequence = 99 }, "golden", 1)).Controls);
        Assert.Empty(RawPopupLayer.ForContext(raw, frame, "golden", 2));
        var family = model.BuildPipeline(UiUnderstandingLevel.RawDataStreams).Nodes.Single(node => node.DisplayName == "Borderless Tool Window");
        Assert.True(family.RepresentsSurface(layer.Surface.Id));
        Assert.False(family.RepresentsSurface(raw.Surfaces.Single(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x1").Id));
    }

    [Fact]
    public void GeometryAndLegacyCropPolicyRespectOverlappingWindowsWithoutDeletingRecords()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var frame = WithPopup(bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json"));
        var covered = new RectI(25, 60, 10, 10);
        Assert.Empty(RawSurfaceImageLayout.VisibleControlRegions(frame, frame.Window, covered));
        Assert.Equal(new[] { covered }, RawSurfaceImageLayout.VisibleControlRegions(frame, frame.ScopedWindows![1], covered));
        var original = new UiEvidenceImage([], null, "frame", new(0, 0, 100, 120), null, null, frame,
            OccludedRegions: RawSurfaceImageLayout.Occluders(frame, frame.Window));
        Assert.False(RawSurfaceImageLayout.CanCropControl(original, covered));
        Assert.True(RawSurfaceImageLayout.CanCropControl(original, new(5, 5, 10, 10)));
        Assert.False(RawSurfaceImageLayout.CanCropControl(original, new(-5, 0, 10, 10)));
        Assert.True(RawSurfaceImageLayout.CanCropControl(original with { OccludedRegions = null }, covered));
        Assert.Null(RawVariantPresentation.CaptureWarning(frame));
        var interrupted = frame with { Acquisition = frame.Acquisition! with { Status = "interrupted", StabilityStatus = "changed" } };
        Assert.Contains("Incomplete capture", RawVariantPresentation.CaptureWarning(interrupted));
        Assert.Null(RawVariantPresentation.ExclusionReason(interrupted, frame.Window.Hwnd));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendererUsesWindowPlaneNotCompositeAndKeepsPopupLayerAndFamilySelection(bool stable)
    {
        MapperOverlayWindowTests.Sta(() =>
        {
            using var temp = new TempDirectory();
            var path = CreateColoredBundle(temp.Path, stable);
            var validation = RecordingBundleValidator.Validate(path);
            Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
            using var reader = UiEvidenceReader.Open(path);
            var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(path));
            var raw = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
            var main = raw.Surfaces.First(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x1");
            var family = model.BuildPipeline(UiUnderstandingLevel.RawDataStreams).Nodes.Single(node => node.SourceIds.Contains(main.Id));
            var scope = model.ResolvePipelineSurfaces(family);
            var viewer = new ExplorerWindow(null);
            try
            {
                Set(viewer, "_model", model); Set(viewer, "_evidence", reader);
                Set(viewer, "_level", UiUnderstandingLevel.RawDataStreams);
                Set(viewer, "_inspectionLevel", UiUnderstandingLevel.RawDataStreams);
                Set(viewer, "_selectedSurfaceScope", scope);
                Set(viewer, "_selectedSurface", main);
                Set(viewer, "_selectedVariant", main.Variants[0]);
                Set(viewer, "_viewMode", "Controls");
                Call(viewer, "DrawTopology"); Call(viewer, "RenderAppMap");
                var rectangles = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>().ToArray();
                var primary = Assert.Single(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Owner button •") == true);
                var popup = Assert.Single(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Popup item •") == true);
                Assert.DoesNotContain(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Covered button •") == true);
                if (stable)
                {
                    Assert.Equal(Colors.Red, Pixel(Assert.IsType<ImageBrush>(primary.Fill)));
                    Assert.Equal(Colors.Lime, Pixel(Assert.IsType<ImageBrush>(popup.Fill)));
                }
                else
                {
                    Assert.IsNotType<ImageBrush>(primary.Fill);
                    Assert.IsNotType<ImageBrush>(popup.Fill);
                    var banner = Assert.IsType<StackPanel>(Get<ContentControl>(viewer, "_traceBannerHost").Content);
                    Assert.Contains("Incomplete capture", Assert.IsType<TextBlock>(Assert.IsType<Border>(banner.Children[0]).Child).Text);
                }
                // Actual card navigation must highlight one family for every instance.
                foreach (var variant in scope.SelectMany(surface => surface.Variants))
                {
                    Call(viewer, "SelectVariant", variant);
                    var selected = Get<Dictionary<string, Border>>(viewer, "_topologyShapes").Values.Where(border => border.BorderThickness.Left == 3).ToArray();
                    Assert.Single(selected);
                    Assert.True(Assert.IsType<UiPipelineNodeView>(selected[0].Tag).RepresentsSurface(variant.SurfaceId));
                }
            }
            finally { viewer.Close(); }
        });
    }

    private static FrameObservation WithPopup(FrameObservation frame)
    {
        var main = frame.Window with { Hwnd = 1, Bounds = new(0, 0, 100, 100), ZOrder = 2 };
        var popup = main with { Hwnd = 2, OwnerHwnd = 1, ClassName = "Net UI Tool Window", Title = "Palette",
            Style = 0x80000000L, IsToolWindow = true, Bounds = new(20, 50, 40, 70), ZOrder = 0 };
        AutomationObservation Control(string id, string name, long hwnd, RectI bounds) => frame.Automation[0] with {
            RuntimeId = id, ParentRuntimeId = "root", Name = name, ControlType = "Button", WindowHwnd = hwnd,
            QueryWindowHwnd = hwnd, ScopeStatus = "verified", Bounds = bounds, IsOffscreen = false };
        return frame with { Window = main, ScopedWindows = [main, popup], ScreenshotBounds = new(0, 0, 100, 120),
            Automation = [], Acquisition = new(RawObservationSemantics.Version, $"observation:{frame.Sequence}", frame.TimestampUtc,
                frame.TimestampUtc, "ok", "screen", "stable", WindowCoverage: [new(1, main.Bounds, "full"), new(2, popup.Bounds, "full")]), RawStreams = [
                RawFrameEvidenceTests.Stream(frame, "owner", Control("owner", "Owner button", 1, new(5, 5, 10, 10)),
                    Control("covered", "Covered button", 1, new(25, 60, 10, 10))) with { Source = "msaa/0x1", TargetWindowHwnd = 1 },
                RawFrameEvidenceTests.Stream(frame, "popup", Control("popup", "Popup item", 2, new(25, 60, 10, 10))) with { Source = "msaa/0x2", TargetWindowHwnd = 2 },
                RawFrameEvidenceTests.Stream(frame, "empty") with { Source = "uia.raw/0x2", TargetWindowHwnd = 2, Status = "timeout" }] };
    }

    private static string CreateColoredBundle(string directory, bool stable)
    {
        using var original = RecordingBundle.Open(SyntheticBundleFactory.Create(directory, includeScreenshot: true));
        using var writer = new RecordingBundleWriter(System.IO.Path.Combine(directory, "colors-stage"));
        foreach (var entry in original.Entries.Where(entry => entry is not ("manifest.json" or "hashes.sha256") && !entry.StartsWith("raw/frames/")))
        {
            if (!entry.StartsWith("raw/observations/")) { writer.WriteBytes(entry, original.ReadBytes(entry)); continue; }
            var frame = WithPopup(original.ReadJson<FrameObservation>(entry));
            var frameEntry = $"raw/frames/frame-{frame.Sequence:D6}.png";
            var planes = frame.ScopedWindows!.Select(window => new WindowImageObservation(window.Hwnd, window.Bounds,
                $"raw/frames/frame-{frame.Sequence:D6}-window-{window.Hwnd:X}.png", frame.TimestampUtc, frame.TimestampUtc,
                "windows-graphics-capture", "ok")).ToArray();
            foreach (var plane in planes) writer.WriteBytes(plane.Entry!, Png(plane.Bounds.Width, plane.Bounds.Height, plane.Hwnd == 1 ? Colors.Red : Colors.Lime));
            writer.WriteBytes(frameEntry, Png(100, 120, Colors.Blue));
            writer.WriteJson(entry, frame with { FrameEntry = frameEntry, WindowImages = planes,
                Acquisition = stable ? frame.Acquisition : frame.Acquisition! with { Status = "interrupted", StabilityStatus = "changed" } });
        }
        writer.WriteJson("manifest.json", original.ReadJson<RecordingManifest>("manifest.json") with { Files = writer.DescribeEntries() });
        var path = System.IO.Path.Combine(directory, "colors.mlrec"); writer.Complete(path); return path;
    }

    private static byte[] Png(int width, int height, Color color)
    {
        var pixels = Enumerable.Range(0, width * height).SelectMany(_ => new byte[] { color.B, color.G, color.R, color.A }).ToArray();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static Color Pixel(ImageBrush brush)
    {
        var pixel = new byte[4]; ((BitmapSource)brush.ImageSource).CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Call(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
}

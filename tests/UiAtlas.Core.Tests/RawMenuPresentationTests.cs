using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RawMenuPresentationTests
{
    private static WindowObservation Window(long hwnd, long owner, string kind, RectI bounds, int z) =>
        new(hwnd, 1, 42, kind, kind, bounds, true, true, false, false, 96, OwnerHwnd: owner, ZOrder: z,
            Style: kind == "Net UI Tool Window" ? 0x80000000L : 0x00C40000L,
            IsToolWindow: kind == "Net UI Tool Window");

    private static FrameObservation Frame(params WindowObservation[] windows) =>
        new(1, DateTimeOffset.UtcNow, "", windows[0], [], false, "ok", "test", windows);

    [Fact]
    public void ContextFollowsImmediateOwnersAndCascadesButNeverCrossesDialogs()
    {
        var main = Window(1, 0, "OpusApp", new(-800, 40, 700, 500), 6);
        var menu = Window(2, 1, "Net UI Tool Window", new(-150, 100, 200, 200), 3);
        var cascade = Window(3, 2, "Net UI Tool Window", new(40, 200, 100, 100), 2);
        var font = Window(4, 1, "bosa_sdm_msword", new(-600, 200, 200, 200), 4);
        var palette = Window(5, 4, "Net UI Tool Window", new(-500, 300, 200, 200), 1);
        var colors = Window(6, 4, "#32770", new(-550, 250, 200, 200), 0);
        var frame = Frame(main, cascade, menu, font, palette, colors);
        var context = RawSurfaceImageLayout.WindowContext(frame, main);
        Assert.Equal(new long[] { 1, 2, 3 }, context.Select(window => window.Hwnd));
        Assert.Equal(new RectI(-800, 40, 940, 500), RawSurfaceImageLayout.ContextBounds(context));
        Assert.Equal(new long[] { 4, 5 }, RawSurfaceImageLayout.WindowContext(frame, font).Select(window => window.Hwnd));
        Assert.Single(RawSurfaceImageLayout.WindowContext(frame, colors));
        Assert.Single(RawSurfaceImageLayout.WindowContext(frame with { ScopedWindows = [main,
            menu with { IsVisible = false }, palette with { OwnerHwnd = 1, ProcessId = 99 }] }, main));
    }

    [Fact]
    public void CroppingRetainsMenusBeyondWindowEdgesWithoutCopyingBackgroundOrCoveringDialogs()
    {
        var main = Window(1, 0, "OpusApp", new(-100, -50, 100, 100), 3);
        var menu = Window(2, 1, "Net UI Tool Window", new(-10, -40, 30, 40), 0);
        var dialog = Window(3, 1, "#32770", new(-60, -20, 80, 40), 1);
        var frame = Frame(main, menu, dialog);
        var context = RawSurfaceImageLayout.WindowContext(frame, main);
        var regions = RawSurfaceImageLayout.VisibleContextRegions(frame, context);
        MapperOverlayWindowTests.Sta(() =>
        {
            var pixels = Enumerable.Repeat((byte)255, 120 * 100 * 4).ToArray();
            var source = BitmapSource.Create(120, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 120 * 4);
            var bounds = RawSurfaceImageLayout.ContextBounds(context);
            var rendered = ExplorerWindow.RenderRawWindowImage(source, bounds, bounds, visibleRegions: regions)!;
            rendered.CopyPixels(pixels, 120 * 4, 0);
            byte Alpha(int x, int y) => pixels[(y * 120 + x) * 4 + 3];
            Assert.Equal(255, Alpha(5, 5)); // observed main window
            Assert.Equal(255, Alpha(110, 15)); // owned menu outside main window
            Assert.Equal(255, Alpha(95, 35)); // menu above the unrelated covering dialog
            Assert.Equal(0, Alpha(50, 35)); // independent dialog masked
            Assert.Equal(0, Alpha(110, 80)); // unrelated desktop outside both regions
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MenuViewUsesExactRecordedFrameBytesAndNeverBorrowsPixels(bool screenshot)
    {
        using var temp = new TempDirectory();
        var source = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: screenshot);
        var path = RawFrameEvidenceTests.Rewrite(source, temp.Path, frame => frame with {
            ScopedWindows = [frame.Window, Window(987, frame.Window.Hwnd, "Net UI Tool Window",
                new(frame.Window.Bounds.X + 20, frame.Window.Bounds.Y + 20, 20, 20), -1) with { ProcessId = frame.Window.ProcessId }] });
        using var reader = UiEvidenceReader.Open(path);
        var frame = reader.ReadObservation("golden", 1);
        var evidence = new EvidenceRef("golden", 1, "raw/observations/frame-000001.json", null);
        var image = reader.ReadWindow(evidence, frame.Window.Hwnd);
        if (!screenshot) { Assert.Null(image); return; }
        Assert.Equal(reader.Read(evidence)!.Png, image!.Png);
        Assert.Equal(frame.FrameEntry, image.Entry);
        Assert.Contains("owned popup", image.PixelProvenance);
        Assert.NotNull(image.VisibleRegions);
        Assert.False(image.InheritedPixels);
    }

    [Fact]
    public void AttemptChoicesUseProviderTargetAndPreserveEmptyFailuresWithoutMovingControls()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var main = frame.Window;
        var popup = main with { Hwnd = 987, OwnerHwnd = main.Hwnd, ClassName = "Net UI Tool Window", IsToolWindow = true, Style = 0x80000000L };
        var mainControl = frame.Automation[0] with { QueryWindowHwnd = main.Hwnd, WindowHwnd = main.Hwnd, ScopeStatus = "verified" };
        // Identical geometry does not make this main-window record belong to the popup.
        var popupControl = mainControl with { RuntimeId = "popup", QueryWindowHwnd = popup.Hwnd, WindowHwnd = popup.Hwnd };
        var popupStream = RawFrameEvidenceTests.Stream(frame, "uia.raw", popupControl) with { TargetWindowHwnd = popup.Hwnd };
        var emptyStream = RawFrameEvidenceTests.Stream(frame, "uia.control/0x3DB") with { Status = "timeout" };
        frame = frame with { ScopedWindows = [main, popup], RawStreams = [
            RawFrameEvidenceTests.Stream(frame, "uia.raw/main", mainControl) with { TargetWindowHwnd = main.Hwnd },
            popupStream, emptyStream, RawFrameEvidenceTests.Stream(frame, "ocr", popupControl)] };
        Assert.Equal(new[] { popupStream, emptyStream }, RawVariantPresentation.StreamsForWindow(frame, popup.Hwnd));
        Assert.Contains("1 verified", RawVariantPresentation.ScopeSummary(popupStream.Controls, popup.Hwnd));
        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));
        var path = Path.Combine(temp.Path, "scope.db");
        SqliteGraphStore.Save(graph, path);
        var model = new UiMappingReadModel(SqliteGraphStore.Load(path));
        var raw = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        var popupSurface = raw.Surfaces.Single(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x3DB");
        var retained = Assert.Single(raw.ControlsForSurface(popupSurface.Id));
        Assert.Equal("popup", UiMappingReadModel.Property(retained.Source, "runtimeId"));
    }

    [Theory]
    [InlineData(UiMapProjectionMode.Controls)]
    [InlineData(UiMapProjectionMode.Overlay)]
    [InlineData(UiMapProjectionMode.Structure)]
    [InlineData(UiMapProjectionMode.StructureOverlay)]
    public void RawLabelsAreOptInAndUnknownFramesDoNotSupplyControlCrops(UiMapProjectionMode mode)
    {
        Assert.False(RawVariantPresentation.PolicyFor(mode, null).ShowsControlLabels);
        Assert.False(RawVariantPresentation.PolicyFor(mode, null).ShowsControlCrops);
        Assert.True(RawVariantPresentation.PolicyFor(mode, null, showLabels: true).ShowsControlLabels);
        var frame = Frame(Window(1, 0, "Main", new(0, 0, 100, 100), 0));
        frame = frame with { Acquisition = new(RawObservationSemantics.Version, "observation:1", frame.TimestampUtc,
            frame.TimestampUtc, "partial", "screen", "stable") };
        Assert.Equal(mode == UiMapProjectionMode.Controls, RawVariantPresentation.PolicyFor(mode, frame).ShowsControlCrops);
    }
}

using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RawStreamParityTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-observation")]
    [InlineData("after-seal")]
    [InlineData("changed-success")]
    [InlineData("missing-window")]
    [InlineData("queued-after-start")]
    public void AcquisitionValidationRejectsCrossObservationAndContradictoryEvidence(string mode)
    {
        using var temp = new TempDirectory();
        var path = RawFrameEvidenceTests.Rewrite(SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true), temp.Path, frame =>
        {
            var acquisition = new RawObservationAcquisition(RawObservationSemantics.Version, $"observation:{frame.Sequence}",
                frame.TimestampUtc, frame.TimestampUtc, "ok", "screen-bounds-explicit", "stable",
                WindowCoverage: (frame.ScopedWindows ?? [frame.Window]).Select(window => new RawWindowCoverage(window.Hwnd, window.Bounds, "full")).ToArray());
            acquisition = mode switch {
                "foreign-observation" => acquisition with { ObservationId = "observation:99999" },
                "after-seal" => acquisition with { SealedUtc = frame.TimestampUtc.AddSeconds(1) },
                "changed-success" => acquisition with { StabilityStatus = "changed" },
                "missing-window" => acquisition with { WindowCoverage = [] },
                _ => acquisition
            };
            return frame with { Acquisition = acquisition, RawStreams = [RawFrameEvidenceTests.Stream(frame, "uia") with {
                QueuedUtc = frame.TimestampUtc.AddSeconds(mode == "queued-after-start" ? 1 : 0) }] };
        });
        var report = RecordingBundleValidator.Validate(path);
        if (mode == "valid") Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
        else Assert.Contains(report.Issues, issue => issue.Code is "bundle.acquisition" or "bundle.raw-streams");
    }

    [Fact]
    public void PartialDialogPixelsRemainAtTheirWindowLocalPosition()
    {
        // Negative monitor origin and a two-times pixel scale must share one transform.
        var window = new RectI(-1000, 100, 800, 600);
        var dialogImage = new RectI(-800, 200, 300, 200);
        var layout = Assert.IsType<RawSurfaceImageLayout>(RawSurfaceImageLayout.Create(window, dialogImage, 600, 400));
        Assert.Equal(new RectI(0, 0, 600, 400), layout.SourcePixels);
        Assert.Equal(new RectI(200, 100, 300, 200), layout.WindowLocalBounds);
        Assert.Equal("partial", layout.Coverage);
        Assert.Equal("full", RawSurfaceImageLayout.Create(dialogImage, window, 1600, 1200)!.Coverage);
        Assert.Equal(new RectI(400, 200, 600, 400), RawSurfaceImageLayout.Create(dialogImage, window, 1600, 1200)!.SourcePixels);
        Assert.Null(RawSurfaceImageLayout.Create(window, new RectI(10, 10, 100, 100), 100, 100));
        Assert.Null(RawSurfaceImageLayout.Create(window, new RectI(int.MaxValue, 0, 100, 100), 100, 100));
        Assert.Equal("missing", RawObservationSemantics.Coverage(window, null));
    }

    [Fact]
    public void EmptyFilterKeepsFailuresAndLegacyUnknownButSupplementaryIsOptIn()
    {
        UiMapVariantView Variant(string id, string status, string kind = "observation", int count = 0) =>
            new(id, id, "bundle", 1, count, null, [], RecordKind: kind, CaptureStatus: status);
        var variants = new[] { Variant("empty", "ok"), Variant("failed", "partial"),
            Variant("unknown", "legacy-unknown"), Variant("probe", "ok", "supplementary", 4), Variant("full", "ok", count: 3) };
        Assert.Equal(new[] { "failed", "unknown", "full" }, RawVariantPresentation.Filter(variants, true, false).Select(item => item.Id));
        Assert.Equal(5, RawVariantPresentation.Filter(variants, false, true).Count);
    }

    [Fact]
    public void ImageEvidenceAndDuplicateAttemptsSurviveBuildSaveAndReopen()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var native = frame.Automation.First() with { RuntimeId = "same", WindowHwnd = frame.Window.Hwnd };
        var image = native with { RuntimeId = "image", WindowHwnd = 0, FrameworkId = "OCR" };
        var unassigned = native with { RuntimeId = "unknown-owner", WindowHwnd = 999 };
        var imageBounds = new RectI(200, 100, 300, 200);
        frame = frame with { ScreenshotBounds = imageBounds, RecordKind = "observation",
            Acquisition = new(RawObservationSemantics.Version, "observation:1", frame.TimestampUtc, frame.TimestampUtc,
                "ok", "screen-bounds-explicit", "stable"),
            RawStreams = [
                RawFrameEvidenceTests.Stream(frame, "uia.control/0x1", native) with { StreamId = "attempt-1" },
                RawFrameEvidenceTests.Stream(frame, "uia.control/0x1", native) with { StreamId = "attempt-2" },
                RawFrameEvidenceTests.Stream(frame, "ocr", image),
                RawFrameEvidenceTests.Stream(frame, "visual.geometry", image with { WindowHwnd = frame.Window.Hwnd }),
                RawFrameEvidenceTests.Stream(frame, "msaa/0x999", unassigned)] };
        var second = frame with { Sequence = 2, FrameEntry = "", ScreenshotBounds = null, RecordKind = "supplementary" };
        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame, second], []));
        var path = Path.Combine(temp.Path, "parity.db");
        SqliteGraphStore.Save(graph, path);
        var reopened = SqliteGraphStore.Load(path);
        Assert.True(GraphValidator.Validate(reopened).IsValid);
        Assert.Equal(graph.Metadata.SemanticHash, reopened.Metadata.SemanticHash);
        var model = new UiMappingReadModel(reopened);
        var layer = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        Assert.Equal(10, layer.Controls.Count);
        Assert.Equal(2, layer.Surfaces.Count(surface => surface.Source.Kind == GraphNodeKind.Window));
        Assert.Equal(4, layer.Surfaces.Count(surface => surface.Source.Kind == GraphNodeKind.Surface));
        var pipeline = model.BuildPipeline(UiUnderstandingLevel.RawDataStreams);
        var family = Assert.Single(pipeline.Nodes, node => node.Kind == UiPipelineNodeKind.NativeSurface);
        Assert.Equal(2, model.ResolvePipelineSurfaces(family).Count);
        var familyVariants = model.VariantsFor(model.ResolvePipelineSurfaces(family));
        Assert.Equal("ok", familyVariants[0].CaptureStatus);
        Assert.Equal("supplementary", familyVariants[1].RecordKind);
        Assert.Single(RawVariantPresentation.Filter(familyVariants, false, false));
        Assert.Equal(2, pipeline.Nodes.Count(node => node.Kind == UiPipelineNodeKind.StreamEvidence));
        Assert.DoesNotContain(pipeline.Nodes.Where(node => node.Kind == UiPipelineNodeKind.NativeSurface), node => node.DisplayName.Contains("Unknown"));
        foreach (var control in layer.Controls.Where(control => UiMappingReadModel.Property(control.Source, "rawEvidenceKind") == "image-derived"))
        {
            var observation = Assert.Single(control.Source.Observations!);
            Assert.Equal(0, observation.EffectiveOwnerHwnd);
            Assert.Equal("source-image", observation.OwnershipReason);
        }
        var firstWindow = layer.Surfaces.Single(surface => surface.Source.Kind == GraphNodeKind.Window && surface.Evidence[0].FrameSequence == 1);
        Assert.Equal("partial", UiMappingReadModel.Property(firstWindow.Source, "pixelCoverage"));
        Assert.Equal(2, firstWindow.ControlCount);
        Assert.Equal(2, layer.Controls.Where(control => control.OwnerSurfaceId == firstWindow.Id)
            .Select(control => UiMappingReadModel.Property(control.Source, "rawStreamId")).Distinct().Count());
    }

    [Fact]
    public void SameClassWindowsInOneFrameRemainSeparateVariants()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        frame = frame with { ScopedWindows = [frame.Window, frame.Window with { Hwnd = 2, Bounds = new(900, 0, 800, 600) }] };
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], [])));
        var family = Assert.Single(model.BuildPipeline(UiUnderstandingLevel.RawDataStreams).Nodes, node => node.Kind == UiPipelineNodeKind.NativeSurface);
        var variants = model.VariantsFor(model.ResolvePipelineSurfaces(family));
        Assert.Equal(2, variants.Count);
        Assert.Equal(2, variants.Select(variant => variant.SurfaceId).Distinct().Count());
        Assert.Equal(2, variants.Select(variant => variant.DisplayName).Distinct().Count());
        Assert.All(variants, variant => Assert.Equal(1, variant.FrameSequence));
        // Reselecting the family in either navigation pane must keep the exact
        // window instance, even when another window shares its frame and class.
        var selected = variants[1];
        var familySurfaces = model.ResolvePipelineSurfaces(family);
        Assert.Equal(selected, RawVariantPresentation.SelectionInScope(selected, familySurfaces));
        Assert.Null(RawVariantPresentation.SelectionInScope(selected,
            familySurfaces.Where(surface => surface.Id != selected.SurfaceId)));
    }

    [Theory]
    [InlineData("#32770", 0L, 0L, false, "dialog-window")]
    [InlineData("Popup", 2147483648L, 128L, false, "borderless-tool-window")]
    [InlineData("Normal", 13565952L, 0L, false, "normal-window")]
    [InlineData("Tool", 13565952L, 0L, true, "tool-window")]
    public void NativeFamiliesUseRecordedWindowAttributes(string className, long style, long exStyle, bool tool, string expected)
    {
        var window = new WindowObservation(1, 1, 1, className, "", new(0, 0, 100, 100), true, true, false, false, 96,
            Style: style, ExStyle: exStyle, IsToolWindow: tool);
        Assert.Equal(expected, RawObservationSemantics.NativeKind(window));
    }
}

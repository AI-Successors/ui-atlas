using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RawFrameEvidenceTests
{
    [Fact]
    public void RawGraphPreservesIndependentProvidersAndEmptyWindowsAcrossReopen()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var first = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var control = first.Automation.First() with { RuntimeId = "same-id", ParentRuntimeId = "", WindowHwnd = first.Window.Hwnd };
        var emptyWindow = first.Window with { Hwnd = 99, Title = "Empty window" };
        first = first with { ScopedWindows = [first.Window, emptyWindow], RecordKind = "observation", RawStreams = [
            Stream(first, "uia", control), Stream(first, "msaa", control with { Name = "Independent MSAA record" })] };
        var second = first with { Sequence = 2, TimestampUtc = first.TimestampUtc.AddSeconds(1),
            RawStreams = [Stream(first, "uia")], Automation = [] };
        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [first, second], []));
        var path = Path.Combine(temp.Path, "raw.db");
        SqliteGraphStore.Save(graph, path);
        var model = new UiMappingReadModel(SqliteGraphStore.Load(path));
        var raw = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        Assert.Equal(4, raw.Surfaces.Count);
        Assert.Equal(2, raw.Controls.Count);
        Assert.Contains(raw.Controls, item => UiMappingReadModel.Property(item.Source, "rawStreamSource") == "msaa");
        Assert.All(raw.Surfaces.Where(surface => surface.Evidence.Any(evidence => evidence.FrameSequence == 2)),
            surface => Assert.Empty(raw.ControlsForSurface(surface.Id, 2, manifest.SessionId)));
        Assert.Contains(raw.Surfaces, surface => surface.DisplayName == "Empty window");
        Assert.All(raw.Controls, item => Assert.Equal(1, Assert.Single(item.Evidence).FrameSequence));
    }

    [Fact]
    public void MissingRawPixelsNeverBorrowCompatibleEarlierPixels()
    {
        using var temp = new TempDirectory();
        var source = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true, controlDelta: true);
        var path = Rewrite(source, temp.Path, frame => frame.Sequence == 2
            ? frame with { Window = ReadFrame(source, 1).Window, ScopedWindows = ReadFrame(source, 1).ScopedWindows }
            : frame);
        using var reader = UiEvidenceReader.Open(path);
        var evidence = new EvidenceRef("golden", 2, "raw/observations/frame-000002.json", null, null);
        Assert.Null(reader.Read(evidence));
        Assert.True(Assert.IsType<UiEvidenceImage>(reader.Read(evidence, allowInheritedPixels: true)).InheritedPixels);
    }

    [Theory]
    [InlineData("pixels")]
    [InlineData("time")]
    [InlineData("duplicate")]
    public void RejectsInvalidStreamProvenance(string corruption)
    {
        using var temp = new TempDirectory();
        var source = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true);
        var path = Rewrite(source, temp.Path, frame =>
        {
            var stream = Stream(frame, "uia");
            return frame with { RawStreams = corruption switch {
                "pixels" => [stream with { ScreenshotEntry = "raw/frames/frame-999999.png" }],
                "time" => [stream with { CompletedUtc = frame.TimestampUtc.AddSeconds(1) }],
                _ => [stream, stream]
            } };
        });
        using var bundle = RecordingBundle.Open(path);
        Assert.Contains(RecordingBundleValidator.Validate(bundle).Issues, issue => issue.Code == "bundle.raw-streams");
    }

    [Fact]
    public void SealedStreamsRedactPrivateTextWithoutChangingCoordinatesOrParentage()
    {
        using var temp = new TempDirectory();
        var frame = ReadFrame(SyntheticBundleFactory.Create(temp.Path), 1);
        var control = frame.Automation.First() with { ControlType = "Edit", Name = "private text", OcrText = "private text" };
        var sealedStreams = RawFrameEvidence.Seal(frame, frame.TimestampUtc, frame.TimestampUtc,
            frame.TimestampUtc, "not-requested", [Stream(frame, "uia", control)]);
        var retained = Assert.Single(sealedStreams.Single(stream => stream.Source == "uia").Controls);
        Assert.Equal("[redacted]", retained.Name);
        Assert.Null(retained.OcrText);
        Assert.Equal(control.Bounds, retained.Bounds);
        Assert.Equal(control.ParentRuntimeId, retained.ParentRuntimeId);
    }

    internal static RawDataStreamObservation Stream(FrameObservation frame, string source, params AutomationObservation[] controls) =>
        new(source, source, frame.TimestampUtc, frame.TimestampUtc, "ok", controls, controls.Length);

    private static FrameObservation ReadFrame(string path, long sequence)
    {
        using var bundle = RecordingBundle.Open(path);
        return bundle.ReadJson<FrameObservation>($"raw/observations/frame-{sequence:D6}.json");
    }

    private static string Rewrite(string source, string directory, Func<FrameObservation, FrameObservation> transform)
    {
        using var bundle = RecordingBundle.Open(source);
        using var writer = new RecordingBundleWriter(Path.Combine(directory, "rewrite-" + Guid.NewGuid().ToString("N")));
        foreach (var entry in bundle.Entries.Where(entry => entry is not ("manifest.json" or "hashes.sha256")))
            if (entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
                writer.WriteJson(entry, transform(bundle.ReadJson<FrameObservation>(entry)));
            else writer.WriteBytes(entry, bundle.ReadBytes(entry));
        writer.WriteJson("manifest.json", bundle.ReadJson<RecordingManifest>("manifest.json") with { Files = writer.DescribeEntries() });
        var output = Path.Combine(directory, "rewrite-" + Guid.NewGuid().ToString("N") + ".mlrec");
        writer.Complete(output);
        return output;
    }
}

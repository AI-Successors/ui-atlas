using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RawFrameEvidenceTests
{
    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(0, 0, 5000, 5000)]
    [InlineData(int.MaxValue - 2, 0, 10, 10)]
    [InlineData(0, 0, -1, 10)]
    [InlineData(0, 0, 0, 10)]
    public void UnusableProviderBoundsSurviveRawBuildAndReopenWithoutBecomingDrawable(int x, int y, int width, int height)
    {
        using var temp = new TempDirectory();
        var bounds = new RectI(x, y, width, height);
        var path = Rewrite(SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true), temp.Path, frame => frame with {
            RawStreams = [Stream(frame, "uia", frame.Automation.First() with {
                RuntimeId = "provider-invalid-bounds", Bounds = bounds, WindowHwnd = frame.Window.Hwnd })]
        });
        Assert.True(RecordingBundleValidator.Validate(path).IsValid);
        var graph = new RecordingGraphBuilder().Build(path);
        var database = Path.Combine(temp.Path, "raw-geometry.db");
        SqliteGraphStore.Save(graph, database);
        var reopened = SqliteGraphStore.Load(database);
        Assert.True(GraphValidator.Validate(reopened).IsValid);
        Assert.Equal(graph.Metadata.SemanticHash, reopened.Metadata.SemanticHash);
        var raw = new UiMappingReadModel(reopened).LayerFor(UiUnderstandingLevel.RawDataStreams);
        using var evidenceReader = UiEvidenceReader.Open(path);
        Assert.NotEmpty(raw.Controls);
        Assert.All(raw.Controls, control => {
            Assert.Equal(bounds, Assert.Single(control.Source.Observations!).Control.Bounds);
            Assert.Equal(bounds, Assert.Single(control.Evidence).Bounds);
            Assert.Equal("unavailable", UiMappingReadModel.Property(control.Source, "geometryStatus"));
            Assert.Null(UiMapPresentation.ProjectToSurface(bounds, new RectI(0, 0, 800, 600)));
            var evidence = Assert.Single(control.Evidence);
            if (evidence.ScreenshotEntry is not null)
                Assert.Null(Assert.IsType<UiEvidenceImage>(evidenceReader.Read(evidence)).Highlight);
            else Assert.Null(evidenceReader.Read(evidence));
        });
        Assert.True(GraphValidator.Validate(GraphExport.ApplyProfile(reopened, false)).IsValid);
        Assert.DoesNotContain(reopened.Nodes.Where(node =>
            UiMappingReadModel.Property(node, "layer") != "raw-data-streams"),
            node => node.Observations?.Any(value => value.Control.RuntimeId == "provider-invalid-bounds") == true);
    }

    [Fact]
    public void RawGeometryAllowanceDoesNotRelaxDerivedGeometryOrEvidencePrivacy()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var derived = graph.Nodes.First(node => node.Kind == GraphNodeKind.Control &&
            UiMappingReadModel.Property(node, "layer") == "raw-world");
        var oversized = derived with { Observations = derived.Observations!.Select(value => value with {
            Control = value.Control with { Bounds = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue) }
        }).ToArray() };
        AssertObservationRejected(oversized);
        var raw = graph.Nodes.First(node => node.Kind == GraphNodeKind.Control &&
            UiMappingReadModel.Property(node, "layer") == "raw-data-streams");
        AssertObservationRejected(raw with { Observations = [raw.Observations![0], raw.Observations[0]] });
        AssertObservationRejected(raw with { Observations = [raw.Observations![0] with { FrameSequence = 999 }] });
        AssertObservationRejected(raw with { Observations = [raw.Observations![0] with {
            Control = raw.Observations[0].Control with { Bounds = null! } }] });
        AssertObservationRejected(raw with { Observations = [raw.Observations![0] with {
            Control = raw.Observations[0].Control with { Name = new string('x', 16385) } }] });
        Assert.Contains(GraphValidator.Validate(graph with { Metadata = graph.Metadata with {
            PrivacyProfile = FormatVersions.SafeExportProfile } }).Issues, issue => issue.Code == "graph.observation");

        void AssertObservationRejected(GraphNode changed)
        {
            var nodes = graph.Nodes.Select(node => node.Id == changed.Id ? changed : node).ToArray();
            var malformed = graph with { Nodes = nodes, Metadata = graph.Metadata with {
                SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };
            Assert.Contains(GraphValidator.Validate(malformed).Issues, issue => issue.Code == "graph.observation");
        }
    }

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

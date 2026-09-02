using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Tests;

public sealed class UiEvidenceReaderTests
{
    [Fact]
    public void ReadsOnlyMatchingValidatedFrameEvidence()
    {
        using var directory = new TempDirectory();
        var path = SyntheticBundleFactory.Create(directory.Path, includeScreenshot: true);
        using var reader = UiEvidenceReader.Open(path);
        var evidence = new EvidenceRef("golden", 1, "raw/observations/frame-000001.json", new(10, 10, 80, 24), "raw/frames/frame-000001.png");

        var image = Assert.IsType<UiEvidenceImage>(reader.Read(evidence));
        Assert.True(image.Png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(new RectI(10, 10, 80, 24), image.Highlight);
        Assert.Throws<InvalidDataException>(() => reader.Read(evidence with { ScreenshotEntry = "raw/frames/not-the-frame.png" }));
        Assert.Throws<InvalidDataException>(() => reader.Read(evidence with { BundleId = "another-bundle" }));
        Assert.Throws<InvalidDataException>(() => reader.Read(evidence with { FrameSequence = 2 }));
        Assert.Throws<InvalidDataException>(() => reader.Read(evidence with { ObservationEntry = "raw/observations/other.json" }));
        Assert.Throws<InvalidDataException>(() => reader.Read(evidence with { ScreenshotEntry = "raw/evidence.png" }));
        Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None));
    }

    [Fact]
    public void ReadsEvidenceAcrossMultipleBundlesWithoutFrameCollisions()
    {
        using var directory = new TempDirectory();
        var first = SyntheticBundleFactory.Create(directory.Path, "first.mlrec", includeScreenshot: true, sessionId: "session-a");
        var second = SyntheticBundleFactory.Create(directory.Path, "second.mlrec", includeScreenshot: true, sessionId: "session-b");
        using var reader = UiEvidenceReader.Open([first, second]);

        Assert.Equal(["session-a", "session-b"], reader.SessionIds.OrderBy(value => value, StringComparer.Ordinal));
        Assert.IsType<UiEvidenceImage>(reader.Read(new EvidenceRef("session-a", 1, "raw/observations/frame-000001.json", new(1, 1, 10, 10), "raw/frames/frame-000001.png")));
        Assert.IsType<UiEvidenceImage>(reader.Read(new EvidenceRef("session-b", 1, "raw/observations/frame-000001.json", new(1, 1, 10, 10), "raw/frames/frame-000001.png")));
        Assert.Throws<InvalidDataException>(() => reader.Read(new EvidenceRef("missing", 1, "raw/observations/frame-000001.json", new(1, 1, 10, 10), "raw/frames/frame-000001.png")));
    }

    [Fact]
    public void PopupDeltaUsesItsOwnScreenshotOrigin()
    {
        using var directory = new TempDirectory();
        var path = SyntheticBundleFactory.Create(directory.Path, includeScreenshot: true, popupDelta: true);
        using var reader = UiEvidenceReader.Open(path);

        var image = Assert.IsType<UiEvidenceImage>(reader.Read(new EvidenceRef(
            "golden", 2, "raw/observations/frame-000002.json",
            new RectI(870, 180, 180, 36), "raw/frames/frame-000002.png")));

        Assert.Equal(new RectI(30, 60, 180, 36), image.Highlight);
    }

    [Fact]
    public void ControlDeltaInheritsScreenshotFromItsBaseFrame()
    {
        using var directory = new TempDirectory();
        var path = SyntheticBundleFactory.Create(directory.Path, includeScreenshot: true, controlDelta: true);
        using var reader = UiEvidenceReader.Open(path);

        var image = Assert.IsType<UiEvidenceImage>(reader.Read(new EvidenceRef(
            "golden", 2, "raw/observations/frame-000002.json",
            new RectI(165, 15, 120, 36), null)));

        Assert.Equal("raw/frames/frame-000001.png", image.Entry);
        Assert.Equal(new RectI(165, 15, 120, 36), image.Highlight);
        Assert.Equal(new RectI(0, 0, 800, 600), image.ScreenshotBounds);
    }
}

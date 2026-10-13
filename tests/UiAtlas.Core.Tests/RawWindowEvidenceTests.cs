using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class RawWindowEvidenceTests
{
    [Fact]
    public void ExplicitFirstBundlePreservesTheResumedSessionAndRejectsUnrelatedEvidence()
    {
        using var temp = new TempDirectory();
        var first = SyntheticBundleFactory.Create(temp.Path, "first.mlrec", includeScreenshot: true, sessionId: "first");
        var second = SyntheticBundleFactory.Create(temp.Path, "second.mlrec", includeScreenshot: true, sessionId: "resumed");
        var resolved = EvidenceBundleResolver.Resolve(["first", "resumed"], [first], [first, second, Path.Combine(temp.Path, "missing")]);
        using var reader = UiEvidenceReader.Open(resolved);
        Assert.Equal(new[] { "first", "resumed" }, reader.SessionIds);
        Assert.NotNull(reader.Read(new("resumed", 1, "raw/observations/frame-000001.json", null)));
        Assert.Throws<InvalidDataException>(() => EvidenceBundleResolver.Resolve(["first"], [second], [first]));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("owned-menu")]
    [InlineData("foreign-frame")]
    [InlineData("foreign-window")]
    [InlineData("missing-entry")]
    [InlineData("late-image")]
    [InlineData("duplicate-window")]
    [InlineData("wrong-size")]
    public void IndependentImagesAreValidatedAndSelectedByExactWindowAndFrame(string mode)
    {
        using var temp = new TempDirectory();
        var source = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true);
        using var original = RecordingBundle.Open(source);
        var frame = original.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var canonical = $"raw/frames/frame-000001-window-{frame.Window.Hwnd:X}.png";
        var png = original.ReadBytes(frame.FrameEntry);
        var imageBounds = frame.Window.Bounds with {
            Width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
            Height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) };
        var plane = new WindowImageObservation(frame.Window.Hwnd, imageBounds, canonical,
            frame.TimestampUtc, frame.TimestampUtc, "windows-graphics-capture", "ok");
        if (mode == "owned-menu") frame = frame with { ScopedWindows = [frame.Window, frame.Window with {
            Hwnd = 987, OwnerHwnd = frame.Window.Hwnd, ClassName = "Net UI Tool Window", IsToolWindow = true,
            Style = 0x80000000L, Bounds = new(frame.Window.Bounds.X + 20, frame.Window.Bounds.Y + 20, 30, 30), ZOrder = -1 }] };
        plane = mode switch {
            "foreign-frame" => plane with { Entry = canonical.Replace("000001", "000002") },
            "foreign-window" => plane with { Hwnd = 987654321 },
            "late-image" => plane with { CompletedUtc = frame.TimestampUtc.AddSeconds(1) },
            "wrong-size" => plane with { Bounds = imageBounds with { Width = imageBounds.Width + 1 } },
            _ => plane
        };
        var output = Path.Combine(temp.Path, "planes.mlrec");
        using (var writer = new RecordingBundleWriter(Path.Combine(temp.Path, "stage")))
        {
            foreach (var entry in original.Entries.Where(entry => entry is not ("manifest.json" or "hashes.sha256")))
                if (entry == "raw/observations/frame-000001.json")
                    writer.WriteJson(entry, frame with { WindowImages = mode == "duplicate-window" ? [plane, plane] : [plane] });
                else writer.WriteBytes(entry, original.ReadBytes(entry));
            if (mode != "missing-entry") writer.WriteBytes(canonical, original.ReadBytes(frame.FrameEntry));
            writer.WriteJson("manifest.json", original.ReadJson<RecordingManifest>("manifest.json") with { Files = writer.DescribeEntries() });
            writer.Complete(output);
        }
        var report = RecordingBundleValidator.Validate(output);
        if (mode is not ("valid" or "owned-menu"))
        { Assert.Contains(report.Issues, issue => issue.Code == "bundle.window-image"); return; }
        Assert.True(report.IsValid, string.Join(";", report.Issues.Select(issue => issue.Message)));
        using var reader = UiEvidenceReader.Open(output);
        var reference = new EvidenceRef("golden", 1, "raw/observations/frame-000001.json", frame.Window.Bounds);
        var image = Assert.IsType<UiEvidenceImage>(reader.ReadWindow(reference, frame.Window.Hwnd));
        Assert.Equal(mode == "owned-menu" ? frame.FrameEntry : canonical, image.Entry);
        Assert.Contains(mode == "owned-menu" ? "owned popup" : "independent HWND", image.PixelProvenance);
        Assert.Null(image.OccludedRegions);
        Assert.Equal(frame.FrameEntry, reader.Read(reference)!.Entry);
        Assert.Throws<InvalidDataException>(() => reader.ReadWindow(reference, 987654321));
    }

    [Fact]
    public void LegacyFontCropMasksColorsAndMainCropMasksBothWithoutChangingOriginalEvidence()
    {
        var root = new WindowObservation(1, 1, 1, "Main", "Main", new(0, 0, 800, 600), true, true, false, false, 96, ZOrder: 3);
        var font = root with { Hwnd = 2, OwnerHwnd = 1, Bounds = new(100, 100, 400, 400), ZOrder = 2 };
        var colors = root with { Hwnd = 3, OwnerHwnd = 2, Bounds = new(200, 200, 200, 200), ZOrder = 1 };
        var frame = new FrameObservation(1, DateTimeOffset.UtcNow, "", root, [], false, "ok", "test", [root, font, colors]);
        Assert.Equal(new[] { font.Bounds, colors.Bounds }, RawSurfaceImageLayout.Occluders(frame, root));
        Assert.Equal(new[] { colors.Bounds }, RawSurfaceImageLayout.Occluders(frame, font));
        Assert.Empty(RawSurfaceImageLayout.Occluders(frame, colors));
        Assert.Empty(RawSurfaceImageLayout.Occluders(frame with { ScopedWindows = [root, font, colors with { IsVisible = false }] }, font));
    }

    [Theory]
    [InlineData("ok", "stable", true)]
    [InlineData("partial", "stable", true)]
    [InlineData("interrupted", "changed", false)]
    [InlineData("partial", "unverified", false)]
    [InlineData("cancelled", "interrupted", false)]
    public void OnlyStableAcquisitionsPermitSynchronizedOverlay(string status, string stability, bool allowed)
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        Assert.False(RawVariantPresentation.AllowsOverlay(frame));
        Assert.False(RawVariantPresentation.AllowsOverlay(null));
        Assert.Equal(allowed, RawVariantPresentation.AllowsOverlay(frame with { Acquisition = new(
            RawObservationSemantics.Version, "observation:1", frame.TimestampUtc, frame.TimestampUtc, status, "screen", stability) }));
    }
}

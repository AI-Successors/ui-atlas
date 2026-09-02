using System.IO.Compression;
using System.Text;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class BundleSecurityTests
{
    [Fact]
    public void AbandonedStagingCleanupRequiresRecorderOwnershipMarker()
    {
        var owned = Path.Combine(Path.GetTempPath(), "ui-atlas-recording-test-" + Guid.NewGuid().ToString("N"));
        var unowned = Path.Combine(Path.GetTempPath(), "ui-atlas-recording-test-" + Guid.NewGuid().ToString("N"));
        using var writer = new RecordingBundleWriter(owned);
        Directory.CreateDirectory(unowned);
        Directory.SetCreationTimeUtc(owned, DateTime.UtcNow.AddDays(-2));
        Directory.SetCreationTimeUtc(unowned, DateTime.UtcNow.AddDays(-2));
        try
        {
            RecordingBundleWriter.CleanupAbandonedStaging(TimeSpan.FromDays(1));
            Assert.False(Directory.Exists(owned));
            Assert.True(Directory.Exists(unowned));
        }
        finally
        {
            if (Directory.Exists(unowned)) Directory.Delete(unowned);
        }
    }

    [Fact]
    public void ValidBundlePassesHashes()
    {
        using var temp = new TempDirectory();
        var path = SyntheticBundleFactory.Create(temp.Path);
        Assert.True(RecordingBundleValidator.Validate(path).IsValid);
    }

    [Fact]
    public void LegacyEmbeddedPopupDeltaIsRecoveredWithWarning()
    {
        using var temp = new TempDirectory();
        var path = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true, popupDelta: true,
            legacyEmbeddedPopupDelta: true);

        var report = RecordingBundleValidator.Validate(path);

        Assert.True(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "bundle.observation.legacy-scope" && issue.Severity == "warning");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/drive")]
    [InlineData("a//b")]
    public void UnsafePathsAreRejected(string entryName)
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "bad.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var content = new StreamWriter(archive.CreateEntry(entryName).Open())) content.Write("x");
        Assert.False(RecordingBundleValidator.Validate(path).IsValid);
    }

    [Fact]
    public void CaseCollisionsAreRejected()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "bad.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("A.json");
            archive.CreateEntry("a.json");
        }
        Assert.False(RecordingBundleValidator.Validate(path).IsValid);
    }

    [Fact]
    public void CorruptChecksumIsRejected()
    {
        using var temp = new TempDirectory();
        var path = SyntheticBundleFactory.Create(temp.Path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("raw/input-events.jsonl")!;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("raw/input-events.jsonl").Open());
            writer.Write("tampered");
        }
        Assert.False(RecordingBundleValidator.Validate(path).IsValid);
    }

    [Fact]
    public void ScreenshotOutsideCanonicalFrameNamespaceIsRejected()
    {
        using var temp = new TempDirectory();
        var path = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true, screenshotEntry: "raw/evidence.png");
        var report = RecordingBundleValidator.Validate(path);
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code == "bundle.image" || x.Code == "bundle.observation");
    }

    [Fact]
    public void ReservedDeviceAliasIsRejected()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "bad.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create)) archive.CreateEntry("raw/CON.txt");
        Assert.False(RecordingBundleValidator.Validate(path).IsValid);
    }

    [Fact]
    public void CompressionBombRatioIsRejectedBeforeRead()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "bad.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("raw/zeros.bin", CompressionLevel.SmallestSize).Open())
            output.Write(new byte[2 * 1024 * 1024]);
        Assert.False(RecordingBundleValidator.Validate(path).IsValid);
    }

    [Fact]
    public void DuplicateEntriesAndLinkMetadataAreRejected()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "bad.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("raw/duplicate.bin");
            archive.CreateEntry("raw/duplicate.bin");
            var link = archive.CreateEntry("raw/link.bin");
            link.ExternalAttributes = unchecked((int)0xA0000000);
        }
        using var read = ZipFile.OpenRead(path);
        var issues = BundleSecurity.Inspect(read);
        Assert.Contains(issues, x => x.StartsWith("duplicate-or-case-collision:", StringComparison.Ordinal));
        Assert.Contains(issues, x => x.StartsWith("link-or-reparse-entry:", StringComparison.Ordinal));
    }

    [Fact]
    public void EntryCountAndByteLimitsAreEnforcedBeforeExtraction()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "bounded.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var first = archive.CreateEntry("one.bin").Open()) first.Write(new byte[5]);
            using (var second = archive.CreateEntry("two.bin").Open()) second.Write(new byte[5]);
        }
        using var read = ZipFile.OpenRead(path);
        var issues = BundleSecurity.Inspect(read, new BundleLimits(MaxEntries: 1, MaxEntryBytes: 4, MaxTotalBytes: 6, MaxCompressionRatio: 1_000));
        Assert.Contains("entry-count-limit", issues);
        Assert.Contains(issues, x => x.StartsWith("entry-size-limit:", StringComparison.Ordinal));
        Assert.Contains("total-size-limit", issues);
    }

    [Fact]
    public void TextEntriesRejectInvalidUtf8InsteadOfReplacingBytes()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "invalid-text.mlrec");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("raw/invalid.jsonl").Open())
            output.Write(new byte[] { 0xC3, 0x28 });

        using var bundle = RecordingBundle.Open(path);
        Assert.Throws<DecoderFallbackException>(() => bundle.ReadText("raw/invalid.jsonl"));
    }

    [Fact]
    public void ValidatorRejectsARehashedBundleContainingInvalidUtf8()
    {
        using var temp = new TempDirectory();
        var path = SyntheticBundleFactory.Create(temp.Path, invalidHealthUtf8: true);
        var report = RecordingBundleValidator.Validate(path);
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code is "bundle.invalid");
    }

    [Fact]
    public void DefaultArchiveEntryLimitMatchesThePublicV1Bound()
    {
        Assert.Equal(RecordingContractLimits.MaxBundleEntries, new BundleLimits().MaxEntries);
    }
}

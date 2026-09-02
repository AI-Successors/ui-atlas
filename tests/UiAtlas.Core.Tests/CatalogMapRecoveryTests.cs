using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class CatalogMapRecoveryTests
{
    [Fact]
    public void RecoverCompletedMaps_RebuildsMissingMapAndSessionManifest()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        var id = "20260101-000000-excel-deadbeef";
        var recordingPath = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec", sessionId: id);

        var recovered = CatalogMapRecovery.RecoverCompletedMaps(catalog);

        Assert.Equal(1, recovered);
        Assert.True(File.Exists(catalog.MapPath(id)));
        Assert.True(File.Exists(catalog.MapSessionPath(id)));

        var manifest = LogicalMapSessionStore.Load(catalog.MapSessionPath(id));
        Assert.Equal(id, manifest.LogicalMapId);
        Assert.Equal("Synthetic", manifest.ProcessName);
        Assert.Single(manifest.Recordings);
        Assert.Equal(id, manifest.Recordings[0].SessionId);
        Assert.Equal(Path.GetFullPath(recordingPath), manifest.Recordings[0].RecordingPath);
    }

    [Fact]
    public void RecoverCompletedMaps_DoesNotRestorePermanentlyDeletedMap()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        var id = "20260101-000000-excel-deleted";
        var recordingPath = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec", sessionId: id);
        var graph = new UiAtlas.Core.Build.RecordingGraphBuilder().Build(recordingPath);
        SqliteGraphStore.Save(graph, catalog.MapPath(id));

        Assert.True(CatalogMapRecovery.DeleteUiAtlasmanently(catalog, id));
        var recovered = CatalogMapRecovery.RecoverCompletedMaps(catalog);

        Assert.Equal(0, recovered);
        Assert.False(File.Exists(catalog.MapPath(id)));
        Assert.True(File.Exists(recordingPath));
        Assert.True(catalog.IsUiAtlasmanentlyDeleted(id));
    }

    [Fact]
    public void RecoverCompletedMaps_SkipsResumeBundles()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        var id = "20260101-000000-excel-deadbeef-resume02";
        SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec", sessionId: id);

        var recovered = CatalogMapRecovery.RecoverCompletedMaps(catalog);

        Assert.Equal(0, recovered);
        Assert.False(File.Exists(catalog.MapPath(id)));
        Assert.False(File.Exists(catalog.MapSessionPath(id)));
    }

    [Fact]
    public void RecoverCompletedMaps_RebuildsTopLevelPartialAutoBundle()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        var id = "20260101-000000-excel-feedface";
        SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory,
            id + ".mlrec",
            sessionId: id,
            outcome: RecordingOutcome.Partial,
            markers: ["auto-tabs:selected:home"]);

        var recovered = CatalogMapRecovery.RecoverCompletedMaps(catalog);

        Assert.Equal(1, recovered);
        Assert.True(File.Exists(catalog.MapPath(id)));
        Assert.True(File.Exists(catalog.MapSessionPath(id)));
    }

    [Fact]
    public void RecoverCompletedMaps_DoesNotRecoverOrdinaryManualPartialBundle()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        var id = "20260101-000000-excel-cafebabe";
        SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory,
            id + ".mlrec",
            sessionId: id,
            outcome: RecordingOutcome.Partial);

        var recovered = CatalogMapRecovery.RecoverCompletedMaps(catalog);

        Assert.Equal(0, recovered);
        Assert.False(File.Exists(catalog.MapPath(id)));
        Assert.False(File.Exists(catalog.MapSessionPath(id)));
    }
}

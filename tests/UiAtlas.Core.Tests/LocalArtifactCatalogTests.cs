using UiAtlas.Core.Build;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class LocalArtifactCatalogTests
{
    [Fact]
    public void CatalogListsAndRecoverablyArchivesArtifacts()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "synthetic-001";
        var bundle = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec");
        var graph = new RecordingGraphBuilder().Build(bundle);
        SqliteGraphStore.Save(graph, catalog.MapPath(id));

        var recording = Assert.Single(catalog.ListRecordings());
        Assert.Equal(id, recording.Id);
        Assert.Equal("complete", recording.Outcome);
        Assert.Equal(2, recording.FrameCount);
        var map = Assert.Single(catalog.ListMaps());
        Assert.Equal(id, map.Id);
        Assert.Equal("valid", map.Status);
        Assert.Equal(graph.Nodes.Count, map.NodeCount);
        Assert.Equal(graph.Edges.Count, map.EdgeCount);

        var archivedRecording = catalog.ArchiveRecording(id);
        var archivedMap = catalog.ArchiveMap(id);

        Assert.False(File.Exists(catalog.RecordingPath(id)));
        Assert.False(File.Exists(catalog.MapPath(id)));
        Assert.True(File.Exists(archivedRecording));
        Assert.True(File.Exists(archivedMap));
        Assert.StartsWith(catalog.TrashDirectory, archivedRecording, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(catalog.TrashDirectory, archivedMap, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogRecoverablyArchivesAllRecordingsAndMaps()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        foreach (var id in new[] { "synthetic-001", "synthetic-002" })
        {
            var bundle = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec");
            SqliteGraphStore.Save(new RecordingGraphBuilder().Build(bundle), catalog.MapPath(id));
        }

        Assert.Equal(2, catalog.ArchiveAllRecordings());
        Assert.Equal(2, catalog.ArchiveAllMaps());
        Assert.Empty(catalog.ListRecordings());
        Assert.Empty(catalog.ListMaps());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(catalog.TrashDirectory, "recordings"), "*.mlrec").Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(catalog.TrashDirectory, "maps"), "*.db").Length);
        Assert.Equal(0, catalog.ArchiveAllRecordings());
        Assert.Equal(0, catalog.ArchiveAllMaps());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:\\escape")]
    [InlineData("bad.id")]
    [InlineData("")]
    public void ArtifactIdsCannotEscapeCatalog(string id)
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));

        Assert.False(LocalArtifactCatalog.IsValidId(id));
        Assert.Throws<ArgumentException>(() => catalog.MapPath(id));
    }

    [Fact]
    public void MalformedCatalogArtifactsAreReportedWithoutBeingOpenedAsValid()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        File.WriteAllText(catalog.RecordingPath("broken-recording"), "not a bundle");
        File.WriteAllText(catalog.MapPath("broken-map"), "not sqlite");

        Assert.Equal("invalid", Assert.Single(catalog.ListRecordings()).Outcome);
        Assert.Equal("invalid", Assert.Single(catalog.ListMaps()).Status);
    }

    [Fact]
    public void CatalogResolvesEvidenceOnlyWhenMapSourceMatchesRecordingSession()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "matching-evidence";
        var bundle = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "session-a.mlrec", sessionId: "session-a");
        SqliteGraphStore.Save(new RecordingGraphBuilder().Build(bundle), catalog.MapPath(id));

        Assert.Equal(catalog.RecordingPath("session-a"), catalog.MatchingRecordingPath(id));

        var other = SyntheticBundleFactory.Create(temp.Path, "other.mlrec", sessionId: "session-b");
        SqliteGraphStore.Save(new RecordingGraphBuilder().Build(other), catalog.MapPath(id));
        Assert.Null(catalog.MatchingRecordingPath(id));
    }

    [Fact]
    public void CatalogResolvesAllMatchingEvidenceBundlesForMergedMap()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "merged-evidence";
        var first = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "session-a.mlrec", sessionId: "session-a");
        var second = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "session-b.mlrec", sessionId: "session-b");
        SqliteGraphStore.Save(new RecordingGraphBuilder().Build([first, second], logicalMapId: id), catalog.MapPath(id));

        Assert.Equal(
            [catalog.RecordingPath("session-a"), catalog.RecordingPath("session-b")],
            catalog.MatchingRecordingPaths(id).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        Assert.Null(catalog.MatchingRecordingPath(id));
    }

    [Fact]
    public void CatalogListsMapsFromSqliteSummaryWithoutMaterializingWholeGraph()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "summary-map";
        var bundle = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec");
        var graph = new RecordingGraphBuilder().Build(bundle);
        SqliteGraphStore.Save(graph, catalog.MapPath(id));

        var map = Assert.Single(catalog.ListMaps());

        Assert.Equal(id, map.Id);
        Assert.Equal(graph.Metadata.BuiltUtc, map.BuiltUtc);
        Assert.Equal(graph.Nodes.Count, map.NodeCount);
        Assert.Equal(graph.Edges.Count, map.EdgeCount);
    }

    [Fact]
    public void CatalogExportCanOnlyBeArchivedFromContainedExportsDirectory()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        var export = catalog.DefaultExportPath("safe-export");
        File.WriteAllText(export, "{}");

        var archived = catalog.ArchiveExport(export);

        Assert.False(File.Exists(export));
        Assert.True(File.Exists(archived));
        Assert.False(catalog.IsCatalogExport(Path.Combine(temp.Path, "outside.json")));
        Assert.Throws<ArgumentException>(() => catalog.ArchiveExport(Path.Combine(temp.Path, "outside.json")));
        var nested = Path.Combine(catalog.ExportsDirectory, "nested");
        Directory.CreateDirectory(nested);
        var nestedExport = Path.Combine(nested, "nested.json");
        File.WriteAllText(nestedExport, "{}");
        Assert.False(catalog.IsCatalogExport(nestedExport));
        Assert.Throws<ArgumentException>(() => catalog.ArchiveExport(nestedExport));
    }

    [Fact]
    public void CatalogCanDeleteUiAtlasmanentlyWithSessionAndExportArtifacts()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "delete-forever";
        var bundle = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec");
        var graph = new RecordingGraphBuilder().Build(bundle);
        SqliteGraphStore.Save(graph, catalog.MapPath(id));
        MapCurationStore.Save(catalog.MapPath(id), MapCurationDocument.Empty(graph.Metadata.EffectiveLogicalMapId));
        File.WriteAllText(catalog.MapSessionPath(id), "{}");
        File.WriteAllText(catalog.DefaultExportPath(id), "{}");
        File.WriteAllText(catalog.DefaultExportPath(id) + ".sha256", "deadbeef");

        var deleted = catalog.DeleteUiAtlasmanently(id);

        Assert.True(deleted);
        Assert.False(File.Exists(catalog.MapPath(id)));
        Assert.False(File.Exists(catalog.MapSessionPath(id)));
        Assert.False(File.Exists(catalog.MapCurationPath(id)));
        Assert.False(File.Exists(catalog.DefaultExportPath(id)));
        Assert.False(File.Exists(catalog.DefaultExportPath(id) + ".sha256"));
        Assert.True(File.Exists(catalog.RecordingPath(id)));
        Assert.True(catalog.IsUiAtlasmanentlyDeleted(id));
        Assert.Empty(catalog.ListMaps());
    }

    [Fact]
    public void CatalogCanClearMapDeletionMarkerForExplicitRestore()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "restore-explicitly";
        var bundle = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec");
        SqliteGraphStore.Save(new RecordingGraphBuilder().Build(bundle), catalog.MapPath(id));
        Assert.True(catalog.DeleteUiAtlasmanently(id));

        catalog.ClearMapDeletionMarker(id);

        Assert.False(catalog.IsUiAtlasmanentlyDeleted(id));
    }

    [Fact]
    public void CatalogDeletesMapWithOnlyItsUnsharedSourceRecordings()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        var shared = SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory, "shared-session.mlrec", sessionId: "shared-session");
        var privateRecording = SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory, "private-session.mlrec", sessionId: "private-session");
        SqliteGraphStore.Save(
            new RecordingGraphBuilder().Build([shared, privateRecording], logicalMapId: "map-to-delete"),
            catalog.MapPath("map-to-delete"));
        SqliteGraphStore.Save(
            new RecordingGraphBuilder().Build([shared], logicalMapId: "map-to-keep"),
            catalog.MapPath("map-to-keep"));

        var result = catalog.DeleteMapAndUnusedRecordingsPermanently("map-to-delete");

        Assert.True(result.MapDeleted);
        Assert.Equal(1, result.RecordingsDeleted);
        Assert.Equal(1, result.SharedRecordingsKept);
        Assert.Equal(0, result.RecordingDeleteFailures);
        Assert.True(result.ReferenceScanComplete);
        Assert.False(File.Exists(catalog.MapPath("map-to-delete")));
        Assert.False(File.Exists(privateRecording));
        Assert.True(File.Exists(shared));
        Assert.True(File.Exists(catalog.MapPath("map-to-keep")));
    }

    [Fact]
    public void CatalogDeletesOnlyRecordingsUnusedBySavedMaps()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        var used = SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory, "used-session.mlrec", sessionId: "used-session");
        var unused = SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory, "unused-session.mlrec", sessionId: "unused-session");
        SqliteGraphStore.Save(
            new RecordingGraphBuilder().Build([used], logicalMapId: "saved-map"),
            catalog.MapPath("saved-map"));

        Assert.Equal(["unused-session"], catalog.ListUnusedRecordingIds());
        var result = catalog.DeleteUnusedRecordingsPermanently();

        Assert.Equal(1, result.RecordingsDeleted);
        Assert.Equal(0, result.RecordingDeleteFailures);
        Assert.True(result.ReferenceScanComplete);
        Assert.True(File.Exists(used));
        Assert.False(File.Exists(unused));
    }

    [Fact]
    public void CatalogDuplicatesMapAsIndependentResumableCopy()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "20260822-120000-synthetic-original";
        var bundle = SyntheticBundleFactory.Create(
            catalog.RecordingsDirectory, "source-session.mlrec", sessionId: "source-session");
        var graph = new RecordingGraphBuilder().Build([bundle], logicalMapId: id);
        SqliteGraphStore.Save(graph, catalog.MapPath(id));
        MapCurationStore.Save(catalog.MapPath(id), MapCurationDocument.Empty(id));
        var sourceSession = LogicalMapSessionStore.AddRecording(
            LogicalMapSessionStore.Create(id, "Synthetic", graph.Metadata.BuiltUtc),
            "source-session",
            bundle,
            graph.Metadata.BuiltUtc);
        LogicalMapSessionStore.Save(catalog.MapSessionPath(id), sourceSession);
        var sourceBytes = File.ReadAllBytes(catalog.MapPath(id));

        var duplicated = catalog.DuplicateMap(
            id,
            new DateTimeOffset(2026, 8, 22, 12, 30, 0, TimeSpan.Zero));

        Assert.NotEqual(id, duplicated.Id);
        Assert.Contains("-synthetic-copy-", duplicated.Id, StringComparison.Ordinal);
        Assert.True(File.Exists(duplicated.MapPath));
        Assert.True(File.Exists(MapCurationStore.PathForMap(duplicated.MapPath)));
        Assert.Equal(duplicated.Id,
            MapCurationStore.Load(duplicated.MapPath, duplicated.Id).LogicalMapId);
        Assert.NotNull(duplicated.SessionManifestPath);
        Assert.True(File.Exists(duplicated.SessionManifestPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(catalog.MapPath(id)));

        var duplicateGraph = SqliteGraphStore.Load(duplicated.MapPath);
        Assert.Equal(duplicated.Id, duplicateGraph.Metadata.EffectiveLogicalMapId);
        Assert.NotEqual(graph.Metadata.GraphId, duplicateGraph.Metadata.GraphId);
        Assert.Equal(graph.Metadata.EffectiveSourceBundleIds, duplicateGraph.Metadata.EffectiveSourceBundleIds);

        var duplicateSession = LogicalMapSessionStore.Load(duplicated.SessionManifestPath!);
        Assert.Equal(duplicated.Id, duplicateSession.LogicalMapId);
        Assert.Equal(sourceSession.Recordings, duplicateSession.Recordings);
    }

    [Fact]
    public void CatalogDuplicatesStandaloneMapWithoutInventingResumeHistory()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(Path.Combine(temp.Path, "catalog"));
        const string id = "standalone-map";
        var bundle = SyntheticBundleFactory.Create(temp.Path);
        SqliteGraphStore.Save(new RecordingGraphBuilder().Build([bundle], logicalMapId: id), catalog.MapPath(id));

        var duplicated = catalog.DuplicateMap(id);

        Assert.True(File.Exists(duplicated.MapPath));
        Assert.Null(duplicated.SessionManifestPath);
        Assert.False(File.Exists(catalog.MapSessionPath(duplicated.Id)));
    }

    [Fact]
    public void CatalogRejectsReparsePointForManagedDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "catalog");
        var catalog = new LocalArtifactCatalog(root);
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(catalog.ExportsDirectory);
        try { Directory.CreateSymbolicLink(catalog.ExportsDirectory, outside); }
        catch (IOException ex) when (ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase)) { return; }

        Assert.Throws<InvalidDataException>(catalog.EnsureSafe);
        Assert.Throws<InvalidDataException>(() => new LocalArtifactCatalog(root));
    }

    [Fact]
    public void CatalogRejectsReparsePointAboveRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(outside);
        var linkedParent = Path.Combine(temp.Path, "linked-parent");
        try { Directory.CreateSymbolicLink(linkedParent, outside); }
        catch (IOException ex) when (ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase)) { return; }

        Assert.Throws<InvalidDataException>(() => new LocalArtifactCatalog(Path.Combine(linkedParent, "catalog")));
    }
}

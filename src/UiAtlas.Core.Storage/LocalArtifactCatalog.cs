using System.Globalization;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using Microsoft.Data.Sqlite;

namespace UiAtlas.Core.Storage;

public sealed record LocalRecordingInfo(
    string Id,
    DateTimeOffset StartedUtc,
    string Outcome,
    string ProcessName,
    int FrameCount);

public sealed record LocalMapInfo(
    string Id,
    DateTimeOffset BuiltUtc,
    string Status,
    int NodeCount,
    int EdgeCount,
    int MappedControlCount);

public sealed record LocalMapDuplicate(
    string Id,
    string MapPath,
    string? SessionManifestPath);

public sealed record LocalMapDeletionResult(
    bool MapDeleted,
    int RecordingsDeleted,
    int SharedRecordingsKept,
    int RecordingDeleteFailures,
    bool ReferenceScanComplete);

public sealed record LocalRecordingCleanupResult(
    int RecordingsDeleted,
    int RecordingDeleteFailures,
    bool ReferenceScanComplete);

public sealed class LocalArtifactCatalog
{
    private const int MaxIdLength = 128;

    public LocalArtifactCatalog(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "Core"));
        RecordingsDirectory = Path.Combine(Root, "recordings");
        MapsDirectory = Path.Combine(Root, "maps");
        ExportsDirectory = Path.Combine(Root, "exports");
        TrashDirectory = Path.Combine(Root, "trash");
        CreateSafeDirectory(Root);
        CreateSafeDirectory(RecordingsDirectory);
        CreateSafeDirectory(MapsDirectory);
        CreateSafeDirectory(ExportsDirectory);
        CreateSafeDirectory(TrashDirectory);
    }

    public string Root { get; }
    public string RecordingsDirectory { get; }
    public string MapsDirectory { get; }
    public string ExportsDirectory { get; }
    public string TrashDirectory { get; }

    public void EnsureSafe() => EnsureCatalogDirectoriesSafe();

    public string CreateId(string processName, DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var slug = new string((processName ?? string.Empty).ToLowerInvariant()
            .Where(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            .Take(32).ToArray()).Trim('-');
        if (slug.Length == 0) slug = "window";
        var suffix = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{timestamp}-{slug}-{suffix}";
    }

    public string RecordingPath(string id) => ResolveArtifactPath(RecordingsDirectory, id, ".mlrec");
    public string MapPath(string id) => ResolveArtifactPath(MapsDirectory, id, ".db");
    public string MapSessionPath(string id) => ResolveArtifactPath(MapsDirectory, id, ".session.json");
    public string MapCurationPath(string id) => MapCurationStore.PathForMap(MapPath(id));
    public string MapDeletionMarkerPath(string id) => ResolveArtifactPath(TrashDirectory, id, ".map-deleted");
    public string DefaultExportPath(string id) => ResolveArtifactPath(ExportsDirectory, id, ".json");

    public bool IsUiAtlasmanentlyDeleted(string id)
    {
        var markerPath = MapDeletionMarkerPath(id);
        if (!File.Exists(markerPath)) return false;
        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Map deletion markers cannot be links.");
        return true;
    }

    public void ClearMapDeletionMarker(string id) =>
        DeleteFileIfPresent(MapDeletionMarkerPath(id), TrashDirectory);

    public IReadOnlyList<string> MatchingRecordingPaths(string id)
    {
        var mapPath = MapPath(id);
        if (!File.Exists(mapPath)) return [];
        try
        {
            var graph = SqliteGraphStore.Load(mapPath);
            var resolved = new List<string>(graph.Metadata.EffectiveSourceBundleIds.Count);
            foreach (var bundleId in graph.Metadata.EffectiveSourceBundleIds)
            {
                var recordingPath = RecordingPath(bundleId);
                if (!File.Exists(recordingPath)) return [];
                using var bundle = RecordingBundle.Open(recordingPath);
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                if (!string.Equals(manifest.SessionId, bundleId, StringComparison.Ordinal))
                    return [];
                resolved.Add(recordingPath);
            }

            return resolved;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public string? MatchingRecordingPath(string id)
    {
        var values = MatchingRecordingPaths(id);
        return values.Count == 1 ? values[0] : null;
    }

    public LocalMapDuplicate DuplicateMap(string id, DateTimeOffset? now = null)
    {
        EnsureCatalogDirectoriesSafe();
        var sourceMapPath = MapPath(id);
        if (!File.Exists(sourceMapPath))
            throw new FileNotFoundException("Catalog map was not found.", sourceMapPath);
        if ((File.GetAttributes(sourceMapPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Catalog map links cannot be duplicated.");

        var sourceSessionPath = MapSessionPath(id);
        var sourceCurationPath = MapCurationPath(id);
        LogicalMapSessionManifest? sourceSession = null;
        if (File.Exists(sourceSessionPath))
        {
            if ((File.GetAttributes(sourceSessionPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Catalog session links cannot be duplicated.");
            sourceSession = LogicalMapSessionStore.Load(sourceSessionPath);
        }

        var graph = SqliteGraphStore.Load(sourceMapPath);
        var createdUtc = now ?? DateTimeOffset.UtcNow;
        var processName = sourceSession?.ProcessName ?? graph.Nodes
            .FirstOrDefault(node => node.Kind == GraphNodeKind.Application)?.Properties
            .FirstOrDefault(property => property.Name == "processName")?.Value;
        if (string.IsNullOrWhiteSpace(processName)) processName = "map";

        string duplicateId;
        string duplicateMapPath;
        string duplicateSessionPath;
        do
        {
            duplicateId = CreateId(processName + "-copy", createdUtc);
            duplicateMapPath = MapPath(duplicateId);
            duplicateSessionPath = MapSessionPath(duplicateId);
        }
        while (File.Exists(duplicateMapPath) || File.Exists(duplicateSessionPath));

        try
        {
            var duplicateGraph = graph with
            {
                Metadata = graph.Metadata with
                {
                    GraphId = StableIdentity.Create("graph", duplicateId, graph.Metadata.SemanticHash),
                    BuiltUtc = createdUtc,
                    LogicalMapId = duplicateId
                }
            };
            SqliteGraphStore.Save(duplicateGraph, duplicateMapPath);

            if (sourceSession is not null)
            {
                var duplicateSession = sourceSession with
                {
                    LogicalMapId = duplicateId,
                    CreatedUtc = createdUtc,
                    UpdatedUtc = createdUtc
                };
                LogicalMapSessionStore.Save(duplicateSessionPath, duplicateSession);
            }

            if (File.Exists(sourceCurationPath))
            {
                var sourceCuration = MapCurationStore.Load(sourceMapPath, graph.Metadata.EffectiveLogicalMapId);
                MapCurationStore.Save(duplicateMapPath, sourceCuration with { LogicalMapId = duplicateId });
            }

            return new(duplicateId, duplicateMapPath,
                sourceSession is null ? null : duplicateSessionPath);
        }
        catch
        {
            DeleteFileIfPresent(duplicateMapPath, MapsDirectory);
            DeleteFileIfPresent(duplicateSessionPath, MapsDirectory);
            DeleteFileIfPresent(MapCurationStore.PathForMap(duplicateMapPath), MapsDirectory);
            throw;
        }
    }

    public IReadOnlyList<LocalRecordingInfo> ListRecordings()
    {
        EnsureCatalogDirectoriesSafe();
        var values = new List<LocalRecordingInfo>();
        foreach (var path in EnumerateArtifacts(RecordingsDirectory, "*.mlrec"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                using var bundle = RecordingBundle.Open(path);
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                values.Add(new(id, manifest.StartedUtc, manifest.Outcome.ToString().ToLowerInvariant(),
                    manifest.Target?.ProcessName ?? string.Empty, manifest.FrameCount));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                values.Add(new(id, File.GetCreationTimeUtc(path), "invalid", string.Empty, 0));
            }
        }
        return values.OrderByDescending(value => value.StartedUtc).ThenBy(value => value.Id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<LocalMapInfo> ListMaps()
    {
        EnsureCatalogDirectoriesSafe();
        var values = new List<LocalMapInfo>();
        foreach (var path in EnumerateArtifacts(MapsDirectory, "*.db"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var summary = SqliteGraphStore.ReadSummary(path);
                var status = summary.HasControlNodes ? "valid" : "incomplete";
                values.Add(new(id, summary.Metadata.BuiltUtc, status, summary.NodeCount, summary.EdgeCount,
                    summary.SemanticControlCount));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
            {
                values.Add(new(id, File.GetCreationTimeUtc(path), "invalid", 0, 0, 0));
            }
        }
        return values.OrderByDescending(value => value.BuiltUtc).ThenBy(value => value.Id, StringComparer.Ordinal).ToArray();
    }

    public string ArchiveRecording(string id) => Archive(RecordingPath(id), "recordings");
    public string ArchiveMap(string id)
    {
        var mapPath = MapPath(id);
        var destination = Archive(mapPath, "maps");
        ArchiveCompanionIfPresent(MapSessionPath(id), "maps", Path.GetFileNameWithoutExtension(destination), ".session.json");
        ArchiveCompanionIfPresent(MapCurationPath(id), "maps", Path.GetFileNameWithoutExtension(destination), ".curation.json");
        return destination;
    }
    public int ArchiveAllRecordings() => ArchiveAll(RecordingsDirectory, "*.mlrec", "recordings");
    public int ArchiveAllMaps()
    {
        var ids = EnumerateArtifacts(MapsDirectory, "*.db")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var id in ids) ArchiveMap(id);
        return ids.Length;
    }

    public bool DeleteUiAtlasmanently(string id)
    {
        EnsureCatalogDirectoriesSafe();

        var mapPath = MapPath(id);
        var sessionPath = MapSessionPath(id);
        var exportPath = DefaultExportPath(id);

        var managedArtifactsExist = File.Exists(mapPath) ||
            File.Exists(mapPath + "-wal") ||
            File.Exists(mapPath + "-shm") ||
            File.Exists(sessionPath) ||
            File.Exists(MapCurationPath(id)) ||
            File.Exists(exportPath) ||
            File.Exists(exportPath + ".sha256");
        if (!managedArtifactsExist)
            return false;

        WriteMapDeletionMarker(id);

        var deletedAny = false;
        deletedAny |= DeleteFileIfPresent(mapPath, MapsDirectory);
        deletedAny |= DeleteFileIfPresent(mapPath + "-wal", MapsDirectory);
        deletedAny |= DeleteFileIfPresent(mapPath + "-shm", MapsDirectory);
        deletedAny |= DeleteFileIfPresent(sessionPath, MapsDirectory);
        deletedAny |= DeleteFileIfPresent(MapCurationPath(id), MapsDirectory);
        deletedAny |= DeleteFileIfPresent(exportPath, ExportsDirectory);
        deletedAny |= DeleteFileIfPresent(exportPath + ".sha256", ExportsDirectory);
        return deletedAny;
    }

    public LocalMapDeletionResult DeleteMapAndUnusedRecordingsPermanently(string id)
    {
        EnsureCatalogDirectoriesSafe();

        var mapPath = MapPath(id);
        if (!File.Exists(mapPath))
            return new(false, 0, 0, 0, true);

        var sourceRecordingIds = SqliteGraphStore.ReadSummary(mapPath).Metadata.EffectiveSourceBundleIds
            .Where(IsValidId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var referencedElsewhere = RecordingIdsReferencedByMaps(id, out var referenceScanComplete);
        var removableRecordingIds = referenceScanComplete
            ? sourceRecordingIds.Where(recordingId => !referencedElsewhere.Contains(recordingId)).ToArray()
            : [];
        var sharedRecordingsKept = sourceRecordingIds.Length - removableRecordingIds.Length;

        var mapDeleted = DeleteUiAtlasmanently(id);
        var recordingsDeleted = 0;
        var recordingDeleteFailures = 0;
        foreach (var recordingId in removableRecordingIds)
        {
            try
            {
                if (DeleteFileIfPresent(RecordingPath(recordingId), RecordingsDirectory))
                    recordingsDeleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                recordingDeleteFailures++;
            }
        }

        return new(mapDeleted, recordingsDeleted, sharedRecordingsKept, recordingDeleteFailures, referenceScanComplete);
    }

    public IReadOnlyList<string> ListUnusedRecordingIds()
    {
        EnsureCatalogDirectoriesSafe();
        var referenced = RecordingIdsReferencedByMaps(excludedMapId: null, out var referenceScanComplete);
        if (!referenceScanComplete)
            return [];

        return EnumerateArtifacts(RecordingsDirectory, "*.mlrec")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Where(id => !referenced.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    public LocalRecordingCleanupResult DeleteUnusedRecordingsPermanently()
    {
        EnsureCatalogDirectoriesSafe();
        var referenced = RecordingIdsReferencedByMaps(excludedMapId: null, out var referenceScanComplete);
        if (!referenceScanComplete)
            return new(0, 0, false);

        var deleted = 0;
        var failures = 0;
        foreach (var recordingPath in EnumerateArtifacts(RecordingsDirectory, "*.mlrec"))
        {
            var id = Path.GetFileNameWithoutExtension(recordingPath)!;
            if (referenced.Contains(id))
                continue;

            try
            {
                if (DeleteFileIfPresent(recordingPath, RecordingsDirectory))
                    deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }

        return new(deleted, failures, true);
    }

    private HashSet<string> RecordingIdsReferencedByMaps(string? excludedMapId, out bool complete)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        complete = true;
        foreach (var candidateMapPath in EnumerateArtifacts(MapsDirectory, "*.db"))
        {
            var candidateMapId = Path.GetFileNameWithoutExtension(candidateMapPath)!;
            if (!string.IsNullOrWhiteSpace(excludedMapId) &&
                candidateMapId.Equals(excludedMapId, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                foreach (var recordingId in SqliteGraphStore.ReadSummary(candidateMapPath).Metadata.EffectiveSourceBundleIds)
                    if (IsValidId(recordingId))
                        referenced.Add(recordingId);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
            {
                complete = false;
            }
        }

        return referenced;
    }

    private void WriteMapDeletionMarker(string id)
    {
        var markerPath = MapDeletionMarkerPath(id);
        if (File.Exists(markerPath) &&
            (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Map deletion markers cannot be links.");

        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    public bool IsCatalogExport(string path)
    {
        EnsureCatalogDirectoriesSafe();
        var resolved = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(resolved), ExportsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public string ArchiveExport(string path)
    {
        var source = Path.GetFullPath(path);
        if (!IsCatalogExport(source)) throw new ArgumentException("Export is outside the catalog.", nameof(path));
        return Archive(source, "exports");
    }

    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength &&
        id.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static IReadOnlyList<string> EnumerateArtifacts(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Where(path => IsValidId(Path.GetFileNameWithoutExtension(path)))
            .ToArray();

    private string ResolveArtifactPath(string directory, string id, string extension)
    {
        EnsureCatalogDirectoriesSafe();
        if (!IsValidId(id)) throw new ArgumentException("Artifact identifier is invalid.", nameof(id));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(directory, id + extension));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact path escaped the catalog.");
        return resolved;
    }

    private string Archive(string source, string category)
    {
        EnsureCatalogDirectoriesSafe();
        if (!File.Exists(source)) throw new FileNotFoundException("Catalog artifact was not found.");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Catalog links cannot be archived.");
        var destinationDirectory = Path.Combine(TrashDirectory, category);
        CreateSafeDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory,
            $"{Path.GetFileNameWithoutExtension(source)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{Path.GetExtension(source)}");
        File.Move(source, destination);
        return destination;
    }

    private void ArchiveCompanionIfPresent(string source, string category, string destinationStem, string suffix)
    {
        if (!File.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Catalog artifact links cannot be archived.");
        var destinationDirectory = Path.Combine(TrashDirectory, category);
        CreateSafeDirectory(destinationDirectory);
        File.Move(source, Path.Combine(destinationDirectory, destinationStem + suffix));
    }

    private int ArchiveAll(string directory, string pattern, string category)
    {
        EnsureCatalogDirectoriesSafe();
        var sources = EnumerateArtifacts(directory, pattern)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var source in sources) Archive(source, category);
        return sources.Length;
    }

    private static bool DeleteFileIfPresent(string path, string expectedDirectory)
    {
        var resolved = Path.GetFullPath(path);
        var expectedRoot = Path.GetFullPath(expectedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Artifact path escaped the catalog.");

        if (!File.Exists(resolved))
            return false;
        if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Catalog links cannot be deleted.");

        File.Delete(resolved);
        return true;
    }

    private void EnsureCatalogDirectoriesSafe()
    {
        RejectExistingReparsePoints(Root);
        RejectReparsePoint(Root);
        RejectReparsePoint(RecordingsDirectory);
        RejectReparsePoint(MapsDirectory);
        RejectReparsePoint(ExportsDirectory);
        RejectReparsePoint(TrashDirectory);
    }

    private static void CreateSafeDirectory(string path)
    {
        RejectExistingReparsePoints(path);
        if (Directory.Exists(path)) RejectReparsePoint(path);
        else Directory.CreateDirectory(path);
        RejectExistingReparsePoints(path);
        RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Catalog directories must exist and cannot be links or reparse points.");
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Catalog paths cannot contain links or reparse points.");
    }
}

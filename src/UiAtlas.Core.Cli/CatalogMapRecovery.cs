using System.Text.RegularExpressions;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal static class CatalogMapRecovery
{
    private static readonly object RecoveryGate = new();
    private static readonly Regex ResumeSuffixPattern = new("-resume\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int RecoverCompletedMaps(LocalArtifactCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (RecoveryGate)
        {
            catalog.EnsureSafe();
            var recovered = 0;

            foreach (var recording in catalog.ListRecordings())
            {
                if (ResumeSuffixPattern.IsMatch(recording.Id))
                    continue;
                if (catalog.IsUiAtlasmanentlyDeleted(recording.Id))
                    continue;

                var recordingPath = catalog.RecordingPath(recording.Id);
                var mapPath = catalog.MapPath(recording.Id);
                var sessionManifestPath = catalog.MapSessionPath(recording.Id);
                var needsMap = !File.Exists(mapPath);
                var needsSessionManifest = !File.Exists(sessionManifestPath);
                if (!needsMap && !needsSessionManifest)
                    continue;
                if (!File.Exists(recordingPath))
                    continue;

                try
                {
                    var report = RecordingBundleValidator.Validate(recordingPath);
                    if (!report.IsValid)
                        continue;

                    using var bundle = RecordingBundle.Open(recordingPath);
                    var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                    if (!ShouldRecover(manifest, bundle))
                        continue;

                    var processName = string.IsNullOrWhiteSpace(manifest.Target?.ProcessName)
                        ? string.IsNullOrWhiteSpace(recording.ProcessName) ? "window" : recording.ProcessName
                        : manifest.Target.ProcessName;

                    if (needsMap)
                    {
                        var graph = new RecordingGraphBuilder().Build([recordingPath], recording.Id);
                        SqliteGraphStore.Save(graph, mapPath);
                    }

                    if (needsSessionManifest)
                    {
                        var sessionManifest = LogicalMapSessionStore.AddRecording(
                            LogicalMapSessionStore.Create(recording.Id, processName, manifest.StartedUtc),
                            recording.Id,
                            recordingPath,
                            manifest.EndedUtc);
                        LogicalMapSessionStore.Save(sessionManifestPath, sessionManifest);
                    }

                    recovered++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    // Leave orphaned completed bundles intact; the next refresh can try again.
                }
            }

            return recovered;
        }
    }

    public static bool DeleteUiAtlasmanently(LocalArtifactCatalog catalog, string id)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (RecoveryGate)
            return catalog.DeleteUiAtlasmanently(id);
    }

    public static LocalMapDeletionResult DeleteMapAndUnusedRecordingsPermanently(
        LocalArtifactCatalog catalog,
        string id)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (RecoveryGate)
            return catalog.DeleteMapAndUnusedRecordingsPermanently(id);
    }

    public static LocalRecordingCleanupResult DeleteUnusedRecordingsPermanently(LocalArtifactCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (RecoveryGate)
        {
            RecoverCompletedMaps(catalog);
            return catalog.DeleteUnusedRecordingsPermanently();
        }
    }

    public static IReadOnlyList<string> ListUnusedRecordingIds(LocalArtifactCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (RecoveryGate)
        {
            RecoverCompletedMaps(catalog);
            return catalog.ListUnusedRecordingIds();
        }
    }

    private static bool ShouldRecover(RecordingManifest manifest, RecordingBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bundle);

        return manifest.Outcome switch
        {
            RecordingOutcome.Complete => true,
            RecordingOutcome.Partial => ContainsAutoTabsMarker(bundle),
            _ => false
        };
    }

    private static bool ContainsAutoTabsMarker(RecordingBundle bundle)
    {
        foreach (var line in bundle.ReadText("raw/input-events.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var input = System.Text.Json.JsonSerializer.Deserialize<InputEvent>(line, JsonDefaults.Options);
                if (input?.Kind == InputEventKind.Marker &&
                    input.Text.Contains("auto-tabs:", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        return false;
    }
}

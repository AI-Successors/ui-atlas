using UiAtlas.Core.Storage;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Cli;

internal sealed class RecorderWorkspace
{
    private readonly Func<int, RecorderSessionTarget> _sessionFactory;
    private readonly List<LogicalMapSessionRecording> _recordings = [];
    private readonly DateTimeOffset _createdUtc;
    private AutoMappingCampaignState? _autoMapping;
    private readonly List<QuickMapSnapshotState> _quickMapSnapshots = [];
    private SpeculativePlanningState? _speculativePlanning;

    public RecorderWorkspace(
        string logicalMapId,
        string processName,
        string mapPath,
        string defaultExportPath,
        string sessionManifestPath,
        Func<int, RecorderSessionTarget> sessionFactory,
        DateTimeOffset? createdUtc = null,
        AutoMappingCampaignState? autoMapping = null,
        IReadOnlyList<QuickMapSnapshotState>? quickMapSnapshots = null,
        SpeculativePlanningState? speculativePlanning = null)
    {
        LogicalMapId = logicalMapId;
        ProcessName = processName;
        MapPath = Path.GetFullPath(mapPath);
        DefaultExportPath = Path.GetFullPath(defaultExportPath);
        SessionManifestPath = Path.GetFullPath(sessionManifestPath);
        _sessionFactory = sessionFactory;
        _createdUtc = createdUtc ?? DateTimeOffset.UtcNow;
        _autoMapping = autoMapping;
        _speculativePlanning = speculativePlanning;
        _quickMapSnapshots.AddRange(quickMapSnapshots ?? []);
    }

    public string LogicalMapId { get; }
    public string ProcessName { get; }
    public string MapPath { get; }
    public string DefaultExportPath { get; }
    public string SessionManifestPath { get; }
    public IReadOnlyList<LogicalMapSessionRecording> Recordings => _recordings;
    public AutoMappingCampaignState? AutoMapping => _autoMapping;
    public IReadOnlyList<QuickMapSnapshotState> QuickMapSnapshots => _quickMapSnapshots;
    public SpeculativePlanningState? SpeculativePlanning => _speculativePlanning;

    public RecorderSessionTarget CreateNextSession()
    {
        for (var ordinal = _recordings.Count + 1; ordinal < 10_000; ordinal++)
        {
            var candidate = _sessionFactory(ordinal);
            if (_recordings.Any(recording => string.Equals(recording.SessionId, candidate.SessionId, StringComparison.Ordinal)))
                continue;
            if (File.Exists(candidate.RecordingPath))
                continue;
            return candidate;
        }

        throw new InvalidOperationException("Could not allocate another recording session path.");
    }

    public void AddCompletedSession(string sessionId, string recordingPath)
    {
        var updated = LogicalMapSessionStore.AddRecording(CurrentManifest(), sessionId, recordingPath, DateTimeOffset.UtcNow);
        _recordings.Clear();
        _recordings.AddRange(updated.Recordings);
        LogicalMapSessionStore.Save(SessionManifestPath, updated);
    }

    public void SaveAutoMapping(AutoMappingCampaignState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _autoMapping = state;
        LogicalMapSessionStore.Save(SessionManifestPath, CurrentManifest());
    }

    public void SaveSpeculativePlanning(SpeculativePlanningState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _speculativePlanning = state;
        LogicalMapSessionStore.Save(SessionManifestPath, CurrentManifest());
    }

    public void AddQuickMapSnapshot(QuickMapSnapshotState snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _quickMapSnapshots.RemoveAll(item => string.Equals(item.SessionId, snapshot.SessionId, StringComparison.Ordinal));
        _quickMapSnapshots.Add(snapshot);
        LogicalMapSessionStore.Save(SessionManifestPath, CurrentManifest());
    }

    public void StageQuickMapSnapshot(QuickMapSnapshotState snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _quickMapSnapshots.RemoveAll(item => string.Equals(item.SessionId, snapshot.SessionId, StringComparison.Ordinal));
        _quickMapSnapshots.Add(snapshot);
    }

    public void DiscardQuickMapSnapshot(string sessionId)
    {
        var removed = _quickMapSnapshots.RemoveAll(item =>
            string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        if (removed > 0 && File.Exists(SessionManifestPath))
            LogicalMapSessionStore.Save(SessionManifestPath, CurrentManifest());
    }

    public IReadOnlyList<string> RecordingPaths() => _recordings.Select(item => item.RecordingPath).ToArray();

    public IReadOnlyList<LogicalMapSessionRecording> RecordingEvidence()
    {
        var evidence = _recordings.ToDictionary(item => item.SessionId, StringComparer.Ordinal);
        var directories = _recordings
            .Select(item => Path.GetDirectoryName(item.RecordingPath))
            .Append(Path.GetDirectoryName(_sessionFactory(1).RecordingPath))
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(directory!, "*.mlrec", SearchOption.TopDirectoryOnly))
            {
                var candidateSessionId = Path.GetFileNameWithoutExtension(path);
                if (!candidateSessionId.Equals(LogicalMapId, StringComparison.Ordinal) &&
                    !candidateSessionId.StartsWith(LogicalMapId + "-resume", StringComparison.Ordinal))
                    continue;

                try
                {
                    if (!RecordingBundleValidator.Validate(path).IsValid)
                        continue;
                    using var bundle = RecordingBundle.Open(path);
                    var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                    if (manifest.SessionId.Equals(LogicalMapId, StringComparison.Ordinal) ||
                        manifest.SessionId.StartsWith(LogicalMapId + "-resume", StringComparison.Ordinal))
                        evidence[manifest.SessionId] = new(manifest.SessionId, Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // An interrupted, unsealed bundle is not immutable evidence.
                }
            }
        }
        return evidence.Values.ToArray();
    }

    public void AdoptReferencedRecordingEvidence(
        IReadOnlyList<LogicalMapSessionRecording> evidence)
    {
        var referencedSessionIds = (_autoMapping?.Items ?? [])
            .Select(item => item.LastSessionId)
            .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var recording in evidence)
        {
            if (!referencedSessionIds.Contains(recording.SessionId) ||
                _recordings.Any(existing => existing.SessionId.Equals(recording.SessionId, StringComparison.Ordinal)))
                continue;
            _recordings.Add(recording);
            changed = true;
        }
        if (changed)
            LogicalMapSessionStore.Save(SessionManifestPath, CurrentManifest());
    }

    public static RecorderWorkspace CreateCatalogWorkspace(LocalArtifactCatalog catalog, string logicalMapId, string processName) =>
        new(
            logicalMapId,
            processName,
            catalog.MapPath(logicalMapId),
            catalog.DefaultExportPath(logicalMapId),
            catalog.MapSessionPath(logicalMapId),
            ordinal =>
            {
                var sessionId = ordinal == 1 ? logicalMapId : $"{logicalMapId}-resume{ordinal:00}";
                return new RecorderSessionTarget(sessionId, catalog.RecordingPath(sessionId));
            });

    public static RecorderWorkspace CreateStandaloneWorkspace(string outputPath, string processName)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput) ?? Environment.CurrentDirectory;
        var baseName = Path.GetFileNameWithoutExtension(fullOutput);
        var logicalMapId = SafeId(baseName, processName);
        return new(
            logicalMapId,
            processName,
            Path.ChangeExtension(fullOutput, ".db"),
            Path.ChangeExtension(fullOutput, ".json"),
            Path.Combine(directory, Path.GetFileNameWithoutExtension(fullOutput) + ".session.json"),
            ordinal =>
            {
                if (ordinal == 1)
                    return new RecorderSessionTarget(logicalMapId, fullOutput);
                var sessionId = $"{logicalMapId}-resume{ordinal:00}";
                var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullOutput) + $".resume{ordinal:00}.mlrec");
                return new RecorderSessionTarget(sessionId, path);
            });
    }

    public static RecorderWorkspace CreateExistingWorkspace(
        string mapPath,
        string defaultExportPath,
        string sessionManifestPath,
        LogicalMapSessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var workspace = new RecorderWorkspace(
            manifest.LogicalMapId,
            manifest.ProcessName,
            mapPath,
            defaultExportPath,
            sessionManifestPath,
            ordinal =>
            {
                var directory = ResolveExistingRecordingDirectory(mapPath, manifest);
                var sessionId = ordinal == 1 ? manifest.LogicalMapId : $"{manifest.LogicalMapId}-resume{ordinal:00}";
                return new RecorderSessionTarget(sessionId, Path.Combine(directory, sessionId + ".mlrec"));
            },
            manifest.CreatedUtc,
            manifest.AutoMapping,
            manifest.QuickMapSnapshots,
            manifest.SpeculativePlanning);
        workspace._recordings.AddRange(manifest.Recordings);
        return workspace;
    }

    private LogicalMapSessionManifest CurrentManifest() =>
        new(
            LogicalMapSessionStore.FormatVersion,
            LogicalMapId,
            ProcessName,
            _createdUtc,
            DateTimeOffset.UtcNow,
            _recordings.ToArray(),
            _autoMapping,
            _quickMapSnapshots.ToArray(),
            _speculativePlanning);

    private static string SafeId(string rawBaseName, string processName)
    {
        var seed = string.IsNullOrWhiteSpace(rawBaseName) ? processName : rawBaseName;
        var chars = seed
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        var value = new string(chars).Trim('-');
        if (value.Length == 0) value = "map";
        if (value.Length > 96) value = value[..96];
        return value;
    }

    private static string ResolveExistingRecordingDirectory(string mapPath, LogicalMapSessionManifest manifest)
    {
        foreach (var recording in manifest.Recordings)
        {
            if (string.IsNullOrWhiteSpace(recording?.RecordingPath))
                continue;

            var directory = Path.GetDirectoryName(Path.GetFullPath(recording.RecordingPath));
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }

        return Path.GetDirectoryName(Path.GetFullPath(mapPath)) ?? Environment.CurrentDirectory;
    }
}

internal sealed record RecorderSessionTarget(string SessionId, string RecordingPath);

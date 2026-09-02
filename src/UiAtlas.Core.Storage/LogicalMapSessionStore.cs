using System.Text.Json;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public sealed record LogicalMapSessionRecording(
    string SessionId,
    string RecordingPath);

public sealed record LogicalMapSessionManifest(
    string FormatVersion,
    string LogicalMapId,
    string ProcessName,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<LogicalMapSessionRecording> Recordings,
    AutoMappingCampaignState? AutoMapping = null,
    IReadOnlyList<QuickMapSnapshotState>? QuickMapSnapshots = null,
    SpeculativePlanningState? SpeculativePlanning = null);

public enum SpeculativePredictionStatus
{
    Predicted,
    Matched,
    Rejected,
    Stale
}

public enum SpeculativeEvidenceKind
{
    ControlObserved,
    SurfaceObserved,
    TransitionConfirmed
}

public sealed record SpeculativePredictionState(
    string PredictionId,
    string? ParentPredictionId,
    string SourceSurfaceFingerprint,
    string ActionFingerprint,
    string DisplayName,
    AutoMappingWorkKind Kind,
    string ExpectedOutcomeKind,
    double Confidence,
    int Depth,
    int Revision,
    string KnowledgeSource,
    SpeculativePredictionStatus Status,
    string SourceSessionId,
    long SourceFrameSequence,
    string? ResultSessionId,
    long? ResultFrameSequence,
    DateTimeOffset UpdatedUtc);

public sealed record SpeculativeEvidenceCoverage(
    int ControlsObserved,
    int SurfacesObserved,
    int TransitionsConfirmed);

public sealed record SpeculativePlanningMetrics(
    int Prepared,
    int Reused,
    int Matched,
    int Rejected,
    long LastPlanningMilliseconds);

public sealed record SpeculativePlanningState(
    string FormatVersion,
    int SurfaceRevision,
    string SurfaceFingerprint,
    IReadOnlyList<SpeculativePredictionState> Predictions,
    SpeculativeEvidenceCoverage Coverage,
    SpeculativePlanningMetrics Metrics,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string>? ObservedSurfaceFingerprints = null)
{
    public const string CurrentFormatVersion = "ui-atlas.speculative-planning/1";

    public static SpeculativePlanningState Empty(DateTimeOffset now) => new(
        CurrentFormatVersion,
        0,
        string.Empty,
        [],
        new(0, 0, 0),
        new(0, 0, 0, 0, 0),
        now);
}

public enum QuickMapCaptureStatus
{
    Complete,
    Partial
}

public sealed record QuickMapSnapshotState(
    string SessionId,
    string SurfaceFingerprint,
    QuickMapCaptureStatus Status,
    int VisibleControlCount,
    int UnverifiedControlCount,
    IReadOnlyList<string> DiagnosticCodes,
    DateTimeOffset CapturedUtc,
    int ConfirmedControlCount = 0,
    int ObservedControlCount = 0,
    int CoverageGapCount = 0,
    string CoverageStatus = "");

public enum AutoMappingWorkKind
{
    Tab,
    Command,
    DialogLauncher,
    Backstage,
    NavigationItem,
    Disclosure
}

public enum AutoMappingWorkStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    NeedsManual,
    Skipped
}

public sealed record AutoMappingWorkItemState(
    string ItemId,
    AutoMappingWorkKind Kind,
    AutoMappingWorkStatus Status,
    string TargetFingerprint,
    string ParentFingerprint,
    int Attempts,
    string DiagnosticCode,
    string? LastSessionId,
    string? LastInteractionId,
    IReadOnlyList<long> ResultFrameSequences,
    DateTimeOffset UpdatedUtc,
    string DisplayName = "");

public sealed record AutoMappingCampaignState(
    string FormatVersion,
    int Revision,
    string IdentityVersion,
    IReadOnlyList<AutoMappingWorkItemState> Items,
    DateTimeOffset UpdatedUtc)
{
    public const string CurrentFormatVersion = "ui-atlas.auto-mapping-campaign/1";
    public const string CurrentIdentityVersion = "ui-atlas.auto-target/1";

    public static AutoMappingCampaignState Empty(DateTimeOffset now) =>
        new(CurrentFormatVersion, 0, CurrentIdentityVersion, [], now);
}

public static class LogicalMapSessionStore
{
    public const string LegacyFormatVersion = "ui-atlas.logical-map-session/1";
    public const string FormatVersion = "ui-atlas.logical-map-session/2";

    public static LogicalMapSessionManifest Create(string logicalMapId, string processName, DateTimeOffset createdUtc) =>
        new(FormatVersion, logicalMapId, processName, createdUtc, createdUtc, []);

    public static LogicalMapSessionManifest Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        StrictJsonValidator.Validate(bytes);
        var manifest = JsonSerializer.Deserialize<LogicalMapSessionManifest>(bytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("Logical map session manifest is invalid.");
        Validate(manifest);
        return manifest;
    }

    public static void Save(string path, LogicalMapSessionManifest manifest)
    {
        Validate(manifest);
        AtomicFile.Publish(Path.GetFullPath(path), temporary =>
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options)));
    }

    public static LogicalMapSessionManifest AddRecording(
        LogicalMapSessionManifest manifest,
        string sessionId,
        string recordingPath,
        DateTimeOffset updatedUtc)
    {
        ValidateSessionId(sessionId);
        var fullRecordingPath = Path.GetFullPath(recordingPath);
        var recordings = manifest.Recordings
            .Where(item => !string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
            .Append(new(sessionId, fullRecordingPath))
            .ToArray();
        return manifest with { FormatVersion = FormatVersion, UpdatedUtc = updatedUtc, Recordings = recordings };
    }

    public static IReadOnlyList<string> RecordingPaths(LogicalMapSessionManifest manifest) =>
        manifest.Recordings.Select(item => item.RecordingPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void Validate(LogicalMapSessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion is not (FormatVersion or LegacyFormatVersion))
            throw new InvalidDataException("Logical map session manifest version is unsupported.");
        ValidateSessionId(manifest.LogicalMapId);
        if (string.IsNullOrWhiteSpace(manifest.ProcessName) || manifest.ProcessName.Length > 256)
            throw new InvalidDataException("Logical map session process name is invalid.");
        if (manifest.Recordings is null)
            throw new InvalidDataException("Logical map session recordings are missing.");
        foreach (var recording in manifest.Recordings)
        {
            if (recording is null)
                throw new InvalidDataException("Logical map session recording is invalid.");
            ValidateSessionId(recording.SessionId);
            if (string.IsNullOrWhiteSpace(recording.RecordingPath))
                throw new InvalidDataException("Logical map session recording path is invalid.");
        }

        ValidateAutoMapping(manifest.AutoMapping);
        ValidateQuickMapSnapshots(manifest.QuickMapSnapshots);
        ValidateSpeculativePlanning(manifest.SpeculativePlanning);
    }

    private static void ValidateSpeculativePlanning(SpeculativePlanningState? planning)
    {
        if (planning is null)
            return;
        if (planning.FormatVersion != SpeculativePlanningState.CurrentFormatVersion ||
            planning.SurfaceRevision < 0 || planning.SurfaceFingerprint is null || planning.SurfaceFingerprint.Length > 160 ||
            planning.Predictions is null || planning.Predictions.Count > 10_000 ||
            planning.ObservedSurfaceFingerprints is { Count: > 10_000 } ||
            planning.ObservedSurfaceFingerprints?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160) == true ||
            planning.Coverage is null || planning.Metrics is null ||
            planning.Coverage.ControlsObserved < 0 || planning.Coverage.SurfacesObserved < 0 || planning.Coverage.TransitionsConfirmed < 0 ||
            planning.Metrics.Prepared < 0 || planning.Metrics.Reused < 0 || planning.Metrics.Matched < 0 ||
            planning.Metrics.Rejected < 0 || planning.Metrics.LastPlanningMilliseconds < 0)
            throw new InvalidDataException("Speculative planning state is invalid.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prediction in planning.Predictions)
        {
            if (prediction is null || string.IsNullOrWhiteSpace(prediction.PredictionId) || prediction.PredictionId.Length > 160 ||
                prediction.ParentPredictionId is { Length: > 160 } ||
                string.IsNullOrWhiteSpace(prediction.SourceSurfaceFingerprint) || prediction.SourceSurfaceFingerprint.Length > 160 ||
                string.IsNullOrWhiteSpace(prediction.ActionFingerprint) || prediction.ActionFingerprint.Length > 128 ||
                prediction.DisplayName is null || prediction.DisplayName.Length > 512 ||
                string.IsNullOrWhiteSpace(prediction.ExpectedOutcomeKind) || prediction.ExpectedOutcomeKind.Length > 128 ||
                prediction.Confidence is < 0 or > 1 || prediction.Depth is < 1 or > 2 || prediction.Revision < 0 ||
                string.IsNullOrWhiteSpace(prediction.KnowledgeSource) || prediction.KnowledgeSource.Length > 128 ||
                !LocalArtifactCatalog.IsValidId(prediction.SourceSessionId) || prediction.SourceFrameSequence <= 0 ||
                prediction.ResultSessionId is { } resultSessionId && !LocalArtifactCatalog.IsValidId(resultSessionId) ||
                prediction.ResultFrameSequence is <= 0 || !ids.Add(prediction.PredictionId))
                throw new InvalidDataException("Speculative prediction state is invalid.");
        }

        foreach (var prediction in planning.Predictions.Where(item => item.ParentPredictionId is not null))
            if (!ids.Contains(prediction.ParentPredictionId!))
                throw new InvalidDataException("Speculative prediction parent is missing.");
    }

    private static void ValidateQuickMapSnapshots(IReadOnlyList<QuickMapSnapshotState>? snapshots)
    {
        if (snapshots is null)
            return;
        if (snapshots.Count > 10_000)
            throw new InvalidDataException("Quick-map snapshot history is invalid.");

        foreach (var snapshot in snapshots)
        {
            if (snapshot is null || !LocalArtifactCatalog.IsValidId(snapshot.SessionId) ||
                string.IsNullOrWhiteSpace(snapshot.SurfaceFingerprint) || snapshot.SurfaceFingerprint.Length > 160 ||
                snapshot.VisibleControlCount < 0 || snapshot.UnverifiedControlCount < 0 ||
                snapshot.ConfirmedControlCount < 0 || snapshot.ObservedControlCount < 0 || snapshot.CoverageGapCount < 0 ||
                snapshot.CoverageStatus is null or { Length: > 128 } ||
                snapshot.DiagnosticCodes is null || snapshot.DiagnosticCodes.Count > 64 ||
                snapshot.DiagnosticCodes.Any(code => code is null || code.Length > 256))
                throw new InvalidDataException("Quick-map snapshot state is invalid.");
        }
    }

    private static void ValidateAutoMapping(AutoMappingCampaignState? campaign)
    {
        if (campaign is null)
            return;
        if (campaign.FormatVersion != AutoMappingCampaignState.CurrentFormatVersion ||
            campaign.IdentityVersion != AutoMappingCampaignState.CurrentIdentityVersion ||
            campaign.Revision < 0 || campaign.Items is null || campaign.Items.Count > 50_000)
            throw new InvalidDataException("Auto-mapping campaign state is invalid.");

        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in campaign.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ItemId) || item.ItemId.Length > 160 ||
                string.IsNullOrWhiteSpace(item.TargetFingerprint) || item.TargetFingerprint.Length > 128 ||
                item.ParentFingerprint is null || item.ParentFingerprint.Length > 128 || item.Attempts is < 0 or > 100 ||
                item.DiagnosticCode is null || item.DiagnosticCode.Length > 256 || item.ResultFrameSequences is null ||
                item.ResultFrameSequences.Count > 64 || item.ResultFrameSequences.Any(sequence => sequence <= 0) ||
                item.LastSessionId is { } lastSessionId && !LocalArtifactCatalog.IsValidId(lastSessionId) ||
                item.LastInteractionId is { Length: > 160 } ||
                item.DisplayName is null || item.DisplayName.Length > 512 ||
                !itemIds.Add(item.ItemId))
                throw new InvalidDataException("Auto-mapping campaign item is invalid.");
        }
    }

    private static void ValidateSessionId(string value)
    {
        if (!LocalArtifactCatalog.IsValidId(value))
            throw new InvalidDataException("Logical map session identifier is invalid.");
    }
}

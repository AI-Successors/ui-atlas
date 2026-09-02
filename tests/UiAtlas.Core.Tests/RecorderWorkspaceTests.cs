using UiAtlas.Core.Cli;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RecorderWorkspaceTests
{
    [Fact]
    public void ExistingWorkspaceAllocatesResumeRecordingBesideExistingBundles()
    {
        using var temp = new TempDirectory();
        var mapsDirectory = Path.Combine(temp.Path, "maps");
        var recordingsDirectory = Path.Combine(temp.Path, "recordings");
        Directory.CreateDirectory(mapsDirectory);
        Directory.CreateDirectory(recordingsDirectory);

        var mapPath = Path.Combine(mapsDirectory, "excel.db");
        var manifestPath = Path.Combine(mapsDirectory, "excel.session.json");
        var existingRecordingPath = Path.Combine(recordingsDirectory, "excel.mlrec");
        File.WriteAllText(mapPath, string.Empty);
        File.WriteAllText(existingRecordingPath, string.Empty);

        var manifest = new LogicalMapSessionManifest(
            LogicalMapSessionStore.FormatVersion,
            "excel",
            "EXCEL",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            [new LogicalMapSessionRecording("excel", existingRecordingPath)]);

        var workspace = RecorderWorkspace.CreateExistingWorkspace(
            mapPath,
            Path.ChangeExtension(mapPath, ".json"),
            manifestPath,
            manifest);

        var next = workspace.CreateNextSession();

        Assert.Equal("excel-resume02", next.SessionId);
        Assert.Equal(Path.Combine(recordingsDirectory, "excel-resume02.mlrec"), next.RecordingPath);
    }

    [Fact]
    public void SavingCampaignMigratesVersionOneManifestToVersionTwo()
    {
        using var temp = new TempDirectory();
        var manifestPath = Path.Combine(temp.Path, "legacy.session.json");
        var mapPath = Path.Combine(temp.Path, "legacy.db");
        var created = DateTimeOffset.UtcNow.AddMinutes(-2);
        LogicalMapSessionStore.Save(manifestPath, new LogicalMapSessionManifest(
            LogicalMapSessionStore.LegacyFormatVersion,
            "legacy-map",
            "REVIT",
            created,
            created,
            []));

        var legacy = LogicalMapSessionStore.Load(manifestPath);
        var workspace = RecorderWorkspace.CreateExistingWorkspace(
            mapPath, Path.ChangeExtension(mapPath, ".json"), manifestPath, legacy);
        workspace.SaveAutoMapping(AutoMappingCampaignState.Empty(DateTimeOffset.UtcNow));

        var migrated = LogicalMapSessionStore.Load(manifestPath);
        Assert.Equal(LogicalMapSessionStore.FormatVersion, migrated.FormatVersion);
        Assert.NotNull(migrated.AutoMapping);
        Assert.Equal(created, migrated.CreatedUtc);
    }

    [Fact]
    public void QuickMapSnapshotRoundTripsWithoutBreakingVersionTwoManifest()
    {
        using var temp = new TempDirectory();
        var manifestPath = Path.Combine(temp.Path, "quick.session.json");
        var workspace = new RecorderWorkspace(
            "quick-map",
            "REVIT",
            Path.Combine(temp.Path, "quick-map.db"),
            Path.Combine(temp.Path, "quick-map.json"),
            manifestPath,
            ordinal => new RecorderSessionTarget(
                ordinal == 1 ? "quick-map" : $"quick-map-resume{ordinal:00}",
                Path.Combine(temp.Path, $"quick-map-{ordinal:00}.mlrec")));
        workspace.StageQuickMapSnapshot(new QuickMapSnapshotState(
            "quick-map", "surface-fingerprint", QuickMapCaptureStatus.Partial,
            12, 4, ["uia-timeout"], DateTimeOffset.UtcNow));
        workspace.AddCompletedSession("quick-map", Path.Combine(temp.Path, "quick-map-01.mlrec"));

        var loaded = LogicalMapSessionStore.Load(manifestPath);

        var snapshot = Assert.Single(loaded.QuickMapSnapshots!);
        Assert.Equal(12, snapshot.VisibleControlCount);
        Assert.Equal(4, snapshot.UnverifiedControlCount);
        Assert.Equal(QuickMapCaptureStatus.Partial, snapshot.Status);
        Assert.Equal(LogicalMapSessionStore.FormatVersion, loaded.FormatVersion);
    }

    [Fact]
    public void SealedInterruptedBundleReferencedByCampaignIsAdoptedForNextMerge()
    {
        using var temp = new TempDirectory();
        const string mapId = "resume-map";
        const string interruptedSessionId = "resume-map-resume02";
        _ = SyntheticBundleFactory.Create(
            temp.Path,
            "resume-map-resume02.mlrec",
            sessionId: interruptedSessionId,
            interactionTrace: true,
            interactionOperationId: "auto-command:save");
        var now = DateTimeOffset.UtcNow;
        var campaign = new AutoMappingCampaignState(
            AutoMappingCampaignState.CurrentFormatVersion,
            2,
            AutoMappingCampaignState.CurrentIdentityVersion,
            [new AutoMappingWorkItemState(
                "auto:item", AutoMappingWorkKind.Command, AutoMappingWorkStatus.Running,
                "fingerprint", "", 1, "", interruptedSessionId, "interaction-1", [], now)],
            now);
        var workspace = new RecorderWorkspace(
            mapId,
            "EXCEL",
            Path.Combine(temp.Path, "resume-map.db"),
            Path.Combine(temp.Path, "resume-map.json"),
            Path.Combine(temp.Path, "resume-map.session.json"),
            ordinal => new RecorderSessionTarget(
                ordinal == 1 ? mapId : $"{mapId}-resume{ordinal:00}",
                Path.Combine(temp.Path, ordinal == 1 ? "resume-map.mlrec" : $"resume-map-resume{ordinal:00}.mlrec")),
            autoMapping: campaign);

        workspace.AdoptReferencedRecordingEvidence(workspace.RecordingEvidence());

        var recording = Assert.Single(workspace.Recordings);
        Assert.Equal(interruptedSessionId, recording.SessionId);
        Assert.True(File.Exists(recording.RecordingPath));
    }

    [Fact]
    public void InvalidInterruptedBundleIsNotAdoptedAsResumeEvidence()
    {
        using var temp = new TempDirectory();
        const string mapId = "resume-map";
        const string interruptedSessionId = "resume-map-resume02";
        File.WriteAllText(Path.Combine(temp.Path, "resume-map-resume02.mlrec"), "incomplete");
        var now = DateTimeOffset.UtcNow;
        var campaign = new AutoMappingCampaignState(
            AutoMappingCampaignState.CurrentFormatVersion,
            2,
            AutoMappingCampaignState.CurrentIdentityVersion,
            [new AutoMappingWorkItemState(
                "auto:item", AutoMappingWorkKind.Command, AutoMappingWorkStatus.Running,
                "fingerprint", "", 1, "", interruptedSessionId, "interaction-1", [], now)],
            now);
        var workspace = new RecorderWorkspace(
            mapId,
            "EXCEL",
            Path.Combine(temp.Path, "resume-map.db"),
            Path.Combine(temp.Path, "resume-map.json"),
            Path.Combine(temp.Path, "resume-map.session.json"),
            ordinal => new RecorderSessionTarget(
                ordinal == 1 ? mapId : $"{mapId}-resume{ordinal:00}",
                Path.Combine(temp.Path, ordinal == 1 ? "resume-map.mlrec" : $"resume-map-resume{ordinal:00}.mlrec")),
            autoMapping: campaign);

        workspace.AdoptReferencedRecordingEvidence(workspace.RecordingEvidence());

        Assert.Empty(workspace.Recordings);
    }
}

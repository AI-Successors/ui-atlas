using UiAtlas.Core.Build;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class SpeculativePlanningTests
{
    [Fact]
    public async Task ParallelPlanningIsDeterministicBoundedAndUsesOnlyObservedControls()
    {
        var frame = SurfaceFrame();
        var profile = ApplicationPlanningProfile.Empty(
            new("EXCEL", "Microsoft Excel", "16", "XLMAIN"), DateTimeOffset.UtcNow);
        var input = new SpeculativePlannerInput(frame, "session-1", 1, null, profile);

        var first = await SpeculativeFrontierPlanner.PlanAsync(input, CancellationToken.None);
        var second = await SpeculativeFrontierPlanner.PlanAsync(input, CancellationToken.None);

        Assert.InRange(first.Predictions.Count, 1, SpeculativeFrontierPlanner.MaximumPredictedStates);
        Assert.All(first.Predictions, prediction => Assert.InRange(prediction.Depth, 1, 2));
        Assert.Equal(
            first.Predictions.Select(PredictionSignature),
            second.Predictions.Select(PredictionSignature));
        var hiddenFingerprint = SpeculativeActionFingerprint.Create(frame.Automation.Single(item => item.IsOffscreen), frame);
        Assert.DoesNotContain(first.Predictions, item => item.ActionFingerprint == hiddenFingerprint);
    }

    [Fact]
    public async Task CancelledRevisionDoesNotPublishAFrontier()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var input = new SpeculativePlannerInput(
            SurfaceFrame(), "session-1", 1, null,
            ApplicationPlanningProfile.Empty(new("EXCEL", "Excel", "16", "XLMAIN"), DateTimeOffset.UtcNow));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SpeculativeFrontierPlanner.PlanAsync(input, cancellation.Token));
    }

    [Fact]
    public void SpeculativeManifestRoundTripsWithoutChangingVersionTwo()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        var prediction = Prediction(now);
        var planning = new SpeculativePlanningState(
            SpeculativePlanningState.CurrentFormatVersion,
            3,
            "surface-fingerprint",
            [prediction],
            new(12, 2, 1),
            new(4, 2, 1, 1, 18),
            now,
            ["surface-fingerprint"]);
        var manifest = new LogicalMapSessionManifest(
            LogicalMapSessionStore.FormatVersion, "map-1", "EXCEL", now, now, [],
            SpeculativePlanning: planning);
        var path = Path.Combine(temp.Path, "map-1.session.json");

        LogicalMapSessionStore.Save(path, manifest);
        var loaded = LogicalMapSessionStore.Load(path);

        Assert.Equal(LogicalMapSessionStore.FormatVersion, loaded.FormatVersion);
        Assert.Equal(prediction, Assert.Single(loaded.SpeculativePlanning!.Predictions));
        Assert.Equal(2, loaded.SpeculativePlanning.Metrics.Reused);
    }

    [Fact]
    public void ApplicationRuleRequiresEvidenceAndDecaysAfterContradictions()
    {
        using var temp = new TempDirectory();
        var store = new ApplicationPlanningProfileStore(temp.Path);
        var key = new ApplicationPlanningProfileKey("REVIT", "Revit", "2026", "Afx");
        var profile = ApplicationPlanningProfile.Empty(key, DateTimeOffset.UtcNow);

        profile = store.RecordOutcome(profile, "fingerprint", AutoMappingWorkKind.Command, "popup", true, DateTimeOffset.UtcNow);
        Assert.False(Assert.Single(profile.Rules).IsReusable);
        profile = store.RecordOutcome(profile, "fingerprint", AutoMappingWorkKind.Command, "popup", true, DateTimeOffset.UtcNow);
        Assert.True(Assert.Single(profile.Rules).IsReusable);
        profile = store.RecordOutcome(profile, "fingerprint", AutoMappingWorkKind.Command, "popup", false, DateTimeOffset.UtcNow);
        profile = store.RecordOutcome(profile, "fingerprint", AutoMappingWorkKind.Command, "popup", false, DateTimeOffset.UtcNow);

        Assert.True(Assert.Single(profile.Rules).Disabled);
        Assert.False(Assert.Single(store.Load(key).Rules).IsReusable);
    }

    [Fact]
    public void PredictionsAreSeparateGraphStatesAndRepeatedProjectionDoesNotDuplicateThem()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var now = DateTimeOffset.UtcNow;
        var planning = new SpeculativePlanningState(
            SpeculativePlanningState.CurrentFormatVersion,
            1,
            "surface-fingerprint",
            [Prediction(now)],
            new(3, 1, 0),
            new(1, 0, 0, 0, 5),
            now,
            ["surface-fingerprint"]);

        var once = SpeculativeGraphProjector.Apply(graph, planning);
        var twice = SpeculativeGraphProjector.Apply(once, planning);

        Assert.True(GraphValidator.Validate(once).IsValid);
        Assert.Single(once.Nodes, node => NodeProperty(node, "layer") == "prediction");
        Assert.Equal(once.Nodes.Count, twice.Nodes.Count);
        Assert.Equal(once.Edges.Count, twice.Edges.Count);
        Assert.Contains(once.Edges, edge => edge.Kind == "predicts-transition");
        Assert.True(GraphValidator.Validate(GraphExport.ApplyProfile(once, includeSensitiveEvidence: false)).IsValid);
    }

    [Fact]
    public void ResumeDoesNotTrustPredictionWhoseRecordingIsMissing()
    {
        var now = DateTimeOffset.UtcNow;
        var stored = new SpeculativePlanningState(
            SpeculativePlanningState.CurrentFormatVersion,
            1,
            "surface-fingerprint",
            [Prediction(now) with
            {
                Status = SpeculativePredictionStatus.Matched,
                ResultSessionId = "golden",
                ResultFrameSequence = 2
            }],
            new(2, 1, 1),
            new(1, 1, 1, 0, 4),
            now,
            ["surface-fingerprint"]);

        var recovered = SpeculativePlanningRecovery.Recover(stored, [], now.AddMinutes(1));

        Assert.Equal(SpeculativePredictionStatus.Stale, Assert.Single(recovered.Predictions).Status);
        Assert.Equal(0, recovered.Metrics.Matched);
        Assert.Equal(string.Empty, recovered.SurfaceFingerprint);
    }

    private static string PredictionSignature(SpeculativePredictionState item) =>
        $"{item.PredictionId}|{item.ParentPredictionId}|{item.ActionFingerprint}|{item.Depth}|{item.Confidence:0.000}";

    private static SpeculativePredictionState Prediction(DateTimeOffset now) => new(
        "prediction:test", null, "surface-fingerprint", "action-fingerprint", "Open options",
        AutoMappingWorkKind.Command, "popup", 0.8, 1, 1, "surface-structure",
        SpeculativePredictionStatus.Predicted, "golden", 1, null, null, now);

    private static FrameObservation SurfaceFrame()
    {
        var bounds = new RectI(0, 0, 1200, 800);
        return new(
            1,
            DateTimeOffset.UtcNow,
            "raw/frames/frame-000001.png",
            new(1, 1, 42, "XLMAIN", "Book1 - Excel", bounds, true, true, false, false, 96),
            [
                new("tab-home", "root", "TabHome", "Home", "TabItem", "NetUITab", new(40, 20, 70, 30), true, false, "Win32", 1, ["SelectionItem"], IsSelected: true),
                new("tab-insert", "root", "TabInsert", "Insert", "TabItem", "NetUITab", new(112, 20, 70, 30), true, false, "Win32", 1, ["SelectionItem"]),
                new("command", "group", "FormatDropdown", "Format", "MenuItem", "NetUIAnchor", new(250, 62, 90, 28), true, false, "Win32", 1, ["ExpandCollapse"]),
                new("hidden", "group", "HiddenButton", "Hidden", "Button", "NetUIButton", new(360, 62, 80, 28), true, true, "Win32", 1, ["Invoke"])
            ],
            false,
            "ok",
            "quick-map:test");
    }

    private static string? NodeProperty(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;
}

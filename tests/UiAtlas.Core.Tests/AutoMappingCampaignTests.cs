using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class AutoMappingCampaignTests
{
    [Fact]
    public void FingerprintSurvivesWindowHandleAndScreenGeometryChanges()
    {
        var first = Control(11, new RectI(200, 100, 40, 20));
        var movedAndScaled = Control(999, new RectI(400, 200, 80, 40));

        var firstFingerprint = AutoMappingTargetFingerprint.Create(first, new RectI(0, 0, 1000, 500));
        var secondFingerprint = AutoMappingTargetFingerprint.Create(movedAndScaled, new RectI(0, 0, 2000, 1000));

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void ParentTabParticipatesInLongTermIdentity()
    {
        var target = Control(1, new RectI(100, 50, 40, 20));
        var fingerprint = AutoMappingTargetFingerprint.Create(target, new RectI(0, 0, 1000, 500));

        Assert.NotEqual(
            AutoMappingTargetFingerprint.ItemId(AutoMappingWorkKind.Command, "home", fingerprint),
            AutoMappingTargetFingerprint.ItemId(AutoMappingWorkKind.Command, "insert", fingerprint));
    }

    [Fact]
    public void ConfirmedFailuresPersistAndBecomeManualAfterSecondAttempt()
    {
        AutoMappingCampaignState? saved = null;
        var now = DateTimeOffset.UtcNow;
        var first = new AutoMappingCampaignTracker(null, state => saved = state, now);
        var itemId = first.Register(AutoMappingWorkKind.Command, Control(1, new RectI(10, 10, 20, 20)),
            new RectI(0, 0, 500, 300), "home");
        first.Start(itemId, "session-1", "interaction-1");
        Assert.False(first.Fail(itemId, "session-1", "interaction-1", "popup-not-confirmed"));

        var resumed = new AutoMappingCampaignTracker(saved, state => saved = state, now.AddMinutes(1));
        resumed.Start(itemId, "session-2", "interaction-2");
        Assert.True(resumed.Fail(itemId, "session-2", "interaction-2", "popup-not-confirmed"));

        Assert.Equal(AutoMappingWorkStatus.NeedsManual, saved!.Items.Single().Status);
        Assert.Equal(2, saved.Items.Single().Attempts);
        Assert.False(resumed.CanAttempt(itemId));
    }

    [Fact]
    public void MatchingManualClickResolvesExactlyOneManualReviewItem()
    {
        AutoMappingCampaignState? saved = null;
        var control = Control(1, new RectI(10, 10, 20, 20));
        var bounds = new RectI(0, 0, 500, 300);
        var tracker = new AutoMappingCampaignTracker(null, state => saved = state, DateTimeOffset.UtcNow);
        var itemId = tracker.Register(AutoMappingWorkKind.Command, control, bounds, "home");
        tracker.Start(itemId, "session-1", "interaction-1");
        tracker.Fail(itemId, "session-1", "interaction-1", "popup-not-confirmed");
        tracker.Start(itemId, "session-2", "interaction-2");
        tracker.Fail(itemId, "session-2", "interaction-2", "popup-not-confirmed");

        var confirmed = tracker.ConfirmManual(control, bounds, "session-3", "interaction-3", [7]);

        Assert.True(confirmed);
        var item = Assert.Single(saved!.Items);
        Assert.Equal(AutoMappingWorkStatus.Succeeded, item.Status);
        Assert.Equal("Localized label", item.DisplayName);
        Assert.Equal([7], item.ResultFrameSequences);
    }

    [Fact]
    public void InterruptedAttemptReturnsToPendingWithoutCountingAsConfirmedFailure()
    {
        AutoMappingCampaignState? saved = null;
        var tracker = new AutoMappingCampaignTracker(null, state => saved = state, DateTimeOffset.UtcNow);
        var itemId = tracker.Register(AutoMappingWorkKind.Tab, Control(1, new RectI(10, 10, 50, 20)),
            new RectI(0, 0, 500, 300));
        tracker.Start(itemId, "session-1", "interaction-1");

        var resumed = new AutoMappingCampaignTracker(saved, state => saved = state, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(resumed.CanAttempt(itemId));
        Assert.Equal(0, resumed.Attempts(itemId));
        Assert.Equal(AutoMappingWorkStatus.Pending, resumed.Status(itemId));
    }

    [Fact]
    public void PlannerDoesNotChooseAmbiguousTarget()
    {
        var tracker = new AutoMappingCampaignTracker(null, _ => { }, DateTimeOffset.UtcNow);
        var itemId = tracker.Register(AutoMappingWorkKind.Command, Control(1, new RectI(10, 10, 20, 20)),
            new RectI(0, 0, 500, 300), "home");

        var plan = AutoMappingCampaignPlanner.Plan(["first", "second"], _ => itemId, tracker);

        Assert.Empty(plan.Ready);
        Assert.Equal([itemId], plan.AmbiguousItemIds);
        Assert.Equal(AutoMappingWorkStatus.NeedsManual, tracker.Status(itemId));
    }

    [Fact]
    public void RecoveryRejectsSuccessWithoutImmutableRecordingEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var stored = new AutoMappingCampaignState(
            AutoMappingCampaignState.CurrentFormatVersion,
            2,
            AutoMappingCampaignState.CurrentIdentityVersion,
            [new AutoMappingWorkItemState(
                "auto:item", AutoMappingWorkKind.Command, AutoMappingWorkStatus.Succeeded,
                "fingerprint", "home", 1, "", "missing-session", "missing-interaction", [3], now)],
            now);

        var recovered = AutoMappingCampaignRecovery.Recover(stored, [], now.AddMinutes(1));

        var item = Assert.Single(recovered.Items);
        Assert.Equal(AutoMappingWorkStatus.Pending, item.Status);
        Assert.Equal("missing-recording-evidence", item.DiagnosticCode);
    }

    [Fact]
    public void RecoveryKeepsSuccessOnlyWhenRecordingContainsMatchingInteractionAndFrame()
    {
        using var temp = new TempDirectory();
        var recordingPath = SyntheticBundleFactory.Create(
            temp.Path,
            interactionTrace: true,
            interactionOperationId: "auto-command:save");
        var source = new AutomationObservation(
            "1.1", "1", "save", "Save", "Button", "Button",
            new RectI(10, 10, 80, 24), true, false, "Synthetic", 1, ["Invoke"]);
        var fingerprint = AutoMappingTargetFingerprint.Create(source, new RectI(0, 0, 800, 600));
        var now = DateTimeOffset.UtcNow;
        var stored = new AutoMappingCampaignState(
            AutoMappingCampaignState.CurrentFormatVersion,
            2,
            AutoMappingCampaignState.CurrentIdentityVersion,
            [new AutoMappingWorkItemState(
                AutoMappingTargetFingerprint.ItemId(AutoMappingWorkKind.Command, "", fingerprint),
                AutoMappingWorkKind.Command,
                AutoMappingWorkStatus.Succeeded,
                fingerprint,
                "",
                1,
                "",
                "golden",
                "interaction-1",
                [2],
                now)],
            now);

        var recovered = AutoMappingCampaignRecovery.Recover(
            stored, [new LogicalMapSessionRecording("golden", recordingPath)], now.AddMinutes(1));

        Assert.Equal(AutoMappingWorkStatus.Succeeded, Assert.Single(recovered.Items).Status);
    }

    [Fact]
    public void RecoveryCanConfirmInterruptedCommandButReplansInterruptedTab()
    {
        using var temp = new TempDirectory();
        var recordingPath = SyntheticBundleFactory.Create(
            temp.Path,
            interactionTrace: true,
            interactionOperationId: "auto-command:save");
        var source = new AutomationObservation(
            "1.1", "1", "save", "Save", "Button", "Button",
            new RectI(10, 10, 80, 24), true, false, "Synthetic", 1, ["Invoke"]);
        var fingerprint = AutoMappingTargetFingerprint.Create(source, new RectI(0, 0, 800, 600));
        var now = DateTimeOffset.UtcNow;
        AutoMappingWorkItemState Running(AutoMappingWorkKind kind, string suffix) => new(
            "auto:" + suffix, kind, AutoMappingWorkStatus.Running, fingerprint, "", 1, "",
            "golden", "interaction-1", [], now);
        var stored = new AutoMappingCampaignState(
            AutoMappingCampaignState.CurrentFormatVersion,
            3,
            AutoMappingCampaignState.CurrentIdentityVersion,
            [Running(AutoMappingWorkKind.Command, "command"), Running(AutoMappingWorkKind.Tab, "tab")],
            now);

        var recovered = AutoMappingCampaignRecovery.Recover(
            stored, [new LogicalMapSessionRecording("golden", recordingPath)], now.AddMinutes(1));

        Assert.Equal(AutoMappingWorkStatus.Succeeded,
            recovered.Items.Single(item => item.Kind == AutoMappingWorkKind.Command).Status);
        var tab = recovered.Items.Single(item => item.Kind == AutoMappingWorkKind.Tab);
        Assert.Equal(AutoMappingWorkStatus.Pending, tab.Status);
        Assert.Equal(0, tab.Attempts);
    }

    private static AutomationObservation Control(long hwnd, RectI bounds) =>
        new("runtime", "parent", "button-id", "Localized label", "ControlType.Button", "RibbonButton",
            bounds, true, false, "WPF", hwnd, ["Invoke"]);
}

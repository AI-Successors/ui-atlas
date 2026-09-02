using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class AutoCaptureQualityGateTests
{
    [Fact]
    public void EmptyAutoFramesCannotPassQualityGate()
    {
        var quality = AutoCaptureQualityGate.Evaluate(
        [
            Frame(1, "auto-tabs:initial-surface", []),
            Frame(2, "auto-tabs:tab:insert:first-visit", [])
        ]);

        Assert.False(quality.IsSufficient);
        Assert.Equal(2, quality.EmptyFrameCount);
    }

    [Fact]
    public void PopulatedRibbonFramesPassQualityGate()
    {
        var controls = Enumerable.Range(1, 12)
            .Select(index => new AutomationObservation($"1.{index}", "1", $"button{index}", $"Button {index}",
                "ControlType.Button", "NetUIRibbonButton", new RectI(index * 20, 20, 18, 18), true, false, "Win32", 1))
            .ToArray();

        var quality = AutoCaptureQualityGate.Evaluate([Frame(1, "auto-tabs:initial-surface", controls)]);

        Assert.True(quality.IsSufficient);
        Assert.Equal(12, quality.ControlCount);
    }

    [Fact]
    public void ShortPartialRibbonCaptureStillBuildsAVisibleMap()
    {
        var controls = Enumerable.Range(1, 7)
            .Select(index => new AutomationObservation($"1.{index}", "1", $"tab{index}", $"Tab {index}",
                "ControlType.Button", "Button", new RectI(index * 60, 20, 56, 24), true, false, "WPF", 1))
            .ToArray();

        var quality = AutoCaptureQualityGate.Evaluate([Frame(1, "auto-tabs:initial-surface", controls)]);

        Assert.True(quality.IsSufficient);
        Assert.Equal(7, quality.ControlCount);
        Assert.False(quality.CampaignComplete);
    }

    [Fact]
    public void CompletedCampaignAllowsShortFinalRecording()
    {
        var now = DateTimeOffset.UtcNow;
        var campaign = new AutoMappingCampaignState(
            AutoMappingCampaignState.CurrentFormatVersion,
            4,
            AutoMappingCampaignState.CurrentIdentityVersion,
            [new AutoMappingWorkItemState(
                "auto:tab", AutoMappingWorkKind.Tab, AutoMappingWorkStatus.Succeeded,
                "fingerprint", "", 1, "", "session", "interaction", [2], now)],
            now);
        var controls = Enumerable.Range(1, 3)
            .Select(index => new AutomationObservation($"1.{index}", "1", $"button{index}", $"Button {index}",
                "ControlType.Button", "NetUIRibbonButton", new RectI(index * 20, 20, 18, 18), true, false, "Win32", 1))
            .ToArray();

        var quality = AutoCaptureQualityGate.Evaluate(
            [Frame(1, "auto-tabs:tab:final:first-visit", controls)], campaign);

        Assert.True(quality.IsSufficient);
        Assert.Equal(3, quality.ControlCount);
    }

    [Fact]
    public void VisualLegacyAutoFramesPassQualityGate()
    {
        var visual = new AutomationObservation(
            "visual:reservations", "", "visual:reservations", "Reservations...",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new RectI(20, 80, 90, 42),
            false, true, "UiAtlas.Visual.Ocr", 1, ["Invoke"]);

        var quality = AutoCaptureQualityGate.Evaluate(
        [
            Frame(1, "quick-map:auto-tabs-initial-surface", [visual]),
            Frame(2, "adaptive-root-change", [visual with { Name = "Rooms Calendar" }])
        ]);

        Assert.True(quality.IsSufficient);
        Assert.Equal(2, quality.FrameCount);
        Assert.Equal(2, quality.ControlCount);
    }

    [Fact]
    public void UsableControlsPassQualityGateWhenCaptureTriggerIsNew()
    {
        var control = new AutomationObservation(
            "1.1", "1", "unknown-trigger-button", "Unknown trigger button",
            "ControlType.Button", "Button", new RectI(20, 20, 100, 30),
            true, false, "Win32", 1);

        var quality = AutoCaptureQualityGate.Evaluate(
        [
            Frame(1, "future-visual-capture-path", [control])
        ]);

        Assert.True(quality.IsSufficient);
        Assert.Equal(1, quality.FrameCount);
        Assert.Equal(1, quality.ControlCount);
    }

    private static FrameObservation Frame(long sequence, string trigger, IReadOnlyList<AutomationObservation> controls) =>
        new(sequence, DateTimeOffset.UnixEpoch, "", new WindowObservation(1, 1, 7, "XLMAIN", "Excel",
            new RectI(0, 0, 800, 600), true, true, false, false, 96), controls, false, "ok", trigger);
}

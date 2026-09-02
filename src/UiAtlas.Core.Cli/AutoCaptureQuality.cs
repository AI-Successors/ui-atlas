using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal sealed record AutoCaptureQuality(
    int FrameCount,
    int EmptyFrameCount,
    int ControlCount,
    bool CampaignComplete = false)
{
    // A short or interrupted automatic pass is still a useful partial map when it
    // contains real observed controls. The former arbitrary ten-control threshold
    // discarded valid Revit recordings (for example seven Ribbon tabs) after the
    // user had waited for finalization.
    public bool IsSufficient => FrameCount > 0 && (ControlCount > 0 || CampaignComplete);
}

internal static class AutoCaptureQualityGate
{
    public static AutoCaptureQuality Inspect(string recordingPath)
    {
        using var bundle = RecordingBundle.Open(recordingPath);
        var frames = bundle.Entries
            .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                            entry.EndsWith(".json", StringComparison.Ordinal))
            .Select(bundle.ReadJson<FrameObservation>);
        return Evaluate(frames);
    }

    public static AutoCaptureQuality Inspect(
        IEnumerable<string> recordingPaths,
        AutoMappingCampaignState? campaign)
    {
        var frames = new List<FrameObservation>();
        foreach (var recordingPath in recordingPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var bundle = RecordingBundle.Open(recordingPath);
            frames.AddRange(bundle.Entries
                .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                                entry.EndsWith(".json", StringComparison.Ordinal))
                .Select(bundle.ReadJson<FrameObservation>));
        }

        var evaluated = Evaluate(frames);
        return evaluated with { CampaignComplete = IsCampaignComplete(campaign) };
    }

    internal static AutoCaptureQuality Evaluate(IEnumerable<FrameObservation> frames)
        => Evaluate(frames, null);

    internal static AutoCaptureQuality Evaluate(
        IEnumerable<FrameObservation> frames,
        AutoMappingCampaignState? campaign)
    {
        // Trigger names describe how a frame was captured, not whether its
        // controls are usable. New adaptive/visual capture paths must not make
        // an otherwise valid automatic recording fail finalization merely
        // because their trigger was not added to this gate yet.
        var autoFrames = frames
            .Where(frame => IsAutoSurfaceFrame(frame) || frame.Automation.Any(IsUsableAutoControl))
            .ToArray();
        return new(
            autoFrames.Length,
            autoFrames.Count(frame => frame.Automation.Count == 0),
            autoFrames.Sum(frame => frame.Automation.Count(IsUsableAutoControl)),
            IsCampaignComplete(campaign));
    }

    private static bool IsAutoSurfaceFrame(FrameObservation frame) =>
        string.Equals(frame.Trigger, "auto-tabs:initial-surface", StringComparison.Ordinal) ||
        string.Equals(frame.Trigger, "quick-map:auto-tabs-initial-surface", StringComparison.Ordinal) ||
        string.Equals(frame.Trigger, "adaptive-root-change", StringComparison.Ordinal) ||
        frame.Trigger.StartsWith("auto-tabs:tab:", StringComparison.Ordinal) &&
        frame.Trigger.EndsWith(":first-visit", StringComparison.Ordinal);

    private static bool IsUsableAutoControl(AutomationObservation control) =>
        AutomationObservationVisibility.FilterEffectivelyVisible([control]).Count > 0 ||
        control.FrameworkId is ("UiAtlas.Visual.Ocr" or "UiAtlas.Visual.Geometry") &&
        control.Bounds.Width > 0 && control.Bounds.Height > 0;

    private static bool IsCampaignComplete(AutoMappingCampaignState? campaign)
    {
        if (campaign?.Items is not { Count: > 0 } items)
            return false;
        var surfaceItems = items.Where(item => item.Kind is AutoMappingWorkKind.Tab or AutoMappingWorkKind.Backstage).ToArray();
        return surfaceItems.Length > 0 &&
               surfaceItems.Any(item => item.Status == AutoMappingWorkStatus.Succeeded) &&
               items.All(item => item.Status is AutoMappingWorkStatus.Succeeded or
                   AutoMappingWorkStatus.NeedsManual or AutoMappingWorkStatus.Skipped);
    }
}

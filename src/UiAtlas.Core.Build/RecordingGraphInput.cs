using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Build;

/// <summary>
/// A validated, transient set of representative recording observations for the
/// canonical graph builder. Durable recording authority remains the sealed
/// recording bundle; this input supports live projections without a second
/// Raw/Raw/Semantic implementation.
/// </summary>
public sealed record RecordingGraphInput(
    RecordingManifest Manifest,
    IReadOnlyList<FrameObservation> Observations,
    IReadOnlyList<InteractionObservation> Interactions);

public static class RecordingGraphInputValidator
{
    public static ValidationReport Validate(RecordingGraphInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = new List<ValidationIssue>();
        var manifest = input.Manifest;
        var observations = input.Observations;
        var interactions = input.Interactions;

        if (manifest is null || observations is null || interactions is null)
            return Invalid("live.required", "input", "Manifest, observations, and interactions are required.");
        var target = manifest.Target;
        if (target is null)
            return Invalid("live.target", "manifest.target", "The sealed target scope is required.");

        if (manifest.FormatVersion != FormatVersions.RecordingBundle)
            Add("live.version", "manifest", "Unsupported recording format version.");
        if (string.IsNullOrWhiteSpace(manifest.SessionId) || manifest.SessionId.Length > 200 ||
            manifest.EndedUtc < manifest.StartedUtc || !manifest.ExplicitConsent)
            Add("live.manifest", "manifest", "Session identity, time range, or consent is invalid.");
        if (manifest.FrameCount < observations.Count || manifest.FrameCount < 0 ||
            manifest.EventCount is < 0 or > RecordingContractLimits.MaxEvents)
            Add("live.count", "manifest", "Manifest counts do not cover the supplied live evidence.");
        if (manifest.Privacy is null || manifest.Retention is null ||
            target.SelectedHwnd == 0 || target.RootOwnerHwnd == 0 ||
            target.ProcessId <= 0 || string.IsNullOrWhiteSpace(target.ProcessName) ||
            target.ProcessName.Length > 512 ||
            target.Policy != "selected-root-owner-and-owned-popups/1")
            Add("live.target", "manifest.target", "The sealed target scope is invalid.");

        var frameSequences = new HashSet<long>();
        long previousSequence = 0;
        foreach (var frame in observations)
        {
            var path = frame is null ? "observations" : $"observations/{frame.Sequence}";
            if (frame is null)
            {
                Add("live.observation", path, "Observation is null.");
                continue;
            }

            if (frame.Sequence <= 0 || frame.Sequence <= previousSequence || !frameSequences.Add(frame.Sequence))
                Add("live.sequence", path, "Observation sequences must be unique and strictly increasing.");
            previousSequence = Math.Max(previousSequence, frame.Sequence);
            if (frame.Window is null || !ValidWindow(frame.Window) ||
                frame.Window.ProcessId != target.ProcessId ||
                frame.Window.RootOwnerHwnd != target.RootOwnerHwnd)
                Add("live.window", path, "The root window observation is invalid or outside the sealed target.");
            if (frame.Automation is null || frame.Automation.Count > RecordingContractLimits.MaxControlsPerFrame ||
                frame.AutomationStatus is null || frame.AutomationStatus.Length > 128 ||
                frame.Trigger is null || frame.Trigger.Length > 256)
                Add("live.observation", path, "Observation fields exceed the live-input contract.");
            if (frame.CapturePhase is not ("baseline" or "action" or "post-trigger" or "materialized" or "final") ||
                frame.ObservationScope is not ("full-root" or "popup-delta" or "control-delta"))
                Add("live.phase", path, "Observation capture phase or scope is invalid.");
            if (frame.ScreenshotBounds is { Width: <= 0 } or { Height: <= 0 })
                Add("live.screenshot", path, "Screenshot bounds must be positive when supplied.");
            if (!string.IsNullOrEmpty(frame.FrameEntry) && manifest.Files is null)
                Add("live.screenshot", path, "Transient inputs cannot claim an unsealed screenshot entry.");

            var scopedWindows = frame.ScopedWindows ?? (frame.Window is null ? [] : [frame.Window]);
            if (scopedWindows.Count == 0 || scopedWindows.Count > RecordingContractLimits.MaxScopedWindows ||
                scopedWindows.Any(window => window is null || !ValidWindow(window) ||
                    window.ProcessId != target.ProcessId ||
                    window.RootOwnerHwnd != target.RootOwnerHwnd &&
                    !IsExplicitSameProcessDialog(frame, window, target)))
                Add("live.scope", path, "Scoped windows are invalid or outside the sealed root-owner family.");
            var scopedHwnds = scopedWindows.Where(window => window is not null).Select(window => window.Hwnd).ToHashSet();
            if (!scopedHwnds.Contains(target.RootOwnerHwnd) ||
                frame.ObservedWindowHwnds is { Count: > RecordingContractLimits.MaxScopedWindows } ||
                frame.ObservedWindowHwnds is not null &&
                (frame.ObservedWindowHwnds.Distinct().Count() != frame.ObservedWindowHwnds.Count ||
                 frame.ObservedWindowHwnds.Any(hwnd => !scopedHwnds.Contains(hwnd))))
                Add("live.scope", path, "Observed-window scope is inconsistent with the captured family.");

            if (frame.Automation is not null)
            foreach (var automation in frame.Automation)
                if (automation is null || !ValidAutomation(automation) ||
                    automation.WindowHwnd != 0 && !scopedHwnds.Contains(automation.WindowHwnd))
                    Add("live.automation", path, "UI Automation evidence is invalid or outside the captured family.");
            if (frame.Extraction is not null && !ValidExtraction(frame.Extraction, scopedHwnds))
                Add("live.extraction", path, "Adaptive extraction evidence is invalid or outside the captured family.");
        }

        if (interactions.Count > RecordingContractLimits.MaxInteractions)
            Add("live.interaction", "interactions", "Interaction count exceeds the contract limit.");
        var interactionIds = new HashSet<string>(StringComparer.Ordinal);
        var interactionSequences = new HashSet<long>();
        foreach (var interaction in interactions)
        {
            var path = interaction is null ? "interactions" : $"interactions/{interaction.Sequence}";
            if (interaction is null || string.IsNullOrWhiteSpace(interaction.InteractionId) ||
                interaction.InteractionId.Length > 128 || string.IsNullOrWhiteSpace(interaction.OperationId) ||
                interaction.OperationId.Length > 256 || interaction.Attempt < 1 || interaction.Sequence < 1 ||
                !Enum.IsDefined(interaction.Actor) || !Enum.IsDefined(interaction.Gesture) ||
                !Enum.IsDefined(interaction.Action) || !Enum.IsDefined(interaction.Outcome) ||
                interaction.InputSequences is null || interaction.ResultFrameSequences is null ||
                interaction.CompletedUtc < interaction.StartedUtc || interaction.DiagnosticCode is null ||
                interaction.DiagnosticCode.Length > 256)
            {
                Add("live.interaction", path, "Interaction evidence is invalid.");
                continue;
            }

            if (!interactionIds.Add(interaction.InteractionId) || !interactionSequences.Add(interaction.Sequence))
                Add("live.interaction", path, "Interaction identifiers and sequences must be unique.");
            if (!frameSequences.Contains(interaction.SourceFrameSequence) ||
                interaction.ResultFrameSequences.Any(sequence => !frameSequences.Contains(sequence)))
                Add("live.interaction", path, "Interaction references missing representative frames.");
            if (interaction.Outcome == InteractionOutcome.Succeeded &&
                (interaction.SourceControl is null || interaction.ResultFrameSequences.Count == 0))
                Add("live.interaction", path, "A successful interaction requires source-control and result-frame evidence.");
            if (interaction.SourceControl is not null && !ValidAutomation(interaction.SourceControl))
                Add("live.interaction", path, "Interaction source-control evidence is invalid.");
        }

        return new(!issues.Any(issue => issue.Severity == "error"), issues);

        void Add(string code, string path, string message) =>
            issues.Add(new(code, "error", path, message));
    }

    internal static void ThrowIfInvalid(RecordingGraphInput input)
    {
        var validation = Validate(input);
        if (!validation.IsValid)
            throw new InvalidDataException("Live recording graph input validation failed: " +
                string.Join(", ", validation.Issues.Take(8)
                    .Select(issue => $"{issue.Code}@{issue.Path}: {issue.Message}")));
    }

    private static ValidationReport Invalid(string code, string path, string message) =>
        new(false, [new(code, "error", path, message)]);

    private static bool ValidWindow(WindowObservation value) =>
        value.ProcessId > 0 && value.Hwnd != 0 && value.RootOwnerHwnd != 0 &&
        value.ClassName is { Length: <= 512 } && value.Title is { Length: <= 4_096 } &&
        value.Bounds.Width >= 0 && value.Bounds.Height >= 0 &&
        (long)value.Bounds.Width * value.Bounds.Height <= 16_000_000 && value.Dpi is >= 48 and <= 960;

    private static bool IsExplicitSameProcessDialog(
        FrameObservation frame,
        WindowObservation window,
        TargetScope target)
    {
        if (!frame.Trigger.StartsWith("adaptive-dialog:", StringComparison.Ordinal) ||
            window.ProcessId != target.ProcessId ||
            frame.ObservedWindowHwnds?.Contains(window.Hwnd) != true)
            return false;

        return frame.Automation?.Any(control =>
        {
            if (control.WindowHwnd != window.Hwnd || control.IsOffscreen ||
                control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                return false;
            var type = control.ControlType.Contains('.')
                ? control.ControlType[(control.ControlType.LastIndexOf('.') + 1)..]
                : control.ControlType;
            if (type is not ("Window" or "Pane" or "Custom"))
                return false;
            var toleranceX = Math.Max(4, window.Bounds.Width / 50);
            var toleranceY = Math.Max(4, window.Bounds.Height / 50);
            return Math.Abs((long)control.Bounds.X - window.Bounds.X) <= toleranceX &&
                   Math.Abs((long)control.Bounds.Y - window.Bounds.Y) <= toleranceY &&
                   Math.Abs((long)control.Bounds.Width - window.Bounds.Width) <= toleranceX * 2L &&
                   Math.Abs((long)control.Bounds.Height - window.Bounds.Height) <= toleranceY * 2L;
        }) == true;
    }

    private static bool ValidAutomation(AutomationObservation item) =>
        item.RuntimeId is { Length: <= 4_096 } && item.ParentRuntimeId is { Length: <= 4_096 } &&
        item.AutomationId is { Length: <= 4_096 } && item.Name is { Length: <= 16_384 } &&
        item.ControlType is { Length: <= 512 } && item.ClassName is { Length: <= 512 } &&
        item.FrameworkId is { Length: <= 512 } && item.Bounds.Width >= 0 && item.Bounds.Height >= 0 &&
        (long)item.Bounds.Width * item.Bounds.Height <= 16_000_000 &&
        item.SupportedPatterns is not { Count: > 128 } &&
        item.SupportedPatterns?.Any(pattern => pattern is null || pattern.Length > 512) != true;

    private static bool ValidExtraction(AdaptiveExtractionSnapshot value, IReadOnlySet<long> scopedHwnds)
    {
        if (value.FormatVersion is null or { Length: > 128 } || value.StopReason is null or { Length: > 256 } ||
            value.Sources is null or { Count: > 256 } || value.Candidates is null or { Count: > RecordingContractLimits.MaxControlsPerFrame } ||
            value.Gaps is null or { Count: > 4_096 } || value.DurationMs is < 0 or > 120_000 || value.ProbeCount is < 0 or > 128)
            return false;
        if (value.Sources.Sum(source => source?.Evidence?.Count ?? 0) > RecordingContractLimits.MaxControlsPerFrame * 8) return false;
        return value.Sources.All(source => source is not null && source.Evidence is not null && source.Evidence.All(evidence =>
                   evidence is not null && evidence.Control is not null && ValidAutomation(evidence.Control) &&
                   (evidence.Control.WindowHwnd == 0 || scopedHwnds.Contains(evidence.Control.WindowHwnd)))) &&
               value.Candidates.All(candidate => candidate is not null && candidate.Control is not null && ValidAutomation(candidate.Control) &&
                   (candidate.Control.WindowHwnd == 0 || scopedHwnds.Contains(candidate.Control.WindowHwnd)));
    }
}

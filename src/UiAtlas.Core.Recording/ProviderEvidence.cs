using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

public static class ProviderEvidence
{
    public static ProviderObservation Create(long sequence, DateTimeOffset started, DateTimeOffset completed,
        WindowObservation target, long baseFrame, IReadOnlyList<AutomationObservation> controls,
        bool timedOut, string status, int accepted)
    {
        var retained = controls.Take(RecordingContractLimits.MaxProviderControls).Select(Redact).ToArray();
        return new(sequence, started, completed, target, baseFrame, "uia-popup/1", Bounded(status), timedOut,
            controls.Count, retained, accepted,
            timedOut ? "provider-timeout" : controls.Count == 0 ? "provider-empty" :
                accepted == 0 ? Rejection(target, controls) : accepted < controls.Count ? "accepted-subtree" : "accepted",
            retained.Length < controls.Count || controls.Any(NeedsTruncation));
    }

    private static bool NeedsTruncation(AutomationObservation control) =>
        new[] { control.RuntimeId, control.ParentRuntimeId, control.AutomationId, control.Name,
            control.OcrText, control.ClassName, control.ControlType, control.FrameworkId, control.VisualRole,
            control.VisualGroupId, control.ToggleState, control.ExpandCollapseState }.Any(value => value is { Length: > 512 }) ||
        control.SupportedPatterns is { Count: > 32 } || control.SupportedPatterns?.Any(value => value.Length > 512) == true;

    private static string Rejection(WindowObservation target, IReadOnlyList<AutomationObservation> controls)
    {
        var owned = controls.Where(control => control.WindowHwnd == 0 || control.WindowHwnd == target.Hwnd).ToArray();
        if (owned.Length == 0) return "wrong-ownership";
        var inside = owned.Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            control.Bounds.X >= target.Bounds.X - 8 && control.Bounds.Y >= target.Bounds.Y - 8 &&
            (long)control.Bounds.X + control.Bounds.Width <= (long)target.Bounds.X + target.Bounds.Width + 8 &&
            (long)control.Bounds.Y + control.Bounds.Height <= (long)target.Bounds.Y + target.Bounds.Height + 8).ToArray();
        if (inside.Length == 0) return "outside-target-bounds";
        var ids = inside.Select(control => control.RuntimeId).ToHashSet(StringComparer.Ordinal);
        var roots = inside.Where(control => !ids.Contains(control.ParentRuntimeId) &&
            control.ControlType.Replace("ControlType.", "", StringComparison.Ordinal) is "Menu" or "List" or "Tree" or "Window" or "Pane" or "Custom" or "ToolBar").ToArray();
        if (roots.Length == 0) return "missing-popup-root";
        var connected = roots.Select(control => control.RuntimeId).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var count = connected.Count;
            foreach (var control in inside) if (connected.Contains(control.ParentRuntimeId)) connected.Add(control.RuntimeId);
            if (count == connected.Count) break;
        }
        return inside.Any(control => !connected.Contains(control.RuntimeId)) ? "disconnected-content" : "empty-or-incomplete-content";
    }

    private static string Bounded(string? value) => value is null ? "" : value[..Math.Min(value.Length, 512)];

    private static AutomationObservation Redact(AutomationObservation control)
    {
        var privateText = control.ControlType is "Edit" or "Document" or "ControlType.Edit" or "ControlType.Document";
        return control with
        {
            RuntimeId = Bounded(control.RuntimeId), ParentRuntimeId = Bounded(control.ParentRuntimeId),
            AutomationId = Bounded(control.AutomationId), Name = privateText ? "[redacted]" : Bounded(control.Name),
            OcrText = privateText ? null : control.OcrText is null ? null : Bounded(control.OcrText),
            ClassName = Bounded(control.ClassName), ControlType = Bounded(control.ControlType),
            FrameworkId = Bounded(control.FrameworkId), VisualRole = control.VisualRole is null ? null : Bounded(control.VisualRole),
            VisualGroupId = control.VisualGroupId is null ? null : Bounded(control.VisualGroupId),
            ToggleState = control.ToggleState is null ? null : Bounded(control.ToggleState),
            ExpandCollapseState = control.ExpandCollapseState is null ? null : Bounded(control.ExpandCollapseState),
            SupportedPatterns = control.SupportedPatterns?.Take(32).Select(Bounded).ToArray()
        };
    }
}

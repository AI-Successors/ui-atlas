using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal static class VisualNativeVerification
{
    public const int MaximumProbePoints = 96;

    public static IReadOnlyList<RectI> Plan(
        IReadOnlyList<AutomationObservation> visualCandidates,
        IReadOnlyList<AutomationObservation> knownNativeControls)
        => PlanAll(visualCandidates, knownNativeControls)
            .Take(MaximumProbePoints)
            .ToArray();

    public static IReadOnlyList<RectI> PlanAll(
        IReadOnlyList<AutomationObservation> visualCandidates,
        IReadOnlyList<AutomationObservation> knownNativeControls)
    {
        ArgumentNullException.ThrowIfNull(visualCandidates);
        ArgumentNullException.ThrowIfNull(knownNativeControls);

        return visualCandidates
            .Where(IsNativeProbeCandidate)
            .Where(candidate => !knownNativeControls.Any(native => NativeConfirmsCandidate(native, candidate)))
            .OrderBy(Priority)
            .ThenBy(candidate => candidate.Bounds.Y)
            .ThenBy(candidate => candidate.Bounds.X)
            .ThenBy(candidate => candidate.Bounds.Width)
            .ThenBy(candidate => candidate.Bounds.Height)
            .Select(candidate => new RectI(
                candidate.Bounds.X + candidate.Bounds.Width / 2,
                candidate.Bounds.Y + candidate.Bounds.Height / 2,
                1,
                1))
            .Distinct()
            .ToArray();
    }

    private static bool IsNativeProbeCandidate(AutomationObservation control)
    {
        if (!IsVisualCandidate(control)) return false;

        // A visually reconstructed grid can contain hundreds of cells. Probing
        // every cell through the same UIA provider serializes several worker
        // deadlines without improving the already known row/column geometry.
        // Keep the complete grid as structural evidence and spend native point
        // probes on actionable leaves and headers instead.
        return NormalizeType(control.ControlType) is not ("Table" or "DataGrid" or "DataItem" or
            "Header" or "List" or "Tree" or "Tab");
    }

    public static IReadOnlyList<AutomationObservation> RetainUnconfirmedVisuals(
        IReadOnlyList<AutomationObservation> visualCandidates,
        IReadOnlyList<AutomationObservation> recoveredNativeControls) =>
        visualCandidates
            .Where(candidate => !recoveredNativeControls.Any(native => NativeConfirmsCandidate(native, candidate)))
            .ToArray();

    public static IReadOnlyList<AutomationObservation> RetainUnconfirmedStructures(
        IReadOnlyList<AutomationObservation> visualCandidates,
        IReadOnlyList<AutomationObservation> recoveredNativeControls) =>
        visualCandidates
            .Where(IsStructuralCandidate)
            .Where(candidate => !recoveredNativeControls.Any(native => NativeConfirmsCandidate(native, candidate)))
            .ToArray();

    internal static bool NativeConfirmsCandidate(
        AutomationObservation native,
        AutomationObservation candidate)
    {
        if (IsVisualCandidate(native) || native.IsOffscreen ||
            native.Bounds.Width < 1 || native.Bounds.Height < 1 || !IsConfirmingLeaf(native))
            return false;
        var centerX = candidate.Bounds.X + candidate.Bounds.Width / 2;
        var centerY = candidate.Bounds.Y + candidate.Bounds.Height / 2;
        if (centerX < native.Bounds.X || centerX >= native.Bounds.X + native.Bounds.Width ||
            centerY < native.Bounds.Y || centerY >= native.Bounds.Y + native.Bounds.Height)
            return false;

        var overlapWidth = Math.Max(0, Math.Min(native.Bounds.X + native.Bounds.Width,
            candidate.Bounds.X + candidate.Bounds.Width) - Math.Max(native.Bounds.X, candidate.Bounds.X));
        var overlapHeight = Math.Max(0, Math.Min(native.Bounds.Y + native.Bounds.Height,
            candidate.Bounds.Y + candidate.Bounds.Height) - Math.Max(native.Bounds.Y, candidate.Bounds.Y));
        var intersection = (long)overlapWidth * overlapHeight;
        var candidateArea = Math.Max(1L, (long)candidate.Bounds.Width * candidate.Bounds.Height);
        return intersection / (double)candidateArea >= .55;
    }

    private static bool IsVisualCandidate(AutomationObservation control) =>
        control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
        control.Bounds.Width > 0 && control.Bounds.Height > 0;

    private static bool IsStructuralCandidate(AutomationObservation control) =>
        IsVisualCandidate(control) && NormalizeType(control.ControlType) is
            "Table" or "DataGrid" or "Header" or "HeaderItem" or "DataItem" or
            "List" or "ListItem" or "Tree" or "TreeItem" or "Tab" or "TabItem";

    private static int Priority(AutomationObservation control) => NormalizeType(control.ControlType) switch
    {
        "Button" or "SplitButton" or "Edit" or "ComboBox" or "Header" or "HeaderItem" or "TabItem" => 0,
        "DataItem" => 2,
        _ => 1
    };

    private static bool IsContainer(string controlType) => NormalizeType(controlType) is
        "Window" or "Pane" or "Group" or "Custom" or "List" or "Table" or "DataGrid" or
        "Tree" or "Tab" or "ToolBar";

    private static bool IsConfirmingLeaf(AutomationObservation control)
    {
        var type = NormalizeType(control.ControlType);
        var buttonClass = control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);
        if (IsContainer(type) && !buttonClass || type is "Text" or "Image" or "Separator" or "Document")
            return false;
        return type is "Button" or "SplitButton" or "MenuItem" or "Hyperlink" or "CheckBox" or
                   "RadioButton" or "ComboBox" or "Edit" or "ListItem" or "TreeItem" or
                   "TabItem" or "Header" or "HeaderItem" or "DataItem" or "ScrollBar" or
                   "Slider" or "Spinner" || buttonClass ||
               (control.SupportedPatterns ?? []).Any(pattern =>
                   pattern.Contains("Invoke", StringComparison.OrdinalIgnoreCase) ||
                   pattern.Contains("SelectionItem", StringComparison.OrdinalIgnoreCase) ||
                   pattern.Contains("Value", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
}

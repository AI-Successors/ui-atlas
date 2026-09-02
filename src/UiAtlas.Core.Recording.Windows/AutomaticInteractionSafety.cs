using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public static class AutomaticInteractionSafety
{
    public static bool CanActivate(
        AutomationObservation control,
        IReadOnlyList<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controls);
        return !IsTreeControlOrDescendant(control, controls);
    }

    public static bool IsTreeControlOrDescendant(
        AutomationObservation control,
        IReadOnlyList<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controls);

        if (IsTreeType(control.ControlType)) return true;

        var byRuntimeId = controls
            .Where(item => !string.IsNullOrWhiteSpace(item.RuntimeId))
            .GroupBy(item => item.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var parentId = control.ParentRuntimeId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId) &&
               byRuntimeId.TryGetValue(parentId, out var parent))
        {
            if (IsTreeType(parent.ControlType)) return true;
            parentId = parent.ParentRuntimeId;
        }

        // Some UIA providers expose a disclosure glyph as an orphan Button even
        // though its hit target is visibly inside the Tree. The geometry fallback
        // keeps that button observable while preventing automatic activation.
        var centerX = control.Bounds.X + control.Bounds.Width / 2;
        var centerY = control.Bounds.Y + control.Bounds.Height / 2;
        return controls.Any(item =>
            NormalizeControlType(item.ControlType).Equals("Tree", StringComparison.OrdinalIgnoreCase) &&
            centerX >= item.Bounds.X && centerX < item.Bounds.X + item.Bounds.Width &&
            centerY >= item.Bounds.Y && centerY < item.Bounds.Y + item.Bounds.Height);
    }

    private static bool IsTreeType(string value)
    {
        var type = NormalizeControlType(value);
        return type.Equals("Tree", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("TreeItem", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }
}

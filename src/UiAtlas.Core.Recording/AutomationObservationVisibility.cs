using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

public static class AutomationObservationVisibility
{
    public static IReadOnlyList<AutomationObservation> FilterEffectivelyVisible(
        IEnumerable<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var values = controls.ToArray();
        var byIdentity = values
            .Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .GroupBy(control => (control.WindowHwnd, control.RuntimeId))
            .ToDictionary(group => group.Key, group => group.First());

        return values.Where(control => IsEffectivelyVisible(control, byIdentity)).ToArray();
    }

    private static bool IsEffectivelyVisible(
        AutomationObservation control,
        IReadOnlyDictionary<(long WindowHwnd, string RuntimeId), AutomationObservation> byIdentity)
    {
        var current = control;
        var visited = new HashSet<(long WindowHwnd, string RuntimeId)>();
        while (true)
        {
            if (current.IsOffscreen || current.Bounds.Width <= 0 || current.Bounds.Height <= 0)
                return false;
            if (string.IsNullOrWhiteSpace(current.ParentRuntimeId))
                return true;

            var parentKey = (current.WindowHwnd, current.ParentRuntimeId);
            if (!visited.Add(parentKey))
                return false;
            if (!byIdentity.TryGetValue(parentKey, out var parent))
                return true;
            current = parent;
        }
    }
}

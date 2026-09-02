using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed record OutlookNavigationCandidate(
    string StableKey,
    string DisplayName,
    bool IsSelected,
    bool OpensPopup,
    AutomationObservation Observation);

[SupportedOSPlatform("windows")]
public static class OutlookNavigationDiscovery
{
    public const string ModuleLayerPrefix = "__outlook_module__:";
    private const string TabLayerSeparator = "::tab::";

    private static readonly HashSet<string> EligibleControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button",
        "ListItem",
        "MenuItem",
        "RadioButton",
        "TabItem"
    };

    public static bool IsSupported(WindowObservation window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.ClassName.Equals("rctrl_renwnd32", StringComparison.OrdinalIgnoreCase) ||
               window.Title.Contains("Microsoft Outlook", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<OutlookNavigationCandidate> Discover(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!IsSupported(frame.Window)) return [];

        var root = frame.Window.Bounds;
        var leftLimit = root.X + Math.Min(560, Math.Max(260, (int)Math.Round(root.Width * 0.38)));
        var topLimit = root.Y + Math.Max(180, (int)Math.Round(root.Height * 0.68));
        var controls = frame.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Where(control => control.Bounds.Width is >= 18 and <= 110 &&
                              control.Bounds.Height is >= 18 and <= 72)
            .Where(control => control.Bounds.X >= root.X - 4 &&
                              control.Bounds.X + control.Bounds.Width <= leftLimit &&
                              control.Bounds.Y >= topLimit &&
                              control.Bounds.Y + control.Bounds.Height <= root.Y + root.Height + 4)
            .Where(control => EligibleControlTypes.Contains(NormalizeControlType(control.ControlType)))
            .Where(control => AutomaticInteractionSafety.CanActivate(control, frame.Automation))
            .OrderBy(control => CenterY(control.Bounds))
            .ThenBy(control => control.Bounds.X)
            .ToArray();
        if (controls.Length < 4) return [];

        var rows = new List<List<AutomationObservation>>();
        foreach (var control in controls)
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(CenterY(control.Bounds) - row.Average(item => CenterY(item.Bounds))) <= 18)
                row.Add(control);
            else
                rows.Add([control]);
        }

        var navigationRow = rows
            .Where(row => row.Count is >= 4 and <= 9)
            .Where(IsCompactHorizontalRow)
            .Select(row => new
            {
                Controls = row.OrderBy(control => control.Bounds.X).ToArray(),
                AnchorCount = row.Count(IsKnownModule),
                Bottom = row.Max(control => control.Bounds.Y + control.Bounds.Height)
            })
            // Two readable module anchors keep unrelated status-bar controls out,
            // while the rest of the row may contain icon-only controls such as … .
            .Where(row => row.AnchorCount >= 2)
            .OrderByDescending(row => row.AnchorCount)
            .ThenByDescending(row => row.Bottom)
            .FirstOrDefault();
        if (navigationRow is null) return [];

        return navigationRow.Controls
            .Select((control, index) => new OutlookNavigationCandidate(
                StableKey(control, root),
                DisplayName(control, index == navigationRow.Controls.Length - 1),
                control.IsSelected || control.HasKeyboardFocus ||
                control.ToggleState?.Equals("On", StringComparison.OrdinalIgnoreCase) == true,
                IsMoreControl(control, index == navigationRow.Controls.Length - 1),
                control))
            .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Observation.Bounds.X)
            .ThenBy(candidate => candidate.Observation.Bounds.Y)
            .ToArray();
    }

    public static OutlookNavigationCandidate? ResolveActive(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var candidates = Discover(frame);
        if (candidates.Count == 0) return null;

        var titleKind = ModuleKind(frame.Window.Title);
        return titleKind is not null
            ? candidates.FirstOrDefault(candidate => ModuleKind(
                $"{candidate.DisplayName} {candidate.Observation.AutomationId}") == titleKind)
              ?? candidates.FirstOrDefault(candidate => candidate.IsSelected)
            : candidates.FirstOrDefault(candidate => candidate.IsSelected);
    }

    public static string ModuleLayerKey(OutlookNavigationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return ModuleLayerPrefix + candidate.StableKey;
    }

    public static string CombineWithTab(string moduleLayerKey, string tabLayerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleLayerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabLayerKey);
        return moduleLayerKey + TabLayerSeparator + tabLayerKey;
    }

    public static bool TryGetModuleLayerKey(string? layerKey, out string moduleLayerKey)
    {
        moduleLayerKey = string.Empty;
        if (string.IsNullOrWhiteSpace(layerKey) ||
            !layerKey.StartsWith(ModuleLayerPrefix, StringComparison.Ordinal))
            return false;
        var separator = layerKey.IndexOf(TabLayerSeparator, StringComparison.Ordinal);
        moduleLayerKey = separator < 0 ? layerKey : layerKey[..separator];
        return true;
    }

    public static string? ExtractTabLayerKey(string? layerKey)
    {
        if (string.IsNullOrWhiteSpace(layerKey)) return null;
        var separator = layerKey.IndexOf(TabLayerSeparator, StringComparison.Ordinal);
        return separator < 0 ? layerKey : layerKey[(separator + TabLayerSeparator.Length)..];
    }

    private static bool IsCompactHorizontalRow(IReadOnlyList<AutomationObservation> row)
    {
        var ordered = row.OrderBy(control => control.Bounds.X).ToArray();
        var span = ordered[^1].Bounds.X + ordered[^1].Bounds.Width - ordered[0].Bounds.X;
        if (span > 520) return false;
        for (var index = 1; index < ordered.Length; index++)
        {
            var gap = ordered[index].Bounds.X -
                      (ordered[index - 1].Bounds.X + ordered[index - 1].Bounds.Width);
            if (gap > 72) return false;
        }
        return true;
    }

    private static bool IsKnownModule(AutomationObservation control)
    {
        var identity = $" {control.Name} {control.AutomationId} {control.ClassName} ";
        string[] names =
        [
            "mail", "calendar", "people", "contacts", "tasks", "notes", "folders",
            "почта", "календар", "люди", "контакт", "задач", "заметк", "папк"
        ];
        return names.Any(name => identity.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ModuleKind(string value)
    {
        if (value.Contains("calendar", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("календар", StringComparison.OrdinalIgnoreCase)) return "calendar";
        if (value.Contains("contacts", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("people", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("контакт", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("люди", StringComparison.OrdinalIgnoreCase)) return "people";
        if (value.Contains("tasks", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("задач", StringComparison.OrdinalIgnoreCase)) return "tasks";
        if (value.Contains("notes", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("заметк", StringComparison.OrdinalIgnoreCase)) return "notes";
        if (value.Contains("folders", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("папк", StringComparison.OrdinalIgnoreCase)) return "folders";
        if (value.Contains("mail", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("inbox", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("почта", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("входящ", StringComparison.OrdinalIgnoreCase)) return "mail";
        return null;
    }

    private static bool IsMoreControl(AutomationObservation control, bool isRightmost)
    {
        var identity = $" {control.Name} {control.AutomationId} {control.ClassName} ";
        return identity.Contains("more", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("navigation options", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("more apps", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("другие", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("параметры навигации", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("...", StringComparison.Ordinal) ||
               identity.Contains("…", StringComparison.Ordinal) ||
               isRightmost && !IsKnownModule(control);
    }

    private static string DisplayName(AutomationObservation control, bool isRightmost)
    {
        if (!string.IsNullOrWhiteSpace(control.Name)) return control.Name.Trim();
        if (!string.IsNullOrWhiteSpace(control.AutomationId)) return control.AutomationId.Trim();
        return isRightmost ? "More" : "Outlook navigation";
    }

    private static string StableKey(AutomationObservation control, RectI root)
    {
        var identity = string.Join('|',
            control.AutomationId.Trim(),
            control.Name.Trim(),
            NormalizeControlType(control.ControlType),
            control.ClassName.Trim());
        if (identity.Replace("|", string.Empty, StringComparison.Ordinal).Length == 0)
            identity = $"position:{control.Bounds.X - root.X}:{control.Bounds.Width}";
        return identity.ToLowerInvariant();
    }

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static double CenterY(RectI bounds) => bounds.Y + bounds.Height / 2.0;
}

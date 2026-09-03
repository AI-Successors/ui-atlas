using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed record AutoTabCandidate(
    string StableKey,
    string DisplayName,
    bool IsSelected,
    bool IsBackstage,
    AutomationObservation Observation);

[SupportedOSPlatform("windows")]
public sealed record AutoBackstageCandidate(
    string StableKey,
    string DisplayName,
    bool IsSelected,
    AutomationObservation Observation);

[SupportedOSPlatform("windows")]
public static class AutoTabDiscovery
{
    private static readonly HashSet<string> EligibleControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TabItem",
        "MenuItem",
        "Button",
        "Pane"
    };

    public static IReadOnlyList<AutoTabCandidate> Discover(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rootBounds = ResolveRootBounds(frame);
        var topBandLimit = rootBounds.Y + Math.Max(96, (int)Math.Round(rootBounds.Height * 0.24));
        var topBandControls = frame.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => control.Bounds.Y < topBandLimit)
            .Where(control => control.Bounds.X >= rootBounds.X - 4 && control.Bounds.X < rootBounds.X + rootBounds.Width)
            .ToArray();
        var preferredBand = ResolvePreferredNavigationBand(topBandControls);
        return topBandControls
            .Where(control => AutomaticInteractionSafety.CanActivate(control, frame.Automation))
            .Where(control => !IsLegacyNavigationButton(control) || IsSafeLegacyNavigationLabel(DisplayName(control)))
            .Where(control => IsEligibleNavigationControl(control, preferredBand, rootBounds))
            .Select(control => new AutoTabCandidate(
                StableKey(control),
                DisplayName(control),
                control.IsSelected || control.HasKeyboardFocus,
                IsBackstage(control),
                control))
            .Where(candidate => candidate.DisplayName.Length > 0)
            .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.IsSelected)
                .ThenBy(candidate => candidate.Observation.Bounds.X)
                .ThenBy(candidate => candidate.Observation.Bounds.Y)
                .First())
            .OrderBy(candidate => candidate.IsBackstage ? 1 : 0)
            .ThenBy(candidate => candidate.Observation.Bounds.X)
            .ThenBy(candidate => candidate.Observation.Bounds.Y)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<AutoBackstageCandidate> DiscoverBackstageNavigation(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rootBounds = frame.Window.Bounds;
        var navigationRight = rootBounds.X + Math.Min(480, (int)Math.Round(rootBounds.Width * 0.34));
        return frame.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => control.Bounds.X >= rootBounds.X - 4 &&
                              control.Bounds.X + control.Bounds.Width <= navigationRight)
            .Where(control => control.Bounds.Y >= rootBounds.Y + 36 &&
                              control.Bounds.Y + control.Bounds.Height <= rootBounds.Y + rootBounds.Height)
            .Where(control => AutomaticInteractionSafety.CanActivate(control, frame.Automation))
            .Where(IsSafeBackstageNavigationControl)
            .Select(control => new AutoBackstageCandidate(
                StableKey(control),
                DisplayName(control),
                control.IsSelected || control.HasKeyboardFocus,
                control))
            .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.IsSelected)
                .ThenBy(candidate => candidate.Observation.Bounds.Y)
                .First())
            .OrderBy(candidate => candidate.Observation.Bounds.Y)
            .ThenBy(candidate => candidate.Observation.Bounds.X)
            .Take(12)
            .ToArray();
    }

    internal static bool IsBackstageSectionSelected(
        IReadOnlyList<AutomationObservation> controls,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        return controls.Any(control =>
            !control.IsOffscreen &&
            (control.IsSelected || control.HasKeyboardFocus) &&
            DisplayName(control).Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            IsSafeBackstageNavigationControl(control));
    }

    private static bool IsSafeBackstageNavigationControl(AutomationObservation control)
    {
        var controlType = NormalizeControlType(control.ControlType);
        if (!controlType.Equals("TabItem", StringComparison.OrdinalIgnoreCase) &&
            !controlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase) &&
            !controlType.Equals("MenuItem", StringComparison.OrdinalIgnoreCase) &&
            !controlType.Equals("Button", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = DisplayName(control);
        var safeName = name.Equals("Info", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("New", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Recent", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("History", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Account", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Feedback", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("Help", StringComparison.OrdinalIgnoreCase);
        if (!safeName) return false;

        var identity = $"{control.AutomationId} {control.ClassName}";
        return controlType.Equals("TabItem", StringComparison.OrdinalIgnoreCase) ||
               controlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase) ||
               controlType.Equals("MenuItem", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("backstage", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("navigation", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("nav", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEligibleNavigationControl(
        AutomationObservation control,
        NavigationBand? preferredBand,
        RectI rootBounds)
    {
        var controlType = NormalizeControlType(control.ControlType);
        if (!EligibleControlTypes.Contains(controlType))
            return false;
        if (controlType.Equals("Pane", StringComparison.OrdinalIgnoreCase) &&
            !IsLegacyNavigationButton(control))
            return false;

        if (preferredBand is not null && !preferredBand.Contains(control.Bounds))
            return false;

        if (preferredBand?.IsButtonTabRow == true &&
            (IsRibbonTabButton(control) || IsRibbonTabOverflow(control) || IsLegacyNavigationButton(control)))
            return true;

        if (string.Equals(controlType, "TabItem", StringComparison.OrdinalIgnoreCase))
            return true;

        // Traditional desktop applications such as Premiere expose their main
        // navigation as a Win32 MenuBar instead of TabItems. Opening these
        // top-level MenuItems is non-destructive and reveals the commands below.
        if (IsApplicationMenuControl(control, rootBounds))
            return true;

        if (IsBackstage(control) || LooksLikeNavigationControl(control))
            return true;

        // Do not promote arbitrary readable Ribbon buttons (Copy, Paste, formatting
        // commands) to tabs. They can open popups and would trigger an expensive
        // full-root capture instead of the popup-only delta path.
        return false;
    }

    public static bool IsApplicationMenu(AutoTabCandidate candidate, RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IsApplicationMenuControl(candidate.Observation, rootBounds);
    }

    public static bool IsLegacyNavigationButton(AutoTabCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IsLegacyNavigationButton(candidate.Observation);
    }

    private static bool IsApplicationMenuControl(AutomationObservation control, RectI rootBounds)
    {
        if (!string.Equals(NormalizeControlType(control.ControlType), "MenuItem", StringComparison.OrdinalIgnoreCase) ||
            !HasReadableLabel(control) ||
            control.Name.Equals("System", StringComparison.OrdinalIgnoreCase))
            return false;

        var menuBandBottom = rootBounds.Y + Math.Max(84, (int)Math.Round(rootBounds.Height * 0.10));
        return control.Bounds.Y >= rootBounds.Y - 4 &&
               control.Bounds.Y + control.Bounds.Height <= menuBandBottom &&
               control.Bounds.Width is >= 20 and <= 240 &&
               control.Bounds.Height is >= 16 and <= 44;
    }

    private static string DisplayName(AutomationObservation control)
    {
        if (!string.IsNullOrWhiteSpace(control.Name))
            return control.Name.Trim();
        if (!string.IsNullOrWhiteSpace(control.AutomationId))
            return control.AutomationId.Trim();
        return NormalizeControlType(control.ControlType);
    }

    private static string StableKey(AutomationObservation control)
    {
        var identity = string.Join('|',
            control.AutomationId,
            control.Name,
            NormalizeControlType(control.ControlType),
            control.ClassName,
            control.WindowHwnd.ToString());
        if (identity.Replace("|", string.Empty, StringComparison.Ordinal).Length == 0)
            identity = $"{control.Bounds.X}|{control.Bounds.Y}|{control.Bounds.Width}|{control.Bounds.Height}";
        return identity.Trim().ToLowerInvariant();
    }

    private static bool IsBackstage(AutomationObservation control)
    {
        var name = control.Name?.Trim() ?? string.Empty;
        var automationId = control.AutomationId?.Trim() ?? string.Empty;
        var className = control.ClassName?.Trim() ?? string.Empty;
        if (automationId.Contains("backstage", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("backstage", StringComparison.OrdinalIgnoreCase))
            return true;
        if (automationId.Contains("filetab", StringComparison.OrdinalIgnoreCase))
            return true;
        if (automationId.Equals("ID_ApplicationMenuButton", StringComparison.OrdinalIgnoreCase))
            return true;
        if ((string.Equals(name, "File", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, "File Tab", StringComparison.OrdinalIgnoreCase)) &&
            className.Contains("tab", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static NavigationBand? ResolvePreferredNavigationBand(IReadOnlyList<AutomationObservation> controls)
    {
        var navigationControls = controls
            .Where(control => EligibleControlTypes.Contains(NormalizeControlType(control.ControlType)))
            .Where(control => HasReadableLabel(control) || IsBackstage(control))
            .OrderBy(control => CenterY(control.Bounds))
            .ThenBy(control => control.Bounds.X)
            .ToArray();
        if (navigationControls.Length == 0)
            return null;

        var rows = new List<List<AutomationObservation>>();
        foreach (var control in navigationControls)
        {
            var centerY = CenterY(control.Bounds);
            var existing = rows.LastOrDefault();
            if (existing is not null && Math.Abs(centerY - existing.Average(item => CenterY(item.Bounds))) <= 24)
                existing.Add(control);
            else
                rows.Add([control]);
        }

        return rows
            .Select(row => NavigationBand.Create(row))
            .Where(row => row.Score >= 4)
            .OrderByDescending(row => row.Score)
            .ThenByDescending(row => row.Count)
            .ThenBy(row => row.Top)
            .FirstOrDefault();
    }

    private static bool LooksLikeNavigationControl(AutomationObservation control)
    {
        var value = $"{control.Name} {control.AutomationId} {control.ClassName}";
        return value.Contains("tab", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("nav", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("navigation", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("pivot", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ribbontab", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("scroll", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("overflow", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("chevron", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("more tabs", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("next tab", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("previous tab", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static bool HasReadableLabel(AutomationObservation control) =>
        !string.IsNullOrWhiteSpace(control.Name) || !string.IsNullOrWhiteSpace(control.AutomationId);

    private static bool IsReasonableNavigationSize(RectI bounds) =>
        bounds.Width is >= 24 and <= 240 &&
        bounds.Height is >= 20 and <= 64;

    private static double CenterY(RectI bounds) => bounds.Y + bounds.Height / 2.0;

    private static bool IsRibbonTabButton(AutomationObservation control)
    {
        if (!NormalizeControlType(control.ControlType).Equals("Button", StringComparison.OrdinalIgnoreCase) ||
            control.Bounds.Width < 44 || control.Bounds.Height is < 18 or > 44 ||
            string.IsNullOrWhiteSpace(control.Name) || string.IsNullOrWhiteSpace(control.AutomationId))
            return false;

        var automationId = control.AutomationId.Trim();
        return !automationId.StartsWith("ID_", StringComparison.OrdinalIgnoreCase) &&
               !automationId.Contains("Flyout", StringComparison.OrdinalIgnoreCase) &&
               !automationId.Contains("RibbonItem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRibbonTabOverflow(AutomationObservation control) =>
        control.AutomationId.Equals("ID_OverflowButton", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyNavigationButton(AutomationObservation control)
    {
        var controlType = NormalizeControlType(control.ControlType);
        return controlType.Equals("Pane", StringComparison.OrdinalIgnoreCase) &&
               control.ClassName.Contains("Button", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(control.Name) &&
               IsReasonableNavigationSize(control.Bounds);
    }

    private static bool IsSafeLegacyNavigationLabel(string label)
    {
        var unsafeWords = new[]
        {
            "add", "buy", "cancel", "clear", "close", "create", "delete", "email",
            "end of day", "exit", "log off", "log out", "new order", "pay", "payment",
            "post", "remove", "save", "send", "submit"
        };
        return !unsafeWords.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static RectI ResolveRootBounds(FrameObservation frame)
    {
        if (frame.Window.Bounds.Width > 0 && frame.Window.Bounds.Height > 0)
            return frame.Window.Bounds;

        return frame.ScopedWindows?
                   .Where(window => window.Bounds.Width > 0 && window.Bounds.Height > 0)
                   .OrderByDescending(window => (long)window.Bounds.Width * window.Bounds.Height)
                   .Select(window => window.Bounds)
                   .FirstOrDefault() ?? frame.Window.Bounds;
    }

    private sealed record NavigationBand(
        int Top,
        int Bottom,
        bool HasExplicitTabs,
        bool IsButtonTabRow,
        double Score,
        int Count)
    {
        public bool Contains(RectI bounds)
        {
            var centerY = CenterY(bounds);
            return centerY >= Top - 10 && centerY <= Bottom + 10;
        }

        public static NavigationBand Create(IReadOnlyList<AutomationObservation> row)
        {
            var top = row.Min(control => control.Bounds.Y);
            var bottom = row.Max(control => control.Bounds.Y + control.Bounds.Height);
            var hasExplicitTabs = row.Any(control => string.Equals(NormalizeControlType(control.ControlType), "TabItem", StringComparison.OrdinalIgnoreCase));
            var buttonTabCount = row.Count(control => IsRibbonTabButton(control) || IsLegacyNavigationButton(control));
            var isButtonTabRow = buttonTabCount >= 4 && buttonTabCount >= row.Count * 0.60;
            var score = row.Sum(control =>
            {
                var controlType = NormalizeControlType(control.ControlType);
                var value = 0.0;
                if (string.Equals(controlType, "TabItem", StringComparison.OrdinalIgnoreCase))
                    value += 6;
                if (IsBackstage(control))
                    value += 4;
                if (LooksLikeNavigationControl(control))
                    value += 3;
                if (HasReadableLabel(control))
                    value += 1.5;
                if (IsReasonableNavigationSize(control.Bounds))
                    value += 1;
                if (control.Bounds.Width > 260)
                    value -= 1.5;
                if (control.Bounds.Height > 72)
                    value -= 1;
                return value;
            });
            if (isButtonTabRow) score += 100;
            return new(top, bottom, hasExplicitTabs, isButtonTabRow, score, row.Count);
        }
    }
}

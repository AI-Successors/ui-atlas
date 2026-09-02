using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed record AutoRibbonCommandCandidate(
    string StableKey,
    string DisplayName,
    AutomationObservation Observation);

[SupportedOSPlatform("windows")]
public static class AutoRibbonCommandDiscovery
{
    public static IReadOnlyList<AutoRibbonCommandCandidate> Discover(FrameObservation frame, AutoTabCandidate activeTab)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(activeTab);

        if (!LooksLikeRibbonSurface(frame, activeTab))
            return [];

        var automationByRuntimeId = frame.Automation
            .Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var childrenByParentRuntimeId = frame.Automation
            .Where(control => !string.IsNullOrWhiteSpace(control.ParentRuntimeId))
            .GroupBy(control => control.ParentRuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AutomationObservation>)group.ToArray(), StringComparer.Ordinal);
        var rootBounds = frame.Window.Bounds;
        var tabRowBottom = ResolveTabRowBottom(frame, activeTab);
        var commandBandTop = tabRowBottom + 2;
        var commandBandHeight = Math.Clamp((int)Math.Round(rootBounds.Height * 0.13), 84, 112);
        var commandBandBottom = Math.Min(rootBounds.Y + rootBounds.Height, commandBandTop + commandBandHeight);
        var isRevitRibbon = IsRevitRibbon(frame, activeTab);

        return frame.Automation
            .Where(control => AutomaticInteractionSafety.CanActivate(control, frame.Automation))
            .Where(control => IsEligibleCommand(
                control,
                activeTab,
                rootBounds,
                commandBandTop,
                commandBandBottom,
                automationByRuntimeId,
                childrenByParentRuntimeId,
                isRevitRibbon))
            .Select(control => new AutoRibbonCommandCandidate(
                StableKey(control),
                DisplayName(control, automationByRuntimeId, childrenByParentRuntimeId),
                control))
            .Where(candidate => candidate.DisplayName.Length > 0)
            .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(candidate => candidate.Observation.Bounds.X)
                .ThenBy(candidate => candidate.Observation.Bounds.Y)
                .First())
            .OrderBy(candidate => candidate.Observation.Bounds.X)
            .ThenBy(candidate => candidate.Observation.Bounds.Y)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEligibleCommand(
        AutomationObservation control,
        AutoTabCandidate activeTab,
        RectI rootBounds,
        int commandBandTop,
        int commandBandBottom,
        IReadOnlyDictionary<string, AutomationObservation> automationByRuntimeId,
        IReadOnlyDictionary<string, IReadOnlyList<AutomationObservation>> childrenByParentRuntimeId,
        bool isRevitRibbon)
    {
        if (!control.IsEnabled || control.IsOffscreen)
            return false;

        if (IsForbiddenAutomaticAction(control))
            return false;

        if (control.Bounds.Width is <= 0 or > 220 || control.Bounds.Height is <= 0 or > 96)
            return false;

        if (control.Bounds.X < rootBounds.X - 4 || control.Bounds.X > rootBounds.X + rootBounds.Width)
            return false;

        // Comments, Share and the account/avatar menu live above the Ribbon command
        // band. They are safe, user-facing surfaces that should be visited once on
        // Home. Window-management buttons are intentionally not included here: they
        // are recorded as graph controls but auto-pass must never minimize or close
        // the target application.
        if (IsPrimaryRibbonTab(activeTab) && IsSafeTopChromeCommand(control, rootBounds))
            return true;

        var centerY = CenterY(control.Bounds);
        if (centerY < commandBandTop - 6 || centerY > commandBandBottom + 6)
            return false;

        if (string.Equals(StableKey(control), activeTab.StableKey, StringComparison.Ordinal))
            return false;

        if (LooksLikeNavigation(control) || IsBackstage(control) || IsObviousChromeNoise(control))
            return false;

        if (isRevitRibbon)
            return IsRevitFlyoutButton(control);

        return LooksLikeChevronControl(control, automationByRuntimeId, childrenByParentRuntimeId);
    }

    private static bool IsRevitRibbon(FrameObservation frame, AutoTabCandidate activeTab) =>
        activeTab.Observation.FrameworkId.Equals("WPF", StringComparison.OrdinalIgnoreCase) &&
        frame.Automation.Any(control =>
            control.AutomationId.Equals("mMainTabPanels", StringComparison.OrdinalIgnoreCase) ||
            control.AutomationId.EndsWith("_PanelTitleBar", StringComparison.OrdinalIgnoreCase) ||
            control.AutomationId.EndsWith("FlyoutButtonShowFlyout", StringComparison.OrdinalIgnoreCase));

    private static bool IsRevitFlyoutButton(AutomationObservation control)
    {
        if (!NormalizeControlType(control.ControlType).Equals("Button", StringComparison.OrdinalIgnoreCase))
            return false;
        return control.AutomationId.EndsWith("FlyoutButtonShowFlyout", StringComparison.OrdinalIgnoreCase) ||
               control.AutomationId.Equals("ID_OverflowButton", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsForbiddenAutomaticAction(AutomationObservation control)
    {
        var words = NormalizeWords($"{control.Name} {control.AutomationId}");
        return words.Contains(" sign out ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" signout ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" log out ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" logout ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" remove account ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" delete account ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" add an account ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" add a new account ", StringComparison.OrdinalIgnoreCase) ||
               words.Contains(" switch to ", StringComparison.OrdinalIgnoreCase) &&
               words.Contains(" account ", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveTabRowBottom(FrameObservation frame, AutoTabCandidate activeTab)
    {
        var activeTabCenterY = CenterY(activeTab.Observation.Bounds);
        return frame.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => Math.Abs(CenterY(control.Bounds) - activeTabCenterY) <= 24)
            .Select(control => control.Bounds.Y + control.Bounds.Height)
            .DefaultIfEmpty(activeTab.Observation.Bounds.Y + activeTab.Observation.Bounds.Height)
            .Max();
    }

    private static bool LooksLikeRibbonSurface(FrameObservation frame, AutoTabCandidate activeTab)
    {
        if (LooksLikeRibbonChrome(activeTab.Observation))
            return true;

        var activeTabBottom = activeTab.Observation.Bounds.Y + activeTab.Observation.Bounds.Height;
        return frame.Automation.Any(control =>
            control.Bounds.Width > 0 &&
            control.Bounds.Height > 0 &&
            control.Bounds.Y >= activeTabBottom - 4 &&
            control.Bounds.Y <= activeTabBottom + 140 &&
            LooksLikeRibbonChrome(control));
    }

    private static bool LooksLikeRibbonChrome(AutomationObservation control)
    {
        var value = $"{control.AutomationId} {control.Name} {control.ClassName}";
        return value.Contains("ribbon", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("gallery", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("split", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNavigation(AutomationObservation control)
    {
        var controlType = NormalizeControlType(control.ControlType);
        if (string.Equals(controlType, "TabItem", StringComparison.OrdinalIgnoreCase))
            return true;

        var joined = $"{control.Name} {control.AutomationId} {control.ClassName}";
        var normalized = NormalizeWords(joined);
        return normalized.Contains(" tab ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" nav ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" navigation ", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("ribbontab", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackstage(AutomationObservation control)
    {
        var name = control.Name?.Trim() ?? string.Empty;
        var automationId = control.AutomationId?.Trim() ?? string.Empty;
        if (automationId.Contains("backstage", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("backstage", StringComparison.OrdinalIgnoreCase))
            return true;
        return automationId.Contains("filetab", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObviousChromeNoise(AutomationObservation control)
    {
        var value = $"{control.Name} {control.AutomationId} {control.ClassName}";
        return value.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("tell me", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("autosave", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("quick access", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("formula bar", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("name box", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("comments", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("share", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("display options", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeTopChromeCommand(AutomationObservation control, RectI rootBounds)
    {
        var name = control.Name?.Trim() ?? string.Empty;
        var automationId = control.AutomationId?.Trim() ?? string.Empty;
        var controlType = NormalizeControlType(control.ControlType);
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(controlType, "MenuItem", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!HasPattern(control, "Invoke") && !HasPattern(control, "ExpandCollapse") &&
            !HasPattern(control, "Toggle"))
            return false;

        // Office exposes the small drop-down halves of Quick Access commands as
        // separate MenuItems above the Ribbon tabs. They are safe to expand and
        // were previously lost because top-chrome discovery only admitted items
        // on the right side of the title bar (account, Comments and Share).
        var isQuickAccessChevron =
            automationId.EndsWith("_Dropdown", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Customize Quick Access Toolbar", StringComparison.OrdinalIgnoreCase);
        var titleBarBottom = rootBounds.Y + Math.Clamp(
            (int)Math.Round(rootBounds.Height * 0.08), 64, 96);
        if (isQuickAccessChevron)
        {
            return control.Bounds.Y >= rootBounds.Y - 4 &&
                   control.Bounds.Y + control.Bounds.Height <= titleBarBottom + 4 &&
                   control.Bounds.X < rootBounds.X + rootBounds.Width / 2;
        }

        var isSafeIdentity = automationId.Equals("MeControlWidget", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("Comments", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("Share", StringComparison.OrdinalIgnoreCase);
        if (!isSafeIdentity) return false;

        var chromeBottom = rootBounds.Y + Math.Max(110, (int)Math.Round(rootBounds.Height * 0.12));
        return control.Bounds.Y < chromeBottom &&
               control.Bounds.X + control.Bounds.Width > rootBounds.X + rootBounds.Width / 2;
    }

    private static bool IsPrimaryRibbonTab(AutoTabCandidate activeTab)
    {
        var name = activeTab.DisplayName?.Trim() ?? string.Empty;
        var automationId = activeTab.Observation.AutomationId?.Trim() ?? string.Empty;
        return name.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
               automationId.Equals("TabHome", StringComparison.OrdinalIgnoreCase) ||
               automationId.EndsWith("Home", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeChevronControl(
        AutomationObservation control,
        IReadOnlyDictionary<string, AutomationObservation> automationByRuntimeId,
        IReadOnlyDictionary<string, IReadOnlyList<AutomationObservation>> childrenByParentRuntimeId)
    {
        var automationId = control.AutomationId?.Trim() ?? string.Empty;
        var name = control.Name?.Trim() ?? string.Empty;
        var hasExpandPattern = HasPattern(control, "ExpandCollapse");
        var hasInvokePattern = HasPattern(control, "Invoke");

        if ((automationId.Contains("dropdown", StringComparison.OrdinalIgnoreCase) ||
             automationId.Contains("drop_down", StringComparison.OrdinalIgnoreCase) ||
             automationId.Contains("chevron", StringComparison.OrdinalIgnoreCase)) &&
            !HasDirectChevronChild(control, childrenByParentRuntimeId))
            return true;

        if (string.Equals(name, "More Options", StringComparison.OrdinalIgnoreCase) && hasExpandPattern)
            return true;

        // Excel exposes compact commands such as Fill, Clear and Orientation as
        // a single NetUIAnchor MenuItem. They have a painted chevron and an
        // ExpandCollapse pattern, but no dedicated chevron child and are shorter
        // than the large Sort & Filter / Find & Select anchors.
        if (IsCompactChevronAnchor(control))
            return true;

        if (IsDirectChevronLeaf(control, automationByRuntimeId))
            return true;

        if (LooksLikeChevronHost(control, childrenByParentRuntimeId) && (hasExpandPattern || hasInvokePattern))
            return true;

        return false;
    }

    private static string DisplayName(
        AutomationObservation control,
        IReadOnlyDictionary<string, AutomationObservation> automationByRuntimeId,
        IReadOnlyDictionary<string, IReadOnlyList<AutomationObservation>> childrenByParentRuntimeId)
    {
        var name = control.Name?.Trim() ?? string.Empty;
        var automationId = control.AutomationId?.Trim() ?? string.Empty;
        var controlType = NormalizeControlType(control.ControlType);

        if (automationId.Equals("MeControlWidget", StringComparison.OrdinalIgnoreCase))
            return name.Length > 0 ? name : "Account";
        if (name.Equals("Comments", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Share", StringComparison.OrdinalIgnoreCase))
            return name;

        if (LooksLikeChevronHost(control, childrenByParentRuntimeId) || IsCompactChevronAnchor(control))
        {
            var hostLabel = !string.IsNullOrWhiteSpace(name)
                ? name
                : automationId;
            if (hostLabel.Length > 0)
                return hostLabel + " chevron";
        }

        if (string.Equals(name, "More Options", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Open", StringComparison.OrdinalIgnoreCase) ||
            name.Length == 0 ||
            string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase))
        {
            var parentLabel = ResolveParentLabel(control, automationByRuntimeId);
            if (parentLabel.Length > 0)
                return parentLabel + " chevron";
        }

        if (!string.IsNullOrWhiteSpace(automationId))
            return automationId;
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        if (!string.IsNullOrWhiteSpace(control.ClassName))
            return control.ClassName.Trim();
        return NormalizeControlType(control.ControlType);
    }

    private static string ResolveParentLabel(
        AutomationObservation control,
        IReadOnlyDictionary<string, AutomationObservation> automationByRuntimeId)
    {
        var current = control;
        for (var depth = 0; depth < 3; depth++)
        {
            if (string.IsNullOrWhiteSpace(current.ParentRuntimeId) ||
                !automationByRuntimeId.TryGetValue(current.ParentRuntimeId, out var parent))
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(parent.Name))
                return parent.Name.Trim();
            if (!string.IsNullOrWhiteSpace(parent.AutomationId))
                return parent.AutomationId.Trim();

            current = parent;
        }

        return string.Empty;
    }

    private static bool LooksLikeChevronHost(
        AutomationObservation control,
        IReadOnlyDictionary<string, IReadOnlyList<AutomationObservation>> childrenByParentRuntimeId)
    {
        var controlType = NormalizeControlType(control.ControlType);
        var hasExpandPattern = HasPattern(control, "ExpandCollapse");
        if (!hasExpandPattern)
            return false;

        if (!HasReadableLabel(control))
            return false;

        if (HasDirectChevronChild(control, childrenByParentRuntimeId))
            return false;

        if (control.Bounds.Width < 28 || control.Bounds.Height < 20)
            return false;

        if (string.Equals(controlType, "ComboBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlType, "SplitButton", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(controlType, "MenuItem", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = $"{control.AutomationId} {control.Name} {control.ClassName}";
        return value.Contains("ribbonbutton", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("gallery", StringComparison.OrdinalIgnoreCase) ||
               (value.Contains("anchor", StringComparison.OrdinalIgnoreCase) && control.Bounds.Height >= 44);
    }

    private static bool IsCompactChevronAnchor(AutomationObservation control) =>
        string.Equals(NormalizeControlType(control.ControlType), "MenuItem", StringComparison.OrdinalIgnoreCase) &&
        control.ClassName.Contains("NetUIAnchor", StringComparison.OrdinalIgnoreCase) &&
        HasPattern(control, "ExpandCollapse");

    private static bool HasDirectChevronChild(
        AutomationObservation control,
        IReadOnlyDictionary<string, IReadOnlyList<AutomationObservation>> childrenByParentRuntimeId)
    {
        if (string.IsNullOrWhiteSpace(control.RuntimeId) ||
            !childrenByParentRuntimeId.TryGetValue(control.RuntimeId, out var children))
            return false;

        return children.Any(child => IsChevronLeafForParent(child, control));
    }

    private static bool IsDirectChevronLeaf(
        AutomationObservation control,
        IReadOnlyDictionary<string, AutomationObservation> automationByRuntimeId)
    {
        if (string.IsNullOrWhiteSpace(control.ParentRuntimeId) ||
            !automationByRuntimeId.TryGetValue(control.ParentRuntimeId, out var parent))
            return false;

        return IsChevronLeafForParent(control, parent);
    }

    private static bool IsChevronLeafForParent(AutomationObservation control, AutomationObservation parent)
    {
        var controlType = NormalizeControlType(control.ControlType);
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(controlType, "MenuItem", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!HasPattern(control, "Invoke") && !HasPattern(control, "ExpandCollapse"))
            return false;

        if (!HasReadableLabel(parent) && string.IsNullOrWhiteSpace(parent.AutomationId))
            return false;

        if (!LooksLikeParentContainer(parent))
            return false;

        var controlValue = $"{control.AutomationId} {control.Name} {control.ClassName}";
        if (controlValue.Contains("dropdown", StringComparison.OrdinalIgnoreCase) ||
            controlValue.Contains("chevron", StringComparison.OrdinalIgnoreCase))
            return true;

        var isSmallLeaf = control.Bounds.Width <= 30 && control.Bounds.Height <= Math.Max(38, parent.Bounds.Height + 6);
        var containedWithinParent =
            control.Bounds.X >= parent.Bounds.X - 4 &&
            control.Bounds.Y >= parent.Bounds.Y - 4 &&
            control.Bounds.X + control.Bounds.Width <= parent.Bounds.X + parent.Bounds.Width + 4 &&
            control.Bounds.Y + control.Bounds.Height <= parent.Bounds.Y + parent.Bounds.Height + 8;
        var nearParentRightEdge =
            control.Bounds.X + control.Bounds.Width >= parent.Bounds.X + parent.Bounds.Width - Math.Max(20, parent.Bounds.Width / 4);
        var startsInRightHalf =
            control.Bounds.X >= parent.Bounds.X + Math.Max(10, parent.Bounds.Width / 2 - 4);
        var belowParentMidline =
            control.Bounds.Y >= parent.Bounds.Y + Math.Max(10, parent.Bounds.Height / 2) - 6;
        return isSmallLeaf &&
               containedWithinParent &&
               ((nearParentRightEdge && startsInRightHalf) || belowParentMidline);
    }

    private static bool LooksLikeParentContainer(AutomationObservation control)
    {
        var controlType = NormalizeControlType(control.ControlType);
        var value = $"{control.AutomationId} {control.Name} {control.ClassName}";
        if (string.Equals(controlType, "ComboBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlType, "SplitButton", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Contains("combo", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("split", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("gallery", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("dropdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReadableLabel(AutomationObservation control) =>
        !string.IsNullOrWhiteSpace(control.Name) || !string.IsNullOrWhiteSpace(control.AutomationId);

    private static bool HasPattern(AutomationObservation control, string patternName) =>
        control.SupportedPatterns?.Any(pattern => pattern.Contains(patternName, StringComparison.OrdinalIgnoreCase)) == true;

    private static string StableKey(AutomationObservation control)
    {
        var identity = string.Join('|',
            control.AutomationId,
            control.Name,
            NormalizeControlType(control.ControlType),
            control.ClassName,
            control.WindowHwnd.ToString(),
            control.Bounds.X,
            control.Bounds.Y,
            control.Bounds.Width,
            control.Bounds.Height);
        if (identity.Replace("|", string.Empty, StringComparison.Ordinal).Length == 0)
            identity = $"{control.Bounds.X}|{control.Bounds.Y}|{control.Bounds.Width}|{control.Bounds.Height}";
        return identity.Trim().ToLowerInvariant();
    }

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static string NormalizeWords(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ').ToArray();
        return " " + new string(chars) + " ";
    }

    private static double CenterY(RectI bounds) => bounds.Y + bounds.Height / 2.0;
}

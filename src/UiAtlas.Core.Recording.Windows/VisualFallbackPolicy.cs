using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal static class VisualFallbackPolicy
{
    public static bool ShouldUseOcrFallback(IReadOnlyList<AutomationObservation> controls) =>
        !HasUsableNativeTree(controls);

    public static IReadOnlyList<RectI> FindOpaqueRegions(
        IReadOnlyList<AutomationObservation> controls,
        RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (rootBounds.Width <= 0 || rootBounds.Height <= 0) return [];
        var rootArea = Math.Max(1L, (long)rootBounds.Width * rootBounds.Height);
        var candidates = controls
            .Where(control => !control.IsOffscreen && control.Bounds.Width >= 80 && control.Bounds.Height >= 40)
            .Where(control =>
            {
                var type = NormalizeType(control.ControlType);
                var className = control.ClassName;
                var isDatabaseGrid = className.Contains("DBGrid", StringComparison.OrdinalIgnoreCase);
                var isPageControl = className.Contains("PageControl", StringComparison.OrdinalIgnoreCase);
                var isLegacyPanel = className.Contains("AbacrePanel", StringComparison.OrdinalIgnoreCase);
                var isOpaqueGallery = IsOpaqueGalleryContainer(control);
                if (!isDatabaseGrid && !isPageControl && !isLegacyPanel && !isOpaqueGallery &&
                    type is not ("Table" or "DataGrid" or "List" or "Tree" or "Tab" or "Pane"))
                    return false;

                var area = (long)control.Bounds.Width * control.Bounds.Height;
                if (!isDatabaseGrid && !isPageControl && !isOpaqueGallery && area / (double)rootArea < .075)
                    return false;
                var descendants = controls.Where(candidate =>
                    !ReferenceEquals(candidate, control) &&
                    !candidate.IsOffscreen &&
                    candidate.Bounds.Width > 0 && candidate.Bounds.Height > 0 &&
                    ContainsCenter(control.Bounds, candidate.Bounds) &&
                    IsMeaningfulContent(candidate)).ToArray();
                if (isPageControl)
                {
                    // A legacy page control commonly exposes only its tab headers.
                    // Those headers prove that the tab strip is usable, not that the
                    // selected page body (for example a customer form) is mapped.
                    var tabHeaderBottom = descendants
                        .Where(candidate => NormalizeType(candidate.ControlType) == "TabItem")
                        .Select(candidate => candidate.Bounds.Y + candidate.Bounds.Height)
                        .DefaultIfEmpty(control.Bounds.Y)
                        .Max();
                    var bodyDescendants = descendants.Count(candidate =>
                        NormalizeType(candidate.ControlType) != "TabItem" &&
                        candidate.Bounds.Y + candidate.Bounds.Height / 2 >= tabHeaderBottom);
                    return bodyDescendants < 2;
                }
                if (isOpaqueGallery)
                    return descendants.Length < 3;
                return isDatabaseGrid
                    ? descendants.Length == 0
                    : descendants.Length < 3;
            })
            .Select(control => Intersect(control.Bounds, rootBounds))
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
            .OrderBy(bounds => (long)bounds.Width * bounds.Height)
            .ToArray();

        var retained = new List<RectI>();
        foreach (var candidate in candidates)
        {
            if (retained.Any(existing => Contains(existing, candidate))) continue;
            retained.RemoveAll(existing => Contains(candidate, existing));
            retained.Add(candidate);
        }
        return retained.OrderBy(bounds => bounds.Y).ThenBy(bounds => bounds.X).ToArray();
    }

    internal static bool IsOpaqueGalleryContainer(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var type = NormalizeType(control.ControlType);
        if (IsTemplateGalleryContainer(control))
            return true;
        var galleryIdentity = control.AutomationId.Contains("Gallery", StringComparison.OrdinalIgnoreCase) ||
                              control.ClassName.Contains("Gallery", StringComparison.OrdinalIgnoreCase) ||
                              control.Name.Contains("Gallery", StringComparison.OrdinalIgnoreCase);
        var expandable = (control.SupportedPatterns ?? []).Any(pattern =>
            pattern.Contains("ExpandCollapse", StringComparison.OrdinalIgnoreCase));
        return galleryIdentity && expandable &&
               type is "MenuItem" or "Custom" or "Group" or "List" &&
               control.Bounds.Width >= 160 && control.Bounds.Height >= 36;
    }

    internal static bool IsTemplateGalleryContainer(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var type = NormalizeType(control.ControlType);
        var templateIdentity = control.Name.Equals("Templates", StringComparison.OrdinalIgnoreCase) ||
                               control.AutomationId.Contains("Template", StringComparison.OrdinalIgnoreCase);
        var officeList = control.ClassName.Contains("NetUIListView", StringComparison.OrdinalIgnoreCase) ||
                         control.FrameworkId.Equals("Win32", StringComparison.OrdinalIgnoreCase);
        return templateIdentity && officeList && type == "List" &&
               control.Bounds.Width >= 320 && control.Bounds.Height >= 80;
    }

    public static bool HasUsableNativeTree(IReadOnlyList<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var titleBars = controls
            .Where(control => NormalizeType(control.ControlType) == "TitleBar")
            .Select(control => control.RuntimeId)
            .Where(runtimeId => !string.IsNullOrWhiteSpace(runtimeId))
            .ToHashSet(StringComparer.Ordinal);

        return controls.Any(control => IsUsableNativeControl(control, titleBars));
    }

    private static bool IsUsableNativeControl(
        AutomationObservation control,
        IReadOnlySet<string> titleBars)
    {
        if (control.IsOffscreen || control.Bounds.Width < 1 || control.Bounds.Height < 1 ||
            control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) ||
            control.FrameworkId.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase) ||
            titleBars.Contains(control.ParentRuntimeId))
            return false;

        var type = NormalizeType(control.ControlType);
        return type is "Button" or "SplitButton" or "MenuItem" or "Hyperlink" or "CheckBox" or
                   "RadioButton" or "ComboBox" or "Edit" or "ListItem" or "TreeItem" or
                   "TabItem" or "Header" or "HeaderItem" or "DataItem" or "Slider" or "Spinner" ||
               control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase) ||
               (control.SupportedPatterns ?? []).Any(pattern =>
                   pattern.Contains("Invoke", StringComparison.OrdinalIgnoreCase) ||
                   pattern.Contains("Value", StringComparison.OrdinalIgnoreCase) ||
                   pattern.Contains("SelectionItem", StringComparison.OrdinalIgnoreCase) ||
                   pattern.Contains("Toggle", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;

    private static bool IsMeaningfulContent(AutomationObservation control)
    {
        if (control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase))
            return false;
        var type = NormalizeType(control.ControlType);
        return type is "Button" or "SplitButton" or "CheckBox" or "RadioButton" or "ComboBox" or "Edit" or
               "DataItem" or "HeaderItem" or "ListItem" or "TreeItem" or "TabItem" or "Slider" or "Spinner" ||
               control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCenter(RectI outer, RectI inner) => Contains(outer,
        new RectI(inner.X + inner.Width / 2, inner.Y + inner.Height / 2, 1, 1));

    private static bool Contains(RectI outer, RectI inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static RectI Intersect(RectI first, RectI second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min((long)first.X + first.Width, (long)second.X + second.Width);
        var bottom = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height);
        return right <= left || bottom <= top
            ? new RectI(0, 0, 0, 0)
            : new RectI(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }
}

using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Cli;

internal enum MapperHighlightKind { Uia, ComputerVision, NativeGrid, GridCandidate }

internal sealed record MapperHighlight(
    string Id, RectI Bounds, MapperHighlightKind Kind, string Name, string Source,
    AutomationObservation Control)
{
    public bool IsGrid => Kind is MapperHighlightKind.NativeGrid or MapperHighlightKind.GridCandidate;
    public string Color => Kind switch
    {
        MapperHighlightKind.Uia => "#0A84FF",
        MapperHighlightKind.ComputerVision => "#8B5CF6",
        MapperHighlightKind.NativeGrid => "#008F83",
        _ => "#C98208"
    };
    public string Label => Kind == MapperHighlightKind.NativeGrid ? "Native grid" : "Grid candidate";
    public string Classification => Kind == MapperHighlightKind.NativeGrid ? "UIA-native data grid" : "Potential data grid";
    public string NativeSupport => Kind == MapperHighlightKind.NativeGrid ? "Available" : "Not detected";
    public string Status => Kind == MapperHighlightKind.NativeGrid ? "Native structure detected" : "Candidate · not yet verified";
}

/// <summary>Presentation of current evidence only. Never qualifies extraction or saves a schema.</summary>
internal static class MapperHighlightModel
{
    internal static bool IsVisual(AutomationObservation control) =>
        control.FrameworkId.StartsWith("UiAtlas.Visual", StringComparison.OrdinalIgnoreCase) ||
        control.RuntimeId.StartsWith("visual:", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<MapperHighlight> Build(RectI surface,
        IReadOnlyList<AutomationObservation> controls)
    {
        var visibleNative = AutomationObservationVisibility.FilterEffectivelyVisible(controls)
            .Select(c => c.RuntimeId).ToHashSet(StringComparer.Ordinal);
        var eligible = controls.Where(c => (IsVisual(c) || visibleNative.Contains(c.RuntimeId)) && c.Bounds.IsValid && Contains(surface, c.Bounds) &&
            (!c.IsOffscreen || IsVisual(c))).ToArray();
        var grids = eligible.Select(ClassifyGrid).OfType<MapperHighlight>()
            .OrderBy(g => g.Kind == MapperHighlightKind.NativeGrid ? 0 :
                g.Source == "Native window hint" ? 1 : 2)
            .ThenByDescending(g => Area(g.Bounds)).ToArray();
        var distinct = new List<MapperHighlight>();
        foreach (var grid in grids)
        {
            if (!distinct.Any(g => Equivalent(g.Bounds, grid.Bounds) ||
                (g.Kind == MapperHighlightKind.NativeGrid || g.Source == "Native window hint") &&
                grid.Source == "Computer vision" && Contains(g.Bounds, grid.Bounds))) distinct.Add(grid);
        }

        var parents = eligible.Where(c => IsUia(c) && !string.IsNullOrWhiteSpace(c.ParentRuntimeId))
            .Select(c => (c.WindowHwnd, c.ParentRuntimeId)).ToHashSet();
        var ordinary = eligible.Where(c => !distinct.Any(g => Contains(g.Bounds, c.Bounds)))
            .Where(c => IsOrdinaryControl(c, parents))
            .Where(c => IsVisual(c) || IsUia(c))
            .Where(c => Area(c.Bounds) < Area(surface) / 3)
            .Select(c => new MapperHighlight(c.RuntimeId, c.Bounds,
                IsVisual(c) ? MapperHighlightKind.ComputerVision : MapperHighlightKind.Uia,
                DisplayName(c), IsVisual(c) ? "Computer vision" : "UI Automation", c))
            .OrderBy(c => c.Kind).ToArray();
        foreach (var control in ordinary)
            if (!distinct.Any(c => Equivalent(c.Bounds, control.Bounds))) distinct.Add(control);
        return distinct.Take(500).ToArray();
    }

    private static bool IsOrdinaryControl(AutomationObservation control,
        HashSet<(long, string)> parents)
    {
        if (Type(control) is "Button" or "Edit" or "ComboBox" or "CheckBox" or "RadioButton" or
            "TabItem" or "MenuItem" or "Slider" or "Hyperlink" or "SplitButton") return true;
        // Legacy providers often expose real buttons as named Pane/Custom leaves (e.g. Delphi).
        // Preserve their UIA evidence without outlining the surrounding layout containers.
        return IsUia(control) && Type(control) is ("Pane" or "Custom" or "Group" or "Text") &&
            !string.IsNullOrWhiteSpace(control.Name) &&
            !parents.Contains((control.WindowHwnd, control.RuntimeId));
    }

    private static MapperHighlight? ClassifyGrid(AutomationObservation c)
    {
        if (c.Bounds.Width < 60 || c.Bounds.Height < 40) return null;
        var type = Type(c);
        if (type is "DataItem" or "HeaderItem" || c.TableRow.HasValue || c.TableColumn.HasValue) return null;
        var native = IsUia(c) && c.SupportedPatterns?.Any(p => p is "Grid" or "Table" or "GridPattern" or "TablePattern" or
            "GridPatternIdentifiers.Pattern" or "TablePatternIdentifiers.Pattern") == true;
        var hint = c.FrameworkId == "UiAtlas.GridHostHint";
        if (!native && !hint && type is not ("Table" or "DataGrid")) return null;
        return new(c.RuntimeId, c.Bounds,
            native ? MapperHighlightKind.NativeGrid : MapperHighlightKind.GridCandidate,
            DisplayName(c), native || IsUia(c) ? "UI Automation" : IsVisual(c) ? "Computer vision" : "Native window hint", c);
    }

    private static bool IsUia(AutomationObservation c) =>
        !IsVisual(c) && !c.FrameworkId.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase) &&
        !c.RuntimeId.StartsWith("win32:", StringComparison.OrdinalIgnoreCase) &&
        !c.RuntimeId.StartsWith("msaa:", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(AutomationObservation c) =>
        string.IsNullOrWhiteSpace(c.Name) ? "Data grid" : c.Name.Length > 64 ? c.Name[..61] + "…" : c.Name;
    private static string Type(AutomationObservation c) => c.ControlType.Replace("ControlType.", "", StringComparison.Ordinal);
    private static long Area(RectI b) => (long)b.Width * b.Height;
    internal static bool Contains(RectI outer, RectI inner) => inner.X >= outer.X - 2 && inner.Y >= outer.Y - 2 &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width + 2 &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height + 2;
    internal static bool Equivalent(RectI a, RectI b)
    {
        var width = Math.Max(0L, Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X));
        var height = Math.Max(0L, Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return width * height >= Math.Min(Area(a), Area(b)) * .85 &&
            Math.Max(Area(a), Area(b)) <= Math.Min(Area(a), Area(b)) * 1.3;
    }
}

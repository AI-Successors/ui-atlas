using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Accessibility;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
internal static class MsaaDialogCollector
{
    private const int ChildSelf = 0;
    private const int StateUnavailable = 0x00000001;
    private const int StateSelected = 0x00000002;
    private const int StateInvisible = 0x00008000;
    private const int StateOffscreen = 0x00010000;
    private static readonly Guid IAccessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static IReadOnlyList<AutomationObservation> Collect(
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        int maxDepth)
    {
        if (maxNodes < 1 || maxDepth < 1) return [];
        var roots = ResolveRoots((nint)hwnd);
        if (roots.Count == 0) return [];
        // GetWindowRect is DPI-virtualized in the isolated worker, but MSAA's
        // accLocation values are physical screen coordinates. Use the largest
        // accessibility root as the coordinate authority before filtering the
        // tree; otherwise the right and bottom of scaled Office dialogs are lost.
        windowRect = ResolveProviderWindowRect(roots, windowRect);
        var accessible = roots[0];
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 512));
        var runtimeRoot = $"{hwnd:x}.msaa";
        result.Add(new(runtimeRoot, "", "msaa-dialog-root", SafeName(accessible),
            "ControlType.Window", WindowCatalog.GetClass((nint)hwnd),
            new RectI(windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top),
            true, false, "Win32", hwnd));
        var sequence = 0;
        for (var rootIndex = 0; rootIndex < roots.Count && result.Count < maxNodes; rootIndex++)
            AppendTree(roots[rootIndex], $"{runtimeRoot}.r{rootIndex}", runtimeRoot, hwnd,
                windowRect, result, maxNodes, maxDepth, ref sequence);

        // Office hosts the tab strip and some owner-drawn controls in separate
        // MsoCommandBar HWNDs. Their MSAA roots are cheap to query and expose the
        // PageTab roles that the parent client omits.
        foreach (var childHwnd in WindowCatalog.ListDescendantHandles(hwnd)
                     .Where(candidate => WindowCatalog.GetClass((nint)candidate)
                         .Equals("MsoCommandBar", StringComparison.OrdinalIgnoreCase))
                     .Take(16))
        {
            if (result.Count >= maxNodes) break;
            var childRoots = ResolveRoots((nint)childHwnd);
            for (var childRootIndex = 0; childRootIndex < childRoots.Count && result.Count < maxNodes; childRootIndex++)
                AppendTree(childRoots[childRootIndex], $"{runtimeRoot}.h{childHwnd:x}.r{childRootIndex}", runtimeRoot, hwnd,
                    windowRect, result, maxNodes, maxDepth, ref sequence);
        }

        var compact = result
            .GroupBy(item => string.IsNullOrWhiteSpace(item.ParentRuntimeId)
                ? item.RuntimeId
                : $"{item.ControlType}|{item.Name}|{item.Bounds.X},{item.Bounds.Y},{item.Bounds.Width},{item.Bounds.Height}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(maxNodes)
            .ToList();
        AppendKnownOfficeTabs(hwnd, windowRect, runtimeRoot, maxNodes, compact);
        AppendInferredListScrollBars(hwnd, maxNodes, compact);
        return compact;
    }

    public static IReadOnlyList<AutomationObservation> CollectPoints(
        long hwnd,
        IReadOnlyList<RectI> points,
        int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count > 96) throw new ArgumentOutOfRangeException(nameof(points));
        if (maxNodes < 1 || !NativeMethods.IsWindow((nint)hwnd) ||
            !NativeMethods.GetWindowRect((nint)hwnd, out var windowRect))
            return [];

        var result = new List<AutomationObservation>(Math.Min(maxNodes, points.Count));
        foreach (var point in points)
        {
            if (result.Count >= maxNodes || point.Width < 1 || point.Height < 1) break;
            var screenPoint = new NativeMethods.Point(point.X, point.Y);
            var pointedWindow = NativeMethods.WindowFromPoint(screenPoint);
            if (pointedWindow == 0 || WindowCatalog.GetTopLevelHandle(pointedWindow).ToInt64() != hwnd)
                continue;

            object? accessibleObject = null;
            try
            {
                var status = NativeMethods.AccessibleObjectFromPoint(
                    screenPoint, out var resolvedAccessible, out var child);
                accessibleObject = resolvedAccessible;
                if (status < 0 || accessibleObject is not IAccessible accessible) continue;

                var candidate = new List<AutomationObservation>(1);
                if (!Add(accessible, child ?? ChildSelf, "msaa-hit", "", hwnd, windowRect, candidate))
                    continue;
                var observation = candidate[0];
                var material = string.Join('|', observation.ControlType, observation.Name,
                    observation.Bounds.X, observation.Bounds.Y, observation.Bounds.Width, observation.Bounds.Height);
                var token = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..20];
                observation = observation with
                {
                    RuntimeId = $"{hwnd:x}.msaa-hit.{token}",
                    AutomationId = "msaa-hit:" + token
                };
                if (!result.Any(existing => SamePointObservation(existing, observation)))
                    result.Add(observation);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or OverflowException) { }
            finally
            {
                if (accessibleObject is not null && Marshal.IsComObject(accessibleObject))
                {
                    try { _ = Marshal.FinalReleaseComObject(accessibleObject); }
                    catch (InvalidComObjectException) { }
                }
            }
        }
        return result;
    }

    private static bool SamePointObservation(
        AutomationObservation left,
        AutomationObservation right) =>
        left.ControlType.Equals(right.ControlType, StringComparison.OrdinalIgnoreCase) &&
        left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
        left.Bounds == right.Bounds;

    private static void AppendInferredListScrollBars(
        long hwnd,
        int maxNodes,
        List<AutomationObservation> result)
    {
        if (result.Count >= maxNodes) return;
        foreach (var scrollbar in InferListScrollBars(result, hwnd, maxNodes - result.Count))
            result.Add(scrollbar);
    }

    internal static IReadOnlyList<AutomationObservation> InferListScrollBars(
        IReadOnlyList<AutomationObservation> controls,
        long hwnd,
        int capacity = int.MaxValue)
    {
        if (capacity < 1 || controls.Count == 0) return [];

        var visible = AutomationObservationVisibility.FilterEffectivelyVisible(controls);
        var visibleIds = visible
            .Select(control => (control.WindowHwnd, control.RuntimeId))
            .ToHashSet();
        var existingScrollBars = visible
            .Where(control => control.ControlType == "ControlType.ScrollBar")
            .ToArray();
        var inferred = new List<AutomationObservation>();

        foreach (var list in visible.Where(control =>
                     control.ControlType == "ControlType.List" &&
                     control.Bounds.Width >= 40 && control.Bounds.Height >= 48))
        {
            if (inferred.Count >= capacity) break;
            if (existingScrollBars.Any(scrollbar => OverlapsListEdge(scrollbar.Bounds, list.Bounds)))
                continue;

            var items = controls
                .Where(control => control.WindowHwnd == list.WindowHwnd &&
                                  control.ParentRuntimeId == list.RuntimeId &&
                                  control.ControlType == "ControlType.ListItem" &&
                                  visibleIds.Contains((control.WindowHwnd, control.RuntimeId)) &&
                                  control.Bounds.Width > 0 && control.Bounds.Height > 0)
                .OrderBy(control => control.Bounds.Y)
                .ToArray();
            if (items.Length < 2) continue;

            var distinctRows = items.Select(item => item.Bounds.Y).Distinct().Count();
            if (distinctRows < 2) continue;
            var rowHeights = items.Select(item => item.Bounds.Height).OrderBy(height => height).ToArray();
            var medianRowHeight = rowHeights[rowHeights.Length / 2];
            var firstTop = items.Min(item => item.Bounds.Y);
            var lastBottom = items.Max(item => item.Bounds.Y + item.Bounds.Height);
            var listBottom = list.Bounds.Y + list.Bounds.Height;
            var contentSpan = lastBottom - firstTop;
            var reachesBottomEdge = lastBottom >= listBottom - Math.Max(8, medianRowHeight);
            var fillsViewport = contentSpan >= list.Bounds.Height * 0.72;
            if (!reachesBottomEdge || !fillsViewport) continue;

            var width = Math.Clamp(medianRowHeight + 4, 14, 24);
            var bounds = new RectI(
                list.Bounds.X + list.Bounds.Width - width,
                list.Bounds.Y,
                width,
                list.Bounds.Height);
            if (inferred.Any(scrollbar => scrollbar.Bounds == bounds)) continue;

            inferred.Add(new(
                $"{list.RuntimeId}.inferred-vscroll",
                list.RuntimeId,
                "inferred-vscroll",
                "Vertical scrollbar",
                "ControlType.ScrollBar",
                "OfficeDialogInferredScrollBar",
                bounds,
                list.IsEnabled,
                false,
                "Win32",
                hwnd,
                ["RangeValuePatternIdentifiers.Pattern"]));
        }

        return inferred;
    }

    private static bool OverlapsListEdge(RectI scrollbar, RectI list)
    {
        var listRight = (long)list.X + list.Width;
        var scrollbarRight = (long)scrollbar.X + scrollbar.Width;
        var verticalOverlap = Math.Min((long)scrollbar.Y + scrollbar.Height, (long)list.Y + list.Height) -
                              Math.Max(scrollbar.Y, list.Y);
        return verticalOverlap > 0 && scrollbar.X < listRight && scrollbarRight >= listRight - Math.Max(8, list.Width / 8);
    }

    private static NativeMethods.Rect ResolveProviderWindowRect(
        IReadOnlyList<IAccessible> roots,
        NativeMethods.Rect fallback)
    {
        var best = fallback;
        var bestArea = Math.Max(0L, (long)(fallback.Right - fallback.Left) * (fallback.Bottom - fallback.Top));
        foreach (var root in roots)
        {
            try
            {
                root.accLocation(out var x, out var y, out var width, out var height, ChildSelf);
                if (width <= 0 || height <= 0) continue;
                var area = (long)width * height;
                if (area <= bestArea) continue;
                best = new NativeMethods.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
                bestArea = area;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or OverflowException) { }
        }
        return best;
    }

    private static void AppendKnownOfficeTabs(
        long hwnd,
        NativeMethods.Rect windowRect,
        string parentRuntime,
        int maxNodes,
        List<AutomationObservation> result)
    {
        if (!WindowCatalog.GetText((nint)hwnd).Equals("Format Cells", StringComparison.OrdinalIgnoreCase))
            return;

        var names = new[] { "Number", "Alignment", "Font", "Border", "Fill", "Protection" };
        var observedNames = result
            .Where(item => item.ControlType == "ControlType.TabItem" &&
                           names.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Prefer the real MSAA PageTab rectangles whenever Office exposes the
        // complete strip. They track the actual theme, DPI and title-bar height.
        if (names.All(observedNames.Contains)) return;

        // A partial provider strip is less useful than one stable fallback strip.
        result.RemoveAll(item => item.ControlType == "ControlType.TabItem" &&
            names.Contains(item.Name, StringComparer.OrdinalIgnoreCase));
        if (result.Count >= maxNodes) return;
        var boundaries = new[] { 0.018, 0.137, 0.267, 0.357, 0.462, 0.542, 0.682 };
        var dialogWidth = windowRect.Right - windowRect.Left;
        var dialogHeight = windowRect.Bottom - windowRect.Top;
        var top = windowRect.Top + Math.Max(8, (int)Math.Round(dialogHeight * 0.018));
        var height = Math.Max(22, (int)Math.Round(dialogHeight * 0.045));
        for (var index = 0; index < names.Length && result.Count < maxNodes; index++)
        {
            var left = windowRect.Left + (int)Math.Round(dialogWidth * boundaries[index]);
            var right = windowRect.Left + (int)Math.Round(dialogWidth * boundaries[index + 1]);
            result.Add(new($"{parentRuntime}.format-cells-tab-{index}", parentRuntime,
                $"format-cells-tab-{index}", names[index], "ControlType.TabItem", "OfficeDialogTab",
                new RectI(left, top, Math.Max(1, right - left), height), true, false, "Win32", hwnd,
                ["SelectionItemPatternIdentifiers.Pattern"]));
        }
    }

    private static void AppendTree(
        IAccessible accessible,
        string runtimePrefix,
        string rootParentRuntime,
        long outputHwnd,
        NativeMethods.Rect windowRect,
        List<AutomationObservation> result,
        int maxNodes,
        int maxDepth,
        ref int sequence)
    {
        var queue = new Queue<(IAccessible Accessible, string Runtime, int Depth)>();
        queue.Enqueue((accessible, rootParentRuntime, 0));

        while (queue.Count > 0 && result.Count < maxNodes)
        {
            var (parent, currentParentRuntime, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            int childCount;
            try { childCount = Math.Clamp(parent.accChildCount, 0, Math.Min(maxNodes, 2_048)); }
            catch (COMException) { continue; }
            if (childCount == 0) continue;
            var children = new object[childCount];
            int obtained;
            try
            {
                if (NativeMethods.AccessibleChildren(parent, 0, childCount, children, out obtained) < 0)
                    continue;
            }
            catch (COMException) { continue; }

            for (var index = 0; index < obtained && result.Count < maxNodes; index++)
            {
                var value = children[index];
                var childId = value is int id ? id : ChildSelf;
                var childAccessible = value as IAccessible;
                if (childAccessible is null && childId != ChildSelf)
                {
                    try { childAccessible = parent.get_accChild(childId) as IAccessible; }
                    catch (COMException) { }
                }
                var propertyOwner = childAccessible ?? parent;
                var propertyChild = childAccessible is null ? childId : ChildSelf;
                var runtime = $"{runtimePrefix}.{++sequence:x}";
                if (Add(propertyOwner, propertyChild, runtime, currentParentRuntime, outputHwnd, windowRect, result) &&
                    childAccessible is not null)
                    queue.Enqueue((childAccessible, runtime, depth + 1));
            }
        }
    }

    private static string SafeName(IAccessible accessible)
    {
        try { return accessible.get_accName(ChildSelf) ?? string.Empty; }
        catch (COMException) { return string.Empty; }
    }

    private static IReadOnlyList<IAccessible> ResolveRoots(nint hwnd)
    {
        var values = new List<IAccessible>(2);
        foreach (var objectId in new[] { NativeMethods.ObjidClient, NativeMethods.ObjidWindow })
        {
            object value;
            var id = IAccessibleId;
            try
            {
                if (NativeMethods.AccessibleObjectFromWindow(hwnd, objectId, ref id, out value) >= 0 &&
                    value is IAccessible accessible)
                    values.Add(accessible);
            }
            catch (COMException) { }
        }
        return values;
    }

    private static bool Add(
        IAccessible accessible,
        object child,
        string runtime,
        string parentRuntime,
        long hwnd,
        NativeMethods.Rect windowRect,
        List<AutomationObservation> result)
    {
        try
        {
            accessible.accLocation(out var x, out var y, out var width, out var height, child);
            if (width <= 0 || height <= 0 || x < windowRect.Left - 6 || y < windowRect.Top - 6 ||
                x + width > windowRect.Right + 6 || y + height > windowRect.Bottom + 6)
                return false;
            var role = Convert.ToInt32(accessible.get_accRole(child), System.Globalization.CultureInfo.InvariantCulture);
            var stateValue = accessible.get_accState(child);
            var state = stateValue is null ? 0 : Convert.ToInt32(stateValue, System.Globalization.CultureInfo.InvariantCulture);
            string name;
            try { name = accessible.get_accName(child) ?? string.Empty; }
            catch (COMException) { name = string.Empty; }
            var type = ControlTypeForRole(role, name);
            if (type == "ControlType.Custom")
            {
                string defaultAction;
                try { defaultAction = accessible.get_accDefaultAction(child) ?? string.Empty; }
                catch (Exception ex) when (ex is COMException or InvalidCastException) { defaultAction = string.Empty; }
                type = ControlTypeForRole(role, name, defaultAction);
            }
            if (type == "ControlType.Edit") name = "[redacted]";
            result.Add(new(runtime, parentRuntime, $"msaa-{runtime}", name, type,
                $"MSAA.Role{role}", new RectI(x, y, width, height),
                (state & StateUnavailable) == 0, (state & (StateInvisible | StateOffscreen)) != 0,
                "Win32", hwnd, PatternNames(type), false, (state & StateSelected) != 0));
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    internal static string ControlTypeForRole(int role, string name = "", string defaultAction = "")
    {
        var type = role switch
        {
            3 => "ControlType.ScrollBar",
            9 or 18 => "ControlType.Window",
            10 or 16 => "ControlType.Pane",
            11 => "ControlType.Menu",
            12 => "ControlType.MenuItem",
            15 => "ControlType.Document",
            20 or 38 => "ControlType.Group",
            21 => "ControlType.Separator",
            22 => "ControlType.ToolBar",
            24 => "ControlType.DataGrid",
            25 => "ControlType.Header",
            29 => "ControlType.DataItem",
            30 => "ControlType.Hyperlink",
            33 => "ControlType.List",
            34 => "ControlType.ListItem",
            35 => "ControlType.Tree",
            36 => "ControlType.TreeItem",
            37 => "ControlType.TabItem",
            40 => "ControlType.Image",
            41 => "ControlType.Text",
            42 => "ControlType.Edit",
            43 => "ControlType.Button",
            44 => "ControlType.CheckBox",
            45 => "ControlType.RadioButton",
            46 or 47 => "ControlType.ComboBox",
            51 => "ControlType.Slider",
            52 => "ControlType.Spinner",
            56 or 57 or 58 or 62 => "ControlType.SplitButton",
            60 => "ControlType.Tab",
            _ => "ControlType.Custom"
        };

        // Office account flyouts expose clickable tiles as ROLE_SYSTEM_CLIENT
        // (role 0), not as buttons. A real MSAA default action is authoritative;
        // the name fallback covers current Office builds that omit even that.
        return type == "ControlType.Custom" &&
               (!string.IsNullOrWhiteSpace(defaultAction) || IsKnownOfficeAccountAction(name))
            ? "ControlType.Button"
            : type;
    }

    private static bool IsKnownOfficeAccountAction(string name)
    {
        var value = name?.Trim() ?? string.Empty;
        return value.Equals("Sign out of this account", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Sign out options for ", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Add a new account or sign in", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Switch to ", StringComparison.OrdinalIgnoreCase) &&
               value.EndsWith(" account", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> PatternNames(string type) => type switch
    {
        "ControlType.TabItem" or "ControlType.ListItem" or "ControlType.RadioButton" =>
            ["SelectionItemPatternIdentifiers.Pattern"],
        "ControlType.CheckBox" => ["TogglePatternIdentifiers.Pattern"],
        "ControlType.ComboBox" or "ControlType.SplitButton" => ["ExpandCollapsePatternIdentifiers.Pattern"],
        "ControlType.Edit" => ["ValuePatternIdentifiers.Pattern"],
        "ControlType.Button" or "ControlType.Hyperlink" => ["InvokePatternIdentifiers.Pattern"],
        "ControlType.ScrollBar" or "ControlType.Slider" or "ControlType.Spinner" =>
            ["RangeValuePatternIdentifiers.Pattern"],
        _ => []
    };
}

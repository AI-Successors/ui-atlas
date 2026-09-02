using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>
/// Reads UI Automation through the native UIA3 COM client. This deliberately
/// lives beside, rather than behind, System.Windows.Automation: several WPF
/// runtime regressions affect only one client path, and comparing both paths is
/// the only reliable way to distinguish a client bridge problem from an opaque
/// provider.
/// </summary>
internal static class NativeUiaCollector
{
    private const int MaximumDepth = 64;
    private const int MaximumChildrenPerNode = 512;

    public static IReadOnlyList<AutomationObservation> CollectView(
        long hwnd,
        AutomationTreeView view,
        int maxNodes)
    {
        Validate(hwnd, maxNodes);
        IUIAutomation6? automation = null;
        IUIAutomationElement? root = null;
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            ConfigureTimeouts(automation);
            root = automation.ElementFromHandle((nint)hwnd);
            var walker = view switch
            {
                AutomationTreeView.Control => automation.ControlViewWalker,
                AutomationTreeView.Content => automation.ContentViewWalker,
                _ => automation.RawViewWalker
            };
            try { return Walk(root, walker, hwnd, maxNodes); }
            finally { Release(walker); }
        }
        finally
        {
            Release(root);
            Release(automation);
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectPoint(
        long hwnd,
        int x,
        int y,
        int maxNodes)
    {
        Validate(hwnd, maxNodes);
        IUIAutomation6? automation = null;
        IUIAutomationElement? scope = null;
        IUIAutomationElement? element = null;
        IUIAutomationTreeWalker? walker = null;
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            ConfigureTimeouts(automation);
            scope = automation.ElementFromHandle((nint)hwnd);
            element = automation.ElementFromPoint(new tagPOINT { x = x, y = y });
            if (element is null || !BelongsToScope(automation, element, scope)) return [];
            walker = automation.RawViewWalker;

            var chain = new List<IUIAutomationElement>(Math.Min(maxNodes, 32));
            var current = element;
            element = null;
            while (current is not null && chain.Count < maxNodes)
            {
                chain.Add(current);
                if (automation.CompareElements(current, scope) != 0) break;
                current = walker.GetParentElement(current);
            }
            chain.Reverse();
            return ConvertChain(chain, hwnd);
        }
        finally
        {
            Release(element);
            Release(walker);
            Release(scope);
            Release(automation);
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectPoints(
        long hwnd,
        IReadOnlyList<RectI> points,
        int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(points);
        Validate(hwnd, maxNodes);
        if (points.Count == 0) return [];
        if (points.Count > 96) throw new ArgumentOutOfRangeException(nameof(points));

        IUIAutomation6? automation = null;
        IUIAutomationElement? scope = null;
        IUIAutomationTreeWalker? walker = null;
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 512));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            ConfigureTimeouts(automation);
            scope = automation.ElementFromHandle((nint)hwnd);
            walker = automation.RawViewWalker;
            foreach (var point in points)
            {
                if (result.Count >= maxNodes) break;
                AppendPointChain(automation, scope, walker, hwnd, point.X, point.Y, maxNodes, result, seen);
            }
            return result;
        }
        finally
        {
            Release(walker);
            Release(scope);
            Release(automation);
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectBand(
        long hwnd,
        RectI band,
        int stepX,
        int stepY,
        int maxNodes)
    {
        Validate(hwnd, maxNodes);
        if (band.Width < 1 || band.Height < 1) return [];
        stepX = Math.Clamp(stepX, 8, 256);
        stepY = Math.Clamp(stepY, 8, 128);

        IUIAutomation6? automation = null;
        IUIAutomationElement? scope = null;
        IUIAutomationTreeWalker? walker = null;
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 512));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            ConfigureTimeouts(automation);
            scope = automation.ElementFromHandle((nint)hwnd);
            walker = automation.RawViewWalker;

            foreach (var y in SampleAxis(band.Y, band.Height, stepY))
            {
                var endX = (long)band.X + band.Width - 1;
                for (var x = band.X + Math.Min(stepX / 2, band.Width / 2);
                     x <= endX && result.Count < maxNodes;)
                {
                    var nextX = x + stepX;
                    AppendPointChain(automation, scope, walker, hwnd, x, y, maxNodes, result, seen);

                    // Once a hit exposes its exact geometry, jump to its right
                    // edge instead of querying several points inside the same
                    // WPF tab. This turns a dense grid into one provider call
                    // per adjacent control.
                    var hit = result
                        .Where(item => item.Bounds.Width is > 0 and <= 320 && item.Bounds.Height is > 0 and <= 96 &&
                                       x >= item.Bounds.X && x < item.Bounds.X + item.Bounds.Width &&
                                       y >= item.Bounds.Y && y < item.Bounds.Y + item.Bounds.Height)
                        .OrderBy(item => (long)item.Bounds.Width * item.Bounds.Height)
                        .FirstOrDefault();
                    if (hit is not null)
                        nextX = Math.Max(nextX, hit.Bounds.X + hit.Bounds.Width + 2);
                    x = nextX;
                }
            }

            return result;
        }
        finally
        {
            Release(walker);
            Release(scope);
            Release(automation);
        }
    }

    private static void AppendPointChain(
        IUIAutomation automation,
        IUIAutomationElement scope,
        IUIAutomationTreeWalker walker,
        long hwnd,
        int x,
        int y,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> seen)
    {
        IUIAutomationElement? current = null;
        var chain = new List<IUIAutomationElement>(24);
        try
        {
            current = automation.ElementFromPoint(new tagPOINT { x = x, y = y });
            if (current is null || !BelongsToScope(automation, current, scope)) return;

            for (var depth = 0; current is not null && depth < 24 && result.Count < maxNodes; depth++)
            {
                var item = current;
                current = null;
                chain.Add(item);
                if (automation.CompareElements(item, scope) != 0) break;
                try { current = walker.GetParentElement(item); }
                catch (COMException) { current = null; }
            }

            chain.Reverse();
            var parentRuntime = string.Empty;
            foreach (var item in chain)
            {
                var runtime = RuntimeKey(item, hwnd);
                if (seen.Contains(runtime))
                {
                    parentRuntime = runtime;
                    continue;
                }
                var observation = ToObservation(item, parentRuntime, hwnd);
                if (observation is null) continue;
                parentRuntime = observation.RuntimeId;
                if (seen.Add(observation.RuntimeId)) result.Add(observation);
            }
        }
        catch (COMException) { }
        finally
        {
            Release(current);
            foreach (var item in chain) Release(item);
        }
    }

    private static IEnumerable<int> SampleAxis(int start, int length, int step)
    {
        var end = (long)start + length - 1;
        if (end < start) yield break;
        for (long value = start + Math.Min(step / 2, length / 2); value <= end; value += step)
            yield return (int)value;
        if (length > step && (end - start) % step > step / 3)
            yield return (int)end;
    }

    private static string RuntimeKey(IUIAutomationElement element, long hwnd)
    {
        try
        {
            var parts = element.GetRuntimeId() ?? [];
            if (parts.Length > 0) return $"{hwnd:x}." + string.Join('.', parts);
            var bounds = element.CurrentBoundingRectangle;
            return $"{hwnd:x}.native-uia.{element.CurrentControlType}:{bounds.left},{bounds.top}";
        }
        catch (COMException)
        {
            return $"{hwnd:x}.native-uia.unavailable";
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectFocused(long hwnd)
    {
        Validate(hwnd, 1);
        IUIAutomation6? automation = null;
        IUIAutomationElement? scope = null;
        IUIAutomationElement? focused = null;
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            ConfigureTimeouts(automation);
            scope = automation.ElementFromHandle((nint)hwnd);
            focused = automation.GetFocusedElement();
            if (focused is null || !BelongsToScope(automation, focused, scope)) return [];
            var observation = ToObservation(focused, "", hwnd);
            return observation is null ? [] : [observation];
        }
        finally
        {
            Release(focused);
            Release(scope);
            Release(automation);
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectFocusWalk(long hwnd, int maxNodes)
    {
        Validate(hwnd, maxNodes);
        var targetRoot = WindowCatalog.GetRootOwnerHandle((nint)hwnd);
        if (targetRoot == 0 ||
            WindowCatalog.GetRootOwnerHandle(NativeMethods.GetForegroundWindow()) != targetRoot)
            return [];

        IUIAutomation6? automation = null;
        IUIAutomationElement? scope = null;
        IUIAutomationElement? originalFocus = null;
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 64));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            ConfigureTimeouts(automation);
            scope = automation.ElementFromHandle((nint)hwnd);
            try { originalFocus = automation.GetFocusedElement(); }
            catch (COMException) { }

            if (!SafeSyntheticInput.PressKey(0x12)) return result; // Alt: enter Ribbon/key-tip focus only.
            Thread.Sleep(80);
            for (var index = 0; index < Math.Min(maxNodes, 64); index++)
            {
                if (!SafeSyntheticInput.PressKey(NativeMethods.VkTab)) break;
                Thread.Sleep(55);
                IUIAutomationElement? focused = null;
                try
                {
                    focused = automation.GetFocusedElement();
                    if (focused is null || !BelongsToScope(automation, focused, scope)) break;
                    var observation = ToObservation(focused, "", hwnd);
                    if (observation is null) continue;
                    var key = $"{observation.AutomationId}|{observation.ControlType}|{observation.Name}|{observation.Bounds}";
                    if (!seen.Add(key)) break;
                    result.Add(observation with { HasKeyboardFocus = true });
                }
                catch (COMException) { break; }
                finally { Release(focused); }
            }
            return result;
        }
        finally
        {
            _ = SafeSyntheticInput.PressKey(NativeMethods.VkEscape);
            _ = SafeSyntheticInput.PressKey(NativeMethods.VkEscape);
            try { originalFocus?.SetFocus(); }
            catch (COMException) { }
            Release(originalFocus);
            Release(scope);
            Release(automation);
        }
    }

    private static IReadOnlyList<AutomationObservation> Walk(
        IUIAutomationElement root,
        IUIAutomationTreeWalker walker,
        long hwnd,
        int maxNodes)
    {
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 2_048));
        var queue = new Queue<(IUIAutomationElement Element, string Parent, int Depth)>();
        queue.Enqueue((root, "", 0));
        while (queue.Count > 0 && result.Count < maxNodes)
        {
            var (element, parent, depth) = queue.Dequeue();
            var observation = ToObservation(element, parent, hwnd);
            var runtime = observation?.RuntimeId ?? parent;
            if (observation is not null) result.Add(observation);

            if (depth < MaximumDepth)
            {
                IUIAutomationElement? child = null;
                try { child = walker.GetFirstChildElement(element); }
                catch (COMException) { }
                for (var count = 0; child is not null && count < MaximumChildrenPerNode &&
                     result.Count + queue.Count < maxNodes; count++)
                {
                    var queued = child;
                    child = null;
                    queue.Enqueue((queued, runtime, depth + 1));
                    try { child = walker.GetNextSiblingElement(queued); }
                    catch (COMException) { }
                }
                Release(child);
            }
            Release(element);
        }
        while (queue.Count > 0) Release(queue.Dequeue().Element);
        return result;
    }

    private static IReadOnlyList<AutomationObservation> ConvertChain(
        IReadOnlyList<IUIAutomationElement> chain,
        long hwnd)
    {
        var result = new List<AutomationObservation>(chain.Count);
        var parent = "";
        foreach (var item in chain)
        {
            var observation = ToObservation(item, parent, hwnd);
            if (observation is not null)
            {
                result.Add(observation);
                parent = observation.RuntimeId;
            }
            Release(item);
        }
        return result;
    }

    private static AutomationObservation? ToObservation(
        IUIAutomationElement element,
        string parentRuntime,
        long hwnd)
    {
        try
        {
            var runtimeParts = element.GetRuntimeId() ?? [];
            var runtime = runtimeParts.Length == 0
                ? $"{hwnd:x}.native-uia.{element.CurrentControlType}:{element.CurrentBoundingRectangle.left},{element.CurrentBoundingRectangle.top}"
                : $"{hwnd:x}." + string.Join('.', runtimeParts);
            var bounds = element.CurrentBoundingRectangle;
            var controlType = ControlTypeName(element.CurrentControlType);
            var name = controlType is "ControlType.Edit" or "ControlType.Document"
                ? "[redacted]"
                : Clamp(element.CurrentName, 4_096);
            var patterns = SupportedPatterns(element);
            return new(
                Clamp(runtime, 4_096),
                parentRuntime,
                Clamp(element.CurrentAutomationId, 512),
                name,
                controlType,
                Clamp(element.CurrentClassName, 512),
                new RectI(bounds.left, bounds.top, Math.Max(0, bounds.right - bounds.left), Math.Max(0, bounds.bottom - bounds.top)),
                element.CurrentIsEnabled != 0,
                element.CurrentIsOffscreen != 0,
                Clamp(element.CurrentFrameworkId, 128),
                hwnd,
                patterns,
                element.CurrentHasKeyboardFocus != 0,
                ReadBoolean(element, UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId));
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SupportedPatterns(IUIAutomationElement element)
    {
        var values = new List<string>(8);
        AddPattern(values, element, UIA_PropertyIds.UIA_IsInvokePatternAvailablePropertyId, "InvokePatternIdentifiers.Pattern");
        AddPattern(values, element, UIA_PropertyIds.UIA_IsSelectionItemPatternAvailablePropertyId, "SelectionItemPatternIdentifiers.Pattern");
        AddPattern(values, element, UIA_PropertyIds.UIA_IsExpandCollapsePatternAvailablePropertyId, "ExpandCollapsePatternIdentifiers.Pattern");
        AddPattern(values, element, UIA_PropertyIds.UIA_IsTogglePatternAvailablePropertyId, "TogglePatternIdentifiers.Pattern");
        AddPattern(values, element, UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId, "ValuePatternIdentifiers.Pattern");
        AddPattern(values, element, UIA_PropertyIds.UIA_IsRangeValuePatternAvailablePropertyId, "RangeValuePatternIdentifiers.Pattern");
        AddPattern(values, element, UIA_PropertyIds.UIA_IsScrollPatternAvailablePropertyId, "ScrollPatternIdentifiers.Pattern");
        return values;
    }

    private static void AddPattern(List<string> values, IUIAutomationElement element, int propertyId, string name)
    {
        if (ReadBoolean(element, propertyId)) values.Add(name);
    }

    private static bool ReadBoolean(IUIAutomationElement element, int propertyId)
    {
        try
        {
            var value = element.GetCurrentPropertyValueEx(propertyId, 1);
            return value is bool boolean ? boolean : value is int integer && integer != 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool BelongsToScope(
        IUIAutomation automation,
        IUIAutomationElement element,
        IUIAutomationElement scope)
    {
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? current = element;
        try
        {
            walker = automation.RawViewWalker;
            for (var depth = 0; current is not null && depth < 96; depth++)
            {
                if (automation.CompareElements(current, scope) != 0)
                {
                    if (!ReferenceEquals(current, element)) Release(current);
                    return true;
                }
                IUIAutomationElement? parent;
                try { parent = walker.GetParentElement(current); }
                catch (COMException) { parent = null; }
                if (!ReferenceEquals(current, element)) Release(current);
                current = parent;
            }
            if (current is not null && !ReferenceEquals(current, element)) Release(current);
            return false;
        }
        finally { Release(walker); }
    }

    private static void ConfigureTimeouts(IUIAutomation6 automation)
    {
        // The helper process remains the final hard timeout. These native UIA6
        // limits make a healthy provider return promptly before the process has
        // to be killed.
        automation.ConnectionTimeout = 700;
        automation.TransactionTimeout = 700;
    }

    private static string ControlTypeName(int id) => id switch
    {
        UIA_ControlTypeIds.UIA_ButtonControlTypeId => "ControlType.Button",
        UIA_ControlTypeIds.UIA_CheckBoxControlTypeId => "ControlType.CheckBox",
        UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => "ControlType.ComboBox",
        UIA_ControlTypeIds.UIA_EditControlTypeId => "ControlType.Edit",
        UIA_ControlTypeIds.UIA_HyperlinkControlTypeId => "ControlType.Hyperlink",
        UIA_ControlTypeIds.UIA_ImageControlTypeId => "ControlType.Image",
        UIA_ControlTypeIds.UIA_ListItemControlTypeId => "ControlType.ListItem",
        UIA_ControlTypeIds.UIA_ListControlTypeId => "ControlType.List",
        UIA_ControlTypeIds.UIA_MenuControlTypeId => "ControlType.Menu",
        UIA_ControlTypeIds.UIA_MenuBarControlTypeId => "ControlType.MenuBar",
        UIA_ControlTypeIds.UIA_MenuItemControlTypeId => "ControlType.MenuItem",
        UIA_ControlTypeIds.UIA_RadioButtonControlTypeId => "ControlType.RadioButton",
        UIA_ControlTypeIds.UIA_ScrollBarControlTypeId => "ControlType.ScrollBar",
        UIA_ControlTypeIds.UIA_SliderControlTypeId => "ControlType.Slider",
        UIA_ControlTypeIds.UIA_SpinnerControlTypeId => "ControlType.Spinner",
        UIA_ControlTypeIds.UIA_StatusBarControlTypeId => "ControlType.StatusBar",
        UIA_ControlTypeIds.UIA_TabControlTypeId => "ControlType.Tab",
        UIA_ControlTypeIds.UIA_TabItemControlTypeId => "ControlType.TabItem",
        UIA_ControlTypeIds.UIA_TextControlTypeId => "ControlType.Text",
        UIA_ControlTypeIds.UIA_ToolBarControlTypeId => "ControlType.ToolBar",
        UIA_ControlTypeIds.UIA_ToolTipControlTypeId => "ControlType.ToolTip",
        UIA_ControlTypeIds.UIA_TreeControlTypeId => "ControlType.Tree",
        UIA_ControlTypeIds.UIA_TreeItemControlTypeId => "ControlType.TreeItem",
        UIA_ControlTypeIds.UIA_GroupControlTypeId => "ControlType.Group",
        UIA_ControlTypeIds.UIA_ThumbControlTypeId => "ControlType.Thumb",
        UIA_ControlTypeIds.UIA_DataGridControlTypeId => "ControlType.DataGrid",
        UIA_ControlTypeIds.UIA_DataItemControlTypeId => "ControlType.DataItem",
        UIA_ControlTypeIds.UIA_DocumentControlTypeId => "ControlType.Document",
        UIA_ControlTypeIds.UIA_SplitButtonControlTypeId => "ControlType.SplitButton",
        UIA_ControlTypeIds.UIA_WindowControlTypeId => "ControlType.Window",
        UIA_ControlTypeIds.UIA_PaneControlTypeId => "ControlType.Pane",
        UIA_ControlTypeIds.UIA_HeaderControlTypeId => "ControlType.Header",
        UIA_ControlTypeIds.UIA_HeaderItemControlTypeId => "ControlType.HeaderItem",
        UIA_ControlTypeIds.UIA_SeparatorControlTypeId => "ControlType.Separator",
        UIA_ControlTypeIds.UIA_CustomControlTypeId => "ControlType.Custom",
        _ => id > 0 ? $"ControlType.Native{id}" : ""
    };

    private static void Validate(long hwnd, int maxNodes)
    {
        if (!NativeMethods.IsWindow((nint)hwnd)) throw new ArgumentException("Window handle is not valid.", nameof(hwnd));
        if (maxNodes is < 1 or > RecordingContractLimits.MaxControlsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
    }

    private static string Clamp(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { _ = Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }
}

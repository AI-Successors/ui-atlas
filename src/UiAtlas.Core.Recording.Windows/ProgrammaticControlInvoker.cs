using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Automation;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
internal static class ProgrammaticControlInvoker
{
    public static bool TryInvoke(WindowTarget target, AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (AutomaticInteractionSafety.IsTreeControlOrDescendant(control, [control])) return false;
        var element = ResolveElement(target, control);
        if (element is null) return false;
        var preferExpandBeforeInvoke = PrefersExpandBeforeInvoke(control);

        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) &&
                selectionPattern is SelectionItemPattern selectionItem)
            {
                selectionItem.Select();
                return true;
            }

            if (preferExpandBeforeInvoke &&
                element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var preferredExpandPattern) &&
                preferredExpandPattern is ExpandCollapsePattern preferredExpandCollapse)
            {
                if (preferredExpandCollapse.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    preferredExpandCollapse.Expand();
                return true;
            }

            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern) &&
                invokePattern is InvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }

            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern) &&
                expandPattern is ExpandCollapsePattern expandCollapse)
            {
                if (expandCollapse.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    expandCollapse.Expand();
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        return false;
    }

    public static bool TryClickCenter(RectI bounds, int clickCount = 1)
    {
        return TryClick(bounds, clickCount, 0.5, 0.5);
    }

    public static bool TryClickObservedControl(AutomationObservation control, int clickCount = 1)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (AutomaticInteractionSafety.IsTreeControlOrDescendant(control, [control])) return false;
        var (horizontalBias, verticalBias) = ResolveClickBias(control);
        return TryClick(control.Bounds, clickCount, horizontalBias, verticalBias);
    }

    public static bool TryScroll(RectI bounds, int wheelDelta)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || wheelDelta == 0) return false;
        if (!NativeMethods.GetCursorPos(out var original)) return false;
        var x = bounds.X + Math.Clamp(bounds.Width / 2, 1, Math.Max(1, bounds.Width - 1));
        var y = bounds.Y + Math.Clamp(bounds.Height / 2, 1, Math.Max(1, bounds.Height - 1));
        if (!NativeMethods.SetCursorPos(x, y)) return false;
        try
        {
            var input = new[]
            {
                new NativeMethods.Input
                {
                    Type = NativeMethods.InputMouse,
                    Union = new NativeMethods.InputUnion
                    {
                        Mouse = new NativeMethods.MouseInput
                        {
                            MouseData = unchecked((uint)wheelDelta),
                            Flags = NativeMethods.MouseeventfWheel
                        }
                    }
                }
            };
            return NativeMethods.SendInput(1, input, Marshal.SizeOf<NativeMethods.Input>()) == 1;
        }
        finally
        {
            _ = NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    public static bool TrySelectObservedControlAtPoint(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var x = control.Bounds.X + Math.Max(1, control.Bounds.Width / 2);
        var y = control.Bounds.Y + Math.Max(1, control.Bounds.Height / 2);
        AutomationElement? element;
        try { element = AutomationElement.FromPoint(new System.Windows.Point(x, y)); }
        catch (ElementNotAvailableException) { return false; }

        for (var depth = 0; depth < 10 && element is not null; depth++)
        {
            try
            {
                var current = element.Current;
                var type = current.ControlType?.ProgrammaticName ?? "";
                if (type.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase) &&
                    element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) &&
                    selectionPattern is SelectionItemPattern selection)
                {
                    selection.Select();
                    return true;
                }
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                return false;
            }
        }
        return false;
    }

    public static bool TrySelectNextDialogTab()
    {
        NativeMethods.keybd_event(NativeMethods.VkControl, 0, 0, 0);
        try
        {
            NativeMethods.keybd_event(NativeMethods.VkTab, 0, 0, 0);
            NativeMethods.keybd_event(NativeMethods.VkTab, 0, NativeMethods.KeyeventfKeyup, 0);
            return true;
        }
        finally
        {
            NativeMethods.keybd_event(NativeMethods.VkControl, 0, NativeMethods.KeyeventfKeyup, 0);
        }
    }

    internal static bool PrefersDirectMouseClick(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var controlType = NormalizeControlType(control.ControlType);
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(controlType, "MenuItem", StringComparison.OrdinalIgnoreCase))
            return false;

        // Revit exposes the whole split-button tile as an Invoke-only UIA button.
        // Invoke reports success without opening the flyout, so the real chevron
        // must be pressed with mouse input near the bottom of the tile.
        if (IsRevitRibbonFlyoutButton(control))
            return HasPattern(control, "Invoke") || HasPattern(control, "ExpandCollapse");

        // Excel's gallery/menu anchors often acknowledge ExpandCollapse.Expand()
        // without painting the menu. A real mouse click is the reliable activation
        // path for these controls (including the eight Insert chart galleries).
        if (IsOfficeExpandableMenuAnchor(control))
            return true;

        if (string.IsNullOrWhiteSpace(control.ParentRuntimeId))
            return false;

        var value = $"{control.AutomationId} {control.Name} {control.ClassName}";
        var isDedicatedDropdown = value.Contains("dropdown", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("drop_down", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("chevron", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(control.Name?.Trim(), "More Options", StringComparison.OrdinalIgnoreCase);
        var maxWidth = isDedicatedDropdown ? 120 : 34;
        var maxHeight = isDedicatedDropdown ? 100 : 42;
        if (control.Bounds.Width is <= 0 || control.Bounds.Width > maxWidth ||
            control.Bounds.Height is <= 0 || control.Bounds.Height > maxHeight)
            return false;

        if (!HasPattern(control, "Invoke") && !HasPattern(control, "ExpandCollapse"))
            return false;

        return isDedicatedDropdown ||
               value.Contains("sticky", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(control.Name?.Trim(), "Open", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(control.Name);
    }

    internal static (double HorizontalBias, double VerticalBias) ResolveClickBias(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (IsRevitRibbonFlyoutButton(control))
            return (0.5, 0.82);
        if (IsOfficeExpandableMenuAnchor(control))
            return control.Bounds.Height >= 44 ? (0.5, 0.82) : (0.78, 0.58);
        return PrefersDirectMouseClick(control)
            ? (0.72, 0.58)
            : (0.5, 0.5);
    }

    private static bool IsOfficeExpandableMenuAnchor(AutomationObservation control) =>
        NormalizeControlType(control.ControlType).Equals("MenuItem", StringComparison.OrdinalIgnoreCase) &&
        control.ClassName.Equals("NetUIAnchor", StringComparison.OrdinalIgnoreCase) &&
        HasPattern(control, "ExpandCollapse");

    internal static bool IsRevitRibbonFlyoutButton(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return NormalizeControlType(control.ControlType).Equals("Button", StringComparison.OrdinalIgnoreCase) &&
               control.AutomationId.EndsWith("FlyoutButtonShowFlyout", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PrefersExpandBeforeInvoke(AutomationObservation control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var controlType = NormalizeControlType(control.ControlType);
        if (string.Equals(controlType, "ComboBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlType, "SplitButton", StringComparison.OrdinalIgnoreCase))
            return HasPattern(control, "ExpandCollapse");

        var value = $"{control.AutomationId} {control.Name} {control.ClassName}";
        return HasPattern(control, "ExpandCollapse") && (
            value.Contains("dropdown", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("drop_down", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("chevron", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(control.Name?.Trim(), "More Options", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryClick(RectI bounds, int clickCount, double horizontalBias, double verticalBias)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || clickCount < 1) return false;
        if (!NativeMethods.GetCursorPos(out var original)) return false;
        horizontalBias = Math.Clamp(horizontalBias, 0.15, 0.85);
        verticalBias = Math.Clamp(verticalBias, 0.15, 0.85);

        var centerX = bounds.X + Math.Clamp((int)Math.Round(bounds.Width * horizontalBias), 1, Math.Max(1, bounds.Width - 1));
        var centerY = bounds.Y + Math.Clamp((int)Math.Round(bounds.Height * verticalBias), 1, Math.Max(1, bounds.Height - 1));
        if (!NativeMethods.SetCursorPos(centerX, centerY)) return false;

        try
        {
            for (var click = 0; click < clickCount; click++)
            {
                var down = new[]
                {
                    new NativeMethods.Input
                    {
                        Type = NativeMethods.InputMouse,
                        Union = new NativeMethods.InputUnion
                        {
                            Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseeventfLeftDown }
                        }
                    }
                };
                var up = new[]
                {
                    new NativeMethods.Input
                    {
                        Type = NativeMethods.InputMouse,
                        Union = new NativeMethods.InputUnion
                        {
                            Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseeventfLeftUp }
                        }
                    }
                };
                if (NativeMethods.SendInput(1, down, Marshal.SizeOf<NativeMethods.Input>()) != 1)
                    return false;
                Thread.Sleep(32);
                if (NativeMethods.SendInput(1, up, Marshal.SizeOf<NativeMethods.Input>()) != 1)
                {
                    // Best effort: never leave the physical button logically held
                    // if Windows accepted the down event but rejected the first up.
                    _ = NativeMethods.SendInput(1, up, Marshal.SizeOf<NativeMethods.Input>());
                    return false;
                }
                // Heavy WPF hosts such as Revit may process the injected messages
                // after SendInput returns. Keep the cursor over the target briefly
                // so their routed MouseUp event is not resolved at the restored
                // controller position.
                Thread.Sleep(70);
                if (click + 1 < clickCount)
                    Thread.Sleep(70);
            }

            return true;
        }
        finally
        {
            _ = NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    private static AutomationElement? ResolveElement(WindowTarget target, AutomationObservation control)
    {
        // The point lookup is effectively O(depth) and is especially important
        // for Revit, whose full RawView tree can take tens of seconds to walk.
        // Fall back to the sealed runtime-id walk only when hit-testing cannot
        // resolve the observed control.
        var pointMatch = FindObservedElementAtPoint(control);
        if (pointMatch is not null)
            return pointMatch;

        var candidateWindows = WindowCatalog.ListScopedWindows(target)
            .Where(window => control.WindowHwnd == 0 || window.Hwnd == control.WindowHwnd)
            .Select(window => window.Hwnd)
            .Append(target.RootOwnerHwnd)
            .Distinct()
            .ToArray();
        foreach (var hwnd in candidateWindows)
        {
            var element = FindByRuntimeId(hwnd, control.RuntimeId);
            if (element is not null)
                return element;
        }

        return null;
    }

    private static AutomationElement? FindObservedElementAtPoint(AutomationObservation control)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return null;

        var x = control.Bounds.X + Math.Max(1, control.Bounds.Width / 2);
        var y = control.Bounds.Y + Math.Max(1, control.Bounds.Height / 2);
        AutomationElement? element;
        try { element = AutomationElement.FromPoint(new System.Windows.Point(x, y)); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }

        for (var depth = 0; depth < 8 && element is not null; depth++)
        {
            try
            {
                var current = element.Current;
                var runtimeId = control.WindowHwnd == 0 ? string.Empty : RuntimeIdFor(control.WindowHwnd, element);
                var automationId = current.AutomationId ?? string.Empty;
                var className = current.ClassName ?? string.Empty;
                if ((!string.IsNullOrWhiteSpace(control.RuntimeId) &&
                     runtimeId.Equals(control.RuntimeId, StringComparison.Ordinal)) ||
                    (!string.IsNullOrWhiteSpace(control.AutomationId) &&
                     automationId.Equals(control.AutomationId, StringComparison.OrdinalIgnoreCase) &&
                     (string.IsNullOrWhiteSpace(control.ClassName) ||
                      className.Equals(control.ClassName, StringComparison.OrdinalIgnoreCase))))
                    return element;

                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return null;
            }
        }

        return null;
    }

    private static AutomationElement? FindByRuntimeId(long hwnd, string runtimeId)
    {
        if (hwnd == 0 || string.IsNullOrWhiteSpace(runtimeId)) return null;
        var root = AutomationElement.FromHandle((nint)hwnd);
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                if (string.Equals(RuntimeIdFor(hwnd, current), runtimeId, StringComparison.Ordinal))
                    return current;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            try
            {
                var children = new List<AutomationElement>();
                var child = TreeWalker.RawViewWalker.GetFirstChild(current);
                while (child is not null)
                {
                    children.Add(child);
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }

                for (var index = children.Count - 1; index >= 0; index--)
                    stack.Push(children[index]);
            }
            catch (ElementNotAvailableException)
            {
                // Ignore transient UIA nodes and continue scanning.
            }
        }

        return null;
    }

    private static string RuntimeIdFor(long hwnd, AutomationElement element) =>
        $"{hwnd:x}." + string.Join('.', element.GetRuntimeId());

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static bool HasPattern(AutomationObservation control, string patternName) =>
        control.SupportedPatterns?.Any(pattern => pattern.Contains(patternName, StringComparison.OrdinalIgnoreCase)) == true;
}

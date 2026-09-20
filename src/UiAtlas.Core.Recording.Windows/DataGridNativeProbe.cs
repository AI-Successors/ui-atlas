using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Read-only capability probe. Invoke only inside the bounded UIA worker.</summary>
public static class DataGridNativeProbe
{
    public static GridNativeProbeResult Collect(long hostHwnd, int maxNodes)
    {
        if (hostHwnd == 0 || maxNodes is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        IUIAutomation6? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationTreeWalker? walker = null;
        var pending = new Queue<IUIAutomationElement>();
        var headers = new List<RectI>();
        var nativeText = new List<GridNativeTextEvidence>();
        var grid = false;
        var table = false;
        var gridItem = false;
        var tableItem = false;
        var value = false;
        var legacy = false;
        var text = false;
        int? rows = null, columns = null;
        var inspected = 0;
        RectI? bounds = null;
        var complete = false;
        string? reason = null;
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            automation.ConnectionTimeout = 700;
            automation.TransactionTimeout = 700;
            root = automation.ElementFromHandle((nint)hostHwnd);
            bounds = Bounds(root);
            grid = Has(root, UIA_PropertyIds.UIA_IsGridPatternAvailablePropertyId);
            table = Has(root, UIA_PropertyIds.UIA_IsTablePatternAvailablePropertyId);
            value = Has(root, UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId);
            legacy = Has(root, UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId);
            text = Has(root, UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId);
            nativeText.Add(ReadTextEvidence(root, "container", true));
            // Probe the container before looking for descendants: opaque providers
            // can implement Grid/Table on a container with no accessible children.
            if (grid)
            {
                var pattern = (IUIAutomationGridPattern)root.GetCurrentPattern(UIA_PatternIds.UIA_GridPatternId);
                try { rows = pattern.CurrentRowCount; columns = pattern.CurrentColumnCount; }
                finally { Release(pattern); }
            }
            if (table)
            {
                var pattern = (IUIAutomationTablePattern)root.GetCurrentPattern(UIA_PatternIds.UIA_TablePatternId);
                try
                {
                    var items = pattern.GetCurrentColumnHeaders();
                    try
                    {
                        for (var index = 0; index < Math.Min(items.Length, 32); index++)
                        {
                            var header = items.GetElement(index);
                            try { headers.Add(Bounds(header)); }
                            finally { Release(header); }
                        }
                    }
                    finally { Release(items); }
                }
                finally { Release(pattern); }
            }
            walker = automation.RawViewWalker;
            var first = walker.GetFirstChildElement(root);
            if (first is not null) pending.Enqueue(first);
            while (pending.Count > 0 && inspected < maxNodes)
            {
                var element = pending.Dequeue();
                try
                {
                    inspected++;
                    gridItem |= Has(element, UIA_PropertyIds.UIA_IsGridItemPatternAvailablePropertyId);
                    tableItem |= Has(element, UIA_PropertyIds.UIA_IsTableItemPatternAvailablePropertyId);
                    value |= Has(element, UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId);
                    legacy |= Has(element, UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId);
                    text |= Has(element, UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId);
                    nativeText.Add(ReadTextEvidence(element, $"descendant-{inspected}", false));
                    var sibling = walker.GetNextSiblingElement(element);
                    if (sibling is not null) pending.Enqueue(sibling);
                    var child = walker.GetFirstChildElement(element);
                    if (child is not null) pending.Enqueue(child);
                }
                finally { Release(element); }
            }
            complete = pending.Count == 0;
            reason = complete ? null : "native-descendant-probe-limit";
        }
        catch (Exception exception)
        {
            reason = $"native-probe-failed:{exception.GetType().Name}:{exception.HResult:X8}";
        }
        finally
        {
            while (pending.TryDequeue(out var item)) Release(item);
            Release(walker);
            Release(root);
            Release(automation);
        }
        return new(new(grid, table, rows, columns, inspected, gridItem, tableItem,
            value, legacy, text, complete, reason), bounds, headers, nativeText);
    }

    private static GridNativeTextEvidence ReadTextEvidence(IUIAutomationElement element, string sourceId, bool isContainer)
    {
        var name = element.CurrentName ?? "";
        string? value = null;
        int? role = null;
        if (Has(element, UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId))
        {
            var pattern = (IUIAutomationLegacyIAccessiblePattern)element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId);
            try
            {
                name = pattern.CurrentName ?? name;
                value = pattern.CurrentValue;
                role = (int)pattern.CurrentRole;
            }
            finally { Release(pattern); }
        }
        return new(sourceId, Bounds(element), element.CurrentControlType,
            name.Length <= 512 ? name : name[..512], value is { Length: > 512 } ? value[..512] : value, role, isContainer);
    }

    private static bool Has(IUIAutomationElement element, int propertyId)
    {
        var result = element.GetCurrentPropertyValueEx(propertyId, 1);
        return result is bool boolean ? boolean : result is int integer && integer != 0;
    }

    private static RectI Bounds(IUIAutomationElement element)
    {
        var rectangle = element.CurrentBoundingRectangle;
        return new(rectangle.left, rectangle.top,
            rectangle.right - rectangle.left, rectangle.bottom - rectangle.top);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}

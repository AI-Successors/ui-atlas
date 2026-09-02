using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public enum AutomationTreeView { Raw, Control, Content }

[SupportedOSPlatform("windows")]
public static class BoundedAutomationCollector
{
    private static readonly Condition PopupInteractiveCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Spinner),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Thumb),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

    private static readonly Condition DialogControlCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Spinner),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

    private static readonly Condition WorksheetControlCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.HeaderItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Thumb));

    private static readonly Condition SheetNavigationCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ClassNameProperty, "ExcelBookTabControl"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "SheetTab"));

    // Excel exposes its account button, window commands, Comments and Share through
    // a small NetUI title/chrome island. The bounded Ribbon TreeWalker can exhaust
    // its visit budget inside the command band before it reaches that island. An
    // exact provider-side query returns only these few elements and avoids falling
    // back to the multi-second full application tree.
    private static readonly Condition TopChromeControlCondition = new OrCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "MeControlWidget"),
        new PropertyCondition(AutomationElement.NameProperty, "Comments"),
        new PropertyCondition(AutomationElement.NameProperty, "Share"),
        new PropertyCondition(AutomationElement.NameProperty, "Minimize"),
        new PropertyCondition(AutomationElement.NameProperty, "Maximize"),
        new PropertyCondition(AutomationElement.NameProperty, "Restore"),
        new PropertyCondition(AutomationElement.NameProperty, "Restore Down"),
        new PropertyCondition(AutomationElement.NameProperty, "Close"));

    // Revit exposes the ribbon, Properties palette, Project Browser and view/status
    // toolbars through one WPF provider. Query only visible interactive roles so a
    // materialized frame represents the whole visible application instead of just
    // the current ribbon buttons. Tree items are observable here, while the separate
    // automatic-interaction safety policy keeps them strictly non-clickable.
    private static readonly Condition RevitVisibleControlCondition = new AndCondition(
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Spinner),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Thumb),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)));

    public static IReadOnlyList<AutomationObservation> Collect(long hwnd, int maxNodes, int maxDepth = 64)
    {
        if (!IsSupportedNodeLimit(maxNodes) || maxDepth is < 1 or > 128) throw new ArgumentOutOfRangeException();
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 4096));
        var visitedNodes = 0;
        foreach (var window in WindowCatalog.ListScopedWindows(hwnd))
        {
            if (visitedNodes >= maxNodes) break;
            CollectWindow(window.Hwnd, maxNodes, maxDepth, result, ref visitedNodes);
        }
        return result;
    }

    public static IReadOnlyList<AutomationObservation> CollectExactWindow(long hwnd, int maxNodes, int maxDepth = 64)
    {
        if (!IsSupportedNodeLimit(maxNodes) || maxDepth is < 1 or > 128) throw new ArgumentOutOfRangeException();
        if (!NativeMethods.IsWindow((nint)hwnd)) throw new ArgumentException("Window handle is not valid.", nameof(hwnd));
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 4096));
        var visitedNodes = 0;
        CollectWindow(hwnd, maxNodes, maxDepth, result, ref visitedNodes);
        // Office popup HWND providers often expose only the Menu root through
        // TreeWalker even though individual items are available through hit testing.
        // Recover those visible descendants without expanding the sealed HWND scope.
        if (result.Count <= 1)
            CollectWindowByHitTesting(hwnd, hwnd, maxNodes, result);
        return result;
    }

    public static IReadOnlyList<AutomationObservation> CollectViewWindow(
        long hwnd,
        AutomationTreeView view,
        int maxNodes,
        int maxDepth = 64)
    {
        if (!IsSupportedNodeLimit(maxNodes) || maxDepth is < 1 or > 128) throw new ArgumentOutOfRangeException();
        if (!NativeMethods.IsWindow((nint)hwnd)) throw new ArgumentException("Window handle is not valid.", nameof(hwnd));
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 4096));
        var visitedNodes = 0;
        CollectWindow(hwnd, maxNodes, maxDepth, result, ref visitedNodes, view);
        return result;
    }

    public static IReadOnlyList<AutomationObservation> CollectLegacyWindow(long hwnd, int maxNodes, int maxDepth = 64)
    {
        if (!IsSupportedNodeLimit(maxNodes) || maxDepth is < 1 or > 128) throw new ArgumentOutOfRangeException();
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var windowRect))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));
        return MsaaDialogCollector.Collect(hwnd, windowRect, maxNodes, maxDepth);
    }

    public static IReadOnlyList<AutomationObservation> CollectNativePeripheralWindow(long hwnd, int maxNodes)
    {
        if (!IsSupportedNodeLimit(maxNodes)) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var nativeRoot))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));

        var root = new RectI(
            nativeRoot.Left,
            nativeRoot.Top,
            nativeRoot.Right - nativeRoot.Left,
            nativeRoot.Bottom - nativeRoot.Top);
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 512));
        foreach (var childHwnd in WindowCatalog.ListDescendantHandles(hwnd, 2_048))
        {
            if (result.Count >= maxNodes || !NativeMethods.IsWindowVisible((nint)childHwnd)) continue;
            var bounds = WindowBounds(childHwnd);
            var className = WindowCatalog.GetClass((nint)childHwnd);
            if (!IsPeripheralNativeControl(className, bounds, root)) continue;

            var name = Clamp(WindowCatalog.GetText((nint)childHwnd), 4_096);
            var controlId = NativeMethods.GetDlgCtrlID((nint)childHwnd);
            var normalizedType = NativeControlType(className);
            var identity = controlId > 0
                ? controlId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{(bounds.X - root.X) / 4}:{(bounds.Y - root.Y) / 4}:{bounds.Width / 4}:{bounds.Height / 4}";
            var automationId = Clamp($"native:{className}:{identity}", 512);
            var runtime = $"{hwnd:x}.native.{childHwnd:x}";
            result.Add(new(
                runtime,
                "",
                automationId,
                name,
                "ControlType." + normalizedType,
                className,
                bounds,
                NativeMethods.IsWindowEnabled((nint)childHwnd),
                false,
                "Win32",
                hwnd,
                string.IsNullOrWhiteSpace(name) ? [] : SuggestedRevitPatterns(normalizedType, automationId)));
        }

        // Revit's owner-drawn property grid exposes a native surface but no child
        // HWNDs for its rows. Preserve the deterministic row geometry as observed
        // structure; it is not auto-clickable without a later semantic match.
        if (IsRevitWindow(hwnd))
            AppendRevitPropertyGridRows(hwnd, nativeRoot, maxNodes, result);
        return result;
    }

    public static IReadOnlyList<AutomationObservation> CollectRevitBrowserWindow(long hwnd, int maxNodes)
    {
        if (!IsSupportedNodeLimit(maxNodes)) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var nativeRoot))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));

        var root = new RectI(
            nativeRoot.Left,
            nativeRoot.Top,
            nativeRoot.Right - nativeRoot.Left,
            nativeRoot.Bottom - nativeRoot.Top);
        var browserHwnd = WindowCatalog.ListDescendantHandles(hwnd, 2_048)
            .Where(candidate => NativeMethods.IsWindowVisible((nint)candidate))
            .FirstOrDefault(candidate =>
                WindowCatalog.GetClass((nint)candidate).Equals(
                    "Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) &&
                IsPeripheralBrowserSurface(WindowBounds(candidate), root));
        if (browserHwnd == 0) return [];

        return CollectViewWindow(browserHwnd, AutomationTreeView.Raw, maxNodes)
            .Where(IsUsefulBrowserControl)
            .Take(maxNodes)
            .ToArray();
    }

    internal static bool IsPeripheralBrowserSurface(RectI candidate, RectI root)
    {
        if (candidate.Width < 120 || candidate.Height < 80 || !IsContainedInBounds(candidate, root))
            return false;
        var leftBand = candidate.X < root.X + root.Width * 40 / 100;
        var rightBand = candidate.X + candidate.Width > root.X + root.Width * 60 / 100;
        return leftBand || rightBand;
    }

    internal static bool IsUsefulBrowserControl(AutomationObservation observation)
    {
        if (observation.IsOffscreen || !observation.Bounds.IsValid || observation.Bounds.Width <= 0 ||
            observation.Bounds.Height <= 0)
            return false;
        var type = NormalizeControlType(observation.ControlType);
        if (type is "Button" or "CheckBox" or "ComboBox" or "Edit" or "Hyperlink" or "ListItem" or
            "MenuItem" or "RadioButton" or "TreeItem")
            return true;
        return type == "Image" && observation.Bounds.Width <= 48 && observation.Bounds.Height <= 48 &&
               observation.SupportedPatterns is not null && observation.SupportedPatterns.Any(pattern =>
                   pattern.Contains("InvokePattern", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsPeripheralNativeControl(string className, RectI candidate, RectI root)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0 || root.Width <= 0 || root.Height <= 0 ||
            !IsContainedInBounds(candidate, root))
            return false;
        var supportedClass = className.Equals("Button", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("ComboBox", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("ScrollBar", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("msctls_statusbar32", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("ToolbarWindow32", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("SysTreeView32", StringComparison.OrdinalIgnoreCase) ||
                             className.Equals("SysListView32", StringComparison.OrdinalIgnoreCase);
        if (!supportedClass) return false;

        var leftBand = candidate.X < root.X + root.Width * 40 / 100;
        var rightBand = candidate.X + candidate.Width > root.X + root.Width * 60 / 100;
        var bottomBand = candidate.Y + candidate.Height > root.Y + root.Height * 80 / 100;
        return leftBand || rightBand || bottomBand;
    }

    private static string NativeControlType(string className)
    {
        if (className.Equals("Button", StringComparison.OrdinalIgnoreCase)) return "Button";
        if (className.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)) return "ComboBox";
        if (className.Equals("Edit", StringComparison.OrdinalIgnoreCase)) return "Edit";
        if (className.Equals("ScrollBar", StringComparison.OrdinalIgnoreCase)) return "ScrollBar";
        if (className.Equals("SysTreeView32", StringComparison.OrdinalIgnoreCase)) return "Tree";
        if (className.Equals("SysListView32", StringComparison.OrdinalIgnoreCase)) return "List";
        if (className.Equals("ToolbarWindow32", StringComparison.OrdinalIgnoreCase)) return "ToolBar";
        return "StatusBar";
    }

    private static bool IsRevitWindow(long hwnd)
    {
        try
        {
            var target = WindowCatalog.Resolve(hwnd);
            return target.ProcessName.Equals("Revit", StringComparison.OrdinalIgnoreCase) ||
                   target.OriginalFilename.Equals("Revit.exe", StringComparison.OrdinalIgnoreCase) ||
                   target.ProductName.Contains("Autodesk Revit", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectPopupWindow(
        long rootHwnd,
        long popupHwnd,
        int maxNodes,
        int maxDepth = 64)
    {
        if (!IsSupportedNodeLimit(maxNodes) || maxDepth is < 1 or > 128) throw new ArgumentOutOfRangeException();
        if (!NativeMethods.IsWindow((nint)rootHwnd) || !NativeMethods.IsWindow((nint)popupHwnd) ||
            !NativeMethods.GetWindowRect((nint)popupHwnd, out var popupRect))
            throw new ArgumentException("Popup window handle is not valid.", nameof(popupHwnd));

        var result = new List<AutomationObservation>(Math.Min(maxNodes, 4096));
        var visitedNodes = 0;
        CollectWindow(popupHwnd, maxNodes, maxDepth, result, ref visitedNodes);
        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        var popupSceneHwnd = WindowCatalog.GetRootOwnerHandle((nint)popupHwnd).ToInt64();
        if (popupSceneHwnd == 0) popupSceneHwnd = rootHwnd;

        // RawView navigation is incomplete for Office galleries. For example, the
        // Effects gallery paints fifteen tiles but TreeWalker exposes only the first
        // six. A descendant query scoped to the popup HWND provider returns the
        // remaining items without walking the Excel workbook or Ribbon.
        try
        {
            var popupRoot = AutomationElement.FromHandle((nint)popupHwnd);
            // GetWindowRect is DPI-virtualized in the isolated CLI worker while UIA
            // bounding rectangles are physical screen coordinates. Mixing the two
            // clipped Effects to its first 3 columns x 2 rows. Use the popup UIA root
            // as the coordinate authority for all ownership/containment checks.
            if (TryPhysicalPopupRect(popupRoot, out var physicalPopupRect))
                popupRect = physicalPopupRect;
            CollectProviderDescendants(popupRoot, popupHwnd, popupRect, maxNodes, result, known);
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }

        // Outlook Classic exposes a complete popup through its own NetUI provider,
        // but the application-wide provider also reports controls painted behind
        // that popup as descendants of the same menu root. Geometry cannot
        // distinguish the occluded controls. Trust the exact popup HWND once it
        // already contains meaningful Outlook items and avoid that false merge.
        if (IsOutlookWindow(rootHwnd) && HasMeaningfulNativePopup(result))
            return result;

        // Office value dropdowns (font, size, numeric choices) often expose only
        // a Menu shell through UIA while their values and scrollbar are available
        // immediately through the native accessibility bridge. Prefer that exact
        // popup-HWND tree over enumerating the entire Excel application provider.
        var nativePopup = MsaaDialogCollector.Collect(popupHwnd, popupRect, maxNodes, maxDepth);
        if (HasMeaningfulNativePopup(nativePopup))
            return nativePopup;

        // Fast path: Office commonly exposes the painted item through FromPoint
        // even when the popup HWND tree is only a shell.
        CollectWindowByHitTesting(popupSceneHwnd, popupHwnd, maxNodes, result);
        known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);

        // Excel exposes complete gallery contents through the application provider
        // even when the popup HWND provider stops after the first visible row. Query
        // interactive types only and accept an element only when it has a Menu
        // ancestor covering this popup. Geometry alone is deliberately insufficient,
        // so worksheet and Ribbon controls behind the popup cannot leak into it.
        try
        {
            var applicationRoot = AutomationElement.FromHandle((nint)rootHwnd);
            CollectApplicationPopupItems(applicationRoot, popupHwnd, popupRect, maxNodes, result, known);
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        return result;
    }

    private static bool HasMeaningfulNativePopup(IReadOnlyList<AutomationObservation> controls) =>
        controls.Count > 1 && controls.Any(control =>
            !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            !control.ControlType.EndsWith(".Document", StringComparison.OrdinalIgnoreCase) &&
            !control.ClassName.StartsWith("XLSpreadsheet", StringComparison.OrdinalIgnoreCase) &&
            !control.ClassName.StartsWith("XLGrid", StringComparison.OrdinalIgnoreCase) &&
            NormalizeControlType(control.ControlType) is
                "Button" or "CheckBox" or "ComboBox" or "DataItem" or "Edit" or "Hyperlink" or
                "List" or "ListItem" or "MenuItem" or "RadioButton" or "ScrollBar" or "Slider" or
                "Spinner" or "SplitButton" or "Text" or "Thumb" or "Tree" or "TreeItem");

    private static bool IsOutlookWindow(long hwnd)
    {
        try
        {
            var target = WindowCatalog.Resolve(hwnd);
            return target.ProcessName.Equals("OUTLOOK", StringComparison.OrdinalIgnoreCase) ||
                   target.OriginalFilename.Equals("OUTLOOK.EXE", StringComparison.OrdinalIgnoreCase) ||
                   target.ProductName.Contains("Microsoft Outlook", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string NormalizeControlType(string value) =>
        value.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase) ? value[12..] : value;

    public static IReadOnlyList<AutomationObservation> CollectDialogWindow(
        long applicationHwnd,
        long dialogHwnd,
        int maxNodes,
        int maxDepth = 64)
    {
        if (!IsSupportedNodeLimit(maxNodes) || maxDepth is < 1 or > 128)
            throw new ArgumentOutOfRangeException();
        if (!NativeMethods.IsWindow((nint)applicationHwnd) || !NativeMethods.IsWindow((nint)dialogHwnd) ||
            !NativeMethods.GetWindowRect((nint)dialogHwnd, out var dialogRect))
            throw new ArgumentException("Dialog window handle is not valid.", nameof(dialogHwnd));

        // Excel's bosa_sdm property dialogs are native MSAA surfaces. Their UIA
        // bridge takes about six seconds to enumerate and therefore cannot satisfy
        // the recorder's bounded transaction. Read the native accessibility tree
        // first; it exposes the same tabs, lists, fields and action buttons without
        // expanding the workbook provider.
        var dialogClass = WindowCatalog.GetClass((nint)dialogHwnd);
        var isOutlook = IsOutlookWindow(dialogHwnd);
        if (ShouldCollectOutlookPeerRootEvidence(dialogClass, isOutlook))
        {
            var outlookPeer = CollectOutlookPeerRootEvidence(
                applicationHwnd, dialogHwnd, dialogRect, maxNodes, []);
            if (outlookPeer.Count > 0)
                return outlookPeer;
        }
        var nativeDialog = ShouldCollectOutlookPeerRootEvidence(dialogClass, isOutlook)
            ? Array.Empty<AutomationObservation>()
            : MsaaDialogCollector.Collect(dialogHwnd, dialogRect, maxNodes, maxDepth);
        if (ShouldPreferNativeDialogEvidence(dialogClass, nativeDialog))
            return nativeDialog;

        var result = new List<AutomationObservation>(Math.Min(maxNodes, 2_048));
        var known = new HashSet<string>(StringComparer.Ordinal);

        AutomationElement dialogRoot;
        try
        {
            dialogRoot = AutomationElement.FromHandle((nint)dialogHwnd);
            if (TryPhysicalPopupRect(dialogRoot, out var physicalDialogRect))
                dialogRect = physicalDialogRect;
            AddObservedElement(dialogRoot, "", dialogHwnd, dialogRect, result, known);
            CollectFilteredProviderDescendants(
                dialogRoot, dialogHwnd, dialogRect, maxNodes, result, known, DialogControlCondition);
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex))
        {
            return result;
        }

        // Never walk Excel's full RawView here: bosa_sdm_XL9 can block the UIA
        // provider until the worker is killed. Native child HWNDs partition that
        // provider into small, bounded islands (MsoCommandBar, edit boxes, lists),
        // from which filtered UIA queries return the six tabs and their controls.
        // Most Office dialogs expose the complete filtered tree from the exact
        // dialog provider. Only probe native islands when that provider returned
        // a shell; querying every nested HWND again adds seconds of duplicate COM
        // calls and would exceed the recorder's four-second worker deadline.
        foreach (var childHwnd in (result.Count <= 4
                     ? WindowCatalog.ListDescendantHandles(dialogHwnd).Take(128)
                     : Enumerable.Empty<long>()))
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var childRoot = AutomationElement.FromHandle((nint)childHwnd);
                AddObservedElement(childRoot, RuntimeId(dialogRoot, dialogHwnd) ?? "", dialogHwnd,
                    dialogRect, result, known);
                CollectFilteredProviderDescendants(
                    childRoot, dialogHwnd, dialogRect, maxNodes, result, known, DialogControlCondition);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }

        return result;
    }

    private static bool HasMeaningfulNativeDialog(IReadOnlyList<AutomationObservation> controls) =>
        controls.Count > 1 && controls.Any(control =>
            !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            control.ControlType is "ControlType.Button" or "ControlType.CheckBox" or
                "ControlType.ComboBox" or "ControlType.Edit" or "ControlType.List" or
                "ControlType.ListItem" or "ControlType.RadioButton" or "ControlType.Tab" or
                "ControlType.TabItem" or "ControlType.Tree" or "ControlType.TreeItem");

    internal static bool ShouldPreferNativeDialogEvidence(
        string dialogClass,
        IReadOnlyList<AutomationObservation> controls) =>
        HasMeaningfulNativeDialog(controls) &&
        (dialogClass.StartsWith("bosa_sdm_", StringComparison.OrdinalIgnoreCase) ||
         controls.Any(control => control.ControlType == "ControlType.TabItem"));

    internal static bool ShouldCollectOutlookPeerRootEvidence(string dialogClass, bool isOutlook) =>
        isOutlook && dialogClass.Equals("rctrl_renwnd32", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AutomationObservation> CollectOutlookPeerRootEvidence(
        long applicationHwnd,
        long dialogHwnd,
        NativeMethods.Rect dialogRect,
        int maxNodes,
        IReadOnlyList<AutomationObservation> nativeDialog)
    {
        var result = nativeDialog.Take(maxNodes).ToList();
        if (result.Count >= maxNodes) return result;
        var known = result.Select(control => control.RuntimeId).ToHashSet(StringComparer.Ordinal);
        try
        {
            var applicationRoot = AutomationElement.FromHandle((nint)applicationHwnd);
            var region = FindOutlookFormRegion(applicationRoot, dialogRect);
            if (region is not null)
            {
                var rootRuntime = AddObservedElement(
                    region, "", dialogHwnd, dialogRect, result, known);
                if (rootRuntime is not null)
                    CollectFilteredProviderDescendants(
                        region, dialogHwnd, dialogRect, maxNodes, result, known, DialogControlCondition);
            }
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        return result;
    }

    private static AutomationElement? FindOutlookFormRegion(
        AutomationElement applicationRoot,
        NativeMethods.Rect dialogRect)
    {
        const int maxVisited = 512;
        const int maxDepth = 16;
        var visited = 0;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((applicationRoot, 0));
        while (queue.Count > 0 && visited++ < maxVisited)
        {
            var (element, depth) = queue.Dequeue();
            try
            {
                var current = element.Current;
                if ((current.AutomationId.Equals("258", StringComparison.Ordinal) ||
                     current.Name.Equals("Form Regions", StringComparison.OrdinalIgnoreCase)) &&
                    IsContainedInWindow(current.BoundingRectangle, dialogRect))
                    return element;
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { continue; }

            if (depth >= maxDepth) continue;
            try
            {
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                var childCount = 0;
                while (child is not null && childCount++ < 256 && queue.Count + visited < maxVisited)
                {
                    queue.Enqueue((child, depth + 1));
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
        return null;
    }



    private static bool IsWorksheetDialogContamination(string controlType, string className) =>
        controlType.EndsWith(".DataItem", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".Document", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("XLSpreadsheet", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("XLGrid", StringComparison.OrdinalIgnoreCase);

    private static void CollectApplicationPopupItems(
        AutomationElement applicationRoot,
        long hwnd,
        NativeMethods.Rect popupRect,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        if (result.Count >= maxNodes) return;
        var popupRootRuntime = result.FirstOrDefault(control =>
            string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
            IsPopupSurfaceType(control.ControlType))?.RuntimeId
            ?? result.FirstOrDefault()?.RuntimeId
            ?? "";
        AutomationElementCollection items;
        // Do not enumerate Excel's entire RawView (including the worksheet) twice
        // for UIA A/B. Asking the provider only for interactive popup types keeps
        // the readiness transaction fast while the popup-surface ancestor check below still
        // enforces ownership and rejects worksheet contamination.
        try { items = applicationRoot.FindAll(TreeScope.Descendants, PopupInteractiveCondition); }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }
        foreach (AutomationElement item in items)
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var properties = item.Current;
                if (!IsPopupItemType(properties.ControlType?.ProgrammaticName ?? "")) continue;
                if (!IsContainedInWindow(properties.BoundingRectangle, popupRect)) continue;
                if (!HasPopupSurfaceAncestor(item, popupRect)) continue;
                AddObservedElement(item, popupRootRuntime, hwnd, popupRect, result, known);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    private static bool TryPhysicalPopupRect(AutomationElement popupRoot, out NativeMethods.Rect rect)
    {
        rect = default;
        try
        {
            var bounds = popupRoot.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
            rect = new NativeMethods.Rect
            {
                Left = (int)Math.Floor(bounds.Left),
                Top = (int)Math.Floor(bounds.Top),
                Right = (int)Math.Ceiling(bounds.Right),
                Bottom = (int)Math.Ceiling(bounds.Bottom)
            };
            return rect.Right > rect.Left && rect.Bottom > rect.Top;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return false; }
    }

    internal static double PopupCoverage(System.Windows.Rect bounds, NativeMethods.Rect popupRect)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return 0;
        var left = Math.Max(bounds.Left, popupRect.Left);
        var top = Math.Max(bounds.Top, popupRect.Top);
        var right = Math.Min(bounds.Right, popupRect.Right);
        var bottom = Math.Min(bounds.Bottom, popupRect.Bottom);
        if (right <= left || bottom <= top) return 0;
        var popupArea = Math.Max(1d, (popupRect.Right - popupRect.Left) * (double)(popupRect.Bottom - popupRect.Top));
        return (right - left) * (bottom - top) / popupArea;
    }

    private static void CollectWindowByHitTesting(
        long rootHwnd,
        long hwnd,
        int maxNodes,
        List<AutomationObservation> result)
    {
        if (!NativeMethods.GetWindowRect((nint)hwnd, out var windowRect) ||
            windowRect.Right <= windowRect.Left || windowRect.Bottom <= windowRect.Top)
            return;

        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        AutomationElement? hitRoot = null;
        var probePoints = new[]
        {
            new NativeMethods.Point(windowRect.Left + width / 2, windowRect.Top + Math.Min(12, height / 2)),
            new NativeMethods.Point(windowRect.Left + width / 2, windowRect.Top + height / 4),
            new NativeMethods.Point(windowRect.Left + width / 2, windowRect.Top + height / 2),
            new NativeMethods.Point(windowRect.Left + width / 2, windowRect.Top + height * 3 / 4),
            new NativeMethods.Point(windowRect.Left + width / 2, windowRect.Bottom - Math.Min(12, height / 2) - 1)
        };
        foreach (var point in probePoints)
        {
            if (!BelongsToRootScene(rootHwnd, point))
                continue;
            try
            {
                hitRoot = FindHitTreeRoot(AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y)), windowRect);
                if (hitRoot is not null) break;
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }

        if (hitRoot is not null)
        {
            CollectDerivedSubtree(hitRoot, hwnd, windowRect, maxNodes, result, known);
            CollectProviderDescendants(hitRoot, hwnd, windowRect, maxNodes, result, known);
        }

        // Office galleries can expose a valid root and a handful of toolbar buttons
        // while omitting every visible row from tree navigation. Sample both the item
        // column and the action/icon column even when the shallow tree is non-empty.
        if (result.Count < Math.Min(24, maxNodes))
        {
            foreach (var point in PopupSamplingPoints(new(
                         windowRect.Left,
                         windowRect.Top,
                         width,
                         height)))
            {
                if (result.Count >= maxNodes) break;
                if (!BelongsToRootScene(rootHwnd, point))
                    continue;
                try
                {
                    var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                    var item = FindPopupItem(hit, windowRect);
                    if (item is not null && HasPopupSurfaceAncestor(item, windowRect))
                        AddObservedElement(item, result.FirstOrDefault()?.RuntimeId ?? "", hwnd, windowRect, result, known);
                }
                catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
            }
        }
    }

    private static bool HasPopupSurfaceAncestor(AutomationElement element, NativeMethods.Rect windowRect)
    {
        var current = element;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            try
            {
                var properties = current.Current;
                if (IsPopupSurfaceType(properties.ControlType?.ProgrammaticName ?? "") &&
                    PopupCoverage(properties.BoundingRectangle, windowRect) >= 0.72)
                    return true;
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return false; }
        }
        return false;
    }

    private static bool BelongsToRootScene(long rootHwnd, NativeMethods.Point point)
    {
        try
        {
            var pointed = NativeMethods.WindowFromPoint(point);
            return pointed != 0 && NativeMethods.IsWindow(pointed) &&
                   WindowCatalog.GetRootOwnerHandle(pointed).ToInt64() == rootHwnd;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static IReadOnlyList<NativeMethods.Point> PopupSamplingPoints(RectI bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return [];
        var left = bounds.X;
        var top = bounds.Y;
        var right = checked(bounds.X + bounds.Width);
        var bottom = checked(bounds.Y + bounds.Height);
        int[] xPositions = bounds.Width < 120
            ? [left + bounds.Width / 2]
            : [left + bounds.Width / 3, right - Math.Max(8, bounds.Width / 8)];
        var yStep = Math.Clamp(bounds.Height / 40, 16, 26);
        var points = new List<NativeMethods.Point>(Math.Max(2, bounds.Height / yStep * xPositions.Length));
        for (var y = top + Math.Min(8, bounds.Height / 2); y < bottom - 3; y += yStep)
            foreach (var x in xPositions)
                points.Add(new(x, y));
        return points;
    }

    private static AutomationElement? FindPopupItem(AutomationElement element, NativeMethods.Rect windowRect)
    {
        var current = element;
        for (var depth = 0; depth < 10 && current is not null; depth++)
        {
            try
            {
                var properties = current.Current;
                if (!IsContainedInWindow(properties.BoundingRectangle, windowRect)) return null;
                if (IsPopupItemType(properties.ControlType?.ProgrammaticName ?? "")) return current;
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return null; }
        }
        return null;
    }

    private static bool IsPopupItemType(string controlType) =>
        controlType.EndsWith(".Button", StringComparison.Ordinal) ||
        controlType.EndsWith(".CheckBox", StringComparison.Ordinal) ||
        controlType.EndsWith(".ComboBox", StringComparison.Ordinal) ||
        controlType.EndsWith(".DataItem", StringComparison.Ordinal) ||
        controlType.EndsWith(".Edit", StringComparison.Ordinal) ||
        controlType.EndsWith(".Hyperlink", StringComparison.Ordinal) ||
        controlType.EndsWith(".List", StringComparison.Ordinal) ||
        controlType.EndsWith(".ListItem", StringComparison.Ordinal) ||
        controlType.EndsWith(".MenuItem", StringComparison.Ordinal) ||
        controlType.EndsWith(".RadioButton", StringComparison.Ordinal) ||
        controlType.EndsWith(".ScrollBar", StringComparison.Ordinal) ||
        controlType.EndsWith(".Slider", StringComparison.Ordinal) ||
        controlType.EndsWith(".Spinner", StringComparison.Ordinal) ||
        controlType.EndsWith(".SplitButton", StringComparison.Ordinal) ||
        controlType.EndsWith(".TabItem", StringComparison.Ordinal) ||
        controlType.EndsWith(".Text", StringComparison.Ordinal) ||
        controlType.EndsWith(".Thumb", StringComparison.Ordinal) ||
        controlType.EndsWith(".Tree", StringComparison.Ordinal) ||
        controlType.EndsWith(".TreeItem", StringComparison.Ordinal) ||
        controlType.EndsWith(".Custom", StringComparison.Ordinal);

    private static bool IsPopupSurfaceType(string controlType) =>
        controlType.EndsWith(".Menu", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".List", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".Tree", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".Window", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".Pane", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".Custom", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".ToolBar", StringComparison.OrdinalIgnoreCase);

    private static AutomationElement? FindHitTreeRoot(AutomationElement element, NativeMethods.Rect windowRect)
    {
        AutomationElement? surfaceCandidate = null;
        var current = element;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            System.Windows.Rect bounds;
            ControlType? type;
            try
            {
                var properties = current.Current;
                bounds = properties.BoundingRectangle;
                type = properties.ControlType;
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { break; }
            var coverage = PopupCoverage(bounds, windowRect);
            if (!IsContainedInWindow(bounds, windowRect) && coverage < 0.72) break;
            if (IsPopupSurfaceType(type?.ProgrammaticName ?? "") && coverage >= 0.72)
                surfaceCandidate = current;
            try { current = TreeWalker.RawViewWalker.GetParent(current); }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { break; }
        }
        return surfaceCandidate;
    }

    private static void CollectDerivedSubtree(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        var queue = new Queue<(AutomationElement Element, string ParentRuntime, int Depth)>();
        queue.Enqueue((root, result.FirstOrDefault()?.RuntimeId ?? "", 0));
        while (queue.Count > 0 && result.Count < maxNodes)
        {
            var (element, parentRuntime, depth) = queue.Dequeue();
            var runtime = AddObservedElement(element, parentRuntime, hwnd, windowRect, result, known) ?? parentRuntime;
            if (depth >= 32) continue;
            try
            {
                var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                var childCount = 0;
                while (child is not null && childCount++ < 512 && result.Count + queue.Count < maxNodes)
                {
                    queue.Enqueue((child, runtime, depth + 1));
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    private static void CollectProviderDescendants(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        if (result.Count >= maxNodes) return;
        AutomationElementCollection descendants;
        try { descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }

        var rootRuntime = RuntimeId(root, hwnd) ?? result.FirstOrDefault()?.RuntimeId ?? "";
        foreach (AutomationElement item in descendants)
        {
            if (result.Count >= maxNodes) break;
            var parentRuntime = rootRuntime;
            try
            {
                var parent = TreeWalker.RawViewWalker.GetParent(item);
                if (parent is not null && IsContainedInWindow(parent.Current.BoundingRectangle, windowRect))
                    parentRuntime = RuntimeId(parent, hwnd) ?? parentRuntime;
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
            AddObservedElement(item, parentRuntime, hwnd, windowRect, result, known);
        }
    }

    private static void CollectFilteredProviderDescendants(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known,
        Condition condition)
    {
        if (result.Count >= maxNodes) return;
        AutomationElementCollection descendants;
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeScope = TreeScope.Element,
            TreeFilter = Condition.TrueCondition
        };
        cache.Add(AutomationElement.RuntimeIdProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.FrameworkIdProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        cache.Add(SelectionItemPattern.IsSelectedProperty);
        cache.Add(TogglePattern.ToggleStateProperty);
        cache.Add(ExpandCollapsePattern.ExpandCollapseStateProperty);
        try
        {
            using (cache.Activate())
                descendants = root.FindAll(TreeScope.Descendants, condition);
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }

        var rootRuntime = RuntimeId(root, hwnd) ?? result.FirstOrDefault()?.RuntimeId ?? "";
        foreach (AutomationElement item in descendants)
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var properties = item.Cached;
                if (IsWorksheetDialogContamination(
                        properties.ControlType?.ProgrammaticName ?? "", properties.ClassName ?? ""))
                    continue;
                AddCachedDialogElement(item, rootRuntime, hwnd, windowRect, result, known);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    private static void AddCachedDialogElement(
        AutomationElement item,
        string parentRuntime,
        long hwnd,
        NativeMethods.Rect windowRect,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        try
        {
            var properties = item.Cached;
            var bounds = properties.BoundingRectangle;
            if (!IsContainedInWindow(bounds, windowRect)) return;
            var runtimeParts = item.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[];
            var runtime = runtimeParts is { Length: > 0 }
                ? Clamp($"{hwnd:x}." + string.Join('.', runtimeParts), 4_096)
                : Clamp($"{hwnd:x}.dialog.{properties.ControlType?.Id}:{properties.AutomationId}:{bounds.X:F0},{bounds.Y:F0},{bounds.Width:F0},{bounds.Height:F0}", 4_096);
            if (!known.Add(runtime)) return;
            var controlType = properties.ControlType?.ProgrammaticName ?? "";
            var name = controlType.EndsWith(".Edit", StringComparison.Ordinal) ||
                       controlType.EndsWith(".Document", StringComparison.Ordinal)
                ? "[redacted]"
                : properties.Name ?? "";
            var patterns = DialogPatternNames(controlType);
            var selectedValue = item.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true);
            var toggleValue = item.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true);
            var expandValue = item.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty, true);
            result.Add(new(runtime, parentRuntime, Clamp(properties.AutomationId, 512), Clamp(name, 4_096),
                Clamp(controlType, 256), Clamp(properties.ClassName, 512), ToRect(bounds), properties.IsEnabled,
                properties.IsOffscreen, Clamp(properties.FrameworkId, 128), hwnd, patterns,
                properties.HasKeyboardFocus, selectedValue is bool selected && selected,
                toggleValue is ToggleState toggle ? toggle.ToString() : null,
                expandValue is ExpandCollapseState expand ? expand.ToString() : null));
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
    }

    private static IReadOnlyList<string> DialogPatternNames(string controlType)
    {
        if (controlType.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase) ||
            controlType.EndsWith(".ListItem", StringComparison.OrdinalIgnoreCase) ||
            controlType.EndsWith(".RadioButton", StringComparison.OrdinalIgnoreCase))
            return [SelectionItemPattern.Pattern.ProgrammaticName];
        if (controlType.EndsWith(".CheckBox", StringComparison.OrdinalIgnoreCase))
            return [TogglePattern.Pattern.ProgrammaticName];
        if (controlType.EndsWith(".ComboBox", StringComparison.OrdinalIgnoreCase))
            return [ExpandCollapsePattern.Pattern.ProgrammaticName, ValuePattern.Pattern.ProgrammaticName];
        if (controlType.EndsWith(".Edit", StringComparison.OrdinalIgnoreCase))
            return [ValuePattern.Pattern.ProgrammaticName];
        if (controlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) ||
            controlType.EndsWith(".Hyperlink", StringComparison.OrdinalIgnoreCase))
            return [InvokePattern.Pattern.ProgrammaticName];
        if (controlType.EndsWith(".ScrollBar", StringComparison.OrdinalIgnoreCase) ||
            controlType.EndsWith(".Slider", StringComparison.OrdinalIgnoreCase) ||
            controlType.EndsWith(".Spinner", StringComparison.OrdinalIgnoreCase))
            return [RangeValuePattern.Pattern.ProgrammaticName];
        return [];
    }

    private static string? RuntimeId(AutomationElement item, long hwnd)
    {
        try { return Clamp($"{hwnd:x}." + string.Join('.', item.GetRuntimeId()), 4_096); }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return null; }
    }

    private static string? AddObservedElement(
        AutomationElement item,
        string parentRuntime,
        long hwnd,
        NativeMethods.Rect windowRect,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        try
        {
            var properties = item.Current;
            var bounds = properties.BoundingRectangle;
            if (!IsContainedInWindow(bounds, windowRect)) return null;
            var runtime = Clamp($"{hwnd:x}." + string.Join('.', item.GetRuntimeId()), 4_096);
            if (!known.Add(runtime)) return runtime;
            var controlType = properties.ControlType?.ProgrammaticName ?? "";
            var name = controlType.EndsWith(".Edit", StringComparison.Ordinal) ||
                controlType.EndsWith(".Document", StringComparison.Ordinal)
                ? "[redacted]"
                : properties.Name ?? "";
            var patterns = item.GetSupportedPatterns()
                .Select(pattern => Clamp(pattern.ProgrammaticName, 256))
                .Where(pattern => pattern.Length > 0)
                .Order(StringComparer.Ordinal)
                .Take(32)
                .ToArray();
            result.Add(new(runtime, parentRuntime, Clamp(properties.AutomationId, 512), Clamp(name, 4_096),
                Clamp(controlType, 256), Clamp(properties.ClassName, 512), ToRect(bounds), properties.IsEnabled,
                properties.IsOffscreen, Clamp(properties.FrameworkId, 128), hwnd, patterns,
                properties.HasKeyboardFocus, TryGetSelectionState(item), TryGetToggleState(item),
                TryGetExpandCollapseState(item)));
            return runtime;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return null; }
    }

    private static bool IsContainedInWindow(System.Windows.Rect bounds, NativeMethods.Rect windowRect) =>
        !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
        bounds.Left >= windowRect.Left - 6 && bounds.Top >= windowRect.Top - 6 &&
        bounds.Right <= windowRect.Right + 6 && bounds.Bottom <= windowRect.Bottom + 6;

    private static bool IsRecoverableAutomationException(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException;

    public static IReadOnlyList<AutomationObservation> CollectNavigationWindow(long hwnd, int maxNodes)
    {
        if (!IsSupportedNodeLimit(maxNodes)) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var windowRect))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));

        var root = AutomationElement.FromHandle((nint)hwnd);
        if (IsRevitRoot(root) && TryCollectRevitNavigation(root, hwnd, windowRect, maxNodes, out var revitNavigation))
            return revitNavigation;

        var navigationTypes = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        var matches = root.FindAll(TreeScope.Descendants, navigationTypes);
        var topLimit = windowRect.Top + Math.Max(120, (int)Math.Round((windowRect.Bottom - windowRect.Top) * 0.30));
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 256));
        foreach (AutomationElement element in matches)
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var current = element.Current;
                var rect = current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 || rect.Top >= topLimit ||
                    rect.Left < windowRect.Left - 4 || rect.Left >= windowRect.Right)
                    continue;
                var runtime = Clamp($"{hwnd:x}." + string.Join('.', element.GetRuntimeId()), 4_096);
                result.Add(new(
                    runtime,
                    "",
                    Clamp(current.AutomationId, 512),
                    Clamp(current.Name, 4_096),
                    Clamp(current.ControlType?.ProgrammaticName, 256),
                    Clamp(current.ClassName, 512),
                    ToRect(rect),
                    current.IsEnabled,
                    current.IsOffscreen,
                    Clamp(current.FrameworkId, 128),
                    hwnd,
                    [],
                    current.HasKeyboardFocus,
                    TryGetSelectionState(element)));
            }
            catch (ElementNotAvailableException) { }
        }
        return result;
    }

    public static IReadOnlyList<AutomationObservation> CollectRibbonWindow(long hwnd, int maxNodes)
    {
        if (!IsSupportedNodeLimit(maxNodes)) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var windowRect))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));

        var root = AutomationElement.FromHandle((nint)hwnd);
        var isRevit = IsRevitRoot(root);
        if (isRevit && TryCollectRevitRibbon(root, hwnd, windowRect, maxNodes, out var revitRibbon))
            return revitRibbon;
        var topLimit = windowRect.Top + Math.Max(180, (int)Math.Round((windowRect.Bottom - windowRect.Top) * 0.34));
        // GetWindowRect is DPI-virtualized inside the isolated worker, while UIA
        // reports physical coordinates. Use only the UIA horizontal extent for
        // traversal so rightmost Ribbon groups (for example Find & Select) are not
        // pruned, without extending the vertical walk into the worksheet.
        var horizontalLeft = windowRect.Left;
        var horizontalRight = windowRect.Right;
        if (TryPhysicalPopupRect(root, out var physicalWindowRect))
        {
            horizontalLeft = physicalWindowRect.Left;
            horizontalRight = physicalWindowRect.Right;
        }
        var result = new List<AutomationObservation>(Math.Min(maxNodes, 600));
        var queue = new Queue<(AutomationElement Element, string ParentRuntime, int Depth)>();
        var traversalRoot = isRevit ? FindRevitRibbonHost(root) ?? root : root;
        queue.Enqueue((traversalRoot, "", 0));
        var visited = 0;
        var visitLimit = Math.Max(1_200, maxNodes * 8);
        while (queue.Count > 0 && result.Count < maxNodes && visited++ < visitLimit)
        {
            var (element, parentRuntime, depth) = queue.Dequeue();
            string runtime = parentRuntime;
            var traverseChildren = depth == 0;
            try
            {
                var current = element.Current;
                var rect = current.BoundingRectangle;
                runtime = Clamp($"{hwnd:x}." + string.Join('.', element.GetRuntimeId()), 4_096);
                traverseChildren = depth == 0 || rect.IsEmpty || (rect.Bottom > windowRect.Top && rect.Top < topLimit &&
                    rect.Right > horizontalLeft && rect.Left < horizontalRight);
                var type = current.ControlType;
                if (traverseChildren && type is not null && IsRibbonControlType(type) &&
                    !rect.IsEmpty && rect.Width > 0 && rect.Height > 0 && rect.Top < topLimit)
                {
                    var patterns = element.GetSupportedPatterns()
                        .Select(pattern => Clamp(pattern.ProgrammaticName, 256))
                        .Where(pattern => pattern.Length > 0)
                        .Order(StringComparer.Ordinal)
                        .Take(32)
                        .ToArray();
                    result.Add(new(runtime, parentRuntime, Clamp(current.AutomationId, 512), Clamp(current.Name, 4_096),
                        Clamp(type.ProgrammaticName, 256), Clamp(current.ClassName, 512), ToRect(rect), current.IsEnabled,
                        current.IsOffscreen, Clamp(current.FrameworkId, 128), hwnd, patterns, current.HasKeyboardFocus,
                        TryGetSelectionState(element), TryGetToggleState(element), TryGetExpandCollapseState(element)));
                }
            }
            catch (ElementNotAvailableException) { }

            if (!traverseChildren || depth >= 16) continue;
            try
            {
                var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                var childCount = 0;
                while (child is not null && childCount++ < 256 && visited + queue.Count < visitLimit)
                {
                    queue.Enqueue((child, runtime, depth + 1));
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { }
        }
        // Keep the bounded Ribbon traversal on its existing rectangle and use
        // physical UIA coordinates only for the right-side chrome query. Changing
        // the whole traversal to the wider physical rectangle expands Excel's raw
        // tree and needlessly adds roughly half a second.
        if (!isRevit)
        {
            var chromeWindowRect = TryPhysicalPopupRect(root, out var physicalChromeWindowRect)
                ? physicalChromeWindowRect
                : windowRect;
            AppendTopChromeControls(root, hwnd, chromeWindowRect, maxNodes, result);
            RemoveRedundantLegacyTitleBarControls(result);
            AppendFormulaBarControls(hwnd, windowRect, maxNodes, result);
        }
        return result;
    }

    private static bool TryCollectRevitNavigation(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        out IReadOnlyList<AutomationObservation> navigation)
    {
        navigation = [];
        var tabs = FindDirectChild(root, element =>
            element.Current.AutomationId.Equals("mMainTabs", StringComparison.OrdinalIgnoreCase), 16);
        if (tabs is null) return false;

        var result = new List<AutomationObservation>(Math.Min(maxNodes, 32));
        var parentRuntime = RuntimeId(tabs, hwnd) ?? string.Empty;
        CollectCachedRevitButtons(tabs, TreeScope.Children, parentRuntime, hwnd, windowRect, maxNodes, result);

        navigation = result
            .Where(control => !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .ToArray();
        return navigation.Count > 0;
    }

    private static bool TryCollectRevitRibbon(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        out IReadOnlyList<AutomationObservation> ribbon)
    {
        ribbon = [];
        var host = FindRevitRibbonHost(root);
        if (host is null) return false;

        var result = new List<AutomationObservation>(Math.Min(maxNodes, 1_024));
        var parentRuntime = RuntimeId(host, hwnd) ?? string.Empty;
        var selectedPanel = AppendRevitSelectedPanelAnchor(host, parentRuntime, hwnd, windowRect, result);

        // Revit virtualizes the Ribbon below one materialized PanelBarScrollViewer.
        // Asking the Ribbon host for every descendant makes the WPF provider expand
        // all inactive tabs; on real projects that request can block for a minute
        // and the isolated worker is killed before it can return even the controls
        // it already found. FromPoint above gives us the selected panel, so descend
        // only through that small, visible subtree. This is the coarse-to-fine step:
        // the application tree locates the gap, then only the gap is expanded.
        if (selectedPanel is not null)
        {
            var selectedPanelRuntime = RuntimeId(selectedPanel, hwnd) ?? parentRuntime;
            CollectCachedRevitControls(
                selectedPanel,
                TreeScope.Descendants,
                selectedPanelRuntime,
                hwnd,
                windowRect,
                maxNodes,
                result);
        }

        // Navigation is a separate, shallow Revit subtree. Keep it in the same
        // surface without re-running the expensive whole-host descendant query.
        if (TryCollectRevitNavigation(root, hwnd, windowRect, maxNodes, out var navigation))
            AppendDistinctObservations(result, navigation, maxNodes);
        // Keep this request limited to the Ribbon. Revit serves the Properties,
        // Project Browser and status-bar providers independently; folding those
        // slower trees into the same worker used to discard an otherwise valid
        // Ribbon result whenever one peripheral provider stalled. The adaptive
        // cascade probes those regions separately and merges their evidence.
        ribbon = result;
        return ribbon.Count > 0;
    }

    private static void AppendDistinctObservations(
        List<AutomationObservation> result,
        IReadOnlyList<AutomationObservation> additions,
        int maxNodes)
    {
        if (result.Count >= maxNodes || additions.Count == 0) return;
        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in additions)
        {
            if (result.Count >= maxNodes) break;
            if (known.Add(item.RuntimeId)) result.Add(item);
        }
    }

    private static void AppendRevitStatusBarControls(
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        if (result.Count >= maxNodes) return;

        var applicationBounds = new RectI(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (var statusHwnd in WindowCatalog.ListDescendantHandles(hwnd, 1_024)
                     .Where(candidate => IsRevitStatusBarWindow(
                         WindowCatalog.GetClass((nint)candidate),
                         WindowBounds(candidate),
                         applicationBounds))
                     .Take(2))
        {
            var statusBounds = WindowBounds(statusHwnd);
            foreach (var childHwnd in WindowCatalog.ListDescendantHandles(statusHwnd, 128))
            {
                if (result.Count >= maxNodes) return;
                var childClass = WindowCatalog.GetClass((nint)childHwnd);
                var childBounds = WindowBounds(childHwnd);
                if (!IsRevitStatusBarControl(childClass, childBounds, statusBounds) ||
                    !NativeMethods.IsWindowVisible((nint)childHwnd))
                    continue;

                try
                {
                    var element = AutomationElement.FromHandle((nint)childHwnd);
                    var runtime = AddObservedElement(element, "", hwnd, windowRect, result, known);
                    if (runtime is null) continue;

                    var index = result.FindIndex(control => control.RuntimeId == runtime);
                    if (index < 0) continue;
                    var controlType = childClass.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
                        ? ControlType.ComboBox.ProgrammaticName
                        : ControlType.Button.ProgrammaticName;
                    result[index] = result[index] with
                    {
                        ControlType = controlType,
                        ClassName = childClass,
                        IsEnabled = NativeMethods.IsWindowEnabled((nint)childHwnd),
                        IsOffscreen = false,
                        FrameworkId = "Win32",
                        SupportedPatterns = SuggestedRevitPatterns(controlType, result[index].AutomationId)
                    };
                }
                catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
            }
        }
    }

    internal static bool IsRevitStatusBarWindow(string className, RectI candidate, RectI application)
    {
        if (!className.Equals("msctls_statusbar32", StringComparison.OrdinalIgnoreCase) ||
            candidate.Width <= 0 || candidate.Height is < 12 or > 80 ||
            application.Width <= 0 || application.Height <= 0)
            return false;

        var applicationBottom = (long)application.Y + application.Height;
        var candidateBottom = (long)candidate.Y + candidate.Height;
        return candidate.Width >= application.Width * 0.50 &&
               candidate.Y >= application.Y + application.Height * 0.82 &&
               candidateBottom >= applicationBottom - 96 &&
               candidateBottom <= applicationBottom + 8;
    }

    internal static bool IsRevitStatusBarControl(string className, RectI candidate, RectI statusBar) =>
        (className.Equals("Button", StringComparison.OrdinalIgnoreCase) ||
         className.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)) &&
        candidate.Width > 0 && candidate.Height > 0 &&
        IsContainedInBounds(candidate, statusBar);

    private static void AppendVisibleRevitPeripheralControls(
        AutomationElement root,
        AutomationElement ribbonHost,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        AutomationElement? child;
        try { child = TreeWalker.RawViewWalker.GetFirstChild(root); }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }

        var rootWidth = Math.Max(1, windowRect.Right - windowRect.Left);
        for (var index = 0; child is not null && index < 96 && result.Count < maxNodes; index++)
        {
            var next = default(AutomationElement);
            try
            {
                next = TreeWalker.RawViewWalker.GetNextSibling(child);
                if (Automation.Compare(child, ribbonHost))
                {
                    child = next;
                    continue;
                }

                var current = child.Current;
                var bounds = current.BoundingRectangle;
                var type = current.ControlType;
                var isVisibleSidePanel = !bounds.IsEmpty && bounds.Width > 0 && bounds.Height >= 40 &&
                                         bounds.Width <= rootWidth * 0.35;
                var isDocumentTabHost = type == ControlType.Tab && !bounds.IsEmpty;
                if (isVisibleSidePanel || isDocumentTabHost)
                {
                    var parentRuntime = RuntimeId(child, hwnd) ?? string.Empty;
                    CollectCachedRevitControls(
                        child, TreeScope.Descendants, parentRuntime, hwnd, windowRect, maxNodes, result);
                }

                if (type == ControlType.Button && IsContainedInWindow(bounds, windowRect))
                {
                    var runtime = RuntimeId(child, hwnd) ?? string.Empty;
                    if (runtime.Length > 0 && result.All(item => item.RuntimeId != runtime))
                    {
                        result.Add(new(runtime, RuntimeId(root, hwnd) ?? string.Empty,
                            Clamp(current.AutomationId, 512), Clamp(current.Name, 4_096),
                            ControlType.Button.ProgrammaticName, Clamp(current.ClassName, 512), ToRect(bounds),
                            current.IsEnabled, current.IsOffscreen, Clamp(current.FrameworkId, 128), hwnd,
                            [InvokePattern.Pattern.ProgrammaticName], current.HasKeyboardFocus));
                    }
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
            child = next;
        }

        AppendRevitPropertyGridRows(hwnd, windowRect, maxNodes, result);
    }

    private static void AppendRevitPropertyGridRows(
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        var dpi = NativeMethods.GetDpiForWindow((nint)hwnd);
        foreach (var gridHwnd in WindowCatalog.ListDescendantHandles(hwnd, 1_024))
        {
            if (result.Count >= maxNodes ||
                !WindowCatalog.GetClass((nint)gridHwnd).Equals("GXWND", StringComparison.OrdinalIgnoreCase) ||
                !NativeMethods.IsWindowVisible((nint)gridHwnd) ||
                !NativeMethods.GetWindowRect((nint)gridHwnd, out var nativeBounds))
                continue;

            var gridBounds = new RectI(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right - nativeBounds.Left,
                nativeBounds.Bottom - nativeBounds.Top);
            if (gridBounds.Width < 180 || gridBounds.Height < 80 ||
                gridBounds.X < windowRect.Left || gridBounds.Y < windowRect.Top ||
                gridBounds.X + gridBounds.Width > windowRect.Right ||
                gridBounds.Y + gridBounds.Height > windowRect.Bottom ||
                gridBounds.X > windowRect.Left + (windowRect.Right - windowRect.Left) * 0.40)
                continue;

            var parentRuntime = $"{hwnd:x}.revit-property-grid.{gridHwnd:x}";
            var rows = RevitPropertyRowBounds(gridBounds, dpi == 0 ? 96u : dpi);
            for (var rowIndex = 0; rowIndex < rows.Count && result.Count < maxNodes; rowIndex++)
            {
                var rowBounds = rows[rowIndex];
                var runtime = $"{parentRuntime}.row-{rowIndex:D2}";
                if (result.Any(item => item.RuntimeId == runtime)) continue;
                result.Add(new(
                    runtime,
                    parentRuntime,
                    $"revit-property-row-{rowIndex:D2}",
                    $"Property row {rowIndex + 1}",
                    ControlType.DataItem.ProgrammaticName,
                    "RevitPropertyGridRow",
                    rowBounds,
                    IsEnabled: false,
                    IsOffscreen: true,
                    FrameworkId: "UiAtlas.Estimated",
                    WindowHwnd: hwnd,
                    SupportedPatterns: [SelectionItemPattern.Pattern.ProgrammaticName]));
            }
        }
    }

    internal static IReadOnlyList<RectI> RevitPropertyRowBounds(RectI gridBounds, uint dpi)
    {
        if (gridBounds.Width < 1 || gridBounds.Height < 1) return [];
        var scale = Math.Clamp(dpi / 96d, 0.75d, 4d);
        // GXWND is owner-drawn: UIA exposes the grid HWND but not its rows. Its
        // screen coordinates are already physical, so use Revit's 20-DIP row and
        // 17-DIP scrollbar metrics once (rather than applying the older, oversized
        // estimate). The grid also has a small top gutter before the first header.
        var topInset = Math.Clamp((int)Math.Round(5d * scale), 4, 16);
        var rowHeight = Math.Clamp((int)Math.Round(20d * scale), 16, 56);
        var scrollbarWidth = Math.Clamp((int)Math.Round(17d * scale), 14, 48);
        var contentWidth = Math.Max(1, gridBounds.Width - scrollbarWidth);
        var contentHeight = Math.Max(0, gridBounds.Height - topInset);
        var rowCount = Math.Min(32, (contentHeight + rowHeight / 2) / rowHeight);
        var rows = new List<RectI>(rowCount);
        for (var index = 0; index < rowCount; index++)
        {
            var y = gridBounds.Y + topInset + index * rowHeight;
            rows.Add(new RectI(gridBounds.X, y, contentWidth, Math.Min(rowHeight, gridBounds.Y + gridBounds.Height - y)));
        }
        return rows;
    }

    private static void CollectCachedRevitControls(
        AutomationElement container,
        TreeScope scope,
        string parentRuntime,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        AutomationElementCollection matches;
        var cache = CreateRevitCacheRequest();
        try
        {
            using (cache.Activate())
                matches = container.FindAll(scope, RevitVisibleControlCondition);
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }

        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (AutomationElement item in matches)
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var current = item.Cached;
                var bounds = current.BoundingRectangle;
                if (!IsContainedInWindow(bounds, windowRect) || current.IsOffscreen) continue;
                var runtimeParts = item.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[];
                var runtime = runtimeParts is { Length: > 0 }
                    ? Clamp($"{hwnd:x}." + string.Join('.', runtimeParts), 4_096)
                    : Clamp($"{hwnd:x}.revit.{current.AutomationId}:{bounds.X:F0},{bounds.Y:F0}", 4_096);
                if (!known.Add(runtime)) continue;
                var controlType = current.ControlType?.ProgrammaticName ?? string.Empty;
                result.Add(new(runtime, parentRuntime, Clamp(current.AutomationId, 512), Clamp(current.Name, 4_096),
                    Clamp(controlType, 256), Clamp(current.ClassName, 512), ToRect(bounds), current.IsEnabled,
                    current.IsOffscreen, Clamp(current.FrameworkId, 128), hwnd,
                    SuggestedRevitPatterns(controlType, current.AutomationId), current.HasKeyboardFocus));
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    private static CacheRequest CreateRevitCacheRequest()
    {
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeScope = TreeScope.Element,
            TreeFilter = Condition.TrueCondition
        };
        cache.Add(AutomationElement.RuntimeIdProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.FrameworkIdProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        return cache;
    }

    internal static IReadOnlyList<string> SuggestedRevitPatterns(string controlType, string automationId)
    {
        var type = NormalizeControlType(controlType);
        if (type == "Button") return [InvokePattern.Pattern.ProgrammaticName];
        if (type is "ComboBox" or "SplitButton") return [ExpandCollapsePattern.Pattern.ProgrammaticName];
        if (type is "TreeItem") return [SelectionItemPattern.Pattern.ProgrammaticName, ExpandCollapsePattern.Pattern.ProgrammaticName];
        if (type is "TabItem" or "ListItem" or "DataItem") return [SelectionItemPattern.Pattern.ProgrammaticName];
        if (type is "CheckBox" or "RadioButton") return [TogglePattern.Pattern.ProgrammaticName];
        if (type == "Edit") return [ValuePattern.Pattern.ProgrammaticName];
        if (type is "ScrollBar" or "Slider" or "Spinner") return [RangeValuePattern.Pattern.ProgrammaticName];
        if (type is "MenuItem" or "Hyperlink") return [InvokePattern.Pattern.ProgrammaticName];
        return [];
    }

    private static AutomationElement? AppendRevitSelectedPanelAnchor(
        AutomationElement host,
        string hostRuntime,
        long hwnd,
        NativeMethods.Rect windowRect,
        List<AutomationObservation> result)
    {
        AutomationElement? selectedPanel = null;
        try
        {
            // Revit virtualizes most Ribbon descendants. A point inside the
            // materialized command band reliably returns its selected panel as an
            // ancestor even when a descendant FindAll only returns two buttons.
            var probeY = windowRect.Top + Math.Min(118, Math.Max(72, (windowRect.Bottom - windowRect.Top) / 10));
            foreach (var probeX in new[]
                     {
                         windowRect.Left + 220,
                         windowRect.Left + Math.Max(220, (windowRect.Right - windowRect.Left) / 2),
                         windowRect.Right - 220
                     })
            {
                var currentElement = AutomationElement.FromPoint(new System.Windows.Point(probeX, probeY));
                for (var depth = 0; currentElement is not null && depth < 10; depth++)
                {
                    var automationId = currentElement.Current.AutomationId;
                    if (automationId.EndsWith("_PanelBarScrollViewer", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPanel = currentElement;
                        break;
                    }
                    currentElement = TreeWalker.RawViewWalker.GetParent(currentElement);
                }
                if (selectedPanel is not null) break;
            }
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        if (selectedPanel is null) return null;

        try
        {
            var current = selectedPanel.Current;
            var bounds = current.BoundingRectangle;
            if (!IsContainedInWindow(bounds, windowRect) || current.IsOffscreen) return null;
            var runtime = RuntimeId(selectedPanel, hwnd) ??
                          Clamp($"{hwnd:x}.revit-selected-panel.{current.AutomationId}", 4_096);
            result.Add(new(
                runtime,
                hostRuntime,
                Clamp(current.AutomationId, 512),
                Clamp(current.Name, 4_096),
                ControlType.Custom.ProgrammaticName,
                Clamp(current.ClassName, 512),
                ToRect(bounds),
                current.IsEnabled,
                current.IsOffscreen,
                "UiAtlas.SurfaceAnchor",
                hwnd,
                [],
                current.HasKeyboardFocus));
            return selectedPanel;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return null; }
    }

    private static void CollectCachedRevitButtons(
        AutomationElement container,
        TreeScope scope,
        string parentRuntime,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        AutomationElementCollection matches;
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeScope = TreeScope.Element,
            TreeFilter = Condition.TrueCondition
        };
        cache.Add(AutomationElement.RuntimeIdProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.FrameworkIdProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        try
        {
            using (cache.Activate())
                matches = container.FindAll(scope,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (AutomationElement item in matches)
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var current = item.Cached;
                var bounds = current.BoundingRectangle;
                if (!IsContainedInWindow(bounds, windowRect) || current.IsOffscreen) continue;
                var runtimeParts = item.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[];
                var runtime = runtimeParts is { Length: > 0 }
                    ? Clamp($"{hwnd:x}." + string.Join('.', runtimeParts), 4_096)
                    : Clamp($"{hwnd:x}.revit.{current.AutomationId}:{bounds.X:F0},{bounds.Y:F0}", 4_096);
                if (!known.Add(runtime)) continue;
                result.Add(new(runtime, parentRuntime, Clamp(current.AutomationId, 512), Clamp(current.Name, 4_096),
                    ControlType.Button.ProgrammaticName, Clamp(current.ClassName, 512), ToRect(bounds), current.IsEnabled,
                    current.IsOffscreen, Clamp(current.FrameworkId, 128), hwnd,
                    [InvokePattern.Pattern.ProgrammaticName], current.HasKeyboardFocus));
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    private static AutomationElement? FindRevitRibbonHost(AutomationElement root) =>
        FindDirectChild(root, element =>
        {
            var current = element.Current;
            var rect = current.BoundingRectangle;
            var rootRect = root.Current.BoundingRectangle;
            return current.ControlType == ControlType.List && !rect.IsEmpty &&
                   rect.Top >= rootRect.Top + 40 && rect.Top <= rootRect.Top + 120 &&
                   rect.Height is >= 80 and <= 220 && rect.Width >= rootRect.Width * 0.65;
        }, 16);

    private static AutomationElement? FindDirectChild(
        AutomationElement root,
        Func<AutomationElement, bool> predicate,
        int limit)
    {
        try
        {
            var child = TreeWalker.RawViewWalker.GetFirstChild(root);
            for (var index = 0; child is not null && index < limit; index++)
            {
                try
                {
                    if (predicate(child)) return child;
                }
                catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
                child = TreeWalker.RawViewWalker.GetNextSibling(child);
            }
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        return null;
    }

    private static bool IsRevitRoot(AutomationElement root)
    {
        try
        {
            var current = root.Current;
            return current.FrameworkId.Equals("WPF", StringComparison.OrdinalIgnoreCase) &&
                   current.Name.Contains("Revit", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex))
        {
            return false;
        }
    }

    private static void RemoveRedundantLegacyTitleBarControls(List<AutomationObservation> result)
    {
        if (!result.Any(control =>
                control.ClassName.Equals("NetUIAppFrameHelper", StringComparison.OrdinalIgnoreCase)))
            return;

        result.RemoveAll(control =>
            control.ClassName.Length == 0 &&
            (control.AutomationId is "Minimize" or "Maximize" or "Restore" or "Close") &&
            NormalizeControlType(control.ControlType).Equals("Button", StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendTopChromeControls(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        if (result.Count >= maxNodes) return;
        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        AutomationElementCollection matches;
        try { matches = root.FindAll(TreeScope.Descendants, TopChromeControlCondition); }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return; }

        foreach (AutomationElement element in matches)
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                    bounds.Left < windowRect.Left - 6 ||
                    bounds.Right > windowRect.Right + 6)
                    continue;

                // Do not accept same-named workbook content. The title/chrome
                // island is always in the first 12% of the application window.
                var chromeBottom = windowRect.Top + Math.Max(110,
                    (int)Math.Round((windowRect.Bottom - windowRect.Top) * 0.12));
                if (bounds.Top >= chromeBottom) continue;
                AddObservedElement(element, "", hwnd, windowRect, result, known);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    public static IReadOnlyList<AutomationObservation> CollectWorksheetWindow(long hwnd, int maxNodes)
    {
        if (!IsSupportedNodeLimit(maxNodes)) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var windowRect))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));

        // The isolated worker can receive DPI-virtualized Win32 bounds while UIA
        // always reports physical screen coordinates. Use the provider root as the
        // coordinate authority or bottom chrome (sheet tabs/status bar) is clipped.
        try
        {
            var applicationRoot = AutomationElement.FromHandle((nint)hwnd);
            if (TryPhysicalPopupRect(applicationRoot, out var physicalWindowRect))
                windowRect = physicalWindowRect;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }

        var result = new List<AutomationObservation>(Math.Min(maxNodes, 1_024));
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var worksheetHwnd in WindowCatalog.ListDescendantHandles(hwnd, 1_024)
                     .Where(candidate => WindowCatalog.GetClass((nint)candidate)
                         .Equals("EXCEL7", StringComparison.OrdinalIgnoreCase))
                     .Take(4))
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var worksheetRoot = AutomationElement.FromHandle((nint)worksheetHwnd);
                var cache = new CacheRequest { TreeScope = TreeScope.Element };
                cache.Add(AutomationElement.RuntimeIdProperty);
                cache.Add(AutomationElement.AutomationIdProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsEnabledProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.FrameworkIdProperty);
                cache.Add(AutomationElement.HasKeyboardFocusProperty);
                AutomationElementCollection controls;
                using (cache.Activate())
                    controls = worksheetRoot.FindAll(TreeScope.Descendants, WorksheetControlCondition);
                foreach (AutomationElement control in controls)
                {
                    if (result.Count >= maxNodes) break;
                    var current = control.Cached;
                    if (!IsWorksheetSurfaceControl(
                            current.ControlType?.ProgrammaticName ?? "",
                            current.ClassName ?? "",
                            current.AutomationId ?? ""))
                        continue;
                    var bounds = ToRect(current.BoundingRectangle);
                    if (bounds.Width <= 0 || bounds.Height <= 0 || current.IsOffscreen ||
                        bounds.X < windowRect.Left - 6 || bounds.Y < windowRect.Top - 6 ||
                        bounds.X + bounds.Width > windowRect.Right + 6 ||
                        bounds.Y + bounds.Height > windowRect.Bottom + 6)
                        continue;
                    var runtimeValue = control.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
                    var runtime = runtimeValue is { Length: > 0 }
                        ? Clamp($"{hwnd:x}." + string.Join('.', runtimeValue), 4_096)
                        : $"{hwnd:x}.worksheet.{result.Count:x}.{bounds.X:x}.{bounds.Y:x}";
                    if (!known.Add(runtime)) continue;
                    var type = current.ControlType?.ProgrammaticName ?? "";
                    result.Add(new(runtime, "", Clamp(current.AutomationId, 512), Clamp(current.Name, 4_096),
                        Clamp(type, 256), Clamp(current.ClassName, 512), bounds, current.IsEnabled,
                        current.IsOffscreen, Clamp(current.FrameworkId, 128), hwnd,
                        WorksheetPatterns(type), current.HasKeyboardFocus));
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }

        foreach (var scrollHwnd in WindowCatalog.ListDescendantHandles(hwnd, 1_024)
                     .Where(candidate => WindowCatalog.GetClass((nint)candidate)
                         .Equals("NUIScrollbar", StringComparison.OrdinalIgnoreCase))
                     .Take(4))
        {
            if (result.Count >= maxNodes) break;
            try
            {
                var scrollControls = new List<AutomationObservation>(12);
                var visited = 0;
                CollectWindow(scrollHwnd, 12, 12, scrollControls, ref visited);
                foreach (var control in scrollControls.Where(control =>
                             !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0))
                {
                    if (result.Count >= maxNodes) break;
                    if (known.Add(control.RuntimeId)) result.Add(control with { WindowHwnd = hwnd });
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
        AppendExcelSheetNavigationControls(hwnd, windowRect, maxNodes, result, known);
        AppendExcelStatusBarControls(hwnd, windowRect, maxNodes, result, known);
        return result;
    }

    private static IReadOnlyList<string> WorksheetPatterns(string controlType) =>
        NormalizeControlType(controlType) switch
        {
            "DataItem" => ["GridItemPatternIdentifiers.Pattern", "SelectionItemPatternIdentifiers.Pattern"],
            "ScrollBar" => ["RangeValuePatternIdentifiers.Pattern"],
            "TabItem" => ["SelectionItemPatternIdentifiers.Pattern"],
            "Button" => ["InvokePatternIdentifiers.Pattern"],
            _ => []
        };

    internal static bool IsWorksheetSurfaceControl(string controlType, string className, string automationId) =>
        className.StartsWith("XLSpreadsheet", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("XLGrid", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("ExcelBookTabControl", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("NUIScrollbar", StringComparison.OrdinalIgnoreCase) ||
        automationId.Equals("SheetTab", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".ScrollBar", StringComparison.OrdinalIgnoreCase) ||
        controlType.EndsWith(".Thumb", StringComparison.OrdinalIgnoreCase);

    private static void AppendExcelSheetNavigationControls(
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        foreach (var worksheetHwnd in WindowCatalog.ListDescendantHandles(hwnd, 1_024)
                     .Where(candidate => WindowCatalog.GetClass((nint)candidate)
                         .Equals("EXCEL7", StringComparison.OrdinalIgnoreCase))
                     .Take(4))
        {
            if (result.Count >= maxNodes) return;
            try
            {
                // The broad worksheet query can omit this provider island. Query its
                // two stable identities directly instead of walking the cell tree.
                var worksheetRoot = AutomationElement.FromHandle((nint)worksheetHwnd);
                var matches = worksheetRoot.FindAll(TreeScope.Descendants, SheetNavigationCondition);
                foreach (AutomationElement element in matches)
                {
                    if (result.Count >= maxNodes) return;
                    AddObservedElement(element, "", hwnd, windowRect, result, known);
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    internal static bool IsExcelSheetNavigationControl(
        string controlType,
        string className,
        string automationId) =>
        className.Equals("ExcelBookTabControl", StringComparison.OrdinalIgnoreCase) ||
        automationId.Equals("SheetTab", StringComparison.OrdinalIgnoreCase) &&
        (controlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) ||
         controlType.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase));

    private static void AppendExcelStatusBarControls(
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        foreach (var statusHwnd in WindowCatalog.ListDescendantHandles(hwnd, 1_024)
                     .Where(candidate => IsExcelStatusBarWindow(
                         WindowCatalog.GetClass((nint)candidate),
                         WindowCatalog.GetText((nint)candidate),
                         WindowBounds(candidate),
                         new RectI(windowRect.Left, windowRect.Top,
                             windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top)))
                     .Take(1))
        {
            if (result.Count >= maxNodes) return;
            try
            {
                var statusControls = new List<AutomationObservation>(32);
                var visited = 0;
                CollectWindow(statusHwnd, Math.Min(64, maxNodes - result.Count), 16, statusControls, ref visited);
                foreach (var control in statusControls.Where(control =>
                             !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0))
                {
                    if (result.Count >= maxNodes) return;
                    if (known.Add(control.RuntimeId)) result.Add(control with { WindowHwnd = hwnd });
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
    }

    internal static bool IsExcelStatusBarWindow(
        string className,
        string title,
        RectI candidate,
        RectI application)
    {
        if (!className.Equals("MsoCommandBar", StringComparison.OrdinalIgnoreCase) ||
            candidate.Width <= 0 || candidate.Height <= 0 || application.Width <= 0 || application.Height <= 0)
            return false;
        if (title.Equals("Status Bar", StringComparison.OrdinalIgnoreCase))
            return true;
        var applicationBottom = (long)application.Y + application.Height;
        return candidate.Width >= application.Width * 0.40 &&
               candidate.Y >= application.Y + application.Height * 0.82 &&
               candidate.Y < applicationBottom;
    }

    private static RectI WindowBounds(long hwnd) =>
        NativeMethods.GetWindowRect((nint)hwnd, out var bounds)
            ? new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
            : new(0, 0, 0, 0);

    private static bool IsContainedInWindow(RectI bounds, NativeMethods.Rect window) =>
        bounds.X >= window.Left - 6 && bounds.Y >= window.Top - 6 &&
        bounds.X + bounds.Width <= window.Right + 6 && bounds.Y + bounds.Height <= window.Bottom + 6;

    private static void AppendFormulaBarControls(
        long hwnd,
        NativeMethods.Rect windowRect,
        int maxNodes,
        List<AutomationObservation> result)
    {
        if (result.Count >= maxNodes) return;
        var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
        var formulaBandLimit = windowRect.Top + Math.Max(180,
            (int)Math.Round((windowRect.Bottom - windowRect.Top) * 0.34));
        var formulaEditActive = false;
        foreach (var childHwnd in WindowCatalog.ListDescendantHandles(hwnd, 512))
        {
            if (result.Count >= maxNodes ||
                !NativeMethods.IsWindowVisible((nint)childHwnd) ||
                !NativeMethods.GetWindowRect((nint)childHwnd, out var childRect) ||
                childRect.Bottom <= windowRect.Top || childRect.Top >= formulaBandLimit)
                continue;
            var childClass = WindowCatalog.GetClass((nint)childHwnd);
            if (childClass == "EXCEL6" && childRect.Right > childRect.Left && childRect.Bottom > childRect.Top)
            {
                formulaEditActive = true;
                continue;
            }
            if (childClass is not ("Button" or "ComboBox" or "EXCEL<")) continue;
            try
            {
                var firstAddedIndex = result.Count;
                var childRoot = AutomationElement.FromHandle((nint)childHwnd);
                AddObservedElement(childRoot, "", hwnd, windowRect, result, known);
                if (childClass == "ComboBox" || childClass == "EXCEL<")
                {
                    var descendants = childRoot.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                    foreach (AutomationElement descendant in descendants)
                    {
                        if (result.Count >= maxNodes) break;
                        AddObservedElement(descendant, "", hwnd, windowRect, result, known);
                    }
                }
                RestoreVisibleFormulaBarControls(result, firstAddedIndex,
                    new RectI(childRect.Left, childRect.Top, childRect.Right - childRect.Left, childRect.Bottom - childRect.Top));
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }

        var nameBox = result.FirstOrDefault(control =>
            control.Name.Equals("Name Box", StringComparison.OrdinalIgnoreCase) &&
            NormalizeControlType(control.ControlType) == "ComboBox");
        var insertFunction = result.FirstOrDefault(control =>
            control.Name.Equals("Insert Function", StringComparison.OrdinalIgnoreCase) &&
            NormalizeControlType(control.ControlType) == "Button");
        if (nameBox is null || insertFunction is null) return;

        var commandHeight = Math.Max(1, insertFunction.Bounds.Height);
        var commandRight = insertFunction.Bounds.X - Math.Max(3, commandHeight / 8);
        var commandLeft = nameBox.Bounds.X + nameBox.Bounds.Width + Math.Max(6, commandHeight / 3);
        var available = commandRight - commandLeft;
        if (available < 42) return;
        var buttonWidth = Math.Min(commandHeight, Math.Max(18, (available - 4) / 2));
        var cancelX = commandRight - buttonWidth * 2 - 4;
        var enterX = commandRight - buttonWidth;
        AddSyntheticFormulaButton("FormulaBarCancel", "Cancel", cancelX, insertFunction.Bounds.Y,
            buttonWidth, commandHeight, formulaEditActive, hwnd, result, maxNodes);
        AddSyntheticFormulaButton("FormulaBarEnter", "Enter", enterX, insertFunction.Bounds.Y,
            buttonWidth, commandHeight, formulaEditActive, hwnd, result, maxNodes);
    }

    internal static void RestoreVisibleFormulaBarControls(
        List<AutomationObservation> controls,
        int firstIndex,
        RectI nativeChildBounds)
    {
        firstIndex = Math.Clamp(firstIndex, 0, controls.Count);
        for (var index = firstIndex; index < controls.Count; index++)
        {
            var control = controls[index];
            if (!control.IsOffscreen || !IsFormulaBarControl(control) ||
                !IsContainedInBounds(control.Bounds, nativeChildBounds))
                continue;
            controls[index] = control with { IsOffscreen = false };
        }
    }

    private static bool IsFormulaBarControl(AutomationObservation control) =>
        control.Name.Equals("Insert Function", StringComparison.OrdinalIgnoreCase) ||
        control.Name.Equals("Name Box", StringComparison.OrdinalIgnoreCase) ||
        control.AutomationId.Equals("FormulaBar", StringComparison.OrdinalIgnoreCase);

    private static bool IsContainedInBounds(RectI bounds, RectI container) =>
        bounds.Width > 0 && bounds.Height > 0 &&
        bounds.X >= container.X - 4 && bounds.Y >= container.Y - 4 &&
        (long)bounds.X + bounds.Width <= (long)container.X + container.Width + 4 &&
        (long)bounds.Y + bounds.Height <= (long)container.Y + container.Height + 4;

    private static void AddSyntheticFormulaButton(
        string automationId,
        string name,
        int x,
        int y,
        int width,
        int height,
        bool isEnabled,
        long hwnd,
        List<AutomationObservation> result,
        int maxNodes)
    {
        if (result.Count >= maxNodes || result.Any(control =>
                control.AutomationId.Equals(automationId, StringComparison.OrdinalIgnoreCase)))
            return;
        result.Add(new($"{hwnd:x}.formula.{automationId}", "", automationId, name,
            "ControlType.Button", "ExcelFormulaBarCommand", new RectI(x, y, width, height),
            isEnabled, false, "Win32", hwnd, ["InvokePatternIdentifiers.Pattern"]));
    }

    private static bool IsRibbonControlType(ControlType type)
    {
        var id = type.Id;
        return id == ControlType.Button.Id || id == ControlType.MenuItem.Id || id == ControlType.ComboBox.Id ||
               id == ControlType.SplitButton.Id || id == ControlType.CheckBox.Id || id == ControlType.RadioButton.Id ||
               id == ControlType.TabItem.Id || id == ControlType.Edit.Id || id == ControlType.Custom.Id;
    }

    public static IReadOnlyList<AutomationObservation> CollectPointWindow(long hwnd, int x, int y, int maxNodes)
    {
        if (maxNodes is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd)) throw new ArgumentException("Window handle is not valid.", nameof(hwnd));
        var pointed = NativeMethods.WindowFromPoint(new NativeMethods.Point(x, y));
        if (pointed == 0 || WindowCatalog.GetTopLevelHandle(pointed).ToInt64() != hwnd)
            return [];

        if (NativeMethods.GetWindowRect((nint)hwnd, out var windowRect))
        {
            try
            {
                var root = AutomationElement.FromHandle((nint)hwnd);
                if (IsRevitRoot(root))
                {
                    var statusControls = new List<AutomationObservation>(24);
                    AppendRevitStatusBarControls(hwnd, windowRect, 24, statusControls);
                    var statusControl = statusControls
                        .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                                          x >= control.Bounds.X && y >= control.Bounds.Y &&
                                          x < control.Bounds.X + control.Bounds.Width &&
                                          y < control.Bounds.Y + control.Bounds.Height)
                        .OrderBy(control => (long)control.Bounds.Width * control.Bounds.Height)
                        .FirstOrDefault();
                    if (statusControl is not null) return [statusControl];
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }

            var formulaControls = new List<AutomationObservation>(12);
            AppendFormulaBarControls(hwnd, windowRect, 12, formulaControls);
            var formulaControl = formulaControls
                .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                                  x >= control.Bounds.X && y >= control.Bounds.Y &&
                                  x < control.Bounds.X + control.Bounds.Width &&
                                  y < control.Bounds.Y + control.Bounds.Height)
                .OrderBy(FormulaPointPriority)
                .ThenBy(control => (long)control.Bounds.Width * control.Bounds.Height)
                .FirstOrDefault();
            if (formulaControl is not null) return [formulaControl];
        }

        var chain = new List<AutomationElement>(maxNodes);
        var scopeRoot = AutomationElement.FromHandle((nint)hwnd);
        AutomationElement? element;
        try { element = AutomationElement.FromPoint(new System.Windows.Point(x, y)); }
        catch (ElementNotAvailableException) { return []; }
        var reachedScopeRoot = false;
        while (element is not null && chain.Count < maxNodes)
        {
            chain.Add(element);
            try
            {
                if (Automation.Compare(element, scopeRoot))
                {
                    reachedScopeRoot = true;
                    break;
                }
            }
            catch (ElementNotAvailableException) { return []; }
            try { element = TreeWalker.RawViewWalker.GetParent(element); }
            catch (ElementNotAvailableException) { break; }
        }
        if (!IsPointAutomationChainScoped(reachedScopeRoot))
            return [];
        chain.Reverse();

        var result = new List<AutomationObservation>(chain.Count);
        string parentRuntime = "";
        foreach (var currentElement in chain)
        {
            try
            {
                var current = currentElement.Current;
                var runtime = Clamp($"{hwnd:x}." + string.Join('.', currentElement.GetRuntimeId()), 4_096);
                var controlType = current.ControlType?.ProgrammaticName ?? "";
                var name = controlType.EndsWith(".Edit", StringComparison.Ordinal) || controlType.EndsWith(".Document", StringComparison.Ordinal)
                    ? "[redacted]" : current.Name ?? "";
                var patterns = currentElement.GetSupportedPatterns()
                    .Select(pattern => Clamp(pattern.ProgrammaticName, 256))
                    .Where(pattern => pattern.Length > 0)
                    .Order(StringComparer.Ordinal)
                    .Take(32)
                    .ToArray();
                result.Add(new(runtime, parentRuntime, Clamp(current.AutomationId, 512), Clamp(name, 4_096), Clamp(controlType, 256),
                    Clamp(current.ClassName, 512), ToRect(current.BoundingRectangle), current.IsEnabled, current.IsOffscreen,
                    Clamp(current.FrameworkId, 128), hwnd, patterns, current.HasKeyboardFocus, TryGetSelectionState(currentElement),
                    TryGetToggleState(currentElement), TryGetExpandCollapseState(currentElement)));
                parentRuntime = runtime;
            }
            catch (ElementNotAvailableException) { }
        }
        if (result.Count > 0 && chain.Count > 0 && NativeMethods.GetWindowRect((nint)hwnd, out var pointWindowRect))
        {
            try
            {
                if (IsRevitRoot(scopeRoot) && IsRevitPointDescentContainer(
                        result[^1],
                        new RectI(pointWindowRect.Left, pointWindowRect.Top,
                            pointWindowRect.Right - pointWindowRect.Left,
                            pointWindowRect.Bottom - pointWindowRect.Top)))
                {
                    var known = result.Select(item => item.RuntimeId).ToHashSet(StringComparer.Ordinal);
                    CollectRawPointDescendantChain(
                        chain[^1], hwnd, pointWindowRect, x, y, maxNodes, result, known);
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
        }
        return result;
    }

    internal static bool IsRevitPointDescentContainer(AutomationObservation control, RectI rootBounds)
    {
        var type = NormalizeControlType(control.ControlType);
        if (type is not ("Custom" or "DataItem" or "Pane" or "Group" or "ToolBar"))
            return false;
        if (control.Bounds.Width < 48 || control.Bounds.Height < 24 ||
            !IsContainedInBounds(control.Bounds, rootBounds))
            return false;
        var rootArea = Math.Max(1L, (long)rootBounds.Width * rootBounds.Height);
        var area = (long)control.Bounds.Width * control.Bounds.Height;
        return area <= rootArea / 2;
    }

    private static void CollectRawPointDescendantChain(
        AutomationElement root,
        long hwnd,
        NativeMethods.Rect windowRect,
        int x,
        int y,
        int maxNodes,
        List<AutomationObservation> result,
        HashSet<string> known)
    {
        var current = root;
        var parentRuntime = RuntimeId(root, hwnd) ?? result[^1].RuntimeId;
        for (var depth = 0; depth < 20 && result.Count < maxNodes; depth++)
        {
            var candidates = new List<(AutomationElement Element, RectI Bounds, int Priority)>();
            AutomationElement? child;
            try { child = TreeWalker.RawViewWalker.GetFirstChild(current); }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { break; }

            var examined = 0;
            while (child is not null && examined++ < 512)
            {
                try
                {
                    var properties = child.Current;
                    var bounds = ToRect(properties.BoundingRectangle);
                    if (bounds.Width > 0 && bounds.Height > 0 &&
                        x >= bounds.X && y >= bounds.Y &&
                        x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height)
                    {
                        candidates.Add((child, bounds, PointDescendantPriority(properties.ControlType)));
                    }
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
                catch (Exception ex) when (IsRecoverableAutomationException(ex))
                {
                    try { child = TreeWalker.RawViewWalker.GetNextSibling(child); }
                    catch (Exception nextEx) when (IsRecoverableAutomationException(nextEx)) { break; }
                }
            }

            var selected = candidates
                .OrderBy(candidate => candidate.Priority)
                .ThenBy(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
                .FirstOrDefault();
            if (selected.Element is null) break;
            var runtime = AddObservedElement(
                selected.Element, parentRuntime, hwnd, windowRect, result, known);
            parentRuntime = runtime ?? parentRuntime;
            current = selected.Element;
        }
    }

    private static int PointDescendantPriority(ControlType? controlType)
    {
        var id = controlType?.Id ?? 0;
        if (id == ControlType.Button.Id || id == ControlType.MenuItem.Id ||
            id == ControlType.SplitButton.Id || id == ControlType.ComboBox.Id ||
            id == ControlType.CheckBox.Id || id == ControlType.RadioButton.Id)
            return 0;
        if (id == ControlType.Group.Id || id == ControlType.Pane.Id ||
            id == ControlType.ToolBar.Id || id == ControlType.Custom.Id)
            return 1;
        return 2;
    }

    public static IReadOnlyList<AutomationObservation> CollectLocalSubtreeWindow(long hwnd, int x, int y, int maxNodes)
    {
        if (maxNodes is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (!NativeMethods.IsWindow((nint)hwnd) || !NativeMethods.GetWindowRect((nint)hwnd, out var nativeRect))
            throw new ArgumentException("Window handle is not valid.", nameof(hwnd));
        var pointed = NativeMethods.WindowFromPoint(new NativeMethods.Point(x, y));
        if (pointed == 0 || WindowCatalog.GetTopLevelHandle(pointed).ToInt64() != hwnd)
            return [];

        var scopeRoot = AutomationElement.FromHandle((nint)hwnd);
        if (TryPhysicalPopupRect(scopeRoot, out var physicalRect)) nativeRect = physicalRect;
        AutomationElement? current;
        try { current = AutomationElement.FromPoint(new System.Windows.Point(x, y)); }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return []; }

        AutomationElement? localRoot = null;
        var reachedScope = false;
        for (var depth = 0; current is not null && depth < 20; depth++)
        {
            try
            {
                if (Automation.Compare(current, scopeRoot))
                {
                    reachedScope = true;
                    break;
                }
                var properties = current.Current;
                if (IsLocalProbeContainer(
                        properties.ControlType?.ProgrammaticName ?? string.Empty,
                        ToRect(properties.BoundingRectangle),
                        new RectI(nativeRect.Left, nativeRect.Top,
                            nativeRect.Right - nativeRect.Left, nativeRect.Bottom - nativeRect.Top)))
                    localRoot = current;
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { break; }
        }
        if (!reachedScope || localRoot is null) return [];

        var result = new List<AutomationObservation>(Math.Min(maxNodes, 256));
        var known = new HashSet<string>(StringComparer.Ordinal);
        CollectDerivedSubtree(localRoot, hwnd, nativeRect, maxNodes, result, known);
        return result;
    }

    internal static bool IsLocalProbeContainer(string controlType, RectI candidate, RectI root)
    {
        var type = NormalizeControlType(controlType);
        if (type is not ("Pane" or "Group" or "Custom" or "List" or "Menu" or "ToolBar" or "Tab"))
            return false;
        if (candidate.Width <= 0 || candidate.Height <= 0 || root.Width <= 0 || root.Height <= 0)
            return false;
        if (!IsContainedInBounds(candidate, root)) return false;
        var area = (long)candidate.Width * candidate.Height;
        var rootArea = Math.Max(1L, (long)root.Width * root.Height);
        return area >= 1_024 && area <= rootArea * 45 / 100 &&
               (candidate.Height <= root.Height * 45 / 100 || candidate.Width <= root.Width * 45 / 100);
    }

    internal static bool IsPointAutomationChainScoped(bool reachedScopeRoot) => reachedScopeRoot;

    internal static bool IsSupportedNodeLimit(int maxNodes) =>
        maxNodes is >= 1 and <= RecordingContractLimits.MaxControlsPerFrame;

    private static int FormulaPointPriority(AutomationObservation control)
    {
        if (NormalizeControlType(control.ControlType) == "Button") return 0;
        if (control.AutomationId.Equals("FormulaBar", StringComparison.OrdinalIgnoreCase) ||
            control.Name.Equals("Name Box", StringComparison.OrdinalIgnoreCase) &&
            NormalizeControlType(control.ControlType) == "ComboBox")
            return 1;
        return 2;
    }

    private static void CollectWindow(
        long hwnd,
        int maxNodes,
        int maxDepth,
        List<AutomationObservation> result,
        ref int visitedNodes,
        AutomationTreeView view = AutomationTreeView.Raw)
    {
        var root = AutomationElement.FromHandle((nint)hwnd);
        // Breadth-first traversal keeps shallow Ribbon/navigation controls ahead of
        // Excel's very large worksheet subtree while preserving every visited item.
        var queue = new Queue<(AutomationElement Element, string Parent, int Depth)>();
        queue.Enqueue((root, "", 0));
        while (queue.Count > 0 && visitedNodes < maxNodes)
        {
            var (element, parent, depth) = queue.Dequeue();
            visitedNodes++;
            string runtime;
            try { runtime = Clamp($"{hwnd:x}." + string.Join('.', element.GetRuntimeId()), 4_096); }
            catch { runtime = $"{hwnd:x}.unavailable-{result.Count}"; }
            try
            {
                var current = element.Current;
                var rect = current.BoundingRectangle;
                var controlType = current.ControlType?.ProgrammaticName ?? "";
                var name = controlType.EndsWith(".Edit", StringComparison.Ordinal) || controlType.EndsWith(".Document", StringComparison.Ordinal)
                    ? "[redacted]" : current.Name ?? "";
                var patterns = element.GetSupportedPatterns()
                    .Select(pattern => Clamp(pattern.ProgrammaticName, 256))
                    .Where(pattern => pattern.Length > 0)
                    .Order(StringComparer.Ordinal)
                    .Take(32)
                    .ToArray();
                var isSelected = TryGetSelectionState(element);
                result.Add(new(runtime, parent, Clamp(current.AutomationId, 512), Clamp(name, 4_096), Clamp(controlType, 256),
                    Clamp(current.ClassName, 512), ToRect(rect), current.IsEnabled, current.IsOffscreen, Clamp(current.FrameworkId, 128), hwnd,
                    patterns, current.HasKeyboardFocus, isSelected));
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { continue; }

            if (depth >= maxDepth) continue;
            var children = new List<AutomationElement>();
            try
            {
                var walker = view switch
                {
                    AutomationTreeView.Control => TreeWalker.ControlViewWalker,
                    AutomationTreeView.Content => TreeWalker.ContentViewWalker,
                    _ => TreeWalker.RawViewWalker
                };
                var child = walker.GetFirstChild(element);
                while (child is not null && visitedNodes + queue.Count + children.Count < maxNodes)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (Exception ex) when (IsRecoverableAutomationException(ex)) { }
            foreach (var child in children) queue.Enqueue((child, runtime, depth + 1));
        }
    }

    private static bool TryGetSelectionState(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                   pattern is SelectionItemPattern selectionItem &&
                   selectionItem.Current.IsSelected;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex))
        {
            return false;
        }
    }

    private static string? TryGetToggleState(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern) && pattern is TogglePattern toggle
                ? toggle.Current.ToggleState.ToString()
                : null;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return null; }
    }

    private static string? TryGetExpandCollapseState(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) && pattern is ExpandCollapsePattern expand
                ? expand.Current.ExpandCollapseState.ToString()
                : null;
        }
        catch (Exception ex) when (IsRecoverableAutomationException(ex)) { return null; }
    }

    private static RectI ToRect(System.Windows.Rect value) => value.IsEmpty || double.IsNaN(value.X)
        ? new(0, 0, 0, 0)
        : new(Clamp(value.X), Clamp(value.Y), Math.Max(0, Clamp(value.Width)), Math.Max(0, Clamp(value.Height)));

    private static int Clamp(double value) => (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
    private static string Clamp(string? value, int maxLength) => string.IsNullOrEmpty(value) ? "" : value.Length <= maxLength ? value : value[..maxLength];
}

using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal sealed record GridExplorationAutomationRequest(RectI Bounds, string Action = "inspect",
    GridScrollAxis Axis = GridScrollAxis.Vertical, int Direction = 1, int? Position = null,
    MappedGridDefinition? Definition = null, GridTargetIdentity? Target = null);

internal sealed record GridExplorationAutomationResult(bool Found, bool HasScroll, GridScrollPosition? Position,
    IReadOnlyList<RectI> Headers, string? Reason = null, RectI? Viewport = null);

/// <summary>Only runs inside the isolated UIA worker. Geometry seals the selected element.</summary>
internal static class GridExplorationAutomation
{
    internal static GridExplorationAutomationResult Run(GridExplorationAutomationRequest request, int processId)
    {
        using var dpi = new DataGridDpiScope();
        IUIAutomation6? automation = null;
        IUIAutomationElement? element = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationScrollPattern? scroll = null;
        try
        {
            automation = (IUIAutomation6)new CUIAutomation8Class();
            automation.ConnectionTimeout = 500; automation.TransactionTimeout = 500;
            element = automation.ElementFromPoint(new tagPOINT
                { x = request.Bounds.X + request.Bounds.Width / 2, y = request.Bounds.Y + request.Bounds.Height / 2 });
            walker = automation.RawViewWalker;
            var found = false;
            for (var depth = 0; element is not null && depth < 24; depth++)
            {
                var r = element.CurrentBoundingRectangle;
                var bounds = new RectI(r.left, r.top, r.right - r.left, r.bottom - r.top);
                if (element.CurrentProcessId == processId && Near(bounds, request.Bounds) &&
                    (Has(element, UIA_PropertyIds.UIA_IsGridPatternAvailablePropertyId) ||
                     Has(element, UIA_PropertyIds.UIA_IsTablePatternAvailablePropertyId)))
                { found = true; break; }
                var parent = walker.GetParentElement(element); Release(element); element = parent;
            }
            if (!found || element is null) return new(false, false, null, []);
            var headers = new List<RectI>();
            var viewport = request.Bounds;
            if (request.Action == "inspect")
            {
                var condition = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_HeaderItemControlTypeId);
                try
                {
                    var items = element.FindAll(TreeScope.TreeScope_Descendants, condition);
                    try
                    {
                        if (items.Length > 128) throw new InvalidOperationException("header-count-limit");
                        for (var i = 0; i < items.Length; i++)
                        {
                            var h = items.GetElement(i);
                            try
                            {
                                var r = h.CurrentBoundingRectangle;
                                if (r.right > r.left && r.bottom > r.top)
                                    headers.Add(new(r.left, r.top, r.right - r.left, r.bottom - r.top));
                            }
                            finally { Release(h); }
                        }
                    }
                    finally { Release(items); }
                }
                finally { Release(condition); }
                var scrollbars = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_ScrollBarControlTypeId);
                try
                {
                    var bars = element.FindAll(TreeScope.TreeScope_Descendants, scrollbars);
                    try
                    {
                        if (bars.Length > 16) throw new InvalidOperationException("scrollbar-count-limit");
                        for (var i = 0; i < bars.Length; i++)
                        {
                            var bar = bars.GetElement(i);
                            try
                            {
                                if (bar.CurrentIsOffscreen != 0) continue;
                                var r = bar.CurrentBoundingRectangle;
                                if (r.bottom <= r.top || r.right <= r.left) continue;
                                if (r.bottom - r.top > r.right - r.left && r.left > viewport.X + viewport.Width / 2)
                                    viewport = viewport with { Width = Math.Min(viewport.Width, r.left - viewport.X) };
                                else if (r.right - r.left > r.bottom - r.top && r.top > viewport.Y + viewport.Height / 2)
                                    viewport = viewport with { Height = Math.Min(viewport.Height, r.top - viewport.Y) };
                            }
                            finally { Release(bar); }
                        }
                    }
                    finally { Release(bars); }
                }
                finally { Release(scrollbars); }
            }
            if (!Has(element, UIA_PropertyIds.UIA_IsScrollPatternAvailablePropertyId))
                return new(true, false, null, headers, "table-scroll-support-unavailable");
            scroll = (IUIAutomationScrollPattern)element.GetCurrentPattern(UIA_PatternIds.UIA_ScrollPatternId);
            if (request.Action == "move")
            {
                if (request.Definition is null || request.Target is null || request.Direction is not (-1 or 1))
                    throw new InvalidOperationException("scroll-request-incomplete");
                using var monitor = new UserInputCancellationMonitor(); monitor.Start();
                var unsafeReason = DataGridTargetBinding.CheckSafe(request.Definition, request.Target, true);
                if (unsafeReason is not null || monitor.StartupFailed || monitor.WasCancelledByUser)
                    throw new InvalidOperationException(unsafeReason ?? "human-takeover");
                if (request.Position is int position)
                {
                    if (position is < 0 or > 10_000) throw new InvalidOperationException("scroll-position-invalid");
                    scroll.SetScrollPercent(request.Axis == GridScrollAxis.Horizontal ? position / 100d : -1,
                        request.Axis == GridScrollAxis.Vertical ? position / 100d : -1);
                }
                else
                {
                    var amount = request.Direction < 0 ? ScrollAmount.ScrollAmount_SmallDecrement : ScrollAmount.ScrollAmount_SmallIncrement;
                    scroll.Scroll(request.Axis == GridScrollAxis.Horizontal ? amount : ScrollAmount.ScrollAmount_NoAmount,
                        request.Axis == GridScrollAxis.Vertical ? amount : ScrollAmount.ScrollAmount_NoAmount);
                }
            }
            return new(true, true, new(Axis(scroll.CurrentHorizontallyScrollable != 0, scroll.CurrentHorizontalScrollPercent),
                Axis(scroll.CurrentVerticallyScrollable != 0, scroll.CurrentVerticalScrollPercent)), headers, Viewport: viewport);
        }
        finally { Release(scroll); Release(element); Release(walker); Release(automation); }
    }

    private static GridAxisPosition Axis(bool scrollable, double percent) => !scrollable
        ? new(true, 0, 0, 1, 0, true, true)
        : new(true, 0, 10_000, 1, (int)Math.Round(percent * 100), percent <= .001, percent >= 99.999);
    private static bool Near(RectI a, RectI b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 &&
        Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;
    private static bool Has(IUIAutomationElement element, int id) => element.GetCurrentPropertyValueEx(id, 1) is bool b && b;
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

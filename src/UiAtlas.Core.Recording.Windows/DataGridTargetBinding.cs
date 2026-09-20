using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Read-only, parent-scoped native binding for the selected Win32 table.</summary>
public static class DataGridTargetBinding
{
    public static ParentScopedGridLocator DescribeLocator(long selectedHwnd, long hostHwnd)
    {
        using var dpi = new DataGridDpiScope();
        var window = WindowCatalog.Resolve(selectedHwnd);
        var host = WindowCatalog.Resolve(hostHwnd);
        if (host.ProcessId != window.ProcessId || host.ProcessStartedUtc != window.ProcessStartedUtc ||
            host.RootOwnerHwnd != window.RootOwnerHwnd ||
            (hostHwnd != selectedHwnd && !NativeGridMethods.IsChild((nint)selectedHwnd, (nint)hostHwnd)))
            throw new InvalidOperationException("host-outside-selected-target");
        if (hostHwnd == selectedHwnd)
            return new(window.ProcessName, window.ClassName, window.Title, [], new(window.ClassName, ControlType: "Window", Name: window.Title));
        var chain = new List<GridLocatorSegment>();
        var current = hostHwnd;
        for (var depth = 0; current != selectedHwnd && depth < 24; depth++)
        {
            var parent = NativeGridMethods.GetParent((nint)current).ToInt64();
            if (parent == 0) throw new InvalidOperationException("host-ancestry-changed");
            var className = WindowCatalog.GetClass((nint)current);
            var name = WindowCatalog.GetText((nint)current);
            var ordinal = 0;
            NativeMethods.EnumChildWindows((nint)parent, (candidate, _) =>
            {
                if (candidate.ToInt64() == current) return false;
                if (NativeGridMethods.GetParent(candidate).ToInt64() == parent &&
                    WindowCatalog.GetClass(candidate) == className && WindowCatalog.GetText(candidate) == name) ordinal++;
                return true;
            }, 0);
            chain.Add(new(className, Name: name, SiblingOrdinal: ordinal));
            current = parent;
        }
        if (current != selectedHwnd) throw new InvalidOperationException("host-ancestry-limit");
        chain.Reverse();
        return new(window.ProcessName, window.ClassName, window.Title, chain.Take(chain.Count - 1).ToArray(), chain[^1]);
    }

    public static GridTargetIdentity ResolveTarget(MappedGridDefinition definition) => ResolveTarget(definition, null);

    public static GridTargetIdentity ResolveTarget(MappedGridDefinition definition, long? selectedHwnd)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var target = ResolveTarget(definition.Locator, selectedHwnd);
        ValidateRegions(definition.Regions, target.HostBounds);
        return target;
    }

    /// <summary>Resolve a saved host without treating reviewed image geometry as a qualified reading schema.</summary>
    public static GridTargetIdentity ResolveTarget(ParentScopedGridLocator locator, long? selectedHwnd = null)
    {
        using var dpi = new DataGridDpiScope();
        ArgumentNullException.ThrowIfNull(locator);
        if (string.IsNullOrWhiteSpace(locator.ProcessName) || string.IsNullOrWhiteSpace(locator.Host.ClassName))
            throw new InvalidOperationException("grid-locator-incomplete");
        // Once selected, bind that exact HWND. An owned HUD can become the catalog's
        // representative for a legacy app with a hidden owner; it must not replace the target.
        IReadOnlyList<WindowTarget> candidates = selectedHwnd is { } selected
            ? [WindowCatalog.Resolve(selected)] : WindowCatalog.ListTopLevelWindows();
        var windows = candidates.Where(target =>
            (selectedHwnd is null || target.Hwnd == selectedHwnd) &&
            NativeMethods.IsWindowVisible((nint)target.Hwnd) &&
            WindowCatalog.GetTopLevelHandle((nint)target.Hwnd).ToInt64() == target.Hwnd &&
            target.ProcessName.Equals(locator.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            Matches(locator.WindowClassName, target.ClassName) && Matches(locator.WindowTitle, target.Title)).ToArray();
        if (windows.Length != 1) throw new InvalidOperationException(windows.Length == 0 ? "target-not-found" : "target-ambiguous");
        var window = windows[0];
        var parent = window.Hwnd;
        foreach (var segment in locator.Ancestors) parent = ResolveChild(parent, segment);
        var hostHwnd = locator.Host.ControlType == "Window" && locator.Ancestors.Count == 0 &&
                       Matches(locator.Host.ClassName, window.ClassName) && Matches(locator.Host.Name, window.Title)
            ? window.Hwnd : ResolveChild(parent, locator.Host);
        var host = WindowCatalog.Resolve(hostHwnd);
        if (host.ProcessId != window.ProcessId || host.ProcessStartedUtc != window.ProcessStartedUtc ||
            host.RootOwnerHwnd != window.RootOwnerHwnd)
            throw new InvalidOperationException("host-outside-selected-target");
        return new(window.Hwnd, window.RootOwnerHwnd, window.ProcessId, window.ProcessStartedUtc,
            hostHwnd, window.Bounds, host.Bounds);
    }

    internal static string? CheckSafe(MappedGridDefinition definition, GridTargetIdentity expected, bool requireForeground)
    {
        try
        {
            using var dpi = new DataGridDpiScope();
            var fresh = ResolveTarget(definition, expected.SelectedHwnd);
            if (fresh.ProcessId != expected.ProcessId || fresh.ProcessStartedUtc != expected.ProcessStartedUtc ||
                fresh.RootOwnerHwnd != expected.RootOwnerHwnd || fresh.HostHwnd != expected.HostHwnd)
                return "target-lost-or-replaced";
            if (fresh.WindowBounds != expected.WindowBounds || fresh.HostBounds != expected.HostBounds)
                return "unsafe-geometry-changed";
            if (!NativeMethods.IsWindowEnabled((nint)expected.HostHwnd) ||
                !NativeMethods.IsWindowVisible((nint)expected.HostHwnd)) return "unsafe-host-disabled-or-hidden";
            if (requireForeground && WindowCatalog.GetTopLevelHandle(NativeMethods.GetForegroundWindow()).ToInt64() != expected.SelectedHwnd)
                return "foreground-lost";

            var data = ToScreen(definition.Regions.Data, expected.HostBounds);
            var occluded = false;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (hwnd.ToInt64() == expected.SelectedHwnd) return false;
                if (!NativeMethods.IsWindowVisible(hwnd) || !NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;
                if (Intersects(data, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)))
                {
                    Console.WriteLine($"Grid capture occluded: title={WindowCatalog.GetText(hwnd)}; hwnd=0x{hwnd:X}; bounds={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}; table={data}.");
                    occluded = true;
                }
                return !occluded;
            }, 0);
            if (occluded) return "unsafe-occluded-table";

            // Root-owner equality alone would incorrectly permit a sibling panel or a modal dialog.
            foreach (var x in new[] { data.X + 2, data.X + data.Width / 2, data.X + data.Width - 3 })
            foreach (var y in new[] { data.Y + 2, data.Y + data.Height / 2, data.Y + data.Height - 3 })
            {
                var hit = NativeMethods.WindowFromPoint(new NativeMethods.Point(x, y));
                if (hit.ToInt64() != expected.HostHwnd && !NativeGridMethods.IsChild((nint)expected.HostHwnd, hit))
                    return "unsafe-data-scope-hit-test";
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
        {
            return "target-lost-or-unresolvable";
        }
    }

    private static long ResolveChild(long parent, GridLocatorSegment segment)
    {
        if (segment.SiblingOrdinal < 0) throw new InvalidOperationException("invalid-host-ordinal");
        // This fixture path uses native HWND ancestry. Unsupported selectors must not be ignored.
        if (!string.IsNullOrEmpty(segment.ControlType))
            throw new InvalidOperationException("unsupported-native-locator-control-type");
        var candidates = new List<long>();
        NativeMethods.EnumChildWindows((nint)parent, (hwnd, _) =>
        {
            if (NativeGridMethods.GetParent(hwnd).ToInt64() != parent) return true;
            if (!MatchesNativeSegment(segment, WindowCatalog.GetClass(hwnd), WindowCatalog.GetText(hwnd),
                    NativeMethods.GetDlgCtrlID(hwnd))) return true;
            candidates.Add(hwnd.ToInt64());
            return candidates.Count <= 256;
        }, 0);
        if (candidates.Count > 256 || segment.SiblingOrdinal >= candidates.Count)
            throw new InvalidOperationException("grid-host-not-found");
        return candidates[segment.SiblingOrdinal];
    }

    internal static void ValidateRegions(GridRegions regions, RectI host)
    {
        if (host.Width != regions.ReferenceHostWidth || host.Height != regions.ReferenceHostHeight)
            throw new InvalidOperationException("grid-host-size-changed");
        var localHost = new RectI(0, 0, host.Width, host.Height);
        if (!Contains(localHost, regions.ScrollScope) || !Contains(regions.ScrollScope, regions.Data) ||
            !Contains(regions.Data, regions.Header) || !Contains(regions.Data, regions.Body) ||
            regions.Header.Y + regions.Header.Height > regions.Body.Y ||
            regions.Header.X != regions.Body.X || regions.Header.Width != regions.Body.Width)
            throw new InvalidOperationException("unsafe-grid-regions");
    }

    internal static RectI ToScreen(RectI local, RectI host) => new(checked(host.X + local.X), checked(host.Y + local.Y), local.Width, local.Height);
    // An empty native caption is an exact caption, not a wildcard. DescribeLocator's
    // sibling ordinal is counted among this same exact class/caption set.
    internal static bool MatchesNativeSegment(GridLocatorSegment segment, string className, string caption, int controlId) =>
        !string.IsNullOrEmpty(segment.ClassName) && segment.ClassName.Equals(className, StringComparison.Ordinal) &&
        segment.Name.Equals(caption, StringComparison.Ordinal) &&
        (string.IsNullOrEmpty(segment.AutomationId) || segment.AutomationId == controlId.ToString(CultureInfo.InvariantCulture));
    internal static bool Contains(RectI outer, RectI inner) => inner.Width > 0 && inner.Height > 0 &&
        inner.X >= outer.X && inner.Y >= outer.Y && (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;
    private static bool Intersects(RectI a, RectI b) => b.Width > 0 && b.Height > 0 &&
        a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width && a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;
    private static bool Matches(string selector, string value) => string.IsNullOrEmpty(selector) || selector.Equals(value, StringComparison.Ordinal);
}

internal static partial class NativeGridMethods
{
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
}

/// <summary>Keep library callers and screen pixels in one physical coordinate space.</summary>
internal sealed class DataGridDpiScope : IDisposable
{
    private readonly nint _previous = NativeGridMethods.SetThreadDpiAwarenessContext((nint)(-4));
    public void Dispose()
    {
        if (_previous != 0) NativeGridMethods.SetThreadDpiAwarenessContext(_previous);
    }
}

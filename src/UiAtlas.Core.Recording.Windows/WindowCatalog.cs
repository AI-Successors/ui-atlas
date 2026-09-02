using System.Diagnostics;
using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed record WindowTarget(
    long Hwnd,
    long RootOwnerHwnd,
    int ProcessId,
    string ProcessName,
    DateTimeOffset ProcessStartedUtc,
    string Title,
    string ClassName,
    RectI Bounds,
    long OwnerHwnd = 0,
    int ZOrder = 0,
    long Style = 0,
    long ExStyle = 0,
    string ProductVersion = "",
    string OriginalFilename = "",
    string CompanyName = "",
    string ProductName = "");

[SupportedOSPlatform("windows")]
public static class WindowCatalog
{
    public static IReadOnlyList<WindowTarget> ListTopLevelWindows()
    {
        var candidates = new List<(nint Hwnd, nint RootOwner, RectI Bounds, int ZOrder, long ExStyle, bool IsCloaked)>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            var rootOwner = GetRootOwnerHandle(hwnd);
            if (rootOwner == 0) return true;
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            var dwmResult = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmaCloaked, out var cloaked, sizeof(int));
            candidates.Add((hwnd, rootOwner,
                new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                candidates.Count, exStyle, dwmResult == 0 && cloaked != 0));
            return true;
        }, 0);

        var foregroundRoot = GetRootOwnerHandle(NativeMethods.GetForegroundWindow());
        var values = new List<WindowTarget>();
        foreach (var family in candidates.Where(candidate => !candidate.IsCloaked).GroupBy(candidate => candidate.RootOwner))
        {
            // Most applications expose one visible, non-tool root window. Legacy Win32
            // applications often keep that root hidden while showing an owned startup,
            // license, or modal dialog. Select the best visible representative in that
            // family instead of dropping the entire application.
            var representative = family
                .Where(candidate => IsSelectableCatalogCandidate(candidate, foregroundRoot))
                .OrderByDescending(candidate => candidate.Hwnd == candidate.RootOwner &&
                                                (candidate.ExStyle & NativeMethods.WsExToolWindow) == 0)
                .ThenByDescending(candidate => candidate.Hwnd == foregroundRoot || family.Key == foregroundRoot)
                .ThenByDescending(candidate => (candidate.ExStyle & NativeMethods.WsExToolWindow) == 0)
                .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(GetText(candidate.Hwnd)))
                .ThenBy(candidate => candidate.ZOrder)
                .ThenByDescending(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
                .FirstOrDefault();
            if (representative.Hwnd == 0) continue;

            NativeMethods.GetWindowThreadProcessId(representative.Hwnd, out var pid);
            string processName;
            Process process;
            try { process = Process.GetProcessById((int)pid); processName = process.ProcessName; }
            catch { continue; }
            DateTimeOffset started;
            try { started = process.StartTime.ToUniversalTime(); } catch { process.Dispose(); continue; }
            var identity = ReadProductIdentity(process);
            process.Dispose();
            values.Add(CreateTarget(representative.Hwnd, representative.RootOwner, (int)pid, processName, started,
                representative.Bounds, representative.ZOrder) with
                { ProductVersion = identity.ProductVersion, OriginalFilename = identity.OriginalFilename, CompanyName = identity.CompanyName, ProductName = identity.ProductName });
        }
        return values.OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static WindowTarget Resolve(long hwndValue)
    {
        var hwnd = (nint)hwndValue;
        var root = GetRootOwnerHandle(hwnd);
        if (root == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) throw new ArgumentException("Window handle is not valid.");
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        using var process = Process.GetProcessById((int)pid);
        var processName = process.ProcessName;
        var started = process.StartTime.ToUniversalTime();
        var identity = ReadProductIdentity(process);
        return CreateTarget(hwnd, root, (int)pid, processName, started,
            new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), 0) with
            { ProductVersion = identity.ProductVersion, OriginalFilename = identity.OriginalFilename, CompanyName = identity.CompanyName, ProductName = identity.ProductName };
    }

    public static IReadOnlyList<WindowTarget> ListScopedWindows(long rootOwnerHwnd) => ListScopedWindows(Resolve(rootOwnerHwnd));

    public static long ForegroundRootOwnerHwnd() => GetRootOwnerHandle(NativeMethods.GetForegroundWindow()).ToInt64();

    public static IReadOnlyList<WindowTarget> ListScopedWindows(WindowTarget target)
    {
        var rootOwnerHwnd = target.RootOwnerHwnd;
        var root = (nint)rootOwnerHwnd;
        var values = new List<WindowTarget>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (GetRootOwnerHandle(hwnd) != root ||
                (!NativeMethods.IsWindowVisible(hwnd) && hwnd != root)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) ||
                hwnd != root && (rect.Right <= rect.Left || rect.Bottom <= rect.Top)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != target.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.StartTime.ToUniversalTime() != target.ProcessStartedUtc) return true;
                values.Add(CreateTarget(hwnd, root, (int)pid, process.ProcessName, process.StartTime.ToUniversalTime(),
                    new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), values.Count) with
                    { ProductVersion = target.ProductVersion, OriginalFilename = target.OriginalFilename, CompanyName = target.CompanyName, ProductName = target.ProductName });
            }
            catch { }
            return true;
        }, 0);
        return values
            .OrderByDescending(x => x.Hwnd == rootOwnerHwnd)
            .ThenByDescending(x => x.Hwnd == target.Hwnd)
            .ThenBy(x => x.ZOrder)
            .ThenBy(x => x.ClassName, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<WindowTarget> ListProcessWindows(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var values = new List<WindowTarget>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) ||
                !NativeMethods.GetWindowRect(hwnd, out var rect) ||
                rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != target.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var started = process.StartTime.ToUniversalTime();
                if (started != target.ProcessStartedUtc) return true;
                var root = GetRootOwnerHandle(hwnd);
                values.Add(CreateTarget(hwnd, root, (int)pid, process.ProcessName, started,
                    new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), values.Count) with
                    { ProductVersion = target.ProductVersion, OriginalFilename = target.OriginalFilename, CompanyName = target.CompanyName, ProductName = target.ProductName });
            }
            catch { }
            return true;
        }, 0);
        return values
            .OrderByDescending(window => window.Hwnd == target.RootOwnerHwnd)
            .ThenBy(window => window.ZOrder)
            .ToArray();
    }

    public static bool IsSameProcessWindow(WindowTarget target, long hwnd)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            var candidate = Resolve(hwnd);
            return candidate.ProcessId == target.ProcessId &&
                   candidate.ProcessStartedUtc == target.ProcessStartedUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool IsPointWithinScope(WindowTarget target, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            var pointed = NativeMethods.WindowFromPoint(new NativeMethods.Point(x, y));
            if (pointed == 0 || GetRootOwnerHandle(pointed).ToInt64() != target.RootOwnerHwnd)
                return false;
            var current = Resolve(target.RootOwnerHwnd);
            return current.RootOwnerHwnd == target.RootOwnerHwnd &&
                   current.ProcessId == target.ProcessId &&
                   current.ProcessStartedUtc == target.ProcessStartedUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static string GetText(nint hwnd)
    {
        var length = Math.Min(NativeMethods.GetWindowTextLengthW(hwnd), 4_096);
        var chars = new char[length + 1];
        var count = NativeMethods.GetWindowTextW(hwnd, chars, chars.Length);
        return count <= 0 ? string.Empty : new string(chars, 0, count);
    }

    internal static IReadOnlyList<long> ListDescendantHandles(long parentHwnd, int maxHandles = 256)
    {
        if (maxHandles is < 1 or > 4_096) throw new ArgumentOutOfRangeException(nameof(maxHandles));
        var values = new List<long>();
        if (parentHwnd == 0 || !NativeMethods.IsWindow((nint)parentHwnd)) return values;
        NativeMethods.EnumChildWindows((nint)parentHwnd, (hwnd, _) =>
        {
            values.Add(hwnd.ToInt64());
            return values.Count < maxHandles;
        }, 0);
        return values;
    }

    internal static nint GetRootOwnerHandle(nint hwnd)
    {
        if (hwnd == 0) return 0;
        var current = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRootOwner);
        if (current == 0) current = hwnd;
        for (var depth = 0; depth < 32; depth++)
        {
            var owner = NativeMethods.GetWindow(current, NativeMethods.GwOwner);
            if (owner == 0 || owner == current) return current;
            current = NativeMethods.GetAncestor(owner, NativeMethods.GaRootOwner);
            if (current == 0) current = owner;
        }
        return current;
    }

    internal static nint GetTopLevelHandle(nint hwnd)
    {
        if (hwnd == 0) return 0;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        return root == 0 ? hwnd : root;
    }

    internal static string GetClass(nint hwnd)
    {
        var chars = new char[512];
        var count = NativeMethods.GetClassNameW(hwnd, chars, chars.Length);
        return count <= 0 ? string.Empty : new string(chars, 0, count);
    }

    private static WindowTarget CreateTarget(
        nint hwnd,
        nint rootOwner,
        int processId,
        string processName,
        DateTimeOffset processStartedUtc,
        RectI bounds,
        int zOrder)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        return new(hwnd.ToInt64(), rootOwner.ToInt64(), processId, processName, processStartedUtc,
            GetText(hwnd), GetClass(hwnd), bounds, NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner).ToInt64(),
            zOrder, style, exStyle);
    }

    private static bool IsSelectableCatalogCandidate(
        (nint Hwnd, nint RootOwner, RectI Bounds, int ZOrder, long ExStyle, bool IsCloaked) candidate,
        nint foregroundRoot)
    {
        if ((candidate.ExStyle & NativeMethods.WsExToolWindow) == 0) return true;
        if (candidate.RootOwner == foregroundRoot) return true;
        return !string.IsNullOrWhiteSpace(GetText(candidate.Hwnd));
    }

    private static (string ProductVersion, string OriginalFilename, string CompanyName, string ProductName) ReadProductIdentity(Process process)
    {
        try
        {
            var info = process.MainModule?.FileVersionInfo;
            return (info?.ProductVersion ?? string.Empty, info?.OriginalFilename ?? string.Empty,
                info?.CompanyName ?? string.Empty, info?.ProductName ?? string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }
}

using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

/// <summary>Foreground the selected surface once and hold it above other apps until exploration ends.</summary>
internal sealed class GridExplorationWindowScope : IDisposable
{
    private const uint PositionFlags = 0x0213; // No move, resize, activation, or owner reordering.
    private readonly WindowTarget _target;
    private readonly bool _wasTopmost;
    private bool _disposed;

    private GridExplorationWindowScope(WindowTarget target)
    {
        _target = target;
        _wasTopmost = (target.ExStyle & NativeMethods.WsExTopmost) != 0;
    }

    internal static async Task<GridExplorationWindowScope> EnterAsync(WindowTarget selectedApp, long surfaceHwnd,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var surface = WindowCatalog.Resolve(surfaceHwnd);
        if (!SameApplication(surface, selectedApp)) throw new InvalidOperationException("target-lost-or-replaced");
        var handle = (nint)surface.Hwnd;
        if (!NativeMethods.IsWindowVisible(handle) || !NativeMethods.IsWindowEnabled(handle))
            throw new InvalidOperationException("target-activation-failed");

        var scope = new GridExplorationWindowScope(surface);
        try
        {
            if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopMost, 0, 0, 0, 0, PositionFlags) ||
                !await WindowActivation.ActivateAsync(handle, cancellation).ConfigureAwait(false))
                throw new InvalidOperationException("target-activation-failed");
            cancellation.ThrowIfCancellationRequested();
            if (!SameApplication(WindowCatalog.Resolve(surfaceHwnd), surface))
                throw new InvalidOperationException("target-lost-or-replaced");
            // A sibling or modal window from the same app is not the selected table surface.
            if (WindowCatalog.GetTopLevelHandle(NativeMethods.GetForegroundWindow()) != handle ||
                (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTopmost) == 0)
                throw new InvalidOperationException("target-activation-failed");
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Do not touch a replacement window, or reclaim foreground after a user switches apps.
            var current = WindowCatalog.Resolve(_target.Hwnd);
            if (!SameApplication(current, _target) ||
                ((current.ExStyle & NativeMethods.WsExTopmost) != 0) == _wasTopmost) return;
            if (!NativeMethods.SetWindowPos((nint)_target.Hwnd,
                    _wasTopmost ? NativeMethods.HwndTopMost : NativeMethods.HwndNoTopMost, 0, 0, 0, 0, PositionFlags))
                Console.Error.WriteLine("Table exploration: could not restore the target's topmost state.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine($"Table exploration window cleanup: {ex.Message}"); }
    }

    private static bool SameApplication(WindowTarget actual, WindowTarget expected) =>
        actual.ProcessId == expected.ProcessId && actual.ProcessStartedUtc == expected.ProcessStartedUtc &&
        actual.RootOwnerHwnd == expected.RootOwnerHwnd;
}

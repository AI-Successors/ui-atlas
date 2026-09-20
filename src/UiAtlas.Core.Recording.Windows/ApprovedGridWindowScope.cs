using System.Runtime.InteropServices;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Used only after local approval. Raises the exact approved window once and restores its topmost flag.</summary>
public sealed class ApprovedGridWindowScope : IDisposable
{
    private readonly GridTargetIdentity _target;
    private readonly bool _wasTopmost;
    private bool _disposed;
    private ApprovedGridWindowScope(GridTargetIdentity target)
    {
        _target = target;
        _wasTopmost = (WindowCatalog.Resolve(target.SelectedHwnd).ExStyle & NativeMethods.WsExTopmost) != 0;
    }

    public static async Task<ApprovedGridWindowScope> EnterAsync(GridTargetIdentity target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var scope = new ApprovedGridWindowScope(target);
        try
        {
            scope.Validate();
            var handle = (nint)target.SelectedHwnd;
            if (!NativeMethods.IsWindowVisible(handle) || !NativeMethods.IsWindowEnabled(handle) ||
                !NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopMost, 0, 0, 0, 0, 0x0213))
                throw new InvalidOperationException("target-activation-failed");
            _ = SetForegroundWindow(handle);
            await Task.Delay(100, cancellation).ConfigureAwait(false);
            scope.Validate();
            if (WindowCatalog.GetTopLevelHandle(NativeMethods.GetForegroundWindow()) != handle)
                throw new InvalidOperationException("target-activation-failed");
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private void Validate(bool checkGeometry = true)
    {
        using var dpi = new DataGridDpiScope();
        var current = WindowCatalog.Resolve(_target.SelectedHwnd);
        if (current.ProcessId != _target.ProcessId || current.ProcessStartedUtc != _target.ProcessStartedUtc ||
            current.RootOwnerHwnd != _target.RootOwnerHwnd || checkGeometry && current.Bounds != _target.WindowBounds)
            throw new InvalidOperationException("approved-target-changed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Validate(checkGeometry: false);
            if (!NativeMethods.SetWindowPos((nint)_target.SelectedHwnd,
                    _wasTopmost ? NativeMethods.HwndTopMost : NativeMethods.HwndNoTopMost, 0, 0, 0, 0, 0x0213))
                Console.Error.WriteLine("Could not restore the approved window's topmost state.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("Approved window cleanup: " + error.Message); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}

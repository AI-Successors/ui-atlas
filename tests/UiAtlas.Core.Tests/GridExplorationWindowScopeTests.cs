using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Recording.Windows;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class GridExplorationWindowScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivatesCoveredAppAndRestoresTopmostWithoutReclaimingFocus(bool initiallyTopmost)
    {
        Sta(() =>
        {
            var window = new Window { Title = "Explore activation fixture", Width = 420, Height = 240,
                Left = 100, Top = 550, Topmost = initiallyTopmost };
            window.Show();
            using var other = StartForeignWindow(150, 590);
            GridExplorationWindowScope? scope = null;
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                var selected = WindowCatalog.Resolve(handle.ToInt64());
                var line = other.StandardOutput.ReadLineAsync(); Await(() => line.IsCompleted);
                var otherHandle = (nint)long.Parse(line.GetAwaiter().GetResult()!, System.Globalization.CultureInfo.InvariantCulture);
                var activateOther = WindowActivation.ActivateAsync(otherHandle, CancellationToken.None);
                Await(() => activateOther.IsCompleted); Assert.True(activateOther.GetAwaiter().GetResult());
                Assert.Equal(otherHandle, GetForegroundWindow());
                var covered = WindowCatalog.Resolve(otherHandle.ToInt64()).Bounds;
                var point = new NativePoint(covered.X + covered.Width / 2, covered.Y + covered.Height / 2);
                Assert.Equal(otherHandle, GetAncestor(WindowFromPoint(point), 2));

                var enter = GridExplorationWindowScope.EnterAsync(selected, selected.Hwnd, CancellationToken.None);
                Await(() => enter.IsCompleted); scope = enter.GetAwaiter().GetResult();
                Assert.Equal(handle, GetForegroundWindow());
                Assert.True(IsTopmost(selected.Hwnd));
                Assert.Equal(selected.Bounds, WindowCatalog.Resolve(selected.Hwnd).Bounds);
                Assert.Equal(handle, GetAncestor(WindowFromPoint(point), 2));

                // Switching away remains possible; cleanup must not reactivate the target.
                activateOther = WindowActivation.ActivateAsync(otherHandle, CancellationToken.None);
                Await(() => activateOther.IsCompleted); Assert.True(activateOther.GetAwaiter().GetResult());
                scope.Dispose();
                Assert.Equal(otherHandle, GetForegroundWindow());
                Assert.Equal(initiallyTopmost, IsTopmost(selected.Hwnd));
                Assert.Equal(selected.Bounds, WindowCatalog.Resolve(selected.Hwnd).Bounds);
            }
            finally
            {
                scope?.Dispose();
                if (!other.HasExited) { other.CloseMainWindow(); if (!other.WaitForExit(3000)) other.Kill(entireProcessTree: true); }
                window.Close();
            }
        });
    }

    [Fact]
    public void CancellationDuringActivationRestoresTopmost()
    {
        Sta(() =>
        {
            var window = new Window { Title = "Explore cancellation fixture", Width = 420, Height = 240, Left = 100, Top = 550 };
            using var cancellation = new CancellationTokenSource();
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                var selected = WindowCatalog.Resolve(handle.ToInt64());
                // Cancel after the native promotion, before EnterAsync can hand back its cleanup scope.
                var source = HwndSource.FromHwnd(handle)!;
                source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (message == 0x0047 && IsTopmost(selected.Hwnd)) cancellation.Cancel(); // WM_WINDOWPOSCHANGED
                    return 0;
                });
                var enter = GridExplorationWindowScope.EnterAsync(selected, selected.Hwnd, cancellation.Token);
                Await(() => enter.IsCompleted);
                Assert.ThrowsAny<OperationCanceledException>(() => enter.GetAwaiter().GetResult());
                Assert.False(IsTopmost(selected.Hwnd));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DisabledTargetIsNotActivatedOrPromoted()
    {
        Sta(() =>
        {
            var window = new Window { Title = "Blocked explore fixture", Width = 420, Height = 240, Left = 100, Top = 550 };
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                var selected = WindowCatalog.Resolve(handle.ToInt64());
                EnableWindow(handle, false);
                var enter = GridExplorationWindowScope.EnterAsync(selected, selected.Hwnd, CancellationToken.None);
                Await(() => enter.IsCompleted);
                Assert.Equal("target-activation-failed", Assert.Throws<InvalidOperationException>(() => enter.GetAwaiter().GetResult()).Message);
                Assert.False(IsTopmost(selected.Hwnd));
                EnableWindow(handle, true);
            }
            finally { window.Close(); }
        });
    }

    private static bool IsTopmost(long hwnd) => (WindowCatalog.Resolve(hwnd).ExStyle & 8) != 0;
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hwnd, bool enable);
}

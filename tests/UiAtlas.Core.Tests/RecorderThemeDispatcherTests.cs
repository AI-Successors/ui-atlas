using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class RecorderThemeDispatcherTests
{
    [Fact]
    public void RecorderAndGridDetailsHandleSystemThemeRefreshOnTheirWindowDispatchers()
    {
        Sta(() =>
        {
            var targetWindow = new Window { Title = "Theme refresh target fixture", Width = 640, Height = 300,
                Left = 80, Top = 100, Content = new TextBlock { Text = "Synthetic table target" } };
            RecordingControlPanel? panel = null;
            RecordingHighlightOverlay? overlay = null;
            try
            {
                targetWindow.Show();
                var target = WindowCatalog.Resolve(new WindowInteropHelper(targetWindow).Handle.ToInt64());
                panel = new("theme-fixture", "Synthetic target", target); panel.Start();
                overlay = new(target); overlay.Start();
                var control = MapperHighlightTests.Control("Orders", "DataGrid", target.Bounds, "WPF", ["Grid"]);
                overlay.ReplaceMapperHighlights(target.Bounds, new FrameObservation(1, DateTimeOffset.UtcNow, "",
                    WindowSnapshotCapture.Observe(target), [control], false, "ok", "synthetic-theme"));
                Await(() => overlay.MapperHighlights.Count > 0);
                var dispatcher = Field<Dispatcher>(overlay, "_dispatcher");
                dispatcher.Invoke(() =>
                {
                    overlay.InspectGrid(Assert.Single(overlay.MapperHighlights, h => h.IsGrid));
                    RefreshTheme();
                });
                var panelWindow = Field<Window>(panel, "_window");
                panelWindow.Dispatcher.Invoke(RefreshTheme);
                Assert.Same(panelWindow.Dispatcher, overlay.GridInspector!.Dispatcher);
                Assert.Same(panelWindow.Dispatcher, overlay.MapperLegend!.Dispatcher);
                MoveAcrossDisplays(overlay.GridInspector);
            }
            finally { overlay?.Dispose(); panel?.Dispose(); targetWindow.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosingEitherWindowKeepsTheOtherAliveAndAllowsAnotherSession(bool closeOverlayFirst)
    {
        Sta(() =>
        {
            var targetWindow = new Window { Title = "Recorder lifetime target fixture", Width = 640, Height = 300 };
            targetWindow.Show();
            try
            {
                var target = WindowCatalog.Resolve(new WindowInteropHelper(targetWindow).Handle.ToInt64());
                var dispatcher = RecorderUiDispatcher.Instance;
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    RecordingControlPanel? panel = null; RecordingHighlightOverlay? overlay = null;
                    try
                    {
                        // Also exercise Start and Dispose from inside the shared dispatcher.
                        dispatcher.Invoke(() =>
                        {
                            panel = new("lifetime-fixture", "Synthetic target", target); panel.Start();
                            overlay = new(target); overlay.Start();
                            panel.SetStatus("Queued before close");
                            if (closeOverlayFirst) { overlay.Dispose(); overlay = null; }
                            else { panel.Dispose(); panel = null; }
                        });
                        Pump(60); // Drain callbacks belonging to the closed window.
                        Assert.False(dispatcher.HasShutdownStarted);
                        dispatcher.Invoke(() =>
                        {
                            var remaining = panel is not null ? Field<Window>(panel, "_window") : Field<Window>(overlay!, "_window");
                            Assert.True(remaining.IsVisible); RefreshTheme();
                        });
                    }
                    finally { overlay?.Dispose(); panel?.Dispose(); }
                }
                Assert.False(dispatcher.HasShutdownStarted);
            }
            finally { targetWindow.Close(); }
        });
    }

    private static void MoveAcrossDisplays(MapperGridInspector inspector)
    {
        var monitors = new List<NativeRect>();
        Assert.True(EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect bounds, nint data) =>
            { monitors.Add(bounds); return true; }, 0));
        var observed = new List<uint>();
        var changes = 0;
        inspector.Dispatcher.Invoke(() =>
        {
            typeof(MapperGridInspector).GetProperty("UserPositioned", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(inspector, true);
            inspector.DpiChanged += (_, _) => changes++;
        });
        foreach (var monitor in monitors.Concat(monitors.Take(1)))
        {
            inspector.Dispatcher.Invoke(() =>
            {
                var handle = new WindowInteropHelper(inspector).Handle;
                Assert.True(SetWindowPos(handle, 0, monitor.Left + 40, monitor.Top + 40, 0, 0, 0x0015));
            });
            Pump(80);
            inspector.Dispatcher.Invoke(() =>
            {
                RefreshTheme();
                var dpi = GetDpiForWindow(new WindowInteropHelper(inspector).Handle);
                observed.Add(dpi); Assert.True(dpi > 0); Assert.True(inspector.IsVisible);
                Assert.True(VisualTreeHelper.GetDpi(inspector).DpiScaleX > 0);
            });
        }
        Console.WriteLine($"Details-window display traversal: {monitors.Count} displays; native DPI {string.Join(",", observed)}; WPF DPI events {changes}.");
    }

    // Invoke the exact failing framework method synchronously, so a regression is
    // reported as an ordinary assertion failure instead of terminating the test host.
    private static void RefreshTheme()
    {
        var type = typeof(Window).Assembly.GetType("System.Windows.SystemResources", throwOnError: true)!;
        var method = type.GetMethod("SystemThemeFilterMessage", BindingFlags.Static | BindingFlags.NonPublic)!;
        // This is local WPF message handling, not a broadcast or a change to OS settings.
        try { method.Invoke(null, [nint.Zero, 0x001A, nint.Zero, nint.Zero, false]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}

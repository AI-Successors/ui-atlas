using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mcp;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class FloatingCapsuleTests
{
    [Fact]
    public void AppHudRequiresAvailableGridSelectionAndKeepsExactWindowUnderHiddenOwner()
    {
        Sta(() =>
        {
            using var fixture = new SavedGridMcpTests.CatalogFixture();
            var hiddenOwner = new Window { Title = "Hidden application owner" };
            var target = Target();
            FloatingCapsuleWindow? capsule = null;
            try
            {
                var owner = new WindowInteropHelper(hiddenOwner).EnsureHandle();
                new WindowInteropHelper(target).Owner = owner;
                target.Show(); Pump();
                var handle = new WindowInteropHelper(target).Handle;
                var window = WindowCatalog.Resolve(handle);
                var locator = DataGridTargetBinding.DescribeLocator(handle, handle);
                var identity = DataGridTargetBinding.ResolveTarget(locator, handle);
                var table = Bounds(handle) with { Y = Bounds(handle).Y + 80, Height = 130 };
                var available = new GridExplorationChoice("map", fixture.Grid, identity, table);
                var unavailable = available with { Grid = fixture.Grid with { DisplayName = "Other screen" },
                    Target = null, Bounds = null, UnavailableReason = "Open the mapped table first." };
                capsule = new("Orders", "Synthetic app", handle, table, [unavailable, available]);
                var approvals = 0;
                capsule.Approve += () => approvals++;
                capsule.ShowApproval(); Pump();
                var picker = Assert.Single(Descendants<ComboBox>(capsule));
                var approve = Button(capsule, "Explore this table");
                Assert.False(approve.IsEnabled);
                picker.SelectedIndex = 0; Pump();
                Assert.False(approve.IsEnabled);
                Click(approve);
                Assert.Equal(0, approvals);
                picker.SelectedIndex = 1; Pump();
                Assert.True(approve.IsEnabled);
                Assert.Equal(available, capsule.SelectedChoice);
                Assert.Equal(identity, DataGridTargetBinding.ResolveTarget(locator, handle));
                Assert.DoesNotContain(WindowCatalog.ListTopLevelWindows(), w => w.Hwnd == handle.ToInt64());
                GridExplorationService.VerifyApp(window, identity);
                Assert.Throws<InvalidOperationException>(() => GridExplorationService.VerifyApp(window,
                    identity with { ProcessStartedUtc = identity.ProcessStartedUtc.AddSeconds(1) }));
                Save(capsule, "capsule-app-grid-selection.png");
                Click(approve);
                Assert.Equal(1, approvals);
            }
            finally { capsule?.Close(); target.Close(); hiddenOwner.Close(); }
        });
    }

    [Fact]
    public void ApprovalRequiresClickAndActiveCapsuleDoesNotActivateOrOccludeTable()
    {
        Sta(() =>
        {
            var target = Target();
            FloatingCapsuleWindow? capsule = null;
            try
            {
                target.Show(); Pump();
                var handle = new WindowInteropHelper(target).Handle;
                var table = Bounds(handle) with { Y = Bounds(handle).Y + 80, Height = 130 };
                capsule = new("Orders", "Abacre Hotel Management · synthetic fixture", handle, table);
                var approved = 0; var stopped = 0;
                capsule.Approve += () => { approved++; capsule.Hide(); };
                capsule.Stop += () => stopped++;
                capsule.ShowApproval(); Pump();
                Assert.Equal(0, approved);
                Assert.Contains("Waiting for your approval", Labels(capsule));
                Assert.Contains("Visible rows only", Labels(capsule));
                Assert.False(Button(capsule, "Explore this table").IsDefault);
                Save(capsule, "capsule-approval.png");
                Click(Button(capsule, "Explore this table"));
                Assert.Equal(1, approved);
                Assert.False(capsule.IsVisible);
                ActivateFixture(handle); Pump();
                Assert.Equal(handle, GetForegroundWindow());
                capsule.ShowExploration(); Pump();
                var hud = new WindowInteropHelper(capsule).Handle;
                Assert.Equal(handle, GetForegroundWindow());
                Assert.NotEqual(0, GetWindowLongPtrW(hud, -20).ToInt64() & 0x08000000);
                Assert.False(FloatingCapsuleWindow.Intersects(Bounds(hud), table));
                // WM_MOUSEACTIVATE must allow a Stop click without making the HUD foreground.
                Assert.Equal((nint)3, SendMessageW(hud, 0x0021, handle, 0));
                capsule.UpdateProgress(Progress(GridAcquisitionStage.Capture, 7)); Pump();
                Assert.Contains("8 / 24 captures", Labels(capsule));
                Assert.Contains("UI Atlas is controlling your desktop", Labels(capsule));
                Save(capsule, "capsule-active.png");
                Click(Button(capsule, "Stop"));
                Assert.Equal(1, stopped);
                Assert.Contains("Stopping desktop control", Labels(capsule));
                Assert.False(Button(capsule, "Stop").IsEnabled);
                capsule.UpdateProgress(Progress(GridAcquisitionStage.Restore, 8));
                Assert.Contains("UI Atlas is restoring your starting view", Labels(capsule));
                Save(capsule, "capsule-restoring.png");
                capsule.UpdateProgress(Progress(GridAcquisitionStage.Restore, 8) with { Lifecycle = GridAcquisitionLifecycle.Finished });
                capsule.UpdateProgress(Progress(GridAcquisitionStage.Capture, 0));
                Assert.Contains("Finishing exploration", Labels(capsule));
                Assert.DoesNotContain("UI Atlas is controlling your desktop", Labels(capsule));
            }
            finally { capsule?.Close(); target.Close(); }
        });
    }

    [Fact]
    public void FullReadApprovalDisclosesAzureAndKeepsStopEnabledDuringExtraction()
    {
        Sta(() =>
        {
            var target = Target(); FloatingCapsuleWindow? capsule = null;
            try
            {
                target.Show(); Pump(); var handle = new WindowInteropHelper(target).Handle;
                capsule = new("Orders", "Synthetic read fixture", handle, Bounds(handle), readTable: true);
                var approvals = 0; capsule.Approve += () => { approvals++; capsule.Hide(); };
                capsule.ShowApproval(); Pump();
                Assert.Equal(0, approvals);
                Assert.Contains("Current filters · Azure OpenAI cell reading", Labels(capsule));
                Assert.False(Button(capsule, "Extract this table").IsDefault);
                Click(Button(capsule, "Extract this table")); Assert.Equal(1, approvals);
                capsule.ShowExploration();
                capsule.UpdateProgress(Progress(GridAcquisitionStage.Extract, 1) with { Lifecycle = GridAcquisitionLifecycle.Running });
                Pump(); Assert.True(Button(capsule, "Stop").IsEnabled);
                Save(capsule, "capsule-extracting.png");
            }
            finally { capsule?.Close(); target.Close(); }
        });
    }

    [Fact]
    public void HudCannotStartOverTheApprovedTable()
    {
        Sta(() =>
        {
            var target = Target();
            FloatingCapsuleWindow? capsule = null;
            try
            {
                target.Show();
                var handle = new WindowInteropHelper(target).Handle;
                // A table filling the monitor leaves no safe area for a top HUD.
                capsule = new("Orders", "Synthetic fixture", handle,
                    new(-100000, -100000, 200000, 200000));
                capsule.ShowApproval(); capsule.Hide();
                var error = Assert.Throws<InvalidOperationException>(capsule.ShowExploration);
                Assert.Contains("no-room-for-exploration-hud", error.Message);
                Assert.False(capsule.IsVisible);
            }
            finally { capsule?.Close(); target.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitCallerOrHostCancellationClosesPendingApprovalWithoutGrantingControl(bool hostCancellation)
    {
        Sta(() =>
        {
            var target = Target();
            try
            {
                target.Show();
                var handle = new WindowInteropHelper(target).Handle;
                var bounds = Bounds(handle);
                using var stop = new CancellationTokenSource();
                using var timeout = new CancellationTokenSource();
                var open = GridExplorationPresentation.OpenAsync("Cancellation fixture", "Synthetic fixture",
                    new(handle, handle, Environment.ProcessId, DateTimeOffset.UtcNow, handle, bounds, bounds), bounds, stop);
                Await(open);
                var presentation = open.GetAwaiter().GetResult();
                var approval = presentation.ConfirmAsync(timeout.Token);
                Assert.False(approval.IsCompleted);
                Assert.Equal("approval-required", Assert.Throws<InvalidOperationException>(
                    () => presentation.StartAsync().GetAwaiter().GetResult()).Message);
                if (hostCancellation) stop.Cancel(); else timeout.Cancel();
                Await(approval);
                Assert.False(approval.GetAwaiter().GetResult());
                Assert.True(stop.IsCancellationRequested);
                var close = presentation.DisposeAsync().AsTask();
                Await(close); close.GetAwaiter().GetResult();
            }
            finally { target.Close(); }
        });
    }

    private static AcquisitionProgress Progress(GridAcquisitionStage stage, int tiles) =>
        new("fixture", GridAcquisitionLifecycle.Running, stage, 12000, tiles, 0, 6, "fixture");
    private static Window Target() => new() { Title = "Capsule synthetic target", Left = 70, Top = 330,
        Width = 720, Height = 300, Background = Brushes.White, Content = "Synthetic table fixture — no business data" };
    private static IEnumerable<string> Labels(DependencyObject root) => Descendants<TextBlock>(root).Select(t => t.Text);
    private static Button Button(DependencyObject root, string label) => Descendants<Button>(root).Single(b => Equals(b.Content, label));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Save(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("UIATLAS_CAPSULE_QA_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            using var dpi = new FloatingCapsuleWindow.DpiScope();
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Capsule test did not terminate.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump();
        Assert.True(task.IsCompleted, "Capsule dispatcher did not complete.");
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame); timer.Stop();
    }
    private static RectI Bounds(nint hwnd)
    {
        Assert.True(GetWindowRect(hwnd, out var rect));
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    private static void ActivateFixture(nint handle)
    {
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { SetForegroundWindow(handle); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
}

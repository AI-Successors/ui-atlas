using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class IndependentWindowCaptureTests
{
    [Fact]
    public async Task CoveringWindowDoesNotBecomePixelsOrControlsOfItsOwner()
    {
        var ready = new TaskCompletionSource<(Window Root, Window Popup, long RootHwnd, long PopupHwnd)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = new Window { Title = "Independent root", Width = 320, Height = 240, Left = 160, Top = 160,
                Background = Brushes.Blue, ShowActivated = false, Content = new System.Windows.Controls.Button {
                    Content = "Root control", Width = 120, Height = 30, VerticalAlignment = VerticalAlignment.Top } };
            root.Show();
            var popup = new Window { Title = "Independent popup", Owner = root, Width = 200, Height = 140,
                Left = 220, Top = 230, Background = Brushes.Red, ShowActivated = false,
                Content = new System.Windows.Controls.Button { Content = "Popup control", Width = 120, Height = 30, VerticalAlignment = VerticalAlignment.Top } };
            popup.Show();
            ready.SetResult((root, popup, new WindowInteropHelper(root).Handle.ToInt64(), new WindowInteropHelper(popup).Handle.ToInt64()));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var host = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(300);
            var planes = await WindowSnapshotCapture.CaptureIndependentAsync(
                [WindowCatalog.Resolve(host.RootHwnd), WindowCatalog.Resolve(host.PopupHwnd)], 1, CancellationToken.None);
            Assert.Equal(2, planes.Count);
            Assert.All(planes, plane => Assert.Equal("ok", plane.Observation.Status));
            var rootImage = WindowSnapshotCapture.DecodeOpaquePng(planes[0].Png!);
            var center = new byte[4];
            rootImage.CopyPixels(new Int32Rect(rootImage.PixelWidth / 2, rootImage.PixelHeight / 2, 1, 1), center, 4, 0);
            Assert.True(center[0] > 200 && center[2] < 30, "Independent root image must remain blue behind the red popup.");
            foreach (var view in new[] { AutomationTreeView.Raw, AutomationTreeView.Control, AutomationTreeView.Content })
            {
                var records = BoundedAutomationCollector.CollectViewWindow(host.PopupHwnd, view, 100);
                Assert.NotEmpty(records);
                Assert.DoesNotContain(records.Where(control => control.ScopeStatus == "verified"), control => control.Name == "Root control");
                Assert.Contains(records, control => control.Name == "Popup control" && control.ScopeStatus == "verified");
                Assert.All(records, control => Assert.Equal(host.PopupHwnd, control.QueryWindowHwnd));
            }
        }
        finally
        {
            host.Root.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Tests;

[CollectionDefinition("Mapper desktop", DisableParallelization = true)]
public sealed class MapperDesktopCollection;

[Collection("Mapper desktop")]
public sealed class MapperOverlayWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExploreButtonCapturesUiaTableAndHeaderEndToEnd(bool initiallyTopmost)
    {
        Sta(() =>
        {
            var table = new DataGrid { Width = 570, Height = 180, IsReadOnly = true, CanUserAddRows = false,
                AutoGenerateColumns = true, RowHeight = 26, ColumnHeaderHeight = 24, HeadersVisibility = DataGridHeadersVisibility.Column,
                UseLayoutRounding = true, SnapsToDevicePixels = true,
                ColumnWidth = new DataGridLength(175), ItemsSource = Enumerable.Range(1, 9)
                    .Select(i => new { Order = $"Order {i:D3}", Name = $"Guest {i * 17:D3}", Total = $"{i * 13}.45" }).ToArray() };
            var window = new Window { Title = "Table explorer UIA fixture", Width = 650, Height = 310,
                Left = 100, Top = 550, Content = table, Background = Brushes.White, Topmost = true };
            TextOptions.SetTextFormattingMode(table, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(table, TextRenderingMode.Aliased);
            System.Windows.Automation.AutomationProperties.SetName(table, "Fixture table");
            VirtualizingPanel.SetIsVirtualizing(table, false);
            RecordingHighlightOverlay? overlay = null;
            ManualRecordingSession? recorder = null;
            System.Diagnostics.Process? other = null;
            try
            {
                window.Show(); window.Activate(); Pump(250);
                var hwnd = new WindowInteropHelper(window).Handle.ToInt64();
                var activate = WindowActivation.ActivateAsync((nint)hwnd, CancellationToken.None);
                Await(() => activate.IsCompleted); Assert.True(activate.GetAwaiter().GetResult());
                window.Topmost = false; window.Topmost = initiallyTopmost;
                var target = WindowCatalog.Resolve(hwnd);
                var p = table.PointToScreen(new Point()); var q = table.PointToScreen(new Point(table.ActualWidth, table.ActualHeight));
                var bounds = new RectI((int)p.X, (int)p.Y, (int)(q.X - p.X), (int)(q.Y - p.Y));
                // Live refresh must see the same identity as the initial snapshot.
                var collect = Task.Run(() => BoundedAutomationCollector.CollectExactWindow(hwnd, 512, 18));
                Await(() => collect.IsCompleted);
                var control = Assert.Single(collect.GetAwaiter().GetResult(), c =>
                    c.Name == "Fixture table" && c.ControlType == "ControlType.DataGrid");
                var frame = new FrameObservation(1, DateTimeOffset.UtcNow, "", WindowSnapshotCapture.Observe(target), [control], false, "ok", "synthetic-live");
                overlay = new RecordingHighlightOverlay(target); overlay.Start(); overlay.ReplaceMapperHighlights(target.Bounds, frame);
                recorder = new ManualRecordingSession(target, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "explorer-recorder-" + Guid.NewGuid().ToString("N") + ".zip"));
                recorder.Start(explicitConsent: true); recorder.SetInputCapturePaused(true);
                overlay.ExplorationRunner = (action, token) => Task.Run(() => recorder.RunGridExplorationAsync(() => action(token), token));
                Await(() => overlay.GridHitWindows.Count > 0);
                var dispatcher = overlay.GridHitWindows.First().Dispatcher;
                dispatcher.Invoke(() => overlay.InspectGrid(overlay.MapperHighlights.Single(g => g.IsGrid)));
                var inspector = overlay.GridInspector!;
                // Explore must bring back a target covered by a different foreground, topmost app.
                other = StartForeignWindow(window.Left + 50, window.Top + 70);
                var handleTask = other.StandardOutput.ReadLineAsync();
                Await(() => handleTask.IsCompleted);
                var otherHandle = (nint)long.Parse(handleTask.GetAwaiter().GetResult()!, System.Globalization.CultureInfo.InvariantCulture);
                var activateOther = WindowActivation.ActivateAsync(otherHandle, CancellationToken.None);
                Await(() => activateOther.IsCompleted); Assert.True(activateOther.GetAwaiter().GetResult());
                Assert.True(SetWindowPos(otherHandle, (nint)(-1), 0, 0, 0, 0, 0x0013));
                Assert.Equal(otherHandle, GetAncestor(GetForegroundWindow(), 2));
                overlay.ReplaceMapperHighlights(target.Bounds, frame with { Automation = [] });
                Pump(80);
                Assert.Same(inspector, overlay.GridInspector);
                dispatcher.Invoke(() => Descendants<Button>(inspector).Single(b => Equals(b.Content, "Explore"))
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
                var deadline = DateTime.UtcNow.AddSeconds(75);
                var observedTopmost = false;
                while (dispatcher.Invoke(() => inspector.IsExploring) && DateTime.UtcNow < deadline)
                {
                    if (GetAncestor(GetForegroundWindow(), 2).ToInt64() == hwnd &&
                        (WindowCatalog.Resolve(hwnd).ExStyle & 8) != 0) observedTopmost = true;
                    Pump(20);
                }
                var result = dispatcher.Invoke(() => inspector.Result);
                Assert.True(result is not null, dispatcher.Invoke(() => inspector.Error));
                Assert.True(observedTopmost, "The selected app must remain topmost during exploration.");
                Assert.Equal(initiallyTopmost, (WindowCatalog.Resolve(hwnd).ExStyle & 8) != 0);
                Assert.True(result.Capture.Status == GridCaptureStatus.Complete, string.Join(';', result.Reasons));
                Assert.Equal(GridCaptureScope.VisibleRowBand, result.Capture.Scope);
                Assert.True(result.Capture.Coverage.ColumnCoverageComplete);
                Assert.False(result.Capture.Coverage.RowCoverageComplete);
                Assert.False(result.Capture.Coverage.VerticalMovementObserved);
                Assert.Empty(result.Capture.Movements); // All three columns already fit; leave the extra rows alone.
                Assert.Equal(GridRestorationStatus.SafelySkipped, result.Capture.Restoration.Status);
                Assert.Equal("no-scroll-input-issued", result.Capture.Restoration.Reason);
                Assert.All(Descendants<ScrollViewer>(table), view => Assert.Equal(0, view.VerticalOffset));
                Assert.NotNull(result.ImagePath); Assert.True(File.Exists(result.ImagePath));
                Assert.True(result.HeaderBounds.Height > 10);
                Assert.True(result.TableBounds.Height <= bounds.Height);
                Assert.Null(DataGridOperationGate.TryAcquire()); // The paused recorder owns its lease again.
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa)) dispatcher.Invoke(() => Render((FrameworkElement)inspector.Content,
                    System.IO.Path.Combine(qa, "uia-result.png")));
                dispatcher.Invoke(() =>
                {
                    inspector.UpdateLayout();
                    Descendants<Button>(inspector).Single(b => Equals(b.Content, "Extract"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                });
                Pump(50);
                var schemaReview = dispatcher.Invoke(() => inspector.SchemaReviewWindow);
                Assert.NotNull(schemaReview);
                Assert.True(dispatcher.Invoke(() => schemaReview.IsActive));
                // A passive probe can omit the source table while its owned review window is in front.
                overlay.ReplaceMapperHighlights(target.Bounds, frame with { Automation = [] });
                Pump(80);
                Assert.Same(inspector, overlay.GridInspector);
                Assert.True(dispatcher.Invoke(() => schemaReview.IsVisible));
                dispatcher.Invoke(schemaReview.Close);
            }
            finally
            {
                overlay?.Dispose();
                if (recorder is not null) { recorder.Cancel(retain: false); recorder.DisposeAsync().GetAwaiter().GetResult(); }
                if (other is not null)
                {
                    if (!other.HasExited) { other.CloseMainWindow(); if (!other.WaitForExit(3000)) other.Kill(entireProcessTree: true); }
                    other.Dispose();
                }
                window.Close();
            }
        });
    }

    [Fact]
    public void InspectorStatesRenderWithoutClipping()
    {
        Sta(() =>
        {
            foreach (var native in new[] { false, true })
            {
                var c = MapperHighlightTests.Control("Orders", "Table", new(0, 0, 600, 400),
                    native ? "WPF" : "UiAtlas.Visual.Ocr", ["Grid"]);
                var selection = Assert.Single(MapperHighlightModel.Build(c.Bounds, [c]));
                var content = MapperGridInspector.BuildContent(selection);
                content.Width = 342;
                content.Measure(new Size(342, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                Assert.InRange(content.ActualHeight, 200, 380);
                var labels = Descendants<TextBlock>(content).ToArray();
                Assert.Contains(labels, text => text.Text == selection.Classification);
                Assert.Contains(Descendants<Button>(content), button => Equals(button.Content, "Explore"));
                Assert.Contains(labels, text => text.Text.Contains("all columns", StringComparison.OrdinalIgnoreCase) &&
                    text.Text.Contains("sample", StringComparison.OrdinalIgnoreCase));
                Assert.All(labels, text => Assert.True(text.ActualHeight >= text.FontSize));
                var path = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Directory.CreateDirectory(path);
                    Render(content, System.IO.Path.Combine(path, native ? "native-inspector.png" : "candidate-inspector.png"));
                }
            }
        });
    }

    [Fact]
    public void RealWindowOverlayRefreshSelectionCaptureAndTeardown()
    {
        Sta(() =>
        {
            // Keep WPF's UIA provider dispatcher alive across both desktop positions.
            foreach (var left in new[] { 100, 1544 })
            {
                var canvas = new Canvas { Background = Brushes.White };
                var clicks = 0;
                var button = new Button { Content = "Ordinary control", Width = 150, Height = 32 };
                Canvas.SetLeft(button, 30); Canvas.SetTop(button, 20); canvas.Children.Add(button);
                button.Click += (_, _) => clicks++;
                var table = new DataGrid { Width = 760, Height = 200, IsReadOnly = true, AutoGenerateColumns = true,
                    ItemsSource = new[] { new { Order = "101", Name = "Example A", Total = "40.29" },
                        new { Order = "102", Name = "Example B", Total = "37.31" } } };
                Canvas.SetLeft(table, 30); Canvas.SetTop(table, 70); canvas.Children.Add(table);
                var candidate = new Border { Width = 760, Height = 180, BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray, Background = Brushes.WhiteSmoke,
                    Child = new TextBlock { Text = "Potential grid · synthetic test data", Margin = new Thickness(12) } };
                Canvas.SetLeft(candidate, 30); Canvas.SetTop(candidate, 300); canvas.Children.Add(candidate);
                var businessClicks = 0;
                canvas.PreviewMouseDown += (_, _) => businessClicks++;
                var targetWindow = new Window { Title = "Mapper overlay verification — synthetic data",
                    Width = 850, Height = 570, Left = left, Top = 100, Content = canvas, Topmost = true };
                RecordingHighlightOverlay? overlay = null;
                try
                {
                    targetWindow.Show(); targetWindow.Activate(); Pump(100);
                    var hwnd = new WindowInteropHelper(targetWindow).Handle.ToInt64();
                    var activate = WindowActivation.ActivateAsync((nint)hwnd, CancellationToken.None);
                    Await(() => activate.IsCompleted);
                    Assert.True(activate.GetAwaiter().GetResult(), "Synthetic test target did not receive foreground.");
                    targetWindow.Topmost = false; targetWindow.Topmost = true;
                    var target = WindowCatalog.Resolve(hwnd);
                    var automationTask = Task.Run(() => BoundedAutomationCollector.Collect(hwnd, 200, 12));
                    Await(() => automationTask.IsCompleted);
                    var actual = automationTask.GetAwaiter().GetResult();
                    var native = actual.First(c => c.ControlType == "ControlType.DataGrid");
                    var nativeOnly = Assert.Single(MapperHighlightModel.Build(target.Bounds, [native]));
                    Assert.Equal(MapperHighlightKind.NativeGrid, nativeOnly.Kind);
                    RectI Screen(FrameworkElement element)
                    {
                        var p = element.PointToScreen(new Point());
                        var q = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                        return new((int)p.X, (int)p.Y, (int)(q.X - p.X), (int)(q.Y - p.Y));
                    }
                    var visual = MapperHighlightTests.Control("visual:orders", "Table", Screen(candidate), "UiAtlas.Visual.Ocr", ["Grid"]);
                    var frame = new FrameObservation(1, DateTimeOffset.UtcNow, "", WindowSnapshotCapture.Observe(target),
                        [native, visual, MapperHighlightTests.Control("ordinary", "Button", Screen(button))], false, "ok", "synthetic-live");
                    overlay = new RecordingHighlightOverlay(target); overlay.Start();
                    overlay.ReplaceMapperHighlights(target.Bounds, frame);
                    Await(() => overlay.GridHitWindows.Count == 2);
                    var hit = overlay.GridHitWindows.First();
                    hit.Dispatcher.Invoke(() =>
                    {
                        Assert.NotNull(overlay.MapperLegend);
                        Assert.True(overlay.MapperLegend.IsVisible);
                        var legend = overlay.MapperLegend;
                        var hideButton = Assert.Single(Descendants<Button>(legend));
                        hideButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.True(legend.IsCollapsed);
                        Assert.Equal(34, legend.Width);
                        var restoreButton = Assert.Single(Descendants<Button>(legend));
                        restoreButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.False(legend.IsCollapsed);
                        Assert.Same(legend, overlay.MapperLegend);
                        Assert.All(overlay.GridHitWindows, h => Assert.True(h.IsVisible));
                        Assert.Contains(overlay.GridHitWindows, h => RecordingHighlightOverlay.ReadPhysicalBounds(h) == native.Bounds);
                        Assert.Contains(overlay.GridHitWindows, h => RecordingHighlightOverlay.ReadPhysicalBounds(h) == visual.Bounds);
                        hit.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                            { RoutedEvent = UIElement.PreviewMouseDownEvent });
                        Assert.NotNull(overlay.GridInspector);
                        Assert.True(overlay.GridInspector.IsVisible);
                        Assert.True(MapperHighlightModel.Contains(target.Bounds,
                            RecordingHighlightOverlay.ReadPhysicalBounds(overlay.GridInspector)),
                            "The native inspector must remain inside the target's physical bounds.");
                        overlay.InspectGrid(overlay.MapperHighlights.Single(g => g.Kind == MapperHighlightKind.GridCandidate));
                        Assert.Equal("Computer vision", overlay.GridInspector.Selection.Source);
                        Assert.True(MapperHighlightModel.Contains(target.Bounds,
                            RecordingHighlightOverlay.ReadPhysicalBounds(overlay.GridInspector)),
                            "The candidate inspector must remain inside the target's physical bounds.");
                    });
                    Assert.Equal(0, businessClicks);
                    Assert.Equal(0, clicks);
                    var hide = overlay.HideForScreenshotAsync(CancellationToken.None);
                    Await(() => hide.IsCompleted); hide.GetAwaiter().GetResult();
                    hit.Dispatcher.Invoke(() =>
                    {
                        Assert.All(overlay.GridHitWindows, h => Assert.False(h.IsVisible));
                        Assert.False(overlay.GridInspector!.IsVisible);
                        Assert.False(overlay.MapperLegend!.IsVisible);
                    });
                    overlay.RestoreAfterScreenshot();
                    hit.Dispatcher.Invoke(() =>
                    {
                        Assert.All(overlay.GridHitWindows, h => Assert.True(h.IsVisible));
                        Assert.True(overlay.MapperLegend!.IsVisible);
                        Assert.False(overlay.MapperLegend.IsCollapsed);
                    });
                    var hiddenInput = overlay.RunHiddenAsync(() =>
                    {
                        hit.Dispatcher.Invoke(() =>
                        {
                            Assert.All(overlay.GridHitWindows, h =>
                            {
                                Assert.True(h.IsVisible);
                                Assert.False(IsWindowEnabled(new WindowInteropHelper(h).Handle));
                            });
                            Assert.True(overlay.MapperLegend!.IsVisible);
                        });
                        return Task.FromResult(true);
                    }, CancellationToken.None);
                    Await(() => hiddenInput.IsCompleted); Assert.True(hiddenInput.GetAwaiter().GetResult());

                    // Passive updates must work without a new recorded frame or business click.
                    button.Content = "Refreshed without another click";
                    Await(() => hit.Dispatcher.Invoke(() => overlay.MapperHighlights.Any(g => g.Name == "Refreshed without another click")));
                    var legendInstance = overlay.MapperLegend;
                    overlay.ReplaceMapperHighlights(target.Bounds, frame with { Automation = [], AutomationStatus = "not-requested" });
                    hit.Dispatcher.Invoke(() => Assert.NotEmpty(overlay.MapperHighlights));

                    using var other = StartForeignWindow(targetWindow.Left + 40, targetWindow.Top + 180);
                    try
                    {
                        var handleTask = other.StandardOutput.ReadLineAsync();
                        Await(() => handleTask.IsCompleted);
                        var otherHandle = (nint)long.Parse(handleTask.GetAwaiter().GetResult()!, System.Globalization.CultureInfo.InvariantCulture);
                        var otherTarget = WindowCatalog.Resolve(otherHandle.ToInt64());
                        Assert.NotEqual(target.ProcessId, otherTarget.ProcessId);
                        var activateOther = WindowActivation.ActivateAsync(otherHandle, CancellationToken.None);
                        Await(() => activateOther.IsCompleted);
                        Assert.True(activateOther.GetAwaiter().GetResult());
                        _ = SetWindowPos(otherHandle, (nint)(-1), 0, 0, 0, 0, 0x0013);
                        Pump(1500);
                        var covered = new Point(otherTarget.Bounds.X + otherTarget.Bounds.Width / 2,
                            otherTarget.Bounds.Y + otherTarget.Bounds.Height / 2);
                        var exposed = new Point(native.Bounds.X + native.Bounds.Width - 30, native.Bounds.Y + 20);
                        hit.Dispatcher.Invoke(() =>
                        {
                            Assert.True(overlay.MapperLegend!.IsVisible);
                            Assert.Same(legendInstance, overlay.MapperLegend);
                            Assert.False(overlay.IsMapperPointVisible(covered));
                            Assert.True(overlay.IsMapperPointVisible(exposed));
                            var pointed = WindowFromPoint(new NativePoint((int)covered.X, (int)covered.Y));
                            Assert.Equal(otherHandle, GetAncestor(pointed, 2));
                        });
                    }
                    finally
                    {
                        if (!other.HasExited)
                        {
                            other.CloseMainWindow();
                            if (!other.WaitForExit(3000)) other.Kill(entireProcessTree: true);
                        }
                        targetWindow.Activate(); Pump(300);
                    }

                    for (var capture = 0; capture < 3; capture++)
                    {
                        var firstHold = overlay.HideForScreenshotAsync(CancellationToken.None);
                        Await(() => firstHold.IsCompleted); firstHold.GetAwaiter().GetResult();
                        var nestedHold = overlay.HideForScreenshotAsync(CancellationToken.None);
                        Await(() => nestedHold.IsCompleted); nestedHold.GetAwaiter().GetResult();
                        overlay.RestoreAfterScreenshot();
                        hit.Dispatcher.Invoke(() => Assert.False(overlay.MapperLegend!.IsVisible));
                        overlay.RestoreAfterScreenshot();
                        hit.Dispatcher.Invoke(() =>
                        {
                            Assert.True(overlay.MapperLegend!.IsVisible);
                            Assert.All(overlay.GridHitWindows, h => Assert.True(h.IsVisible));
                        });
                    }

                    // An optional bounded hold makes the production overlay available for physical UI smoke testing.
                    if (left == 100 && int.TryParse(Environment.GetEnvironmentVariable("UIATLAS_MAPPER_SMOKE_SECONDS"), out var seconds))
                    {
                        var selectionReceipts = new List<string>();
                        var status = new TextBlock { Foreground = Brushes.DarkSlateGray, FontSize = 12 };
                        Canvas.SetLeft(status, 30); Canvas.SetTop(status, 495); canvas.Children.Add(status);
                        hit.Dispatcher.Invoke(() =>
                        {
                            foreach (var region in overlay.GridHitWindows)
                                region.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) =>
                                {
                                    selectionReceipts.Add(overlay.GridInspector?.Selection.Kind.ToString() ?? "none");
                                }), true);
                        });
                        var statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                            (_, _) => status.Text = $"Application clicks: {businessClicks} · Button clicks: {clicks} · " +
                                $"Selected rows: {table.SelectedItems.Count} · " + hit.Dispatcher.Invoke(() =>
                                    $"Inspector: {overlay.GridInspector?.Selection.Kind.ToString() ?? "closed"}"), Dispatcher.CurrentDispatcher);
                        Pump(Math.Clamp(seconds, 0, 600) * 1000);
                        statusTimer.Stop();
                        Assert.Equal(clicks, businessClicks);
                        Assert.Empty(table.SelectedItems);
                        var qaPath = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                        if (!string.IsNullOrWhiteSpace(qaPath))
                        {
                            Directory.CreateDirectory(qaPath);
                            File.WriteAllText(System.IO.Path.Combine(qaPath, "physical-click-receipts.json"),
                                System.Text.Json.JsonSerializer.Serialize(new { businessClicks, buttonClicks = clicks,
                                    selectedRows = table.SelectedItems.Count,
                                    inspected = hit.Dispatcher.Invoke(() => selectionReceipts.ToArray()) }));
                        }
                    }

                    targetWindow.Left += 25; Pump(400);
                    var movedNative = Screen(table);
                    var movedCandidate = Screen(candidate);
                    hit.Dispatcher.Invoke(() =>
                    {
                        Assert.All(overlay.GridHitWindows, h => Assert.True(h.IsVisible));
                        Assert.Contains(overlay.GridHitWindows, h => RecordingHighlightOverlay.ReadPhysicalBounds(h) == movedNative);
                        Assert.Contains(overlay.GridHitWindows, h => RecordingHighlightOverlay.ReadPhysicalBounds(h) == movedCandidate);
                    });
                    targetWindow.WindowState = WindowState.Minimized; Pump(400);
                    hit.Dispatcher.Invoke(() => Assert.All(overlay.GridHitWindows, h => Assert.False(h.IsVisible)));
                    targetWindow.WindowState = WindowState.Normal;
                    var reactivate = WindowActivation.ActivateAsync((nint)hwnd, CancellationToken.None);
                    Await(() => reactivate.IsCompleted); Assert.True(reactivate.GetAwaiter().GetResult()); Pump(400);
                    targetWindow.Width += 20; Pump(400);
                    Await(() => hit.Dispatcher.Invoke(() => overlay.MapperHighlights.Any(g => g.Kind == MapperHighlightKind.NativeGrid)));
                    hit.Dispatcher.Invoke(() => Assert.True(overlay.MapperLegend!.IsVisible));
                    targetWindow.Width -= 20; Pump(100);
                    overlay.ReplaceMapperHighlights(target.Bounds, frame); Await(() => overlay.GridHitWindows.Count == 2);
                    overlay.ReplaceMapperHighlights(target.Bounds, frame with { Automation = [] });
                    Await(() => overlay.GridHitWindows.Count == 0);
                    hit.Dispatcher.Invoke(() => Assert.True(overlay.MapperLegend!.IsVisible));
                    Await(() => hit.Dispatcher.Invoke(() => overlay.MapperHighlights.Any(g => g.Kind == MapperHighlightKind.NativeGrid)));

                    var stays = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true,
                        ItemsSource = new[] { new { Stay = "201", Room = "Example room" } } };
                    System.Windows.Automation.AutomationProperties.SetName(stays, "Stays grid");
                    System.Windows.Automation.AutomationProperties.SetName(table, "Orders grid");
                    targetWindow.Content = null;
                    var tabs = new TabControl();
                    tabs.Items.Add(new TabItem { Header = "Orders", Content = canvas });
                    tabs.Items.Add(new TabItem { Header = "Stays", Content = stays });
                    targetWindow.Content = tabs;
                    foreach (var index in new[] { 1, 0, 1 })
                    {
                        tabs.SelectedIndex = index;
                        var expectedName = index == 1 ? "Stays grid" : "Orders grid";
                        Await(() => hit.Dispatcher.Invoke(() => overlay.MapperHighlights.Any(g =>
                            g.Kind == MapperHighlightKind.NativeGrid && g.Name == expectedName)));
                        hit.Dispatcher.Invoke(() =>
                        {
                            Assert.NotEmpty(overlay.GridHitWindows);
                            Assert.True(overlay.MapperLegend!.IsVisible);
                        });
                    }
                }
                finally { overlay?.Dispose(); targetWindow.Close(); }
                Assert.Null(overlay?.MapperLegend);
            }
        });
    }

    private static readonly Lazy<Dispatcher> DesktopDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); })
            { IsBackground = true, Name = "Mapper desktop fixture dispatcher" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    internal static void Sta(Action action) => DesktopDispatcher.Value.InvokeAsync(action).Task
        .WaitAsync(TimeSpan.FromMinutes(12)).GetAwaiter().GetResult();

    internal static System.Diagnostics.Process StartForeignWindow(double left, double top)
    {
        // A separate synthetic process is essential: same-process focus was exempt from the old hiding rule.
        var start = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell/v1.0/powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-STA"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(FormattableString.Invariant($"""
            Add-Type -AssemblyName PresentationFramework
            $window = [System.Windows.Window]::new()
            $window.Title = 'Unrelated synthetic verification window'
            $window.Topmost = $true
            $window.Width = 160; $window.Height = 100; $window.Left = {left}; $window.Top = {top}
            $window.Content = 'Separate application'
            $window.Show()
            [Console]::WriteLine(([System.Windows.Interop.WindowInteropHelper]::new($window)).Handle.ToInt64())
            [Console]::Out.Flush()
            $app = [System.Windows.Application]::new()
            $app.Run($window) | Out-Null
            """));
        return System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic focus target.");
    }
    internal static void Await(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) Pump(20);
        Assert.True(predicate(), "Expected mapper state was not reached.");
    }
    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame); timer.Stop();
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    internal static void Render(FrameworkElement content, string path)
    {
        var width = Math.Max(content.DesiredSize.Width, content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = Math.Max(content.DesiredSize.Height, content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

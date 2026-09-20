using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mcp;

/// <summary>The capsule never overlays the approved capture rectangle during exploration.</summary>
internal sealed class FloatingCapsuleWindow : Window
{
    private string _table;
    private readonly string _application;
    private readonly long _target;
    private RectI _tableBounds;
    private readonly IReadOnlyList<GridExplorationChoice>? _choices;
    private readonly bool _readTable;
    private string _phaseLimit = "01:00";
    internal GridExplorationChoice? SelectedChoice { get; private set; }
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _headline = Text("UI Atlas is controlling your desktop", 14, "#FFFFFF", true);
    private readonly TextBlock _stage = Text("Preparing capture", 12, "#D6DCE6");
    private readonly TextBlock _time = Text("00:00 / 01:00", 12, "#FFFFFF");
    private readonly TextBlock _hint = Text("Move your mouse or type to take over", 11, "#E5E9F0");
    private readonly TextBlock _captures = Text("0 / 24 captures", 12, "#FFFFFF");
    private readonly Button _stop;
    private bool _approved;
    private bool _stopping;
    private bool _finished;
    internal bool IsExploring { get; private set; }
    internal event Action? Approve;
    internal event Action? Cancel;
    internal event Action? Stop;

    internal FloatingCapsuleWindow(string table, string application, long target, RectI tableBounds,
        IReadOnlyList<GridExplorationChoice>? choices = null, bool readTable = false)
    {
        _table = table; _application = application; _target = target; _tableBounds = tableBounds;
        _choices = choices;
        _readTable = readTable;
        if (readTable) { _phaseLimit = "02:00"; _time.Text = "00:00 / 02:00"; }
        Title = "UI Atlas — Explore " + table;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new("Segoe UI");
        UseLayoutRounding = true;
        SizeToContent = SizeToContent.Height;
        _stop = ActionButton("Stop", "#E32F46", "#FFFFFF", () =>
        {
            ShowStopping();
            Stop?.Invoke();
        });
        _stop.MinWidth = 86;
        _timer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => _time.Text = FormatElapsed(_elapsed.Elapsed) + " / " + _phaseLimit, Dispatcher);
        _timer.Stop();
        Closed += (_, _) => { _timer.Stop(); _elapsed.Stop(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (IsExploring) { ShowStopping(); Stop?.Invoke(); }
            else Cancel?.Invoke();
        };
    }

    internal void ShowApproval()
    {
        Width = 504;
        var stack = new StackPanel { Margin = new(12) };
        var pill = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        pill.Children.Add(Dot("#98A2B3"));
        pill.Children.Add(Text("UI Atlas", 15, "#FFFFFF", true));
        pill.Children.Add(Divider());
        pill.Children.Add(Text("Waiting for your approval", 13, "#E5E9F0"));
        stack.Children.Add(Surface(pill, "#252A32", 28, new(18, 14, 18, 14)));
        var stem = new Polygon { Points = new([new(0, 0), new(16, 0), new(8, 8)]), Fill = Brush("#252A32"),
            HorizontalAlignment = HorizontalAlignment.Center, Width = 16, Height = 8 };
        stack.Children.Add(stem);

        var card = new StackPanel();
        card.Children.Add(Text(_readTable ? "Extract this table?" : _choices is { Count: > 1 } ? "Choose a data grid" : "Explore this table?", 24, "#101828", true));
        var app = Text(_application, 13, "#475467");
        app.Margin = new(0, 5, 0, 22); app.MaxHeight = 40;
        card.Children.Add(app);
        var selected = new Grid();
        selected.ColumnDefinitions.Add(new() { Width = new(72) });
        selected.ColumnDefinitions.Add(new());
        // A table glyph, never a fabricated preview or an unapproved screen capture.
        var icon = new Border { Width = 56, Height = 56, CornerRadius = new(10), Background = Brush("#EFF5FF"),
            Child = new TextBlock { Text = "\uE80A", FontFamily = new("Segoe MDL2 Assets"), FontSize = 27,
                Foreground = Brush("#1764E8"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
        selected.Children.Add(icon);
        var summary = new StackPanel();
        var name = Text(_choices is { Count: > 1 } ? "Select a saved table below" : _table, 16, "#101828", true);
        name.MaxHeight = 46; summary.Children.Add(name);
        summary.Children.Add(Text(_readTable ? "Read all reachable rows and columns." : "Read all columns across the visible rows.", 14, "#344054"));
        var scope = Text(_readTable ? "Current filters · Azure OpenAI cell reading" : "Visible rows only", 12, "#667085"); scope.Margin = new(0, 4, 0, 0); summary.Children.Add(scope);
        Grid.SetColumn(summary, 1); selected.Children.Add(summary); card.Children.Add(selected);
        ComboBox? picker = null;
        var availability = Text("", 12, "#475467");
        if (_choices is not null)
        {
            picker = new ComboBox { ItemsSource = _choices, DisplayMemberPath = nameof(GridExplorationChoice.Label),
                Margin = new(0, 16, 0, 4), MinHeight = 36, FontSize = 13, MaxDropDownHeight = 220 };
            AutomationProperties.SetName(picker, "Saved data grids");
            availability.MaxHeight = 64;
            card.Children.Add(picker);
            card.Children.Add(availability);
        }
        var limits = new UniformGrid { Columns = 3, Margin = new(0, 22, 0, 18) };
        limits.Children.Add(Limit("\uE823", _readTable ? "120 sec max" : "60 sec max"));
        limits.Children.Add(Limit("\uE8AB", _readTable ? "192 moves max" : "32 moves max"));
        limits.Children.Add(Limit("\uE722", _readTable ? "96 captures max" : "24 captures max"));
        card.Children.Add(new Border { BorderBrush = Brush("#EAECF0"), BorderThickness = new(0, 1, 0, 1),
            Margin = new(0, 20, 0, 14), Child = limits });
        var note = Text(_readTable ? "Scroll, capture and send table crops to saved Azure.\nNo records changed. Restore the view when safe.\nAzure reading may take up to 3 minutes after capture." : "Scroll and capture only. No records changed.\nWe will try to restore your starting position.", 12, "#667085");
        note.TextAlignment = TextAlignment.Center; card.Children.Add(note);
        var buttons = new Grid { Margin = new(0, 20, 0, 0) };
        buttons.ColumnDefinitions.Add(new()); buttons.ColumnDefinitions.Add(new() { Width = new(12) }); buttons.ColumnDefinitions.Add(new());
        var cancel = ActionButton("Cancel", "#E9EDF3", "#1D2939", () => Cancel?.Invoke());
        cancel.IsCancel = true; cancel.IsDefault = true;
        buttons.Children.Add(cancel);
        var approve = ActionButton(_readTable ? "Extract this table" : "Explore this table", "#1469EF", "#FFFFFF", () =>
        {
            if (_approved || _choices is not null && SelectedChoice?.Target is null) return;
            _approved = true;
            Approve?.Invoke();
        });
        if (picker is not null)
        {
            approve.IsEnabled = false;
            picker.SelectionChanged += (_, _) =>
            {
                SelectedChoice = picker.SelectedItem as GridExplorationChoice;
                approve.IsEnabled = SelectedChoice?.Target is not null;
                if (SelectedChoice is not { } choice) return;
                _table = choice.Grid.DisplayName;
                name.Text = _table;
                if (choice.Bounds is { } selectedBounds) _tableBounds = selectedBounds;
                availability.Text = choice.UnavailableReason ??
                    string.Join(", ", choice.Grid.Review.Schema.Columns.Select(c => c.Label));
                availability.ToolTip = availability.Text;
            };
            if (_choices!.Count == 1) picker.SelectedIndex = 0;
        }
        Grid.SetColumn(approve, 2); buttons.Children.Add(approve); card.Children.Add(buttons);
        var once = Text("Approve once for this table · Expires in 2 minutes", 11, "#667085");
        once.TextAlignment = TextAlignment.Center; once.Margin = new(0, 12, 0, 0); card.Children.Add(once);
        var panel = Surface(card, "#FFFFFF", 18, new(24));
        panel.BorderBrush = Brush("#DDE2EA"); panel.BorderThickness = new(1);
        stack.Children.Add(panel); Content = stack;
        PrepareHandle();
        PositionAtTop(false);
        Show(); UpdateLayout(); PositionAtTop(false);
        cancel.Focus();
    }

    internal void ShowExploration()
    {
        Hide();
        IsExploring = true;
        ShowActivated = false;
        var handle = new WindowInteropHelper(this).Handle;
        Native.SetWindowLongPtrW(handle, -20, Native.GetWindowLongPtrW(handle, -20) | (nint)0x08000000); // WS_EX_NOACTIVATE
        var monitor = Monitor();
        var scale = Math.Max(1, Native.GetDpiForWindow(handle)) / 96d;
        Width = Math.Min(980, (monitor.Right - monitor.Left) / scale - 24);
        if (Width < 680) throw new InvalidOperationException("no-room-for-exploration-hud; leave space above the table");
        var row = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new() { Width = width });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 20, 0) };
        brand.Children.Add(Dot("#398BFF")); brand.Children.Add(Text("UI Atlas", 15, "#FFFFFF", true));
        row.Children.Add(brand);
        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 18, 0) };
        status.Children.Add(_headline);
        var detail = Text(_table + " · " + _application, 11, "#B6BFCD");
        detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis; detail.ToolTip = detail.Text;
        status.Children.Add(detail); status.Children.Add(_stage);
        Grid.SetColumn(status, 1); row.Children.Add(status);
        _time.VerticalAlignment = VerticalAlignment.Center; _time.Margin = new(0, 0, 18, 0);
        Grid.SetColumn(_time, 2); row.Children.Add(_time);
        _captures.VerticalAlignment = VerticalAlignment.Center; _captures.Margin = new(0, 0, 18, 0);
        Grid.SetColumn(_captures, 3); row.Children.Add(_captures);
        Grid.SetColumn(_stop, 4); row.Children.Add(_stop);
        var content = new StackPanel { Margin = new(10) };
        content.Children.Add(Surface(row, "#252A32", 34, new(20, 12, 16, 12)));
        var hintSurface = Surface(_hint, "#333943", 7, new(12, 5, 12, 5));
        hintSurface.HorizontalAlignment = HorizontalAlignment.Center; hintSurface.Margin = new(0, 5, 0, 0);
        content.Children.Add(hintSurface); Content = content;
        Show(); UpdateLayout();
        try { PositionAtTop(true); }
        catch { Hide(); throw; }
        _elapsed.Restart(); _timer.Start();
    }

    internal void UpdateProgress(AcquisitionProgress progress)
    {
        if (!IsExploring || _finished) return;
        // Coordinator tiles exclude the one preparation capture. No guessed coverage percentage.
        _captures.Text = $"{progress.TileCount + 1} / {(_readTable ? 96 : 24)} captures";
        if (_readTable && progress.Stage == GridAcquisitionStage.Extract)
        {
            if (_phaseLimit != "03:00") { _phaseLimit = "03:00"; _elapsed.Restart(); }
            _time.Text = FormatElapsed(_elapsed.Elapsed) + " / " + _phaseLimit;
            _hint.Text = "Capture finished. You can use your desktop.";
            _headline.Text = "UI Atlas is reading the captured table";
            _stage.Text = progress.Reason;
            return;
        }
        if (progress.Lifecycle == GridAcquisitionLifecycle.Finished)
        {
            _finished = true; _elapsed.Stop(); _timer.Stop();
            _headline.Text = "Finishing exploration"; _stage.Text = "Preparing captured image";
            _stop.IsEnabled = false;
        }
        else if (progress.Stage == GridAcquisitionStage.Restore)
        {
            _headline.Text = "UI Atlas is restoring your starting view";
            _stage.Text = "Restoring when safe";
        }
        else if (!_stopping)
        {
            _headline.Text = "UI Atlas is controlling your desktop";
            _stage.Text = progress.Stage == GridAcquisitionStage.Probe ? "Reading starting view" : "Reading columns";
        }
    }

    internal void ShowStarting(int seconds)
    {
        _headline.Text = $"Capture starts in {seconds}…";
        _stage.Text = "Please leave the mouse and keyboard idle during capture";
    }

    internal void ShowStopping()
    {
        if (!IsExploring || _finished) return;
        _stopping = true;
        _headline.Text = "Stopping desktop control";
        _stage.Text = "Waiting for a safe stop";
        _stop.IsEnabled = false;
    }

    private void PrepareHandle()
    {
        var interop = new WindowInteropHelper(this) { Owner = (nint)_target };
        var handle = interop.EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (IsExploring && message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE, still deliver Stop clicks.
            return 0;
        });
    }

    private Native.Rectangle Monitor()
    {
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfoW(Native.MonitorFromWindow((nint)_target, 2), ref info))
            throw new InvalidOperationException("exploration-monitor-unavailable");
        return info.Work;
    }

    private void PositionAtTop(bool avoidTable)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = Monitor();
        var scale = Math.Max(1, Native.GetDpiForWindow(handle)) / 96d;
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 520) * scale);
        var x = monitor.Left + (monitor.Right - monitor.Left - width) / 2;
        var y = monitor.Top + 8;
        var area = new RectI(x, y, width, height);
        if (width > monitor.Right - monitor.Left || height > monitor.Bottom - monitor.Top - 8 ||
            avoidTable && Intersects(area, _tableBounds))
            throw new InvalidOperationException("no-room-for-exploration-hud; move the table below the top of the screen");
        if (!Native.SetWindowPos(handle, (nint)(-1), x, y, width, height, 0x0010))
            throw new InvalidOperationException("exploration-hud-position-failed");
        if (avoidTable && (!Native.GetWindowRect(handle, out var actual) ||
            Intersects(new(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top), _tableBounds)))
            throw new InvalidOperationException("no-room-for-exploration-hud; move the table below the top of the screen");
    }

    internal sealed class DpiScope : IDisposable
    {
        private readonly nint _previous = Native.SetThreadDpiAwarenessContext((nint)(-4));
        public void Dispose() { if (_previous != 0) Native.SetThreadDpiAwarenessContext(_previous); }
    }

    internal static bool Intersects(RectI a, RectI b) => a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width &&
        a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;
    internal static string FormatElapsed(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    private static Border Surface(UIElement child, string color, double radius, Thickness padding) => new()
    {
        Child = child, Background = Brush(color), CornerRadius = new(radius), Padding = padding,
        Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = .16, Color = Colors.Black }
    };
    private static TextBlock Text(string text, double size, string color, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static Ellipse Dot(string color) => new() { Width = 10, Height = 10, Fill = Brush(color), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private static Border Divider() => new() { Width = 1, Height = 22, Background = Brush("#56606F"), Margin = new(18, 0, 18, 0) };
    private static UIElement Limit(string glyph, string caption)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = new("Segoe MDL2 Assets"), FontSize = 17, Foreground = Brush("#344054"), Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Text(caption, 12, "#1D2939", true));
        return row;
    }
    private static Button ActionButton(string label, string background, string foreground, Action action)
    {
        var button = new Button { Content = label, Background = Brush(background), Foreground = Brush(foreground),
            FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new(14, 10, 14, 10), MinHeight = 44,
            BorderThickness = new(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(OpacityProperty, .85)); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .5)); template.Triggers.Add(disabled);
        button.Template = template;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rectangle { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rectangle Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rectangle rect);
        [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
        [DllImport("user32.dll")] internal static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    }
}

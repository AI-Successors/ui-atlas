using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal sealed class MapperGridInspector : Window
{
    internal MapperHighlight Selection { get; private set; }
    internal bool UserPositioned { get; private set; }
    internal bool IsExploring => _cancellation is not null;
    internal GridImageExplorationResult? Result => _result;
    internal string? Error => _error;
    internal GridSchemaReviewWindow? SchemaReviewWindow { get; private set; }
    internal GridSchemaReview? SavedSchema { get; private set; }
    internal bool IsReviewingSchema => SchemaReviewWindow is not null || _settingsWindow is not null;
    internal Func<MapperHighlight, IProgress<AcquisitionProgress>, CancellationToken, Task<GridImageExplorationResult>>? Explore { get; set; }
    internal Func<GridImageExplorationResult, RectI, IReadOnlyList<int>?, CancellationToken, Task<GridSchemaReview>>? SchemaExtractor { get; set; }
    internal event Action? ExplorationStateChanged;
    internal event Action? Finished;
    internal Action<string, GridSchemaReview>? SaveDataGrid { get; set; }
    internal DataGridReviewContext? ReviewContext { get; set; }
    internal event Action? NamingStarted;
    private string? _gridName;
    private bool _closed;
    private CancellationTokenSource? _cancellation;
    private GridImageExplorationResult? _result;
    private string? _error;
    private TextBlock? _progress;
    private RecorderSettingsWindow? _settingsWindow;

    internal MapperGridInspector(MapperHighlight selection)
    {
        Selection = selection; Title = "UiAtlas table explorer";
        Width = 360; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Inter, Segoe UI"); FontSize = 13;
        Render();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (IsExploring) _cancellation?.Cancel(); else Close(); } };
        Closed += (_, _) => { _closed = true; _cancellation?.Cancel(); if (!IsExploring) Finished?.Invoke(); };
    }

    internal void Update(MapperHighlight selection)
    {
        if (Selection.Id == selection.Id && Selection.Bounds == selection.Bounds) { Selection = selection; return; }
        SchemaReviewWindow?.Close(); _settingsWindow?.Close(); SavedSchema = null;
        _cancellation?.Cancel(); Selection = selection; _result = null; _error = null;
        if (!IsExploring) Render();
    }

    internal async Task StartExplorationAsync()
    {
        if (IsExploring || IsReviewingSchema || Explore is null) return;
        var selection = Selection;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; _result = null; _error = null; SavedSchema = null;
        Render(); ExplorationStateChanged?.Invoke();
        try
        {
            var progress = new Progress<AcquisitionProgress>(p =>
            {
                if (_progress is not null) _progress.Text = p.Stage == GridAcquisitionStage.Restore
                    ? "Returning to the starting view…" : $"{p.TileCount} views captured · {p.ElapsedMs / 1000}s";
            });
            var result = await Explore(selection, progress, cancellation.Token);
            if (Selection.Id == selection.Id) _result = result;
        }
        catch (OperationCanceledException) { _error = "Exploration stopped."; }
        catch (Exception ex) { Console.Error.WriteLine($"Table exploration: {ex.Message}"); _error = FriendlyReason(ex.Message); }
        finally
        {
            _cancellation = null;
            if (_closed) Finished?.Invoke();
            else if (!Dispatcher.HasShutdownStarted) { Render(); ExplorationStateChanged?.Invoke(); }
        }
    }

    private void Render()
    {
        Width = _result?.ImagePath is null ? 360 : 510;
        var shell = BuildContent(Selection, Close, (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || IsExploring) return;
            UserPositioned = true; DragMove();
        }, includeAction: false);
        var stack = (StackPanel)shell.Child;
        if (SavedSchema is not null)
        {
            stack.Children.Add(Label($"Schema ready · {SavedSchema.Schema.Columns.Count} columns", 16, "#087D6D"));
            stack.Children.Add(Label("Data grid name", 13, "#333943"));
            _gridName ??= SuggestGridName(Selection.Name, SavedSchema);
            var name = new TextBox { Text = _gridName, MaxLength = 200, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(name, "Data grid name");
            stack.Children.Add(name);
            stack.Children.Add(Label("Save this data grid for the current screen. Recording resumes when you save or close this window.", 12, "#697386"));
            var error = Label("", 12, "#AF7200"); stack.Children.Add(error);
            var save = ActionButton("Save", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || SaveDataGrid is null) return;
                try { SaveDataGrid(name.Text.Trim(), SavedSchema); Close(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { error.Text = "The data grid could not be saved. Try again or close to resume recording."; }
            }, true);
            save.IsEnabled = SaveDataGrid is not null && !string.IsNullOrWhiteSpace(name.Text);
            name.TextChanged += (_, _) => { _gridName = name.Text; save.IsEnabled = SaveDataGrid is not null && !string.IsNullOrWhiteSpace(name.Text); };
            stack.Children.Add(save);
            var edit = ActionButton("Edit schema", OpenSchemaReview, false); edit.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(edit);
            Content = shell;
            return;
        }
        if (IsExploring)
        {
            stack.Children.Clear();
            stack.Children.Add(Label("Exploring table…", 16, "#17191D"));
            _progress = Label("Identifying the table and header row…", 12, "#697386"); stack.Children.Add(_progress);
            stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 12, 0, 12) });
            stack.Children.Add(ActionButton("Stop", () => _cancellation?.Cancel(), primary: false));
        }
        else if (_result?.ImagePath is { } path)
        {
            var complete = _result.Capture.Status == GridCaptureStatus.Complete;
            var rowBand = _result.Capture.Scope == GridCaptureScope.VisibleRowBand;
            stack.Children.Add(Label(complete ? rowBand ? "Columns explored" : "Table captured" : "Partial capture", 16, complete ? "#087D6D" : "#AF7200"));
            stack.Children.Add(Label(complete ? rowBand ? "Full width captured; rows are a sample" : "Table and header row identified" : "Only the verified area is shown", 12, "#697386"));
            stack.Children.Add(BuildPreview(path, _result.HeaderBounds));
            var restore = _result.Capture.Restoration;
            stack.Children.Add(Label(restore.Status == GridRestorationStatus.Succeeded ? "Returned to the starting view" :
                restore.Reason.Contains("no-scroll-input", StringComparison.Ordinal) ? "Starting view unchanged" :
                restore.Status == GridRestorationStatus.SafelySkipped ? "View restoration skipped safely" :
                restore.PositionVerified ? "Starting position restored; image changed" : "Could not restore the starting view", 11, "#697386"));
            if (!complete) stack.Children.Add(Label(FriendlyReason(_result.Reasons.FirstOrDefault() ?? "capture-incomplete",
                _result.Capture.Joins.LastOrDefault()?.Axis), 12, "#AF7200"));
            var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
            var again = ActionButton("Explore again", () => _ = StartExplorationAsync(), false); again.Margin = new Thickness(0, 0, 8, 0);
            var save = ActionButton("Save image", SaveImage, false); save.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(save, 1);
            var extract = ActionButton("Extract", OpenSchemaReview, true); Grid.SetColumn(extract, 2);
            actions.Children.Add(again); actions.Children.Add(save); actions.Children.Add(extract); stack.Children.Add(actions);
            if (SavedSchema is not null) stack.Children.Add(Label($"Schema saved · {SavedSchema.Schema.Columns.Count} columns", 12, "#087D6D"));
        }
        else
        {
            if (_result is not null) _error = FriendlyReason(_result.Reasons.FirstOrDefault() ?? "capture-incomplete");
            if (_error is not null) stack.Children.Add(Label(_error, 12, "#AF7200"));
            AddReady(stack, () => _ = StartExplorationAsync());
            if (_result is not null)
            {
                var extract = ActionButton("Extract", OpenSchemaReview, true);
                extract.IsEnabled = _result.Capture.Tiles.Count > 0;
                extract.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(extract);
                stack.Children.Add(Label(extract.IsEnabled ? "Review headers from a retained view." :
                    "No image was captured. Explore again to identify its headers.", 11, "#697386"));
            }
        }
        if (!IsExploring)
        {
            var settings = ActionButton("Header reading settings", OpenHeaderSettings, false);
            settings.Margin = new Thickness(0, 8, 0, 0);
            settings.ToolTip = "Choose Local OCR or Azure OpenAI for reading column headers.";
            stack.Children.Add(settings);
        }
        Content = shell;
    }

    private void OpenHeaderSettings()
    {
        if (IsExploring) return;
        if (_settingsWindow is { } existing) { existing.Activate(); return; }
        var settings = new RecorderSettingsWindow { Owner = this };
        _settingsWindow = settings;
        try { settings.ShowDialog(); }
        finally { _settingsWindow = null; }
    }

    internal void OpenSchemaReview()
    {
        if (IsExploring || _result is null) return;
        if (SchemaReviewWindow is { } existing) { existing.Activate(); return; }
        try
        {
            var source = GridSchemaExtractor.PrepareReviewSource(_result);
            var review = new GridSchemaReviewWindow(source, SavedSchema, SchemaExtractor) { Owner = this };
            SchemaReviewWindow = review;
            review.Closed += (_, _) =>
            {
                SavedSchema = review.SavedReview ?? SavedSchema; SchemaReviewWindow = null;
                if (IsVisible)
                {
                    Render();
                    if (SavedSchema is not null) { NamingStarted?.Invoke(); Activate(); }
                }
            };
            review.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Schema review: {ex.Message}");
            MessageBox.Show(this, "The captured image could not be opened. Explore again and retry.", "Extract schema");
        }
    }

    private FrameworkElement BuildPreview(string path, RectI header)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 10) };
        var canvas = new Canvas { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Background = Brush("#E9EDF3") };
        canvas.Children.Add(new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight });
        var tableOutline = new Rectangle { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight,
            Stroke = Brush("#087D6D"), StrokeThickness = 3, IsHitTestVisible = false };
        var headerOutline = new Rectangle { Width = header.Width, Height = header.Height,
            Stroke = Brush("#D48A00"), Fill = new SolidColorBrush(Color.FromArgb(35, 255, 181, 44)), StrokeThickness = 2, IsHitTestVisible = false };
        Canvas.SetLeft(headerOutline, header.X); Canvas.SetTop(headerOutline, header.Y);
        canvas.Children.Add(tableOutline); canvas.Children.Add(headerOutline);
        var fit = Math.Min(1, Math.Min(432d / bitmap.PixelWidth, 215d / bitmap.PixelHeight));
        var scale = new ScaleTransform(fit, fit); canvas.LayoutTransform = scale;
        panel.Children.Add(new ScrollViewer { Content = canvas, Height = 235, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = Brush("#E1E5EC"), BorderThickness = new Thickness(1) });
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var zoom = new ComboBox { Width = 76, ItemsSource = new[] { "Fit", "100%", "200%" }, SelectedIndex = 0 };
        zoom.SelectionChanged += (_, _) => scale.ScaleX = scale.ScaleY = zoom.SelectedIndex == 0 ? fit : zoom.SelectedIndex;
        DockPanel.SetDock(zoom, Dock.Right); row.Children.Add(zoom);
        var show = new CheckBox { Content = "Show table & header", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        show.Checked += (_, _) => tableOutline.Visibility = headerOutline.Visibility = Visibility.Visible;
        show.Unchecked += (_, _) => tableOutline.Visibility = headerOutline.Visibility = Visibility.Hidden;
        row.Children.Add(show); panel.Children.Add(row); return panel;
    }

    private void SaveImage()
    {
        if (_result?.ImagePath is not { } path) return;
        var save = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = "table.png", AddExtension = true, DefaultExt = ".png" };
        if (save.ShowDialog(this) != true) return;
        try { if (!string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(save.FileName), StringComparison.OrdinalIgnoreCase))
            System.IO.File.Copy(path, save.FileName, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "The image could not be saved. Choose another location.", "Save image"); }
    }

    internal static string SuggestGridName(string selectionName, GridSchemaReview schema)
    {
        var name = selectionName.Trim();
        if (name.Length > 0 && !new[] { "Table", "DataGrid", "Data grid", "Grid", "Grid candidate" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            return name.Length > 200 ? name[..200] : name;
        var labels = schema.Schema.Columns.Select(column => column.Label.Trim()).Where(label => label.Length > 0).Distinct().Take(2);
        var suggestion = string.Join(" / ", labels);
        return suggestion.Length == 0 ? "Data grid" : (suggestion.Length > 190 ? suggestion[..190] : suggestion) + " table";
    }

    internal static Border BuildContent(MapperHighlight selection, Action? close = null,
        MouseButtonEventHandler? drag = null, bool includeAction = true)
    {
        var stack = new StackPanel { Margin = new Thickness(20, 12, 20, 18) };
        var grip = new Border { Width = 30, Height = 4, CornerRadius = new CornerRadius(2), Background = Brush("#CDD3DD"), IsHitTestVisible = false };
        // Include the surrounding top padding in the drag target without moving the grip or heading.
        var dragArea = new Border { Background = Brushes.Transparent, Margin = new Thickness(-20, -12, -20, 0),
            Padding = new Thickness(20, 12, 20, 10), Child = grip };
        if (drag is not null) { dragArea.MouseLeftButtonDown += drag; dragArea.Cursor = Cursors.SizeAll; }
        stack.Children.Add(dragArea);
        var heading = new DockPanel { Background = Brushes.Transparent };
        if (drag is not null) heading.MouseLeftButtonDown += drag;
        var closeButton = new Button { Content = "×", Width = 26, Height = 26, FontSize = 20,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Close table explorer" };
        closeButton.Click += (_, _) => close?.Invoke(); DockPanel.SetDock(closeButton, Dock.Right); heading.Children.Add(closeButton);
        heading.Children.Add(Label("TABLE EXPLORER", 11, "#7F8896")); stack.Children.Add(heading);
        stack.Children.Add(Label("▦  " + selection.Name, 19, "#17191D"));
        stack.Children.Add(Label(selection.Classification, 12, selection.Color));
        stack.Children.Add(new Border { Height = 1, Background = Brush("#E1E5EC"), Margin = new Thickness(0, 12, 0, 12) });
        if (includeAction) AddReady(stack, () => { });
        return new Border { Background = Brush("#F9F9FA"), BorderBrush = Brush("#DCE1E9"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22), Child = stack, Margin = new Thickness(6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = .16 } };
    }

    private static void AddReady(StackPanel stack, Action explore)
    {
        stack.Children.Add(Label("Capture the table and identify its header row.", 13, "#333943"));
        stack.Children.Add(Label("Explore scrolls horizontally to identify all columns, using the visible rows as a sample. Up to 60s / 24 views. Stops if you take control and returns to the starting view when safe.", 11, "#697386"));
        var button = ActionButton("Explore", explore, true); button.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(button);
    }
    private static TextBlock Label(string text, double size, string color) => new()
        { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private static Button ActionButton(string text, Action action, bool primary)
    {
        var button = new Button { Content = text, Height = 36, FontSize = 13, Cursor = Cursors.Hand,
            Background = Brush(primary ? "#1769E0" : "#FFFFFF"), Foreground = primary ? Brushes.White : Brush("#333943"),
            BorderBrush = Brush(primary ? "#1769E0" : "#DCE1E9"), BorderThickness = new Thickness(1) };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action(); return button;
    }
    internal static string FriendlyReason(string reason, GridScrollAxis? axis = null) => reason switch
    {
        "operation-busy" => "Finish the current recording before exploring this table.",
        "header-row-not-identified" => "Could not identify a header row in this table.",
        "human-takeover" => "Stopped because you took control.",
        "foreground-lost" => "Stopped because the app is no longer in front.",
        "target-activation-failed" => "Could not bring the app to the front. Close any blocking dialog and try again.",
        "cancelled" => "Exploration stopped.",
        "duration-limit" or "tile-limit" or "movement-limit" or "png-byte-limit" => "Exploration reached its limit. More content may remain.",
        "table-scroll-support-unavailable" => "This table does not expose safe scrolling.",
        "no-room-for-explorer" => "Move or resize the app to leave room for the Stop control beside the table.",
        "no-compatible-content-overlap" or "ambiguous-repeated-content" when axis == GridScrollAxis.Horizontal =>
            "Stopped because the overlap after horizontal scrolling could not be verified. More columns may remain.",
        "no-progress-without-boundary" when axis == GridScrollAxis.Horizontal =>
            "Horizontal scrolling stopped making progress before the right or left edge could be verified.",
        "no-compatible-content-overlap" or "ambiguous-repeated-content" or "no-progress-without-boundary" =>
            "Stopped because the next view could not be matched reliably.",
        "unsafe-occluded-table" => "Keep the table fully visible, then try again.",
        _ => "Could not verify the complete table. Keep it fully visible and try again."
    };
    internal static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}

using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;
using Path = System.IO.Path;

namespace UiAtlas.Core.Cli;

/// <summary>Review only the retained image; no live target or input authority is needed.</summary>
internal sealed class GridSchemaReviewWindow : Window
{
    private readonly GridImageExplorationResult _source;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Canvas _canvas;
    private readonly ScaleTransform _previewScale = new();
    private readonly Rectangle _headerHalo;
    private readonly Rectangle _headerOutline;
    private readonly List<Line> _lines = [];
    private readonly DataGrid _columns;
    private readonly TextBlock _message;
    private readonly TextBlock _selectionLabel;
    private readonly Button _extract;
    private readonly Button _readerSettings;
    private readonly TextBlock _readerLabel;
    private readonly Button _save;
    private readonly CheckBox _editDividers;
    private readonly WrapPanel _dividerTools;
    private readonly Button _moveLeft, _moveRight, _removeDivider, _addDivider;
    private readonly TextBlock _dividerHelp;
    private readonly string _imageHash;
    private readonly int _imageWidth, _imageHeight;
    private readonly Func<GridImageExplorationResult, RectI, IReadOnlyList<int>?, CancellationToken, Task<GridSchemaReview>> _extractor;
    private Point? _dragStart;
    private bool _draggingDivider, _addingDivider;
    private double _dividerDragOffset;
    private int _selectedDivider = -1;
    private List<int>? _dividers;
    private List<ColumnRow> _rows = [];
    private bool _closed;
    internal bool IsBusy { get; private set; }
    internal RectI HeaderSelection { get; private set; }
    internal GridSchemaReview? Draft { get; private set; }
    internal GridSchemaReview? SavedReview { get; private set; }
    internal string? SavedPath { get; private set; }

    internal GridSchemaReviewWindow(GridImageExplorationResult source, GridSchemaReview? saved = null,
        Func<GridImageExplorationResult, RectI, IReadOnlyList<int>?, CancellationToken, Task<GridSchemaReview>>? extractor = null)
    {
        _source = source;
        _extractor = extractor ?? ExtractConfiguredAsync;
        Title = "Extract table schema"; Width = 820; Height = 740; MinWidth = 620; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Topmost = true;
        Background = MapperGridInspector.Brush("#F9F9FA"); FontFamily = new("Inter, Segoe UI"); FontSize = 13;
        var bytes = File.ReadAllBytes(source.ImagePath ?? throw new InvalidOperationException("schema-image-unavailable"));
        _imageHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var stream = new MemoryStream(bytes);
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); bitmap.Freeze();
        _imageWidth = bitmap.PixelWidth; _imageHeight = bitmap.PixelHeight;
        HeaderSelection = source.HeaderBounds.IsValid ? source.HeaderBounds : new(0, 0, _imageWidth, Math.Min(24, _imageHeight));

        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(170) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Text("Identify the header row", 23, "#17191D"));
        heading.Children.Add(Text(source.Capture.Status == GridCaptureStatus.Complete ?
            source.Capture.Scope == GridCaptureScope.VisibleRowBand ? "All columns captured · rows are a sample" : "Captured table" :
            "Partial capture · review the captured columns; more columns may exist.", 12,
            source.Capture.Status == GridCaptureStatus.Complete ? "#087D6D" : "#AF7200"));
        heading.Children.Add(Text("Drag around the header row, then extract its column names. You can correct the names before saving.", 13));
        heading.Margin = new(0, 0, 0, 12); root.Children.Add(heading);

        _canvas = new Canvas { Width = _imageWidth, Height = _imageHeight, Background = Brushes.White, Focusable = true,
            ToolTip = "Drag to select the header. Arrow keys move it; Shift+Up/Down changes its height." };
        _canvas.Children.Add(new Image { Source = bitmap, Width = _imageWidth, Height = _imageHeight, IsHitTestVisible = false });
        _headerHalo = new Rectangle { Stroke = Brushes.White, IsHitTestVisible = false };
        _headerOutline = new Rectangle { Stroke = MapperGridInspector.Brush("#C47B00"), StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 181, 44)), IsHitTestVisible = false };
        _canvas.Children.Add(_headerHalo); _canvas.Children.Add(_headerOutline);
        Panel.SetZIndex(_headerHalo, 2); Panel.SetZIndex(_headerOutline, 3);
        _canvas.MouseLeftButtonDown += (_, e) => { BeginPreviewInteraction(e.GetPosition(_canvas)); e.Handled = true; };
        _canvas.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) ContinuePreviewInteraction(e.GetPosition(_canvas)); };
        _canvas.MouseLeftButtonUp += (_, e) => { EndPreviewInteraction(); e.Handled = true; };
        _canvas.LostMouseCapture += (_, _) => { _dragStart = null; _draggingDivider = false; };
        _canvas.KeyDown += MoveSelection;
        _canvas.LayoutTransform = _previewScale;
        var preview = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = MapperGridInspector.Brush("#E9EDF3") };
        Grid.SetRow(preview, 1); root.Children.Add(preview);

        var controls = new StackPanel { Margin = new(0, 8, 0, 10) }; Grid.SetRow(controls, 2); root.Children.Add(controls);
        var toolbar = new DockPanel();
        var zoom = new ComboBox { ItemsSource = new[] { "Fit", "100%", "200%" }, SelectedIndex = 0, Width = 85 };
        System.Windows.Automation.AutomationProperties.SetName(zoom, "Header preview zoom");
        void Zoom() { var fit = Math.Min(1, Math.Min(Math.Max(1, preview.ViewportWidth) / _imageWidth,
            Math.Max(1, preview.ViewportHeight) / _imageHeight));
            _previewScale.ScaleX = _previewScale.ScaleY = zoom.SelectedIndex == 0 ? fit : zoom.SelectedIndex;
            DrawSelection(); }
        zoom.SelectionChanged += (_, _) => Zoom(); preview.SizeChanged += (_, _) => Zoom(); Loaded += (_, _) => Zoom();
        DockPanel.SetDock(zoom, Dock.Right); toolbar.Children.Add(zoom);
        _editDividers = new CheckBox { Content = "Edit column dividers", VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Select and drag a blue line, or use the divider controls." };
        _editDividers.Checked += (_, _) => ChangeDividerMode();
        _editDividers.Unchecked += (_, _) => ChangeDividerMode();
        toolbar.Children.Add(_editDividers); controls.Children.Add(toolbar);
        _dividerTools = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new(0, 6, 0, 0) };
        _moveLeft = Button("Move left", () => NudgeDivider(-1), false);
        _moveRight = Button("Move right", () => NudgeDivider(1), false);
        _removeDivider = Button("Remove divider", RemoveSelectedDivider, false);
        _addDivider = Button("Add divider", () => { _addingDivider = !_addingDivider; UpdateDividerControls(); _canvas.Focus(); }, false);
        foreach (var button in new[] { _moveLeft, _moveRight, _removeDivider, _addDivider })
        { button.Margin = new(0, 0, 6, 4); _dividerTools.Children.Add(button); }
        controls.Children.Add(_dividerTools);
        _dividerHelp = Text("", 12, "#333943"); _dividerHelp.Visibility = Visibility.Collapsed; controls.Children.Add(_dividerHelp);
        _selectionLabel = Text("", 12, "#697386"); controls.Children.Add(_selectionLabel);
        var readerRow = new DockPanel();
        _readerSettings = Button("Header reading settings", () =>
        { new RecorderSettingsWindow { Owner = this }.ShowDialog(); RefreshReaderLabel(); }, false);
        _readerSettings.Height = 28; DockPanel.SetDock(_readerSettings, Dock.Right); readerRow.Children.Add(_readerSettings);
        _readerLabel = Text("", 12, "#697386"); readerRow.Children.Add(_readerLabel); controls.Children.Add(readerRow);
        RefreshReaderLabel();
        var readRow = new DockPanel();
        _extract = Button("Extract schema", () => _ = ExtractAsync(), true); _extract.Width = 160;
        DockPanel.SetDock(_extract, Dock.Right); readRow.Children.Add(_extract);
        readRow.Children.Add(Text("Review each name below, or right-click a row to remove a column. Duplicate and blank headers are allowed.", 12, "#697386"));
        controls.Children.Add(readRow);

        _columns = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserSortColumns = false, CanUserReorderColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Background = Brushes.White };
        _columns.Columns.Add(new DataGridTextColumn { Header = "Column", Binding = new Binding("Number"), IsReadOnly = true, Width = 65 });
        _columns.Columns.Add(new DataGridTextColumn { Header = "Header name", Binding = new Binding("Label") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _columns.Columns.Add(new DataGridTextColumn { Header = "Reading", Binding = new Binding("Reading"), IsReadOnly = true, Width = 150 });
        _columns.LoadingRow += (_, e) => AddColumnMenu(e.Row);
        Grid.SetRow(_columns, 3); root.Children.Add(_columns);
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) }; Grid.SetRow(footer, 4); root.Children.Add(footer);
        _message = Text("Select the header row and choose Extract schema.", 12, "#697386"); footer.Children.Add(_message);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new()); buttons.ColumnDefinitions.Add(new());
        var back = Button("Back to image", Close, false); back.Margin = new(0, 0, 8, 0); buttons.Children.Add(back);
        _save = Button("Save schema", () => _ = SaveAsync(closeAfterSave: true), true); _save.IsEnabled = false;
        Grid.SetColumn(_save, 1); buttons.Children.Add(_save); footer.Children.Add(buttons);
        Content = new Border { Padding = new Thickness(22), Background = Background, Child = root };
        if (saved is not null && saved.ImageSha256 == _imageHash)
        {
            HeaderSelection = saved.HeaderBounds; SavedReview = saved; SetDraft(saved);
        }
        DrawSelection(); UpdateDividerControls();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    internal void SelectHeader(RectI bounds)
    {
        if (IsBusy || bounds.X < 0 || bounds.Y < 0 || bounds.Width < 8 || bounds.Height < 4 ||
            (long)bounds.X + bounds.Width > _imageWidth || (long)bounds.Y + bounds.Height > _imageHeight) return;
        if (HeaderSelection == bounds) return;
        HeaderSelection = bounds; _dividers = null; _selectedDivider = -1; _addingDivider = false;
        InvalidateDraft(); DrawSelection();
    }

    private static Task<GridSchemaReview> ExtractConfiguredAsync(GridImageExplorationResult source, RectI header,
        IReadOnlyList<int>? dividers, CancellationToken cancellation)
    {
        var store = new AzureHeaderSettingsStore(); var settings = store.Load();
        var extractor = settings.Enabled ? new GridSchemaExtractor(store.CreateReader(settings)) : new GridSchemaExtractor();
        return extractor.ExtractAsync(source, header, dividers, cancellation);
    }
    private void RefreshReaderLabel()
    {
        try { _readerLabel.Text = new AzureHeaderSettingsStore().Load().Enabled ? "Header reader: Azure · GPT-5.6 Sol · Light" : "Header reader: Local OCR"; }
        catch { _readerLabel.Text = "Header settings need attention"; }
    }

    internal async Task ExtractAsync()
    {
        if (IsBusy || _closed) return;
        SetBusy(true); _message.Text = "Reading the selected headers…";
        try
        {
            var draft = await _extractor(_source, HeaderSelection, _dividers, _lifetime.Token);
            if (_closed) return;
            if (draft.ImageSha256 != _imageHash) throw new InvalidOperationException("schema-image-changed");
            SetDraft(draft);
            _message.Text = draft.Reasons.Contains("column-dividers-not-detected")
                ? "No column dividers were detected. Add any missing dividers in the preview, then extract again."
                : $"{draft.Schema.Columns.Count} columns found. Review the names, then save the schema.";
        }
        catch (OperationCanceledException) { if (!_closed) _message.Text = "Header extraction stopped."; }
        catch (Exception ex) { if (!_closed) _message.Text = Error(ex); }
        finally { if (!_closed) SetBusy(false); }
    }

    internal async Task SaveAsync(string? path = null, bool closeAfterSave = false)
    {
        if (IsBusy || Draft is null || _closed) return;
        _columns.CommitEdit(DataGridEditingUnit.Cell, true); _columns.CommitEdit(DataGridEditingUnit.Row, true);
        SetBusy(true);
        try
        {
            var reviewed = GridSchemaExtractor.Confirm(Draft, _rows.Select(row => row.Label).ToArray());
            path ??= Path.Combine(Path.GetDirectoryName(_source.ImagePath!)!, "schema-review.json");
            await GridSchemaExtractor.SaveAsync(reviewed, path, _lifetime.Token);
            SavedReview = reviewed; SavedPath = path;
            if (!_closed) _message.Text = $"Schema saved · {reviewed.Schema.Columns.Count} columns." +
                (reviewed.Schema.IsComplete ? "" : " Column coverage remains partial.");
            if (!_closed && closeAfterSave) { SetBusy(false); Close(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _message.Text = Error(ex); }
        finally { if (!_closed) SetBusy(false); }
    }

    private void SetDraft(GridSchemaReview draft)
    {
        Draft = draft;
        _rows = draft.Schema.Columns.Select((column, i) => new ColumnRow { Number = i + 1, Label = column.Label,
            Reading = draft.ReviewedUtc is not null ? "Reviewed" :
                draft.HeaderReadings[i].Status == GridCellReadStatus.Text ? "Read · review name" : "Needs your input" }).ToList();
        _columns.ItemsSource = _rows;
        // Removed columns leave their neighbours at the original image coordinates.
        _dividers = draft.HeaderReadings.SelectMany(reading => new[] { reading.Bounds.X, reading.Bounds.X + reading.Bounds.Width })
            .Where(x => x > HeaderSelection.X && x < HeaderSelection.X + HeaderSelection.Width).Distinct().Order().ToList();
        _selectedDivider = -1; _addingDivider = false;
        DrawSelection(); _save.IsEnabled = !IsBusy;
    }

    private void AddColumnMenu(DataGridRow row)
    {
        var remove = new MenuItem { Header = "Remove column" };
        var menu = new ContextMenu(); menu.Items.Add(remove); row.ContextMenu = menu;
        menu.Opened += (_, _) =>
        {
            // Resolve the current item because DataGrid row containers can be recycled.
            _columns.SelectedItem = row.Item;
            remove.IsEnabled = !IsBusy && !_closed && Draft is not null && _rows.Count > 1 && row.Item is ColumnRow column && _rows.Contains(column);
            remove.ToolTip = _rows.Count <= 1 ? "Keep at least one column in the schema." : null;
        };
        remove.Click += (_, _) => { if (row.Item is ColumnRow column) RemoveColumn(column); };
    }

    private void RemoveColumn(ColumnRow row)
    {
        if (IsBusy || _closed || Draft is null || _rows.Count <= 1) return;
        var index = _rows.IndexOf(row);
        if (index < 0 || !_columns.CommitEdit(DataGridEditingUnit.Cell, true) || !_columns.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var key = Draft.Schema.Columns[index].ColumnKey;
        var columns = Draft.Schema.Columns.Select((column, i) => column with { Label = _rows[i].Label })
            .Where((_, i) => i != index).Select((column, i) => column with { Ordinal = i }).ToArray();
        SetDraft(Draft with
        {
            Schema = Draft.Schema with { Revision = "draft-" + Guid.NewGuid().ToString("N"), Columns = columns,
                IsComplete = false, KnownColumnCount = null },
            HeaderReadings = Draft.HeaderReadings.Where(reading => reading.ColumnKey != key).ToArray(),
            // Omitting a physical interval must not claim coverage of every captured column.
            Reasons = Draft.Reasons.Append("schema-width-unverified").Distinct(StringComparer.Ordinal).ToArray(),
            ReviewedUtc = null
        });
        _columns.SelectedIndex = Math.Min(index, _rows.Count - 1);
        _message.Text = $"Column {index + 1} removed · {_rows.Count} columns remaining. Save the schema to keep this change.";
    }

    private void InvalidateDraft()
    {
        Draft = null; _columns.ItemsSource = null; _save.IsEnabled = false;
        _message.Text = "Selection changed. Extract the headers again before saving.";
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy; _extract.IsEnabled = _canvas.IsEnabled = _editDividers.IsEnabled = _columns.IsEnabled = _readerSettings.IsEnabled = !busy;
        _save.IsEnabled = !busy && Draft is not null;
        UpdateDividerControls();
    }

    private void ChangeDividerMode()
    {
        EndPreviewInteraction(); _addingDivider = false; _selectedDivider = -1;
        DrawSelection();
    }

    internal void BeginPreviewInteraction(Point point)
    {
        if (IsBusy || _closed) return;
        _canvas.Focus();
        if (_editDividers.IsChecked == true)
        {
            if (point.Y < HeaderSelection.Y || point.Y > _imageHeight) return;
            if (_addingDivider)
            {
                var x = (int)Math.Round(point.X);
                if (x - HeaderSelection.X < 4 || HeaderSelection.X + HeaderSelection.Width - x < 4 ||
                    (_dividers?.Count ?? 0) >= 127 || _dividers?.Any(d => Math.Abs(d - x) < 4) == true)
                { _dividerHelp.Text = "Choose a position between the existing dividers, inside the selected row."; return; }
                _dividers ??= []; _dividers.Add(x); _dividers.Sort();
                _selectedDivider = _dividers.IndexOf(x); _addingDivider = false;
                InvalidateDraft(); DrawSelection();
            }
            else
            {
                _selectedDivider = NearestDivider(point.X);
                _draggingDivider = _selectedDivider >= 0;
                if (_draggingDivider) { _dividerDragOffset = point.X - _dividers![_selectedDivider]; _canvas.CaptureMouse(); }
                DrawSelection();
            }
        }
        else { _dragStart = point; _canvas.CaptureMouse(); }
    }

    internal void ContinuePreviewInteraction(Point end)
    {
        if (IsBusy || _closed) return;
        if (_draggingDivider) { MoveDividerTo((int)Math.Round(end.X - _dividerDragOffset)); return; }
        if (_dragStart is not { } start) return;
        var x = (int)Math.Clamp(Math.Min(start.X, end.X), 0, _imageWidth - 1);
        var y = (int)Math.Clamp(Math.Min(start.Y, end.Y), 0, _imageHeight - 1);
        var right = (int)Math.Clamp(Math.Max(start.X, end.X), 0, _imageWidth);
        var bottom = (int)Math.Clamp(Math.Max(start.Y, end.Y), 0, _imageHeight);
        SelectHeader(new(x, y, right - x, bottom - y));
    }

    internal void EndPreviewInteraction()
    {
        _dragStart = null; _draggingDivider = false; _canvas.ReleaseMouseCapture();
    }

    private int NearestDivider(double x)
    {
        if (_dividers is not { Count: > 0 }) return -1;
        var nearest = _dividers.Select((position, index) => (position, index)).MinBy(d => Math.Abs(d.position - x));
        return Math.Abs(nearest.position - x) * _previewScale.ScaleX <= 8 ? nearest.index : -1;
    }

    private bool HasSelectedDivider => _dividers is not null && _selectedDivider >= 0 && _selectedDivider < _dividers.Count;

    private (int Left, int Right) DividerLimits() =>
        (_selectedDivider == 0 ? HeaderSelection.X + 4 : _dividers![_selectedDivider - 1] + 4,
         _selectedDivider == _dividers!.Count - 1 ? HeaderSelection.X + HeaderSelection.Width - 4 : _dividers[_selectedDivider + 1] - 4);

    private void MoveDividerTo(int x)
    {
        if (IsBusy || _addingDivider || _editDividers.IsChecked != true || !HasSelectedDivider) return;
        var (left, right) = DividerLimits(); x = Math.Clamp(x, left, right);
        if (_dividers![_selectedDivider] == x) return;
        _dividers[_selectedDivider] = x; InvalidateDraft(); DrawSelection();
    }

    private void NudgeDivider(int delta)
    {
        if (HasSelectedDivider) MoveDividerTo(_dividers![_selectedDivider] + delta);
    }

    private void RemoveSelectedDivider()
    {
        if (IsBusy || _addingDivider || _editDividers.IsChecked != true || !HasSelectedDivider) return;
        _dividers!.RemoveAt(_selectedDivider); _selectedDivider = -1; _addingDivider = false;
        InvalidateDraft(); DrawSelection();
    }

    private void UpdateDividerControls()
    {
        var editing = _editDividers.IsChecked == true;
        _dividerTools.Visibility = _dividerHelp.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        var selected = editing && !IsBusy && !_addingDivider && HasSelectedDivider;
        _removeDivider.IsEnabled = selected;
        _moveLeft.IsEnabled = selected && _dividers![_selectedDivider] > DividerLimits().Left;
        _moveRight.IsEnabled = selected && _dividers![_selectedDivider] < DividerLimits().Right;
        _addDivider.IsEnabled = editing && !IsBusy && (_dividers?.Count ?? 0) < 127;
        _addDivider.Content = _addingDivider ? "Cancel adding" : "Add divider";
        System.Windows.Automation.AutomationProperties.SetName(_addDivider, (string)_addDivider.Content);
        _canvas.Cursor = _addingDivider ? Cursors.Cross : editing ? Cursors.SizeWE : Cursors.Cross;
        _dividerHelp.Text = _addingDivider ? "Click in the table to place a new divider. Press Esc to cancel." :
            HasSelectedDivider ? $"Divider {_selectedDivider + 1} selected. Drag to move; arrow keys fine-tune; Delete removes it." :
            "Select a blue line to move or remove it, or choose Add divider.";
    }

    private void MoveSelection(object sender, KeyEventArgs e)
    {
        if (IsBusy) return;
        if (_editDividers.IsChecked == true)
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            if (e.Key == Key.Left) NudgeDivider(-step);
            else if (e.Key == Key.Right) NudgeDivider(step);
            else if (e.Key == Key.Delete) RemoveSelectedDivider();
            else if (e.Key == Key.Escape) { _addingDivider = false; EndPreviewInteraction(); DrawSelection(); }
            else return;
            e.Handled = true; return;
        }
        var dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        var dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (dx == 0 && dy == 0) return;
        SelectHeader((Keyboard.Modifiers & ModifierKeys.Shift) != 0
            ? HeaderSelection with { Width = HeaderSelection.Width + dx, Height = HeaderSelection.Height + dy }
            : HeaderSelection with { X = HeaderSelection.X + dx, Y = HeaderSelection.Y + dy });
        e.Handled = true;
    }

    private void DrawSelection()
    {
        // Keep overlays legible in Fit view; image coordinates still define the exact schema geometry.
        var unit = 1 / Math.Max(.0001, _previewScale.ScaleX);
        foreach (var outline in new[] { _headerHalo, _headerOutline })
        {
            outline.StrokeThickness = (outline == _headerHalo ? 4 : 2) * unit;
            // Rectangle already draws its stroke inside its layout bounds.
            Canvas.SetLeft(outline, HeaderSelection.X); Canvas.SetTop(outline, HeaderSelection.Y);
            outline.Width = HeaderSelection.Width; outline.Height = HeaderSelection.Height;
        }
        foreach (var line in _lines) _canvas.Children.Remove(line); _lines.Clear();
        for (var i = 0; i < (_dividers?.Count ?? 0); i++)
        {
            var x = _dividers![i]; var selected = i == _selectedDivider && _editDividers.IsChecked == true;
            foreach (var halo in new[] { true, false })
            {
                var line = new Line { X1 = x, X2 = x, Y1 = HeaderSelection.Y, Y2 = _imageHeight,
                    Stroke = halo ? Brushes.White : MapperGridInspector.Brush(selected ? "#003D9E" : "#1769E0"),
                    StrokeThickness = (halo ? selected ? 6 : 4 : selected ? 3 : 1.5) * unit, IsHitTestVisible = false };
                _lines.Add(line); _canvas.Children.Add(line);
            }
        }
        _selectionLabel.Text = "Amber rectangle: selected header row. Blue lines: column dividers.";
        UpdateDividerControls();
    }

    private static string Error(Exception ex) => ex is AzureHeaderException ? ex.Message : ex.Message switch
    {
        "schema-image-changed" => "The captured image changed. Close this review and open it again.",
        "schema-dividers-invalid" => "Column dividers must be distinct and at least four pixels apart.",
        "schema-header-outside-image" => "Select a header row inside the captured image.",
        _ when ex is IOException or UnauthorizedAccessException => "The image or schema file could not be accessed. Check its location and try again.",
        _ => "The headers could not be read. Adjust the selection or column dividers and try again."
    };
    private static TextBlock Text(string value, double size, string color = "#333943") => new()
        { Text = value, FontSize = size, Foreground = MapperGridInspector.Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 5) };
    private static Button Button(string value, Action action, bool primary)
    {
        var button = new Button { Content = value, Height = 36, Padding = new(10, 0, 10, 0),
            Background = MapperGridInspector.Brush(primary ? "#1769E0" : "#FFFFFF"), Foreground = primary ? Brushes.White : MapperGridInspector.Brush("#333943"),
            BorderBrush = MapperGridInspector.Brush(primary ? "#1769E0" : "#DCE1E9") };
        System.Windows.Automation.AutomationProperties.SetName(button, value);
        button.Click += (_, _) => action(); return button;
    }
    internal sealed class ColumnRow
    {
        public int Number { get; init; }
        public string Label { get; set; } = "";
        public string Reading { get; init; } = "";
    }
}

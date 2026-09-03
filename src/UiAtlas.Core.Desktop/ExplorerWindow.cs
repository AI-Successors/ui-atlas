using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Data;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace UiAtlas.Core.Desktop;

public sealed class ExplorerWindow : Window
{
    private const double AppMapZoomStep = 1.15;
    private const double AppMapMinZoomFloor = 0.05;
    private const double AppMapMaxZoom = 8.0;
    private const double HierarchyPanelWidth = 295;
    private const double HeaderHierarchyWidth = 319;
    private static readonly Brush Page = Brush("#F7F8FC");
    private static readonly Brush Card = Brushes.White;
    private static readonly Brush UiBorder = Brush("#E2E5EF");
    private static readonly Brush Ink = Brush("#252936");
    private static readonly Brush Muted = Brush("#70778B");
    private static readonly Brush Blue = Brush("#4F7EF7");
    private static readonly Brush Orange = Brush("#FF8B3D");
    private static readonly Brush Violet = Brush("#8B5CF6");
    private static readonly Brush Green = Brush("#2DCE91");
    private static readonly Style HierarchyTreeItemStyle = CreateHierarchyTreeItemStyle();
    private static readonly Style ModernComboBoxStyle = CreateModernComboBoxStyle();
    private static readonly Lazy<ImageSource?> ToolbarImportArt = new(() => LoadToolbarAssetImage("explorer-icon-import.png"));
    private static readonly Lazy<ImageSource?> ToolbarExportArt = new(() => LoadToolbarAssetImage("explorer-icon-export.png"));

    private readonly TreeView _hierarchy = new()
    {
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(8, 2, 8, 10),
        ItemContainerStyle = HierarchyTreeItemStyle,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        FocusVisualStyle = null
    };
    private readonly Canvas _topologyCanvas = new() { Background = Brush("#F8F9FD"), MinWidth = 1180, MinHeight = 300 };
    private readonly ScaleTransform _topologyZoomTransform = new(1, 1);
    private readonly Canvas _appMapOverlay = new() { Background = Brushes.Transparent };
    private readonly Image _appMapImage = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _appMapViewport = new() { Background = Brush("#F4F5F9"), ClipToBounds = true };
    private readonly ContentControl _traceBannerHost = new() { Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _appMapZoomTransform = new(1, 1);
    private readonly ComboBox _variantPicker = new();
    private readonly TextBlock _variantPosition = Text(string.Empty, 10, FontWeights.SemiBold, Muted);
    private readonly StackPanel _properties = new() { Margin = new Thickness(18) };
    private readonly TextBlock _title = Text("UI Knowledge Graph Editor", 16, FontWeights.SemiBold, Ink);
    private readonly TextBlock _breadcrumb = Text("Open a map to begin", 11, FontWeights.Normal, Muted);
    private readonly TextBlock _topologyTitle = Text("Understanding Pipeline", 14, FontWeights.SemiBold, Ink);
    private readonly TextBlock _topologySummary = Text(string.Empty, 11, FontWeights.Normal, Muted);
    private readonly TextBlock _appMapTitle = Text("AppMap", 14, FontWeights.SemiBold, Ink);
    private readonly TextBlock _appMapSummary = Text(string.Empty, 11, FontWeights.Normal, Muted);
    private readonly TextBlock _status = Text("Ready", 11, FontWeights.Normal, Muted);
    private readonly TextBlock _captureSummary = Text(string.Empty, 11, FontWeights.SemiBold, Muted);
    private readonly Button _manualReviewButton = new() { Visibility = Visibility.Collapsed };
    private readonly ToggleButton _predictionLayerToggle = new() { Content = "Predicted", IsChecked = true };
    private readonly Button _editMapButton = new();
    private readonly Button _selectMapToolButton = new();
    private readonly Button _doneMapEditingButton = new();
    private readonly Border _mapEditingToolsHost = new() { Visibility = Visibility.Collapsed };
    private readonly ToggleButton _drawButtonToggle = new();
    private readonly Border _loadingOverlay;
    private readonly TextBlock _loadingTitle;
    private readonly TextBlock _loadingDetail;
    private readonly TextBox _search = new() { MinWidth = 220, Height = 34, Padding = new Thickness(10, 6, 10, 6), BorderBrush = UiBorder, Background = Brushes.White };
    private readonly ComboBox _surfaceKindFilter = new() { MinWidth = 145, Height = 34, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(7, 4, 7, 4), BorderBrush = UiBorder };
    private readonly Dictionary<UiUnderstandingLevel, Button> _levelButtons = new();
    private readonly Dictionary<UiUnderstandingLevel, TextBlock> _levelButtonLabels = new();
    private readonly Dictionary<string, Button> _viewModeButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _topologyShapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeViewItem> _hierarchyItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _curatingControlIds = new(StringComparer.Ordinal);
    private readonly ColumnDefinition _hierarchyColumn = new() { Width = new GridLength(HierarchyPanelWidth) };
    private readonly ColumnDefinition _headerHierarchyColumn = new() { Width = new GridLength(HeaderHierarchyWidth) };
    private readonly ColumnDefinition _propertiesColumn = new() { Width = new GridLength(285) };
    private Grid? _centerPanel;
    private UIElement? _hierarchyCard;
    private UIElement? _propertiesCard;
    private Button? _previousVariantButton;
    private Button? _nextVariantButton;
    private UIElement? _topologyCard;
    private UIElement? _appMapCard;
    private GridSplitter? _centerSplitter;
    private Button? _layoutToggleButton;
    private ScrollViewer? _topologyScrollViewer;
    private ScrollViewer? _appMapScrollViewer;
    private UiMappingReadModel? _model;
    private UiEvidenceReader? _evidence;
    private UiUnderstandingLevel _level = UiUnderstandingLevel.SemanticWorld;
    private UiUnderstandingLevel _inspectionLevel = UiUnderstandingLevel.SemanticWorld;
    private UiMapSurfaceView? _selectedSurface;
    private UiMapControlView? _selectedControl;
    private UiMapVariantView? _selectedVariant;
    private IReadOnlyList<UiMapVariantView> _visibleVariants = [];
    private IReadOnlyList<UiMapSurfaceView> _selectedSurfaceScope = [];
    private string _viewMode = "Structure";
    private string _interactionActorFilter = "All actors";
    private string _interactionOutcomeFilter = "All outcomes";
    private bool _sideBySideLayout = true;
    private bool _synchronizing;
    private bool _refreshingFilters;
    private bool _refreshingVariantPicker;
    private ScrollViewer? _activePanScrollViewer;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private bool _panActive;
    private double _topologyZoom = 1;
    private double _topologyMinZoom = 1;
    private bool _topologyZoomPinnedToFit = true;
    private double _appMapZoom = 1;
    private double _appMapMinZoom = 1;
    private bool _appMapZoomPinnedToFit = true;
    private bool _resetAppMapZoomOnNextRender = true;
    private bool _drawingManualButton;
    private Point _manualDrawStart;
    private Rectangle? _manualDrawPreview;
    private Popup? _manualButtonEditor;
    private Action? _cancelManualButtonEditor;
    private RectI? _renderedViewportBounds;
    private byte[]? _renderedEvidencePng;
    private Point _renderedImageSourceOffset;
    private string? _resizingAnnotationId;
    private string? _resizeDirection;
    private Point _resizeStart;
    private Rect _resizeOriginal;
    private Rectangle? _resizeOutline;
    private int _graphLoadVersion;
    private string? _graphPath;
    private IReadOnlyList<string> _attachedEvidencePaths = [];
    private readonly Dictionary<string, LegacyGridEvidenceRepair?> _legacyGridRepairCache = new(StringComparer.Ordinal);
    private readonly HashSet<(int AttachmentVersion, string RepairKey)> _legacyGridRepairInFlight = [];
    private readonly SemaphoreSlim _legacyGridRepairGate = new(1, 1);
    private CancellationTokenSource _legacyGridRepairCancellation = new();
    private int _evidenceAttachmentVersion;
    private string? _activeLegacyGridRepairKey;
    private IReadOnlyList<AutoMappingWorkItemState> _manualReviewItems = [];
    private readonly TextBlock _statusLevel = Text("Semantic World", 11, FontWeights.SemiBold, Ink);

    public ExplorerWindow(string? initialPath, string? initialEvidencePath = null)
    {
        var workArea = SystemParameters.WorkArea;
        Title = "UiAtlas Core — UI Knowledge Graph Editor";
        Width = Math.Max(760, Math.Min(1680, workArea.Width - 32));
        Height = Math.Max(560, Math.Min(980, workArea.Height - 32));
        MinWidth = Math.Min(1100, Width);
        MinHeight = Math.Min(700, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _loadingTitle = Text("Please wait, map is loading...", 18, FontWeights.SemiBold, Ink);
        _loadingDetail = Text("Large maps can take a moment to open.", 12, FontWeights.Normal, Muted);
        _loadingOverlay = BuildLoadingOverlay();
        Background = Page;
        FontFamily = new FontFamily("Segoe UI");
        _topologyCanvas.LayoutTransform = _topologyZoomTransform;
        _appMapViewport.LayoutTransform = _appMapZoomTransform;
        Resources[typeof(ComboBox)] = ModernComboBoxStyle;
        _hierarchy.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _hierarchy.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        Content = BuildLayout();
        AttachManualButtonDrawingHandlers();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || _drawButtonToggle.IsChecked != true) return;
            _drawButtonToggle.IsChecked = false;
            args.Handled = true;
        };
        PreviewMouseWheel += HandleZoomMouseWheel;
        _search.TextChanged += (_, _) => RefreshAll();
        _surfaceKindFilter.SelectionChanged += (_, _) => { if (!_refreshingFilters) RefreshAll(); };
        _hierarchy.SelectedItemChanged += (_, args) =>
        {
            if (_synchronizing || args.NewValue is not TreeViewItem { Tag: SelectionRef selection }) return;
            if (selection.InteractionId is not null) SelectInteractionById(selection.InteractionId);
            else if (selection.ControlId is not null) SelectControlById(selection.ControlId);
            else if (selection.SurfaceId is not null) SelectSurfaceById(selection.SurfaceId);
        };
        Closed += (_, _) =>
        {
            _legacyGridRepairCancellation.Cancel();
            _legacyGridRepairCancellation.Dispose();
            _evidence?.Dispose();
        };
        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialPath)) await LoadGraphAsync(initialPath);
            if (!string.IsNullOrWhiteSpace(initialEvidencePath)) AttachEvidence(initialEvidencePath);
        };
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(BuildHeader());

        var body = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        body.ColumnDefinitions.Add(_hierarchyColumn);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(_propertiesColumn);
        _hierarchyCard = BuildHierarchyCard();
        body.Children.Add(_hierarchyCard);
        var center = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        _centerPanel = center;
        _topologyCard = BuildTopologyCard();
        _appMapCard = BuildAppMapCard();
        _centerSplitter = new GridSplitter
        {
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true,
            Cursor = Cursors.SizeNS
        };
        ApplyCenterLayout();
        Grid.SetColumn(center, 1);
        body.Children.Add(center);
        _propertiesCard = BuildPropertiesCard();
        Grid.SetColumn(_propertiesCard, 2);
        body.Children.Add(_propertiesCard);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(_loadingOverlay, 1);
        Panel.SetZIndex(_loadingOverlay, 20);
        root.Children.Add(_loadingOverlay);

        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);
        return root;
    }

    private UIElement BuildStatusBar()
    {
        var leftPaneToggle = PaneVisibilityToggleButton(leftPane: true, "Show or hide the Environment Hierarchy pane.");
        leftPaneToggle.IsChecked = true;
        leftPaneToggle.Checked += (_, _) => SetHierarchyCollapsed(false);
        leftPaneToggle.Unchecked += (_, _) => SetHierarchyCollapsed(true);

        var rightPaneToggle = PaneVisibilityToggleButton(leftPane: false, "Show or hide the Properties pane.");
        rightPaneToggle.IsChecked = true;
        rightPaneToggle.Checked += (_, _) => SetPropertiesCollapsed(false);
        rightPaneToggle.Unchecked += (_, _) => SetPropertiesCollapsed(true);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(leftPaneToggle, Dock.Left);
        dock.Children.Add(leftPaneToggle);
        DockPanel.SetDock(rightPaneToggle, Dock.Right);
        dock.Children.Add(rightPaneToggle);

        var levelStatus = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        levelStatus.Children.Add(Text("Level:", 11, FontWeights.Normal, Muted, verticalAlignment: VerticalAlignment.Center));
        _statusLevel.Margin = new Thickness(5, 0, 0, 0);
        _statusLevel.VerticalAlignment = VerticalAlignment.Center;
        levelStatus.Children.Add(_statusLevel);
        DockPanel.SetDock(levelStatus, Dock.Right);
        dock.Children.Add(levelStatus);

        _manualReviewButton.Height = 26;
        _manualReviewButton.Margin = new Thickness(6, 0, 0, 0);
        _manualReviewButton.Padding = new Thickness(10, 0, 10, 0);
        _manualReviewButton.Background = Brush("#FFF7ED");
        _manualReviewButton.BorderBrush = Orange;
        _manualReviewButton.Foreground = Brush("#9A3412");
        _manualReviewButton.Cursor = Cursors.Hand;
        _manualReviewButton.Template = RoundedButtonTemplate(7);
        _manualReviewButton.Click += (_, _) => ShowNextManualReview();
        DockPanel.SetDock(_manualReviewButton, Dock.Right);
        dock.Children.Add(_manualReviewButton);

        _captureSummary.Margin = new Thickness(12, 0, 4, 0);
        _captureSummary.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_captureSummary, Dock.Right);
        dock.Children.Add(_captureSummary);

        _status.Margin = new Thickness(12, 0, 12, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(_status);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = UiBorder,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Padding = new Thickness(14, 7, 14, 7),
            Child = dock
        };
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid { Background = Brushes.White, Height = 92 };
        grid.ColumnDefinitions.Add(_headerHierarchyColumn);
        var levelColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        grid.ColumnDefinitions.Add(levelColumn);
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        var brand = new Grid { Margin = new Thickness(14, 8, 10, 8) };
        brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        brand.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var brandIcon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = Blue,
            VerticalAlignment = VerticalAlignment.Top,
            Child = Text("M", 15, FontWeights.Bold, Brushes.White, HorizontalAlignment.Center, VerticalAlignment.Center)
        };
        brand.Children.Add(brandIcon);

        var brandContent = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _breadcrumb.TextTrimming = TextTrimming.CharacterEllipsis;
        brandContent.Children.Add(_title);
        brandContent.Children.Add(_breadcrumb);

        Grid.SetColumn(brandContent, 1);
        brand.Children.Add(brandContent);
        grid.Children.Add(brand);

        var levels = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(30, 0, 24, 8)
        };
        levels.Children.Add(Text(
            "Level (Understanding)",
            11,
            FontWeights.SemiBold,
            Ink,
            HorizontalAlignment.Center));
        var selector = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        AddLevelButton(selector, UiUnderstandingLevel.RawDataStreams, "Raw Data Streams", Blue);
        AddLevelButton(selector, UiUnderstandingLevel.RawWorld, "Raw World", Violet);
        AddLevelButton(selector, UiUnderstandingLevel.SemanticWorld, "Semantic World", Green);
        _predictionLayerToggle.Height = 30;
        _predictionLayerToggle.Margin = new Thickness(8, 0, 0, 0);
        _predictionLayerToggle.Padding = new Thickness(11, 0, 11, 0);
        _predictionLayerToggle.Background = Brush("#F5F6FA");
        _predictionLayerToggle.BorderBrush = Muted;
        _predictionLayerToggle.Foreground = Muted;
        _predictionLayerToggle.HorizontalContentAlignment = HorizontalAlignment.Center;
        _predictionLayerToggle.VerticalContentAlignment = VerticalAlignment.Center;
        _predictionLayerToggle.Focusable = false;
        _predictionLayerToggle.FocusVisualStyle = null;
        _predictionLayerToggle.Template = RoundedToggleButtonTemplate(8);
        _predictionLayerToggle.Cursor = Cursors.Hand;
        _predictionLayerToggle.ToolTip = "Show or hide calculated next states. Predictions never create screenshot overlays.";
        _predictionLayerToggle.Checked += (_, _) => RefreshAll();
        _predictionLayerToggle.Unchecked += (_, _) => RefreshAll();
        selector.Children.Add(_predictionLayerToggle);
        levels.Children.Add(selector);
        Grid.SetColumn(levels, 1);
        grid.Children.Add(levels);

        selector.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var expandedSelectorWidth = selector.DesiredSize.Width;
        void UpdateLevelPresentation() =>
            UpdateLevelButtonPresentation(Math.Max(0, levelColumn.ActualWidth - 54), expandedSelectorWidth);
        grid.Loaded += (_, _) => UpdateLevelPresentation();
        grid.SizeChanged += (_, _) => UpdateLevelPresentation();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 16, 8)
        };
        var appFilter = new ComboBox { Width = 125, Height = 34, Padding = new Thickness(7, 4, 7, 4), BorderBrush = UiBorder, IsEnabled = false };
        appFilter.Items.Add("Selected app");
        appFilter.SelectedIndex = 0;
        actions.Children.Add(RoundedField(appFilter));
        actions.Children.Add(RoundedField(_surfaceKindFilter));
        _search.Width = 190;
        _search.Margin = new Thickness(6, 0, 0, 0);
        _search.ToolTip = "Search surfaces or controls";
        actions.Children.Add(RoundedField(_search));

        _layoutToggleButton = IconActionButton(CreateSplitViewIcon(), compactToolTip: "Toggle between stacked and side-by-side center panels.");
        _layoutToggleButton.ToolTip = "Toggle between stacked and side-by-side center panels.";
        _layoutToggleButton.Click += (_, _) =>
        {
            _sideBySideLayout = !_sideBySideLayout;
            ApplyCenterLayout();
        };
        actions.Children.Add(_layoutToggleButton);

        var resume = IconActionButton(CreateResumeIcon(), "Resume recording");
        resume.Background = Blue;
        resume.BorderBrush = Blue;
        resume.Foreground = Brushes.White;
        resume.ToolTip = "Resume recording on the currently opened map.";
        resume.Click += (_, _) => ResumeCurrentMapRecording();
        actions.Children.Add(resume);

        var import = ToolbarIconActionButton(ToolbarImportArt, "Import or open managed map locations.");
        import.ToolTip = "Import a map into the local catalog or open a managed maps or recordings folder.";
        import.Click += (_, _) => ShowImportMenu(import);
        actions.Children.Add(import);

        var export = ToolbarIconActionButton(ToolbarExportArt, "Export the current map.");
        export.ToolTip = "Export the current map or reveal its attached recording bundle.";
        export.Click += (_, _) => ShowExportMenu(export);
        actions.Children.Add(export);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return new Border { BorderBrush = UiBorder, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    private void AddLevelButton(Panel owner, UiUnderstandingLevel level, string label, Brush color)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = LevelGlyph(level),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 16,
            Width = 18,
            Height = 18,
            TextAlignment = TextAlignment.Center,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var labelText = new TextBlock
        {
            Text = label,
            Margin = new Thickness(7, 0, 0, 0),
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(labelText);

        var button = new Button { Content = content, MinWidth = 135, Height = 32,
            Margin = new Thickness(_levelButtons.Count == 0 ? 0 : 4, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            BorderBrush = color, BorderThickness = new Thickness(1), Background = Brushes.White, Foreground = color,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false, FocusVisualStyle = null,
            Template = RoundedButtonTemplate(8) };
        button.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => SetLevel(level);
        _levelButtons[level] = button;
        _levelButtonLabels[level] = labelText;
        owner.Children.Add(button);
    }

    private void UpdateLevelButtonPresentation(double availableWidth, double expandedSelectorWidth)
    {
        var showLabels = UiMapPresentation.ShouldShowModeLabels(availableWidth, expandedSelectorWidth);
        foreach (var pair in _levelButtons)
        {
            _levelButtonLabels[pair.Key].Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed;
            pair.Value.MinWidth = showLabels ? 135 : 36;
            pair.Value.Width = showLabels ? double.NaN : 36;
            pair.Value.Padding = showLabels ? new Thickness(10, 0, 10, 0) : new Thickness(0);
        }
    }

    private UIElement BuildHierarchyCard()
    {
        var panel = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(14, 13, 14, 8) };
        header.Children.Add(Text("Environment Hierarchy", 14, FontWeights.SemiBold, Ink));
        header.Children.Add(Text("Select a surface or control", 11, FontWeights.Normal, Muted));
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(_hierarchy);
        return CardBorder(panel);
    }

    private UIElement BuildTopologyCard()
    {
        var panel = new DockPanel();
        var headerGrid = new Grid { Margin = new Thickness(14, 11, 14, 8) };
        headerGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel();
        labels.Children.Add(_topologyTitle);
        labels.Children.Add(_topologySummary);
        headerGrid.Children.Add(labels);
        var level = Text("Semantic World", 11, FontWeights.SemiBold, Green);
        level.Name = "TopologyLevel";
        Grid.SetColumn(level, 1);
        headerGrid.Children.Add(level);
        DockPanel.SetDock(headerGrid, Dock.Top);
        panel.Children.Add(headerGrid);
        _topologyScrollViewer = new ScrollViewer
        {
            Content = _topologyCanvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Cursor = Cursors.Hand,
            PanningMode = PanningMode.Both
        };
        AttachPanHandlers(_topologyScrollViewer);
        AttachTopologyZoomHandlers(_topologyScrollViewer);
        panel.Children.Add(_topologyScrollViewer);
        var card = CardBorder(panel);
        card.Margin = new Thickness(0, 0, 0, 6);
        return card;
    }

    private UIElement BuildAppMapCard()
    {
        var panel = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(14, 10, 14, 7) };
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel();
        labels.Children.Add(_appMapTitle);
        labels.Children.Add(_appMapSummary);
        headerRow.Children.Add(labels);

        _editMapButton.Content = "✎  Edit map";
        _editMapButton.Height = 32;
        _editMapButton.Padding = new Thickness(13, 0, 13, 0);
        _editMapButton.Background = Blue;
        _editMapButton.BorderBrush = Brush("#3867E8");
        _editMapButton.Foreground = Brushes.White;
        _editMapButton.Cursor = Cursors.Hand;
        _editMapButton.ToolTip = "Edit controls directly on this map";
        _editMapButton.Template = RoundedButtonTemplate(8);
        System.Windows.Automation.AutomationProperties.SetName(_editMapButton, "Edit map");
        _editMapButton.Click += (_, _) => SetMapEditingMode(true);
        Grid.SetColumn(_editMapButton, 1);
        headerRow.Children.Add(_editMapButton);

        var editingTools = new StackPanel { Orientation = Orientation.Horizontal };
        var editingLabel = Text("Editing map", 11, FontWeights.SemiBold, Brush("#1D4ED8"),
            verticalAlignment: VerticalAlignment.Center);
        editingLabel.Margin = new Thickness(2, 0, 10, 0);
        editingTools.Children.Add(editingLabel);
        ConfigureMapEditingButton(_selectMapToolButton, "↖  Select", "Select and edit an existing control");
        _selectMapToolButton.Click += (_, _) => _drawButtonToggle.IsChecked = false;
        editingTools.Children.Add(_selectMapToolButton);
        _drawButtonToggle.Content = "▭  Draw button";
        _drawButtonToggle.Height = 28;
        _drawButtonToggle.Padding = new Thickness(10, 0, 10, 0);
        _drawButtonToggle.Margin = new Thickness(5, 0, 0, 0);
        _drawButtonToggle.Background = Brushes.White;
        _drawButtonToggle.BorderBrush = Brush("#93B4FF");
        _drawButtonToggle.Foreground = Brush("#1D4ED8");
        _drawButtonToggle.Cursor = Cursors.Hand;
        _drawButtonToggle.ToolTip = "Draw a confirmed button on the current screen";
        _drawButtonToggle.Template = RoundedToggleButtonTemplate(
            7, Brush("#2563EB"), Brush("#1D4ED8"), Brushes.White);
        System.Windows.Automation.AutomationProperties.SetName(_drawButtonToggle, "Draw button on map");
        _drawButtonToggle.Checked += (_, _) => SetManualButtonDrawingMode(true);
        _drawButtonToggle.Unchecked += (_, _) => SetManualButtonDrawingMode(false);
        editingTools.Children.Add(_drawButtonToggle);
        ConfigureMapEditingButton(_doneMapEditingButton, "Done", "Finish editing the map");
        _doneMapEditingButton.Margin = new Thickness(8, 0, 0, 0);
        _doneMapEditingButton.Background = Brush("#E8F0FF");
        _doneMapEditingButton.Click += (_, _) => SetMapEditingMode(false);
        editingTools.Children.Add(_doneMapEditingButton);
        _mapEditingToolsHost.Padding = new Thickness(8, 4, 8, 4);
        _mapEditingToolsHost.Background = Brush("#EFF6FF");
        _mapEditingToolsHost.BorderBrush = Brush("#BFDBFE");
        _mapEditingToolsHost.BorderThickness = new Thickness(1);
        _mapEditingToolsHost.CornerRadius = new CornerRadius(9);
        _mapEditingToolsHost.Child = editingTools;
        Grid.SetColumn(_mapEditingToolsHost, 1);
        headerRow.Children.Add(_mapEditingToolsHost);
        header.Children.Add(headerRow);

        var modes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // Fluent scrollbars float over their viewport. Reserve a dedicated
            // strip so the horizontal thumb never covers the mode controls.
            Margin = new Thickness(0, 0, 0, 10)
        };
        foreach (var mode in new[] { "Window", "Controls", "Overlay", "Structure", "Structure Overlay", "Trace", "Routes" })
        {
            var button = new Button { Content = mode, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(3, 0, 0, 0),
                Background = mode == _viewMode ? Brush("#EEE7FF") : Brushes.White, BorderBrush = UiBorder, Tag = mode,
                Foreground = mode == _viewMode ? Brush("#6D28D9") : Ink, ToolTip = mode,
                Template = RoundedButtonTemplate(8) };
            System.Windows.Automation.AutomationProperties.SetName(button, mode);
            button.Click += (_, _) =>
            {
                SetViewMode(mode);
                _appMapSummary.Text = $"{_inspectionLevel.DisplayName()} | {_viewMode}";
                RenderAppMap();
            };
            _viewModeButtons[mode] = button;
            modes.Children.Add(button);
        }
        var actorFilter = new ComboBox { Width = 125, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Filter interaction actor" };
        foreach (var value in new[] { "All actors", "User", "AutoExplorer", "DerivedCandidate" }) actorFilter.Items.Add(value);
        actorFilter.SelectedItem = _interactionActorFilter;
        actorFilter.SelectionChanged += (_, _) =>
        {
            _interactionActorFilter = actorFilter.SelectedItem as string ?? "All actors";
            BuildHierarchy(_search.Text.Trim());
            if (_viewMode is "Trace" or "Routes") RenderAppMap();
        };
        modes.Children.Add(actorFilter);
        var outcomeFilter = new ComboBox { Width = 125, Margin = new Thickness(5, 0, 0, 0), ToolTip = "Filter interaction outcome" };
        foreach (var value in new[] { "All outcomes", "Succeeded", "Failed", "NoChange", "TimedOut", "Unobserved" }) outcomeFilter.Items.Add(value);
        outcomeFilter.SelectedItem = _interactionOutcomeFilter;
        outcomeFilter.SelectionChanged += (_, _) =>
        {
            _interactionOutcomeFilter = outcomeFilter.SelectedItem as string ?? "All outcomes";
            BuildHierarchy(_search.Text.Trim());
            if (_viewMode is "Trace" or "Routes") RenderAppMap();
        };
        modes.Children.Add(outcomeFilter);
        var modeScroller = new ScrollViewer
        {
            Content = modes,
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        header.Children.Add(modeScroller);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var variants = BuildVariantNavigator();
        DockPanel.SetDock(variants, Dock.Top);
        panel.Children.Add(variants);

        DockPanel.SetDock(_traceBannerHost, Dock.Top);
        panel.Children.Add(_traceBannerHost);

        var content = new Grid();
        _appMapViewport.Children.Add(_appMapImage);
        _appMapViewport.Children.Add(_appMapOverlay);
        _appMapScrollViewer = new ScrollViewer
        {
            Content = _appMapViewport,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Cursor = Cursors.Hand,
            PanningMode = PanningMode.Both
        };
        AttachPanHandlers(_appMapScrollViewer);
        AttachAppMapZoomHandlers(_appMapScrollViewer);
        var viewportScroll = _appMapScrollViewer;
        content.Children.Add(viewportScroll);
        panel.Children.Add(content);
        return CardBorder(panel);
    }

    private static void ConfigureMapEditingButton(Button button, string content, string toolTip)
    {
        button.Content = content;
        button.Height = 28;
        button.Padding = new Thickness(10, 0, 10, 0);
        button.Background = Brushes.White;
        button.BorderBrush = Brush("#93B4FF");
        button.Foreground = Brush("#1D4ED8");
        button.Cursor = Cursors.Hand;
        button.ToolTip = toolTip;
        button.Template = RoundedButtonTemplate(7);
        System.Windows.Automation.AutomationProperties.SetName(button, content);
    }

    private Border BuildVariantNavigator()
    {
        _previousVariantButton = VariantNavigationButton("‹", "Previous frame", -1);
        _nextVariantButton = VariantNavigationButton("›", "Next frame", 1);

        _variantPicker.Width = double.NaN;
        _variantPicker.MinWidth = 150;
        _variantPicker.Height = 34;
        _variantPicker.Padding = new Thickness(8, 4, 8, 4);
        _variantPicker.IsEditable = true;
        _variantPicker.IsTextSearchEnabled = true;
        _variantPicker.IsTextSearchCaseSensitive = false;
        _variantPicker.StaysOpenOnEdit = true;
        _variantPicker.MaxDropDownHeight = 420;
        _variantPicker.ToolTip = "Choose a frame or type its number";
        System.Windows.Automation.AutomationProperties.SetName(_variantPicker, "Frame selector");
        _variantPicker.DisplayMemberPath = nameof(VariantOption.Label);
        TextSearch.SetTextPath(_variantPicker, nameof(VariantOption.Label));
        _variantPicker.SelectionChanged += (_, _) =>
        {
            if (_refreshingVariantPicker || _variantPicker.SelectedItem is not VariantOption option) return;
            _variantPicker.Text = option.Label;
            SelectVariant(option.Variant);
        };
        _variantPicker.DropDownOpened += (_, _) => RestoreVariantPickerText();
        _variantPicker.LostKeyboardFocus += (_, _) => RestoreVariantPickerText();
        _variantPicker.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            var match = System.Text.RegularExpressions.Regex.Match(_variantPicker.Text ?? string.Empty, @"\d+");
            if (!match.Success || !long.TryParse(match.Value, out var requestedFrame)) return;
            var index = _visibleVariants.ToList().FindIndex(variant => variant.FrameSequence == requestedFrame);
            if (index < 0) return;
            _variantPicker.SelectedIndex = index;
            RestoreVariantPickerText();
            args.Handled = true;
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_previousVariantButton);
        var picker = RoundedField(_variantPicker);
        picker.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(picker, 1);
        row.Children.Add(picker);
        Grid.SetColumn(_nextVariantButton, 2);
        row.Children.Add(_nextVariantButton);
        _variantPosition.Margin = new Thickness(10, 8, 0, 0);
        Grid.SetColumn(_variantPosition, 3);
        row.Children.Add(_variantPosition);

        return new Border
        {
            BorderBrush = UiBorder,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Background = Brush("#FAFAFD"),
            Padding = new Thickness(8, 6, 8, 6),
            Child = row
        };
    }

    private Button VariantNavigationButton(string glyph, string toolTip, int offset)
    {
        var button = new Button
        {
            Content = CreateChevronIcon(offset > 0),
            Width = 32,
            Height = 34,
            Padding = new Thickness(0),
            Background = Brushes.White,
            BorderBrush = UiBorder,
            Foreground = Ink,
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Focusable = false,
            Template = RoundedButtonTemplate(8)
        };
        AttachModernIconButtonFeedback(button);
        button.Click += (_, _) => NavigateVariant(offset);
        System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
        return button;
    }

    private static FrameworkElement CreateChevronIcon(bool pointsRight)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(pointsRight ? "M 2,1 L 7,6 L 2,11" : "M 7,1 L 2,6 L 7,11"),
            Stroke = Ink,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent
        };
        return new Viewbox
        {
            Width = 10,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = path
        };
    }

    private static ToggleButton PaneVisibilityToggleButton(bool leftPane, string toolTip)
    {
        var button = new ToggleButton
        {
            Content = CreatePaneVisibilityIcon(leftPane),
            Width = 35,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 3),
            Padding = new Thickness(0),
            Background = Brushes.White,
            BorderBrush = UiBorder,
            BorderThickness = new Thickness(1),
            Foreground = Ink,
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Focusable = false,
            Template = PaneToggleButtonTemplate()
        };
        button.Checked += (_, _) =>
        {
            button.Background = Ink;
            button.BorderBrush = Ink;
            button.Foreground = Brushes.White;
        };
        button.Unchecked += (_, _) =>
        {
            button.Background = Brushes.White;
            button.BorderBrush = UiBorder;
            button.Foreground = Ink;
        };
        System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
        return button;
    }

    private static FrameworkElement CreatePaneVisibilityIcon(bool leftPane)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(leftPane ? "M3,3H17V17H3Z M8,3V17" : "M3,3H17V17H3Z M12,3V17"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1)
        });
        return new Viewbox { Width = 17, Height = 17, Stretch = Stretch.Uniform, Child = path };
    }

    private static ControlTemplate PaneToggleButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Ink));
        checkedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, Ink));
        checkedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(checkedTrigger);
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.84));
        template.Triggers.Add(hoverTrigger);
        return template;
    }

    private static FrameworkElement CreateResumeIcon()
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,1 L15,9 L3,17 Z"),
            Fill = Brushes.White,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Round
        };
        return new Viewbox { Width = 16, Height = 18, Stretch = Stretch.Uniform, Child = path };
    }

    private static void AttachModernIconButtonFeedback(Button button)
    {
        button.MouseEnter += (_, _) =>
        {
            if (!button.IsEnabled) return;
            button.Background = Brush("#F2F5FB");
            button.BorderBrush = Brush("#C8D3E8");
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.White;
            button.BorderBrush = UiBorder;
        };
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : 0.45;
    }

    private UIElement BuildPropertiesCard()
    {
        var panel = new DockPanel();
        var header = Text("Properties", 14, FontWeights.SemiBold, Ink);
        header.Margin = new Thickness(16, 13, 16, 9);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ShowEmptyProperties();
        return CardBorder(panel);
    }

    private void SetHierarchyCollapsed(bool collapsed)
    {
        _hierarchyColumn.Width = new GridLength(collapsed ? 0 : HierarchyPanelWidth);
        if (_hierarchyCard is not null)
            _hierarchyCard.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetPropertiesCollapsed(bool collapsed)
    {
        _propertiesColumn.Width = new GridLength(collapsed ? 0 : 285);
        if (_propertiesCard is not null)
            _propertiesCard.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyCenterLayout()
    {
        if (_centerPanel is null || _topologyCard is null || _appMapCard is null || _centerSplitter is null)
            return;

        _centerPanel.Children.Clear();
        _centerPanel.RowDefinitions.Clear();
        _centerPanel.ColumnDefinitions.Clear();

        if (_sideBySideLayout)
        {
            _centerPanel.ColumnDefinitions.Add(new() { Width = new GridLength(48, GridUnitType.Star), MinWidth = 320 });
            _centerPanel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            _centerPanel.ColumnDefinitions.Add(new() { Width = new GridLength(52, GridUnitType.Star), MinWidth = 320 });
            _centerPanel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

            _centerSplitter.Width = 8;
            _centerSplitter.Height = double.NaN;
            _centerSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            _centerSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            _centerSplitter.ResizeDirection = GridResizeDirection.Columns;
            _centerSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            _centerSplitter.Cursor = Cursors.SizeWE;

            Grid.SetColumn(_appMapCard, 0);
            Grid.SetRow(_appMapCard, 0);
            Grid.SetColumn(_centerSplitter, 1);
            Grid.SetRow(_centerSplitter, 0);
            Grid.SetColumn(_topologyCard, 2);
            Grid.SetRow(_topologyCard, 0);
        }
        else
        {
            _centerPanel.RowDefinitions.Add(new() { Height = new GridLength(43, GridUnitType.Star), MinHeight = 220 });
            _centerPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _centerPanel.RowDefinitions.Add(new() { Height = new GridLength(57, GridUnitType.Star), MinHeight = 260 });
            _centerPanel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });

            _centerSplitter.Width = double.NaN;
            _centerSplitter.Height = 8;
            _centerSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _centerSplitter.VerticalAlignment = VerticalAlignment.Center;
            _centerSplitter.ResizeDirection = GridResizeDirection.Rows;
            _centerSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            _centerSplitter.Cursor = Cursors.SizeNS;

            Grid.SetColumn(_topologyCard, 0);
            Grid.SetRow(_topologyCard, 0);
            Grid.SetColumn(_centerSplitter, 0);
            Grid.SetRow(_centerSplitter, 1);
            Grid.SetColumn(_appMapCard, 0);
            Grid.SetRow(_appMapCard, 2);
        }

        _centerPanel.Children.Add(_topologyCard);
        _centerPanel.Children.Add(_centerSplitter);
        _centerPanel.Children.Add(_appMapCard);

        if (_layoutToggleButton is not null)
        {
            _layoutToggleButton.Content = CreateSplitViewIcon();
            _layoutToggleButton.Background = _sideBySideLayout ? Brush("#EEF4FF") : Brushes.White;
            _layoutToggleButton.BorderBrush = _sideBySideLayout ? Blue : UiBorder;
        }
    }

    private void AttachPanHandlers(ScrollViewer scrollViewer)
    {
        scrollViewer.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (ReferenceEquals(scrollViewer, _appMapScrollViewer) && _drawButtonToggle.IsChecked == true)
                return;
            if (IsInteractiveMapHit(args.OriginalSource as DependencyObject))
                return;

            _activePanScrollViewer = scrollViewer;
            _panStartPoint = args.GetPosition(scrollViewer);
            _panStartHorizontalOffset = scrollViewer.HorizontalOffset;
            _panStartVerticalOffset = scrollViewer.VerticalOffset;
            _panActive = true;
            scrollViewer.Cursor = Cursors.SizeAll;
        };

        scrollViewer.PreviewMouseMove += (_, args) =>
        {
            if (!_panActive || _activePanScrollViewer != scrollViewer || args.LeftButton != MouseButtonState.Pressed)
                return;

            var point = args.GetPosition(scrollViewer);
            var deltaX = point.X - _panStartPoint.X;
            var deltaY = point.Y - _panStartPoint.Y;
            scrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - deltaX));
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - deltaY));
            if (!scrollViewer.IsMouseCaptured)
                scrollViewer.CaptureMouse();
            args.Handled = true;
        };

        scrollViewer.PreviewMouseLeftButtonUp += (_, args) => ReleasePan(scrollViewer);
        scrollViewer.MouseLeave += (_, _) =>
        {
            if (!_panActive)
                scrollViewer.Cursor = Cursors.Hand;
        };
        scrollViewer.LostMouseCapture += (_, _) => ReleasePan(scrollViewer);
    }

    private void AttachManualButtonDrawingHandlers()
    {
        _appMapOverlay.MouseLeftButtonDown += (_, args) =>
        {
            if (_drawButtonToggle.IsChecked != true || _selectedSurface is null ||
                _renderedViewportBounds is null || _viewMode == "Routes")
                return;
            if (_manualButtonEditor is not null) _manualButtonEditor.IsOpen = false;
            _manualDrawStart = ClampToAppMap(args.GetPosition(_appMapOverlay));
            _manualDrawPreview = new Rectangle
            {
                Stroke = Green,
                StrokeThickness = 3,
                StrokeDashArray = [4, 2],
                Fill = new SolidColorBrush(Color.FromArgb(54, 45, 206, 145)),
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_manualDrawPreview, 100);
            _appMapOverlay.Children.Add(_manualDrawPreview);
            _drawingManualButton = true;
            _appMapOverlay.CaptureMouse();
            args.Handled = true;
        };
        _appMapOverlay.MouseMove += (_, args) =>
        {
            var point = ClampToAppMap(args.GetPosition(_appMapOverlay));
            if (_drawingManualButton && _manualDrawPreview is not null)
            {
                ApplyCanvasRect(_manualDrawPreview, NormalizeRect(_manualDrawStart, point));
                args.Handled = true;
                return;
            }
            if (_resizingAnnotationId is not null && _resizeOutline is not null &&
                args.LeftButton == MouseButtonState.Pressed)
            {
                ApplyCanvasRect(_resizeOutline, ResizeRect(_resizeOriginal, _resizeStart, point, _resizeDirection!));
                args.Handled = true;
            }
        };
        _appMapOverlay.MouseLeftButtonUp += async (_, args) =>
        {
            if (_resizingAnnotationId is not null && _resizeOutline is not null)
            {
                var annotationId = _resizingAnnotationId;
                var resizedBounds = CanvasRect(_resizeOutline);
                ClearResizeState();
                _appMapOverlay.ReleaseMouseCapture();
                await ResizeManualButtonAsync(annotationId, resizedBounds);
                args.Handled = true;
                return;
            }
            if (!_drawingManualButton || _manualDrawPreview is null) return;
            var preview = _manualDrawPreview;
            var bounds = CanvasRect(preview);
            _drawingManualButton = false;
            _appMapOverlay.ReleaseMouseCapture();
            _drawButtonToggle.IsChecked = false;
            if (bounds.Width < 6 || bounds.Height < 6)
            {
                _appMapOverlay.Children.Remove(preview);
                _manualDrawPreview = null;
                _status.Text = "Draw a larger rectangle around the button.";
                return;
            }
            await ShowManualButtonEditorAsync(bounds, preview);
            args.Handled = true;
        };
        _appMapOverlay.LostMouseCapture += (_, _) =>
        {
            if (!_drawingManualButton) return;
            _drawingManualButton = false;
            if (_manualDrawPreview is not null) _appMapOverlay.Children.Remove(_manualDrawPreview);
            _manualDrawPreview = null;
        };
    }

    private void SetMapEditingMode(bool enabled)
    {
        _editMapButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _mapEditingToolsHost.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            CancelManualButtonEditor();
            _drawButtonToggle.IsChecked = false;
            SetManualButtonDrawingMode(false);
            _status.Text = "Map editing finished.";
            return;
        }

        _drawButtonToggle.IsChecked = false;
        UpdateMapEditingToolState();
        if (_model is not null && _selectedSurface?.Level == UiUnderstandingLevel.SemanticWorld &&
            _renderedViewportBounds is not null)
        {
            SetViewMode("Overlay");
            _appMapSummary.Text = $"{_inspectionLevel.DisplayName()} | {_viewMode}";
            RenderAppMap();
            _status.Text = "Select a control or choose Draw button.";
        }
        else
        {
            _status.Text = "Select a Semantic World screen with a screenshot to edit the map.";
        }
    }

    private void UpdateMapEditingToolState()
    {
        var selecting = _drawButtonToggle.IsChecked != true;
        _selectMapToolButton.Background = selecting ? Brush("#2563EB") : Brushes.White;
        _selectMapToolButton.BorderBrush = selecting ? Brush("#1D4ED8") : Brush("#93B4FF");
        _selectMapToolButton.Foreground = selecting ? Brushes.White : Brush("#1D4ED8");
    }

    private void SetManualButtonDrawingMode(bool enabled)
    {
        UpdateMapEditingToolState();
        if (!enabled)
        {
            _drawingManualButton = false;
            _appMapOverlay.Cursor = Cursors.Arrow;
            _appMapScrollViewer?.SetCurrentValue(CursorProperty, Cursors.Hand);
            return;
        }
        if (_model is null || _selectedSurface is null || _renderedViewportBounds is null ||
            _selectedSurface.Level != UiUnderstandingLevel.SemanticWorld || _viewMode == "Routes")
        {
            _drawButtonToggle.IsChecked = false;
            _status.Text = "Select a Semantic World screen with a screenshot before drawing a button.";
            return;
        }
        SetViewMode("Overlay");
        RenderAppMap();
        _appMapOverlay.Cursor = Cursors.Cross;
        _appMapScrollViewer?.SetCurrentValue(CursorProperty, Cursors.Cross);
        _status.Text = "Drag a rectangle around the button.";
    }

    private async Task ShowManualButtonEditorAsync(Rect bounds, Rectangle preview)
    {
        var suggested = string.Empty;
        if (_renderedEvidencePng is not null)
        {
            var region = new RectI(
                Math.Max(0, (int)Math.Floor(bounds.X + _renderedImageSourceOffset.X)),
                Math.Max(0, (int)Math.Floor(bounds.Y + _renderedImageSourceOffset.Y)),
                Math.Max(1, (int)Math.Ceiling(bounds.Width)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height)));
            _status.Text = "Reading the button label...";
            suggested = await ManualControlLabelRecognizer.SuggestAsync(_renderedEvidencePng, region);
        }

        var name = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(suggested) ? "Button" : suggested,
            MinWidth = 190,
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5),
            BorderBrush = UiBorder,
            Background = Brushes.White
        };
        var validation = Text(string.Empty, 10, FontWeights.SemiBold, Brush("#C93845"));
        validation.Margin = new Thickness(0, 5, 0, 0);
        validation.Visibility = Visibility.Collapsed;
        Popup? popup = null;
        var completed = false;

        void RemoveDraft()
        {
            _appMapOverlay.Children.Remove(preview);
            if (ReferenceEquals(_manualDrawPreview, preview)) _manualDrawPreview = null;
        }

        void CancelDraft()
        {
            if (completed) return;
            completed = true;
            if (popup?.IsOpen == true) popup.IsOpen = false;
            RemoveDraft();
            _status.Text = "Button drawing cancelled.";
        }

        async Task ConfirmDraftAsync()
        {
            if (completed) return;
            var label = string.Join(' ', name.Text.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (label.Length == 0)
            {
                validation.Text = "Enter a button name or press Cancel.";
                validation.Visibility = Visibility.Visible;
                name.BorderBrush = Brush("#E34D59");
                _status.Text = "A button name is required.";
                name.Focus();
                return;
            }

            completed = true;
            if (popup?.IsOpen == true) popup.IsOpen = false;
            RemoveDraft();
            await AddManualButtonAsync(bounds, label);
        }

        name.TextChanged += (_, _) =>
        {
            if (validation.Visibility != Visibility.Visible) return;
            validation.Visibility = Visibility.Collapsed;
            name.BorderBrush = UiBorder;
        };
        var confirm = CandidateActionButton("M2,6 L5,9 L11,2", Green, Brush("#1FAD78"),
            "Confirm button", "Confirm drawn button", () => _ = ConfirmDraftAsync());
        var cancel = CandidateActionButton("M2,2 L10,10 M10,2 L2,10", Brush("#E34D59"), Brush("#C93845"),
            "Cancel", "Cancel drawn button", CancelDraft, new Thickness(5, 0, 0, 0));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(name);
        row.Children.Add(actions);
        var editorContent = new StackPanel();
        editorContent.Children.Add(row);
        editorContent.Children.Add(validation);
        var shell = new Border
        {
            Background = Brushes.White,
            BorderBrush = Green,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 2, Opacity = .24 },
            Child = editorContent
        };
        popup = new Popup
        {
            Child = shell,
            PlacementTarget = _appMapOverlay,
            Placement = PlacementMode.MousePoint,
            StaysOpen = true,
            AllowsTransparency = true,
            IsOpen = true
        };
        _manualButtonEditor = popup;
        _cancelManualButtonEditor = CancelDraft;
        popup.Closed += (_, _) =>
        {
            if (!completed) CancelDraft();
            if (ReferenceEquals(_manualButtonEditor, popup))
            {
                _manualButtonEditor = null;
                _cancelManualButtonEditor = null;
            }
        };
        name.SelectAll();
        name.Focus();
        _status.Text = "Confirm or edit the proposed button name.";
    }

    private void CancelManualButtonEditor()
    {
        if (_cancelManualButtonEditor is { } cancel)
        {
            cancel();
            return;
        }
        if (_manualButtonEditor?.IsOpen == true) _manualButtonEditor.IsOpen = false;
        _manualButtonEditor = null;
    }

    private Point ClampToAppMap(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, _appMapOverlay.Width)),
        Math.Clamp(point.Y, 0, Math.Max(0, _appMapOverlay.Height)));

    private static Rect NormalizeRect(Point first, Point second) => new(
        Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

    private static void ApplyCanvasRect(FrameworkElement element, Rect bounds)
    {
        element.Width = Math.Max(1, bounds.Width);
        element.Height = Math.Max(1, bounds.Height);
        Canvas.SetLeft(element, bounds.X);
        Canvas.SetTop(element, bounds.Y);
    }

    private static Rect CanvasRect(FrameworkElement element) => new(
        Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);

    private Rect ResizeRect(Rect original, Point start, Point current, string direction)
    {
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (direction.Contains('w')) left = Math.Min(right - 6, Math.Max(0, left + dx));
        if (direction.Contains('e')) right = Math.Max(left + 6, Math.Min(_appMapOverlay.Width, right + dx));
        if (direction.Contains('n')) top = Math.Min(bottom - 6, Math.Max(0, top + dy));
        if (direction.Contains('s')) bottom = Math.Max(top + 6, Math.Min(_appMapOverlay.Height, bottom + dy));
        return new Rect(left, top, right - left, bottom - top);
    }

    private void ClearResizeState()
    {
        if (_resizeOutline is not null) _appMapOverlay.Children.Remove(_resizeOutline);
        _resizingAnnotationId = null;
        _resizeDirection = null;
        _resizeOutline = null;
    }

    private void AddManualControlEditingAdornments(RectI local, string annotationId)
    {
        var bounds = new Rect(local.X, local.Y, local.Width, local.Height);
        var delete = CandidateActionButton(
            "M3,4 H11 M5,4 V2 H9 V4 M4,5 V12 H10 V5 M6,7 V10 M8,7 V10",
            Brush("#E34D59"), Brush("#C93845"), "Delete manual button", "Delete manual button",
            () => _ = DeleteManualButtonAsync(annotationId));
        Panel.SetZIndex(delete, 130);
        Canvas.SetLeft(delete, Math.Clamp(bounds.Right - 20, 0, Math.Max(0, _appMapOverlay.Width - 20)));
        Canvas.SetTop(delete, Math.Clamp(bounds.Top - 24, 0, Math.Max(0, _appMapOverlay.Height - 20)));
        _appMapOverlay.Children.Add(delete);

        foreach (var handle in new[]
                 {
                     ("nw", bounds.Left, bounds.Top), ("n", bounds.Left + bounds.Width / 2, bounds.Top),
                     ("ne", bounds.Right, bounds.Top), ("e", bounds.Right, bounds.Top + bounds.Height / 2),
                     ("se", bounds.Right, bounds.Bottom), ("s", bounds.Left + bounds.Width / 2, bounds.Bottom),
                     ("sw", bounds.Left, bounds.Bottom), ("w", bounds.Left, bounds.Top + bounds.Height / 2)
                 })
        {
            var grip = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = Green,
                StrokeThickness = 2,
                Cursor = handle.Item1 switch
                {
                    "n" or "s" => Cursors.SizeNS,
                    "e" or "w" => Cursors.SizeWE,
                    "nw" or "se" => Cursors.SizeNWSE,
                    _ => Cursors.SizeNESW
                },
                ToolTip = "Resize manual button"
            };
            Panel.SetZIndex(grip, 125);
            Canvas.SetLeft(grip, handle.Item2 - 4.5);
            Canvas.SetTop(grip, handle.Item3 - 4.5);
            grip.MouseLeftButtonDown += (_, args) =>
            {
                _resizingAnnotationId = annotationId;
                _resizeDirection = handle.Item1;
                _resizeStart = ClampToAppMap(args.GetPosition(_appMapOverlay));
                _resizeOriginal = bounds;
                _resizeOutline = new Rectangle
                {
                    Stroke = Green,
                    StrokeThickness = 3,
                    StrokeDashArray = [3, 2],
                    Fill = new SolidColorBrush(Color.FromArgb(42, 45, 206, 145)),
                    IsHitTestVisible = false
                };
                ApplyCanvasRect(_resizeOutline, bounds);
                Panel.SetZIndex(_resizeOutline, 120);
                _appMapOverlay.Children.Add(_resizeOutline);
                _appMapOverlay.CaptureMouse();
                args.Handled = true;
            };
            _appMapOverlay.Children.Add(grip);
        }
    }

    private async Task AddManualButtonAsync(Rect localBounds, string label)
    {
        if (_model is null || _selectedSurface is null || string.IsNullOrWhiteSpace(_graphPath) ||
            !TryNormalizeManualBounds(localBounds, out var normalized))
            return;
        var now = DateTimeOffset.UtcNow;
        var annotation = new ManualControlAnnotation(
            "manual-" + Guid.NewGuid().ToString("N"),
            _selectedSurface.Source.StableKey,
            _selectedSurface.Id,
            label,
            "Button",
            normalized,
            now,
            now);
        var document = LoadCurationDocument();
        document = MapCurationStore.UpsertManualControl(document, annotation);
        await PersistCurationAsync(document, annotation.Id, "Button added to the semantic screen.");
    }

    private async Task ResizeManualButtonAsync(string annotationId, Rect localBounds)
    {
        if (!TryNormalizeManualBounds(localBounds, out var normalized)) return;
        var document = LoadCurationDocument();
        var annotation = document.ManualControls.FirstOrDefault(item => item.Id == annotationId);
        if (annotation is null) return;
        document = MapCurationStore.UpsertManualControl(document,
            annotation with { Bounds = normalized, UpdatedUtc = DateTimeOffset.UtcNow });
        await PersistCurationAsync(document, annotationId, "Button size saved.");
    }

    private async Task RenameManualButtonAsync(string annotationId, string label)
    {
        var normalizedLabel = string.Join(' ', label.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalizedLabel.Length == 0) return;
        var document = LoadCurationDocument();
        var annotation = document.ManualControls.FirstOrDefault(item => item.Id == annotationId);
        if (annotation is null || annotation.Label == normalizedLabel) return;
        document = MapCurationStore.UpsertManualControl(document,
            annotation with { Label = normalizedLabel, UpdatedUtc = DateTimeOffset.UtcNow });
        await PersistCurationAsync(document, annotationId, "Button name saved.");
    }

    private async Task DeleteManualButtonAsync(string annotationId)
    {
        if (_model is null || string.IsNullOrWhiteSpace(_graphPath)) return;
        var answer = MessageBox.Show(this,
            "Delete this manually added button from every variant of the semantic screen?",
            "Delete button", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var document = MapCurationStore.RemoveManualControl(LoadCurationDocument(), annotationId);
        _selectedControl = null;
        await PersistCurationAsync(document, null, "Manual button deleted.");
    }

    private bool TryNormalizeManualBounds(Rect localBounds, out NormalizedControlBounds normalized)
    {
        normalized = new(0, 0, 0, 0);
        if (_selectedSurface is null || _renderedViewportBounds is not { } viewport) return false;
        var surface = _selectedSurface.Evidence.FirstOrDefault(evidence =>
                          _selectedVariant is not null && MatchesVariant(evidence, _selectedVariant))?.Bounds
                      ?? _selectedSurface.Bounds;
        if (surface.Width <= 0 || surface.Height <= 0) return false;
        var left = Math.Max(surface.X, viewport.X + localBounds.Left);
        var top = Math.Max(surface.Y, viewport.Y + localBounds.Top);
        var right = Math.Min(surface.X + surface.Width, viewport.X + localBounds.Right);
        var bottom = Math.Min(surface.Y + surface.Height, viewport.Y + localBounds.Bottom);
        if (right - left < 2 || bottom - top < 2) return false;
        var value = MapCurationStore.NormalizeBounds(
            new RectI((int)Math.Round(left), (int)Math.Round(top),
                Math.Max(1, (int)Math.Round(right - left)), Math.Max(1, (int)Math.Round(bottom - top))),
            surface);
        if (value is null) return false;
        normalized = value;
        return true;
    }

    private MapCurationDocument LoadCurationDocument()
    {
        if (_model is null || string.IsNullOrWhiteSpace(_graphPath))
            throw new InvalidOperationException("No map is open.");
        return MapCurationStore.Load(_graphPath, _model.Graph.Metadata.EffectiveLogicalMapId);
    }

    private async Task PersistCurationAsync(
        MapCurationDocument document,
        string? selectAnnotationId,
        string successMessage)
    {
        if (_model is null || string.IsNullOrWhiteSpace(_graphPath)) return;
        var graphPath = _graphPath;
        var sourceGraph = _model.Graph;
        try
        {
            _status.Text = "Saving map curation...";
            var updated = await Task.Run(() =>
            {
                MapCurationStore.Save(graphPath, document);
                var curated = MapCurationStore.Reapply(sourceGraph, document);
                SaveGraph(curated, graphPath);
                return curated;
            });
            _model = new UiMappingReadModel(updated);
            UpdateCaptureSummary(graphPath);
            RefreshSurfaceKindFilter();
            RefreshAll();
            if (!string.IsNullOrWhiteSpace(selectAnnotationId))
            {
                var selected = updated.Nodes.FirstOrDefault(node =>
                    node.Kind == GraphNodeKind.Control &&
                    PropertyValue(node, "layer") == "semantic-world" &&
                    PropertyValue(node, "manualAnnotationId") == selectAnnotationId);
                if (selected is not null) SelectControlById(selected.Id);
            }
            _status.Text = successMessage;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                                       UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = "Could not save map curation.";
            MessageBox.Show(this, ex.Message, "Map curation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AttachTopologyZoomHandlers(ScrollViewer scrollViewer)
    {
        scrollViewer.PreviewMouseWheel += (_, args) =>
        {
            if (!TryHandleTopologyZoomWheel(scrollViewer, args.GetPosition(scrollViewer), args.Delta))
                return;

            args.Handled = true;
        };

        scrollViewer.SizeChanged += (_, _) => QueueTopologyZoomBoundsUpdate(resetToFit: false);
    }

    private void HandleZoomMouseWheel(object? sender, MouseWheelEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        if (_topologyScrollViewer is not null && IsDescendantOf(source, _topologyScrollViewer))
        {
            if (TryHandleTopologyZoomWheel(_topologyScrollViewer, args.GetPosition(_topologyScrollViewer), args.Delta))
                args.Handled = true;
            return;
        }

        if (_appMapScrollViewer is not null && IsDescendantOf(source, _appMapScrollViewer))
        {
            if (TryHandleAppMapZoomWheel(_appMapScrollViewer, args.GetPosition(_appMapScrollViewer), args.Delta))
                args.Handled = true;
        }
    }

    private bool TryHandleTopologyZoomWheel(ScrollViewer scrollViewer, Point anchor, int delta)
    {
        if (_topologyCanvas.Width <= 0 || _topologyCanvas.Height <= 0)
            return false;

        var factor = delta > 0 ? AppMapZoomStep : 1 / AppMapZoomStep;
        var targetZoom = Math.Clamp(_topologyZoom * factor, _topologyMinZoom, AppMapMaxZoom);
        if (Math.Abs(targetZoom - _topologyZoom) < 0.0001)
            return false;

        SetTopologyZoom(scrollViewer, targetZoom, anchor);
        return true;
    }

    private void SetTopologyZoom(ScrollViewer scrollViewer, double zoom, Point anchor)
    {
        var oldZoom = _topologyZoom;
        if (oldZoom <= 0 || Math.Abs(zoom - oldZoom) < 0.0001)
            return;

        var anchorX = (scrollViewer.HorizontalOffset + anchor.X) / oldZoom;
        var anchorY = (scrollViewer.VerticalOffset + anchor.Y) / oldZoom;
        ApplyTopologyZoom(zoom);
        scrollViewer.UpdateLayout();
        scrollViewer.ScrollToHorizontalOffset(Math.Max(0, anchorX * zoom - anchor.X));
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, anchorY * zoom - anchor.Y));
        _topologyZoomPinnedToFit = false;
    }

    private void ApplyTopologyZoom(double zoom)
    {
        _topologyZoom = zoom;
        _topologyZoomTransform.ScaleX = zoom;
        _topologyZoomTransform.ScaleY = zoom;
    }

    private void UpdateTopologyZoomBounds(bool resetToFit)
    {
        if (_topologyScrollViewer is null)
            return;

        var viewportWidth = FirstFinitePositive(_topologyScrollViewer.ViewportWidth, _topologyScrollViewer.ActualWidth);
        var viewportHeight = FirstFinitePositive(_topologyScrollViewer.ViewportHeight, _topologyScrollViewer.ActualHeight);
        var contentWidth = FirstFinitePositive(_topologyCanvas.Width, _topologyCanvas.ActualWidth, _topologyCanvas.DesiredSize.Width);
        var contentHeight = FirstFinitePositive(_topologyCanvas.Height, _topologyCanvas.ActualHeight, _topologyCanvas.DesiredSize.Height);
        if (viewportWidth is null || viewportHeight is null || contentWidth is null || contentHeight is null)
            return;

        var fullContentFit = Math.Min(viewportWidth.Value / contentWidth.Value, viewportHeight.Value / contentHeight.Value);
        _topologyMinZoom = Math.Clamp(Math.Min(1d, fullContentFit), AppMapMinZoomFloor, 1d);

        // The pipeline's primary lineage is horizontal. Fitting the complete height here makes a map with
        // many popup branches open as a tiny vertical strip. Start by fitting the columns to the viewport
        // width instead; the secondary rows remain available through vertical scrolling.
        var horizontalStartZoom = Math.Clamp(
            Math.Min(1d, viewportWidth.Value / contentWidth.Value),
            _topologyMinZoom,
            1d);
        var shouldResetToHorizontalStart = resetToFit || _topologyZoomPinnedToFit;
        var targetZoom = shouldResetToHorizontalStart
            ? horizontalStartZoom
            : Math.Clamp(_topologyZoom, _topologyMinZoom, AppMapMaxZoom);
        ApplyTopologyZoom(targetZoom);
        if (!shouldResetToHorizontalStart)
            return;

        _topologyScrollViewer.ScrollToHorizontalOffset(0);
        _topologyScrollViewer.ScrollToVerticalOffset(0);
        _topologyZoomPinnedToFit = true;
    }

    private void QueueTopologyZoomBoundsUpdate(bool resetToFit)
    {
        if (_topologyScrollViewer is null)
            return;

        _topologyScrollViewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => UpdateTopologyZoomBounds(resetToFit)));
    }

    private void AttachAppMapZoomHandlers(ScrollViewer scrollViewer)
    {
        scrollViewer.PreviewMouseWheel += (_, args) =>
        {
            if (!TryHandleAppMapZoomWheel(scrollViewer, args.GetPosition(scrollViewer), args.Delta))
                return;

            args.Handled = true;
        };

        scrollViewer.SizeChanged += (_, _) => QueueAppMapZoomBoundsUpdate(resetToFit: false);
    }

    private bool TryHandleAppMapZoomWheel(ScrollViewer scrollViewer, Point anchor, int delta)
    {
        if (_selectedSurface is null || _appMapViewport.Width <= 0 || _appMapViewport.Height <= 0)
            return false;

        var factor = delta > 0 ? AppMapZoomStep : 1 / AppMapZoomStep;
        var targetZoom = Math.Clamp(_appMapZoom * factor, _appMapMinZoom, AppMapMaxZoom);
        if (Math.Abs(targetZoom - _appMapZoom) < 0.0001)
            return false;

        SetAppMapZoom(scrollViewer, targetZoom, anchor);
        return true;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
                return true;

            source = GetDependencyParent(source);
        }

        return false;
    }

    private static DependencyObject? GetDependencyParent(DependencyObject source) =>
        source switch
        {
            Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
            FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            ContentElement contentElement => ContentOperations.GetParent(contentElement),
            _ => LogicalTreeHelper.GetParent(source)
        };

    private static bool IsInteractiveMapHit(DependencyObject? source)
    {
        while (source is not null)
        {
            // Let the native ScrollViewer controls own thumb/track dragging.
            // Starting canvas panning from the same mouse-down makes the custom
            // offset fight the Windows scrollbar and reverses horizontal motion.
            if (source is Button or ScrollBar or Thumb)
                return true;
            if (source is Border border && Equals(border.Cursor, Cursors.Hand))
                return true;
            if (source is Rectangle rectangle && Equals(rectangle.Cursor, Cursors.Hand))
                return true;
            source = GetDependencyParent(source);
        }

        return false;
    }

    private void SetAppMapZoom(ScrollViewer scrollViewer, double zoom, Point anchor)
    {
        var oldZoom = _appMapZoom;
        if (oldZoom <= 0 || Math.Abs(zoom - oldZoom) < 0.0001)
            return;

        var anchorX = (scrollViewer.HorizontalOffset + anchor.X) / oldZoom;
        var anchorY = (scrollViewer.VerticalOffset + anchor.Y) / oldZoom;
        ApplyAppMapZoom(zoom);
        scrollViewer.UpdateLayout();
        scrollViewer.ScrollToHorizontalOffset(Math.Max(0, anchorX * zoom - anchor.X));
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, anchorY * zoom - anchor.Y));
        _appMapZoomPinnedToFit = Math.Abs(zoom - _appMapMinZoom) < 0.0001;
    }

    private void ApplyAppMapZoom(double zoom)
    {
        _appMapZoom = zoom;
        _appMapZoomTransform.ScaleX = zoom;
        _appMapZoomTransform.ScaleY = zoom;
    }

    private void UpdateAppMapZoomBounds(bool resetToFit)
    {
        if (_appMapScrollViewer is null)
            return;

        var viewportWidth = FirstFinitePositive(_appMapScrollViewer.ViewportWidth, _appMapScrollViewer.ActualWidth);
        var viewportHeight = FirstFinitePositive(_appMapScrollViewer.ViewportHeight, _appMapScrollViewer.ActualHeight);
        var contentWidth = FirstFinitePositive(_appMapViewport.Width, _appMapViewport.ActualWidth, _appMapViewport.DesiredSize.Width);
        var contentHeight = FirstFinitePositive(_appMapViewport.Height, _appMapViewport.ActualHeight, _appMapViewport.DesiredSize.Height);
        if (viewportWidth is null || viewportHeight is null || contentWidth is null || contentHeight is null)
            return;

        var fitZoom = Math.Min(viewportWidth.Value / contentWidth.Value, viewportHeight.Value / contentHeight.Value);
        _appMapMinZoom = Math.Clamp(Math.Min(1d, fitZoom), AppMapMinZoomFloor, 1d);

        var shouldFit = resetToFit || _appMapZoomPinnedToFit || _appMapZoom < _appMapMinZoom;
        var targetZoom = shouldFit ? _appMapMinZoom : Math.Clamp(_appMapZoom, _appMapMinZoom, AppMapMaxZoom);
        ApplyAppMapZoom(targetZoom);
        if (!shouldFit)
            return;

        _appMapScrollViewer.ScrollToHorizontalOffset(0);
        _appMapScrollViewer.ScrollToVerticalOffset(0);
        _appMapZoomPinnedToFit = true;
    }

    private void QueueAppMapZoomBoundsUpdate(bool resetToFit)
    {
        if (_appMapScrollViewer is null)
            return;

        _appMapScrollViewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => UpdateAppMapZoomBounds(resetToFit)));
    }

    private static double? FirstFinitePositive(params double[] values)
    {
        foreach (var value in values)
            if (double.IsFinite(value) && value > 0)
                return value;
        return null;
    }

    private void ReleasePan(ScrollViewer scrollViewer)
    {
        if (_activePanScrollViewer == scrollViewer)
        {
            _activePanScrollViewer = null;
            _panActive = false;
        }

        if (scrollViewer.IsMouseCaptured)
            scrollViewer.ReleaseMouseCapture();
        scrollViewer.Cursor = Cursors.Hand;
    }

    private void SetLevel(UiUnderstandingLevel level)
    {
        _level = level;
        _statusLevel.Text = level.DisplayName();
        foreach (var pair in _levelButtons)
        {
            var color = LevelBrush(pair.Key);
            pair.Value.Background = pair.Key == level ? color : Brushes.White;
            pair.Value.Foreground = pair.Key == level ? Brushes.White : color;
        }
        RefreshSurfaceKindFilter();
        RefreshAll();
    }

    private void LoadGraph(string path)
    {
        _ = LoadGraphAsync(path);
    }

    private async Task LoadGraphAsync(string path)
    {
        var loadVersion = Interlocked.Increment(ref _graphLoadVersion);
        var fullPath = IOPath.GetFullPath(path);
        SetGraphLoadingState(true, $"Opening map: {IOPath.GetFileName(fullPath)}...");
        try
        {
            var model = await Task.Run(() => new UiMappingReadModel(new UiGraphReader().Load(fullPath)));
            if (loadVersion != Volatile.Read(ref _graphLoadVersion))
                return;

            _model = model;
            _graphPath = fullPath;
            if (_evidence is not null &&
                _evidence.SessionIds.Any(sessionId => !_model.Graph.Metadata.EffectiveSourceBundleIds.Contains(sessionId, StringComparer.Ordinal)))
            {
                _evidence.Dispose();
                _evidence = null;
                _attachedEvidencePaths = [];
            }
            Title = $"UiAtlas Core — {IOPath.GetFileName(fullPath)}";
            _title.Text = $"{_model.ApplicationDisplayName}: UI Knowledge Graph Editor";
            _breadcrumb.Text = $"{_model.ApplicationDisplayName}  ›  Window  ›  Control";
            SetLevel(UiUnderstandingLevel.SemanticWorld);
            UpdateCaptureSummary(fullPath);
            TryAutoAttachEvidence(fullPath);
            _status.Text = $"Opened map: {IOPath.GetFileName(fullPath)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            if (loadVersion != Volatile.Read(ref _graphLoadVersion))
                return;
            _status.Text = "Could not open map.";
            MessageBox.Show(this, ex.Message, "Could not open map", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _graphLoadVersion))
                SetGraphLoadingState(false, _status.Text);
        }
    }

    private void SetGraphLoadingState(bool isLoading, string? statusText)
    {
        Cursor = isLoading ? Cursors.Wait : null;
        _search.IsEnabled = !isLoading;
        _surfaceKindFilter.IsEnabled = !isLoading && _surfaceKindFilter.Items.Count > 0;
        _hierarchy.IsEnabled = !isLoading;
        _topologyCanvas.IsEnabled = !isLoading;
        _appMapViewport.IsEnabled = !isLoading;
        if (isLoading)
        {
            _variantPicker.IsEnabled = false;
            if (_previousVariantButton is not null) _previousVariantButton.IsEnabled = false;
            if (_nextVariantButton is not null) _nextVariantButton.IsEnabled = false;
        }
        else
        {
            UpdateVariantNavigatorState();
        }
        _properties.IsEnabled = !isLoading;
        _loadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        _loadingTitle.Text = isLoading ? "Please wait, map is loading..." : string.Empty;
        _loadingDetail.Text = isLoading
            ? string.IsNullOrWhiteSpace(statusText) ? "Large maps can take a moment to open." : statusText
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(statusText))
            _status.Text = statusText;
    }

    private Border BuildLoadingOverlay()
    {
        var progress = new ProgressBar
        {
            Width = 260,
            Height = 8,
            IsIndeterminate = true,
            Foreground = Blue,
            Background = Brush("#E8ECF7"),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 16)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(progress);
        stack.Children.Add(_loadingTitle);
        stack.Children.Add(new Border { Height = 8, Background = Brushes.Transparent });
        stack.Children.Add(_loadingDetail);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(166, 247, 248, 252)),
            BorderBrush = Brush("#D8DEEE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(24, 18, 24, 18),
            Padding = new Thickness(28, 24, 28, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Child = stack
        };
    }

    private void AttachEvidence(string path)
    {
        if (TryAttachEvidence([path], showErrors: true))
            RenderAppMap();
    }

    private bool TryAttachEvidence(IEnumerable<string> paths, bool showErrors)
    {
        try
        {
            var resolvedPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(IOPath.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (resolvedPaths.Length == 0)
                throw new InvalidDataException("No recording bundles were selected.");

            if (_evidence is not null && resolvedPaths.Length == _attachedEvidencePaths.Count &&
                resolvedPaths.All(path => _attachedEvidencePaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                return true;

            var evidence = UiEvidenceReader.Open(resolvedPaths);
            if (_model is not null &&
                evidence.SessionIds.Any(sessionId => !_model.Graph.Metadata.EffectiveSourceBundleIds.Contains(sessionId, StringComparer.Ordinal)))
            {
                evidence.Dispose();
                throw new InvalidDataException("The recording bundle does not match this map.");
            }
            _evidence?.Dispose();
            _evidence = evidence;
            _attachedEvidencePaths = resolvedPaths;
            _legacyGridRepairCancellation.Cancel();
            _legacyGridRepairCancellation.Dispose();
            _legacyGridRepairCancellation = new CancellationTokenSource();
            _evidenceAttachmentVersion++;
            _legacyGridRepairCache.Clear();
            _status.Text = evidence.SessionIds.Count == 1
                ? $"Evidence attached: {IOPath.GetFileName(resolvedPaths[0])}"
                : $"Evidence attached: {evidence.SessionIds.Count} bundles";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (showErrors)
                MessageBox.Show(this, ex.Message, "Could not attach evidence", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void TryAutoAttachEvidence(string graphPath)
    {
        if (_model is null || _evidence is not null) return;
        var candidates = MatchingEvidenceCandidates(graphPath, _model.Graph.Metadata.EffectiveSourceBundleIds)
            .Where(File.Exists)
            .ToArray();
        if (candidates.Length == 0) return;
        if (!TryAttachEvidence(candidates, showErrors: false)) return;
        _status.Text = candidates.Length == 1
            ? $"Evidence auto-attached: {System.IO.Path.GetFileName(candidates[0])}"
            : $"Evidence auto-attached: {candidates.Length} bundles";
        RenderAppMap();
    }

    private static IReadOnlyList<string> MatchingEvidenceCandidates(string graphPath, IReadOnlyList<string> sourceBundleIds)
    {
        var values = new List<string>();
        foreach (var sourceBundleId in sourceBundleIds)
        {
            if (!LocalArtifactCatalog.IsValidId(sourceBundleId)) continue;
            try
            {
                var catalog = new LocalArtifactCatalog();
                values.Add(catalog.RecordingPath(sourceBundleId));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or UnauthorizedAccessException)
            {
                // Best-effort discovery only.
            }
        }

        var graphDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(graphPath));
        if (!string.IsNullOrWhiteSpace(graphDirectory))
        {
            var sessionManifestPath = System.IO.Path.Combine(graphDirectory, System.IO.Path.GetFileNameWithoutExtension(graphPath) + ".session.json");
            if (File.Exists(sessionManifestPath))
            {
                try
                {
                    var manifest = LogicalMapSessionStore.Load(sessionManifestPath);
                    values.AddRange(LogicalMapSessionStore.RecordingPaths(manifest));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // Best-effort discovery only.
                }
            }
            foreach (var sourceBundleId in sourceBundleIds)
            {
                values.Add(System.IO.Path.Combine(graphDirectory, sourceBundleId + ".mlrec"));
                values.Add(System.IO.Path.Combine(Directory.GetParent(graphDirectory)?.FullName ?? graphDirectory, "recordings", sourceBundleId + ".mlrec"));
            }
            values.Add(System.IO.Path.Combine(graphDirectory, System.IO.Path.GetFileNameWithoutExtension(graphPath) + ".mlrec"));
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void UpdateCaptureSummary(string graphPath)
    {
        if (_model is null) return;
        var semanticControls = _model.Graph.Nodes.Where(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world")).ToArray();
        var unverified = semanticControls.Count(node => node.Properties.Any(property =>
            property.Name == "verificationStatus" && property.Value == "Unverified"));
        var visible = semanticControls.Length - unverified;
        var predictionMetrics = new SpeculativePlanningMetrics(0, 0, 0, 0, 0);
        _manualReviewItems = [];

        var manifestPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(graphPath) ?? Environment.CurrentDirectory,
            System.IO.Path.GetFileNameWithoutExtension(graphPath) + ".session.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = LogicalMapSessionStore.Load(manifestPath);
                _manualReviewItems = (manifest.AutoMapping?.Items ?? [])
                    .Where(item => item.Status == AutoMappingWorkStatus.NeedsManual)
                    .OrderBy(item => item.Kind)
                    .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (manifest.SpeculativePlanning?.Metrics is { } metrics)
                    predictionMetrics = metrics;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // The graph remains usable even when its optional progress manifest is unavailable.
            }
        }

        _captureSummary.Text = $"Visible: {visible}   Unverified: {unverified}   " +
            $"Prepared: {predictionMetrics.Prepared}   Reused: {predictionMetrics.Reused}   " +
            $"Matched: {predictionMetrics.Matched}   Rejected: {predictionMetrics.Rejected}";
        _manualReviewButton.Content = $"Show next ({_manualReviewItems.Count})";
        _manualReviewButton.Visibility = _manualReviewItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _manualReviewButton.ToolTip = _manualReviewItems.Count > 0
            ? "Show the next item that needs a manual click."
            : null;
    }

    private void ShowNextManualReview()
    {
        if (_manualReviewItems.Count == 0)
            return;
        var next = _manualReviewItems[0];
        var label = string.IsNullOrWhiteSpace(next.DisplayName) ? next.Kind.ToString() : next.DisplayName;
        var remaining = string.Join(Environment.NewLine, _manualReviewItems.Take(8).Select((item, index) =>
            $"{index + 1}. {(string.IsNullOrWhiteSpace(item.DisplayName) ? item.Kind : item.DisplayName)} — {item.DiagnosticCode} ({item.Attempts}/2)"));
        var result = MessageBox.Show(
            this,
            $"Next: {label}\n\n{remaining}\n\nOpen the recorder and highlight the next item?",
            $"Manual review: {_manualReviewItems.Count}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
            ResumeCurrentMapRecording(manualReview: true);
    }

    private void ResumeCurrentMapRecording(bool manualReview = false)
    {
        if (string.IsNullOrWhiteSpace(_graphPath))
        {
            MessageBox.Show(this, "Open a map first, then resume recording from that map.", "Resume recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sessionManifestPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_graphPath) ?? Environment.CurrentDirectory,
            System.IO.Path.GetFileNameWithoutExtension(_graphPath) + ".session.json");
        if (!File.Exists(sessionManifestPath))
        {
            MessageBox.Show(this, "This map does not have a session manifest yet, so resume is not available.", "Resume recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LogicalMapSessionManifest manifest;
        try
        {
            manifest = LogicalMapSessionStore.Load(sessionManifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Resume recording", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var processOpen = Process.GetProcessesByName(manifest.ProcessName)
            .Any(process =>
            {
                try { return process.MainWindowHandle != IntPtr.Zero; }
                catch { return false; }
            });

        var prompt = processOpen
            ? $"Do you want to continue recording on this map?\n\nUiAtlas will reconnect to the open {manifest.ProcessName} window and append a new recording bundle."
            : $"Do you want to continue recording on this map?\n\nUiAtlas knows this map belongs to {manifest.ProcessName}, but that application window is not open right now. Open it first, then press Resume again.";
        var result = MessageBox.Show(this, prompt, "Resume recording", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK || !processOpen)
            return;

        try
        {
            StartResumeRecorderProcess(_graphPath, manualReview);
            _status.Text = $"Resume requested for {System.IO.Path.GetFileName(_graphPath)}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Resume recording", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void StartResumeRecorderProcess(string graphPath, bool manualReview = false)
    {
        var basePath = AppContext.BaseDirectory;
        var executable = System.IO.Path.Combine(basePath, "ui-atlas.exe");
        var assembly = System.IO.Path.Combine(basePath, "ui-atlas.dll");
        if (!File.Exists(executable) && !File.Exists(assembly))
        {
            var outputDirectory = new DirectoryInfo(basePath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var configuration = outputDirectory.Parent?.Name;
            var repository = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, "..", "..", "..", "..", ".."));
            if (configuration is not null)
            {
                executable = System.IO.Path.Combine(repository, "src", "UiAtlas.Core.Cli", "bin", configuration, "net10.0-windows10.0.19041.0", "ui-atlas.exe");
                assembly = System.IO.Path.Combine(repository, "src", "UiAtlas.Core.Cli", "bin", configuration, "net10.0-windows10.0.19041.0", "ui-atlas.dll");
            }
        }

        ProcessStartInfo start;
        if (File.Exists(executable))
        {
            start = new ProcessStartInfo(executable);
        }
        else if (File.Exists(assembly))
        {
            start = new ProcessStartInfo("dotnet");
            start.ArgumentList.Add(assembly);
        }
        else
        {
            throw new InvalidOperationException("The recorder CLI is not installed beside the desktop explorer.");
        }

        start.ArgumentList.Add("recording");
        start.ArgumentList.Add("resume");
        start.ArgumentList.Add(System.IO.Path.GetFullPath(graphPath));
        if (manualReview)
            start.ArgumentList.Add("--manual-review");
        Process.Start(start);
    }

    private void ShowExportMenu(Button anchor)
    {
        if (_model is null)
        {
            MessageBox.Show(this, "Open a map first, then export the graph or the attached recording bundle.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        menu.Items.Add(ExportMenuItem(
            "Training JSON...",
            "Exports a human-readable map JSON for model training and offline analysis.",
            ExportHumanReadableJson));
        menu.Items.Add(ExportMenuItem(
            "Compatibility JSON...",
            "Exports ui_knowledge_graph_vnext.json for downstream pipelines.",
            ExportCompatibilityJson));
        menu.Items.Add(ExportMenuItem(
            "SQLite Copy...",
            "Exports a standalone SQLite copy of the current graph.",
            ExportSqliteCopy));
        if (_attachedEvidencePaths.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ExportMenuItem(
                "Open bundle location",
                "Opens Explorer to the attached .mlrec bundle so you can copy the raw screenshots and events.",
                RevealRecordingBundle));
        }

        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowImportMenu(Button anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        menu.Items.Add(ExportMenuItem(
            "Import map into catalog...",
            "Copies a map into AppData\\Local\\UiAtlas\\Core\\maps and imports any matching recording bundles.",
            ImportMapIntoCatalog));
        menu.Items.Add(new Separator());
        menu.Items.Add(ExportMenuItem(
            "Open maps folder",
            "Opens the managed maps folder.",
            OpenMapsFolder));
        menu.Items.Add(ExportMenuItem(
            "Open recordings folder",
            "Opens the managed recordings folder.",
            OpenRecordingsFolder));

        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static MenuItem ExportMenuItem(string header, string toolTip, Action onClick)
    {
        var item = new MenuItem { Header = header, ToolTip = toolTip };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ImportMapIntoCatalog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import map into catalog",
            Filter = "UI KG map|*.db;*.sqlite;*.json|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var imported = ImportMapToCatalog(dialog.FileName);
            if (imported is null) return;

            _evidence?.Dispose();
            _evidence = null;
            _attachedEvidencePaths = [];
            LoadGraph(imported.MapPath);
            _status.Text = imported.SkippedRecordingCount > 0
                ? $"Imported '{imported.MapId}' to the catalog. {imported.ImportedRecordingCount} bundle(s) ready, {imported.SkippedRecordingCount} skipped."
                : imported.ImportedRecordingCount > 0
                    ? $"Imported '{imported.MapId}' to the catalog with {imported.ImportedRecordingCount} bundle(s)."
                    : $"Imported '{imported.MapId}' to the catalog.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private CatalogImportResult? ImportMapToCatalog(string sourceMapPath)
    {
        var sourceFullPath = IOPath.GetFullPath(sourceMapPath);
        var reader = new UiGraphReader();
        var graph = reader.Load(sourceFullPath);
        var catalog = new LocalArtifactCatalog();
        catalog.EnsureSafe();

        var mapId = ResolveCatalogMapId(catalog, graph, sourceFullPath);
        var targetMapPath = catalog.MapPath(mapId);
        var sameMapPath = string.Equals(sourceFullPath, targetMapPath, StringComparison.OrdinalIgnoreCase);
        if (!sameMapPath && File.Exists(targetMapPath))
        {
            var replace = MessageBox.Show(
                this,
                $"A catalog map named '{mapId}' already exists.{Environment.NewLine}{Environment.NewLine}Replace the existing catalog copy?",
                "Import map",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return null;
        }

        var curation = MapCurationStore.ResolveForImport(sourceFullPath, graph, targetMapPath);
        SqliteGraphStore.SaveImported(graph, targetMapPath, curation);

        var importedRecordingPaths = new List<string>();
        var skippedRecordingCount = 0;
        var expectedSessionIds = graph.Metadata.EffectiveSourceBundleIds
            .Where(LocalArtifactCatalog.IsValidId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in MatchingEvidenceCandidates(sourceFullPath, graph.Metadata.EffectiveSourceBundleIds)
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var bundle = RecordingBundle.Open(candidate);
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                if (!expectedSessionIds.Contains(manifest.SessionId) || !LocalArtifactCatalog.IsValidId(manifest.SessionId))
                    continue;

                var targetRecordingPath = catalog.RecordingPath(manifest.SessionId);
                var candidateFullPath = IOPath.GetFullPath(candidate);
                if (!string.Equals(candidateFullPath, targetRecordingPath, StringComparison.OrdinalIgnoreCase))
                    File.Copy(candidateFullPath, targetRecordingPath, overwrite: true);
                importedRecordingPaths.Add(targetRecordingPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                skippedRecordingCount++;
            }
        }

        var manifestPath = catalog.MapSessionPath(mapId);
        var orderedRecordingPaths = importedRecordingPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedRecordingPaths.Length > 0)
        {
            var sessionManifest = LogicalMapSessionStore.Create(
                mapId,
                ResolveCatalogProcessName(graph, sourceFullPath),
                graph.Metadata.BuiltUtc);
            foreach (var recordingPath in orderedRecordingPaths)
            {
                var sessionId = IOPath.GetFileNameWithoutExtension(recordingPath);
                sessionManifest = LogicalMapSessionStore.AddRecording(sessionManifest, sessionId, recordingPath, DateTimeOffset.UtcNow);
            }
            LogicalMapSessionStore.Save(manifestPath, sessionManifest);
        }
        else if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        return new(mapId, targetMapPath, orderedRecordingPaths.Length, skippedRecordingCount);
    }

    private void OpenMapsFolder()
    {
        var catalog = new LocalArtifactCatalog();
        OpenDirectory(catalog.MapsDirectory, "Could not open the maps folder");
    }

    private void OpenRecordingsFolder()
    {
        var catalog = new LocalArtifactCatalog();
        OpenDirectory(catalog.RecordingsDirectory, "Could not open the recordings folder");
    }

    private void OpenDirectory(string directory, string errorTitle)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            _status.Text = $"Opened folder: {directory}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ResolveCatalogMapId(LocalArtifactCatalog catalog, UiKnowledgeGraph graph, string sourceMapPath)
    {
        foreach (var candidate in new[]
                 {
                     graph.Metadata.EffectiveLogicalMapId,
                     IOPath.GetFileNameWithoutExtension(sourceMapPath),
                     ResolveCatalogProcessName(graph, sourceMapPath)
                 })
        {
            var normalized = NormalizeCatalogId(candidate);
            if (normalized is not null) return normalized;
        }

        return catalog.CreateId(ResolveCatalogProcessName(graph, sourceMapPath), graph.Metadata.BuiltUtc);
    }

    private static string ResolveCatalogProcessName(UiKnowledgeGraph graph, string sourceMapPath)
    {
        var application = graph.Nodes.FirstOrDefault(node => node.Kind == GraphNodeKind.Application);
        var processName = application?.Properties.FirstOrDefault(property => property.Name == "processName")?.Value;
        return string.IsNullOrWhiteSpace(processName)
            ? IOPath.GetFileNameWithoutExtension(sourceMapPath)
            : processName;
    }

    private static string? NormalizeCatalogId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length == 0 || builder[^1] == '-') continue;
            builder.Append('-');
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length == 0) return null;
        if (normalized.Length > 128)
            normalized = normalized[..128].Trim('-');
        return LocalArtifactCatalog.IsValidId(normalized) ? normalized : null;
    }

    private void ExportHumanReadableJson()
    {
        if (_model is null) return;
        var path = PromptExportPath(
            "Export training JSON",
            "JSON files|*.json|All files|*.*",
            ".json",
            SuggestedFileName(".map.json"));
        if (path is null) return;

        try
        {
            HumanReadableMapExporter.Publish(_model.Graph, path, acknowledgeSensitiveIdentities: true);
            _status.Text = $"Exported training JSON: {IOPath.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportCompatibilityJson()
    {
        if (_model is null) return;
        var path = PromptExportPath(
            "Export compatibility JSON",
            "JSON files|*.json|All files|*.*",
            ".json",
            UiAtlasVNextCompatibilityExporter.RequiredFileName);
        if (path is null) return;

        try
        {
            UiAtlasVNextCompatibilityExporter.Publish(
                _model.Graph,
                path,
                SuggestedProjectId(),
                acknowledgeSensitiveIdentities: true);
            _status.Text = $"Exported compatibility JSON: {IOPath.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportSqliteCopy()
    {
        if (_model is null) return;
        var path = PromptExportPath(
            "Export SQLite copy",
            "SQLite database|*.db;*.sqlite|All files|*.*",
            ".db",
            SuggestedFileName(".export.db"));
        if (path is null) return;

        try
        {
            SqliteMapExporter.Publish(_model.Graph, path);
            _status.Text = $"Exported SQLite copy: {IOPath.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RevealRecordingBundle()
    {
        if (_attachedEvidencePaths.Count == 0) return;
        var bundlePath = _attachedEvidencePaths[0];
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{bundlePath}\"") { UseShellExecute = true });
            _status.Text = $"Opened bundle location: {IOPath.GetFileName(bundlePath)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Could not open bundle location", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? PromptExportPath(string title, string filter, string defaultExtension, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = fileName,
            InitialDirectory = SuggestedExportDirectory()
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string SuggestedExportDirectory()
    {
        var directories = new[]
        {
            _graphPath is null ? null : IOPath.GetDirectoryName(_graphPath),
            _attachedEvidencePaths.Select(IOPath.GetDirectoryName).FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory))
        };

        foreach (var directory in directories)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return directory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string SuggestedFileName(string suffix)
    {
        var baseName = _graphPath is not null
            ? IOPath.GetFileNameWithoutExtension(_graphPath)
            : _model?.Graph.Metadata.EffectiveLogicalMapId ?? "ui-atlas-export";

        var invalidCharacters = IOPath.GetInvalidFileNameChars();
        var builder = new StringBuilder(baseName.Length);
        foreach (var character in baseName)
            builder.Append(Array.IndexOf(invalidCharacters, character) >= 0 ? '-' : character);

        var safeBaseName = builder.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(safeBaseName))
            safeBaseName = "ui-atlas-export";
        return safeBaseName + suffix;
    }

    private string SuggestedProjectId()
    {
        var source = _model?.Graph.Metadata.EffectiveLogicalMapId;
        if (string.IsNullOrWhiteSpace(source) && _graphPath is not null)
            source = IOPath.GetFileNameWithoutExtension(_graphPath);
        if (string.IsNullOrWhiteSpace(source))
            return "ui-atlas-project";

        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length == 0 || builder[^1] == '-') continue;
            builder.Append('-');
        }

        var value = builder.ToString().Trim('-');
        if (value.Length > 128)
            value = value[..128].Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "ui-atlas-project" : value;
    }

    private void RefreshAll()
    {
        if (_model is null) return;
        var query = _search.Text.Trim();
        var layer = _model.LayerFor(_level);
        var surfaceKind = _surfaceKindFilter.SelectedItem as string;
        var surfaces = layer.Surfaces.Where(surface => Matches(surface, query, surfaceKind)).ToArray();
        BuildHierarchy(query);
        DrawTopology();
        _topologyTitle.Text = $"{_model.ApplicationDisplayName} - Understanding Pipeline";
        _topologySummary.Text = $"{_level.DisplayName()} | {surfaces.Length} surfaces | {layer.Controls.Count} controls";
        _appMapSummary.Text = $"{_level.DisplayName()} | {_viewMode}";
        _status.Text = $"Surfaces: {surfaces.Length}   Controls: {layer.Controls.Count}   Level: {_level.DisplayName()}";
        var requestedId = _selectedSurface?.Level == _level ? _selectedSurface.Id : null;
        var selected = requestedId is null ? surfaces.FirstOrDefault() : surfaces.FirstOrDefault(surface => surface.Id == requestedId) ?? surfaces.FirstOrDefault();
        if (selected is not null) SelectSurface(selected);
        else ClearSelection();
    }

    private void BuildHierarchy(string query, string? revealedControlId = null)
    {
        _hierarchy.Items.Clear();
        _hierarchyItems.Clear();
        if (_model is null) return;
        var revealedControl = string.IsNullOrWhiteSpace(revealedControlId)
            ? null
            : _model.Layers.SelectMany(layer => layer.Controls)
                .FirstOrDefault(control => control.Id == revealedControlId);
        var app = TreeItem("Application", null, true, Ink, FontWeights.SemiBold);
        app.Items.Add(TreeItem(_model.ApplicationDisplayName, null, true, Blue, FontWeights.SemiBold));
        _hierarchy.Items.Add(app);
        foreach (var hierarchyGroup in _model.BuildHierarchy(_level))
        {
            var level = hierarchyGroup.Level;
            var group = TreeItem(level.DisplayName(), null, true, LevelBrush(level), FontWeights.SemiBold);
            if (level == UiUnderstandingLevel.RawDataStreams)
            {
                foreach (var native in _model.BuildPipeline(UiUnderstandingLevel.RawDataStreams).Nodes
                             .Where(node => node.Kind == UiPipelineNodeKind.NativeSurface))
                {
                    var sourceSurfaces = _model.LayerFor(level).Surfaces.Where(surface => native.SourceIds.Contains(surface.Id, StringComparer.Ordinal)).ToArray();
                    if (!sourceSurfaces.Any(surface => Matches(surface, query, level == _level ? _surfaceKindFilter.SelectedItem as string : null))) continue;
                    var item = TreeItem(native.DisplayName, new SelectionRef(native.SurfaceId, null), level == _level, LevelBrush(level), FontWeights.SemiBold);
                    item.ToolTip = native.Subtitle;
                    foreach (var sourceId in native.SourceIds) _hierarchyItems[sourceId] = item;
                    group.Items.Add(item);
                }
                _hierarchy.Items.Add(group);
                continue;
            }
            foreach (var surface in _model.LayerFor(level).Surfaces.Where(surface =>
                         (revealedControl is not null && revealedControl.Level == level &&
                          surface.Id == revealedControl.OwnerSurfaceId) ||
                         Matches(surface, query,
                             level == _level ? _surfaceKindFilter.SelectedItem as string : null)))
            {
                var item = TreeItem(surface.DisplayName, new SelectionRef(surface.Id, null), level == _level, LevelBrush(level), FontWeights.SemiBold);
                var visibleVariantCount = surface.Variants.Count(variant => variant.IsVisibleByDefault);
                item.ToolTip = $"{surface.SurfaceKind} | {surface.ControlCount} controls | {visibleVariantCount} variants";
                _hierarchyItems[surface.Id] = item;
                foreach (var control in _model.LayerFor(level).ControlsForSurface(surface.Id)
                             .Where(control => control.Id == revealedControlId ||
                                               string.IsNullOrWhiteSpace(query) ||
                                               control.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                               control.CanonicalKind.Contains(query, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(control => control.DisplayName, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(control => control.Id, StringComparer.Ordinal))
                {
                    var unverified = control.Source.Properties.Any(property =>
                        property.Name == "verificationStatus" && property.Value == "Unverified");
                    var canConfirmButton = GraphControlConfirmation.IsConfirmableButtonCandidate(control.Source);
                    var canRemoveButton = GraphControlConfirmation.IsRemovableButtonCandidate(control.Source);
                    var controlItem = TreeItem(
                        canConfirmButton
                            ? $"{control.DisplayName} · button candidate"
                            : unverified ? $"{control.DisplayName} · unverified" : control.DisplayName,
                        new SelectionRef(surface.Id, control.Id),
                        false,
                        canConfirmButton ? Brush("#F97316") : unverified ? Muted : LevelBrush(level),
                        FontWeights.Normal,
                        canConfirmButton ? () => _ = ConfirmButtonCandidateAsync(control.Id) : null,
                        canRemoveButton ? () => _ = RemoveButtonCandidateAsync(control.Id) : null);
                    controlItem.Opacity = unverified && !canConfirmButton ? 0.68 : 1;
                    controlItem.ToolTip = canConfirmButton
                        ? $"{control.CanonicalKind} | potential button: hover to confirm or remove"
                        : unverified
                            ? $"{control.CanonicalKind} | discovered in the UI tree but not yet visible or confirmed"
                        : $"{control.CanonicalKind} | observed";
                    item.Items.Add(controlItem);
                    _hierarchyItems[control.Id] = controlItem;
                }
                group.Items.Add(item);
            }
            _hierarchy.Items.Add(group);
        }
        if (_predictionLayerToggle.IsChecked == true)
            AddPredictionHierarchy();
        if (_model.InteractionSteps.Count > 0)
        {
            var traces = TreeItem("Interaction Traces", null, true, Brush("#2563EB"), FontWeights.SemiBold);
            foreach (var session in _model.InteractionSteps.GroupBy(step => step.BundleId, StringComparer.Ordinal))
            {
                var sessionItem = TreeItem($"Session {session.Key}", null, false, Ink, FontWeights.SemiBold);
                foreach (var step in session.Where(MatchesInteractionFilters).OrderBy(step => step.Sequence))
                {
                    var control = _model.Layers.SelectMany(layer => layer.Controls)
                        .FirstOrDefault(item => item.Id == step.SourceControlId);
                    var label = $"{step.Sequence}. {control?.DisplayName ?? "Control"} → {step.Action} [{step.Outcome}]";
                    var stepItem = TreeItem(label, new SelectionRef(null, null, step.Id), false,
                        step.Outcome == InteractionOutcome.Succeeded ? Green : Brush("#DC2626"), FontWeights.Normal);
                    stepItem.Items.Add(TreeItem($"Source · {control?.DisplayName ?? step.SourceControlId}",
                        new SelectionRef(null, step.SourceControlId, step.Id), false, Blue, FontWeights.Normal));
                    foreach (var resultFrame in step.ResultFrameSequences)
                        stepItem.Items.Add(TreeItem($"Result · frame {resultFrame}",
                            new SelectionRef(null, null, step.Id), false, Green, FontWeights.Normal));
                    if (step.ResultFrameSequences.Count == 0)
                        stepItem.Items.Add(TreeItem("Result · unknown", null, false, Muted, FontWeights.Normal));
                    sessionItem.Items.Add(stepItem);
                }
                traces.Items.Add(sessionItem);
            }
            _hierarchy.Items.Add(traces);
        }
    }

    private void AddPredictionHierarchy()
    {
        if (_model is null) return;
        var predictions = _model.Graph.Nodes.Where(node =>
            node.Kind == GraphNodeKind.State &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "prediction"))
            .OrderBy(node => int.TryParse(NodeProperty(node, "revision"), out var revision) ? revision : 0)
            .ThenBy(node => int.TryParse(NodeProperty(node, "depth"), out var depth) ? depth : 0)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        if (predictions.Length == 0) return;

        var group = TreeItem("Predicted", null, true, Muted, FontWeights.SemiBold);
        var byParent = predictions.GroupBy(node => node.ParentId, StringComparer.Ordinal)
            .ToDictionary(items => items.Key, items => items.ToArray(), StringComparer.Ordinal);
        var predictionIds = predictions.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var root in predictions.Where(node => !predictionIds.Contains(node.ParentId)))
            group.Items.Add(PredictionTreeItem(root, byParent));
        _hierarchy.Items.Add(group);
    }

    private static TreeViewItem PredictionTreeItem(
        GraphNode node,
        IReadOnlyDictionary<string, GraphNode[]> byParent)
    {
        var status = NodeProperty(node, "predictionStatus") ?? "Predicted";
        var confidence = double.TryParse(NodeProperty(node, "confidence"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : 0;
        var path = NodeProperty(node, "predictedPath") ?? node.Label;
        var source = NodeProperty(node, "knowledgeSource") ?? "surface";
        var item = TreeItem(
            $"┈ {node.Label} · {confidence:P0}",
            null,
            false,
            Muted,
            status == "Matched" ? FontWeights.SemiBold : FontWeights.Normal);
        item.Opacity = status == "Stale" ? 0.48 : 0.78;
        item.ToolTip = $"{path}\nStatus: {status}\nSource: {source}";
        if (byParent.TryGetValue(node.Id, out var children))
            foreach (var child in children.OrderBy(value => value.Id, StringComparer.Ordinal))
                item.Items.Add(PredictionTreeItem(child, byParent));
        return item;
    }

    private void DrawTopology()
    {
        _topologyCanvas.Children.Clear();
        _topologyShapes.Clear();
        if (_model is null) return;
        var topology = _model.BuildPipeline(_level);
        var visibleColumns = _level switch
        {
            UiUnderstandingLevel.RawDataStreams => 3,
            UiUnderstandingLevel.RawWorld => 4,
            _ => 5
        };
        var columnWidth = 230d;
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        for (var column = 0; column < visibleColumns; column++)
        {
            var header = Text(topology.ColumnHeaders[column], 11, FontWeights.SemiBold, Muted);
            Canvas.SetLeft(header, 28 + column * columnWidth);
            Canvas.SetTop(header, 12);
            _topologyCanvas.Children.Add(header);
            foreach (var node in topology.Nodes.Where(node => node.Column == column))
                positions[node.Id] = new Point(28 + column * columnWidth, 52 + node.Row * 76);
        }
        foreach (var edge in topology.Edges)
        {
            if (!positions.TryGetValue(edge.SourceId, out var source) || !positions.TryGetValue(edge.TargetId, out var target)) continue;
            _topologyCanvas.Children.Add(new Line { X1 = source.X + 175, Y1 = source.Y + 27, X2 = target.X, Y2 = target.Y + 27,
                Stroke = Brush("#BCC3D3"), StrokeThickness = 1.2 });
            var label = Text(edge.DisplayName, 9, FontWeights.Normal, Muted);
            Canvas.SetLeft(label, (source.X + 175 + target.X) / 2 - 28);
            Canvas.SetTop(label, (source.Y + target.Y) / 2 + 8);
            _topologyCanvas.Children.Add(label);
        }
        foreach (var node in topology.Nodes)
        {
            if (!positions.TryGetValue(node.Id, out var point)) continue;
            var border = new Border { Width = 175, Height = 55, CornerRadius = new CornerRadius(7), Background = NodeBackground(node.Kind),
                BorderBrush = NodeBrush(node.Kind), BorderThickness = new Thickness(1.2), Padding = new Thickness(9, 7, 9, 5), Cursor = Cursors.Hand };
            var text = new StackPanel();
            text.Children.Add(Text(node.DisplayName, 11, FontWeights.SemiBold, NodeBrush(node.Kind), textTrimming: TextTrimming.CharacterEllipsis));
            text.Children.Add(Text(node.Subtitle, 9, FontWeights.Normal, Muted, textTrimming: TextTrimming.CharacterEllipsis));
            border.Child = text;
            if (node.InspectionLevel is not null) border.MouseLeftButtonDown += (_, _) => SelectPipelineNode(node);
            _topologyShapes[node.SurfaceId ?? node.Id] = border;
            Canvas.SetLeft(border, point.X);
            Canvas.SetTop(border, point.Y);
            _topologyCanvas.Children.Add(border);
        }
        _topologyCanvas.Width = Math.Max(900, visibleColumns * columnWidth + 30);
        _topologyCanvas.Height = Math.Max(260, positions.Values.Select(point => point.Y).DefaultIfEmpty(0).Max() + 85);
        QueueTopologyZoomBoundsUpdate(resetToFit: true);
    }

    private void SelectSurfaceById(string id)
    {
        if (_model is null) return;
        var surface = _model.Layers.SelectMany(layer => layer.Surfaces).FirstOrDefault(candidate => candidate.Id == id);
        if (surface is not null) SelectSurface(surface);
    }

    private void SelectPipelineNode(UiPipelineNodeView node)
    {
        if (_model is null || node.InspectionLevel is null) return;
        var surfaces = _model.ResolvePipelineSurfaces(node);
        var surface = surfaces.FirstOrDefault(candidate => candidate.Id == node.SurfaceId) ?? surfaces.FirstOrDefault();
        if (surface is not null) SelectSurface(surface, surfaces);
    }

    private void SelectSurface(UiMapSurfaceView surface, IReadOnlyList<UiMapSurfaceView>? scope = null)
    {
        if (_model is null) return;
        _inspectionLevel = surface.Level;
        _selectedSurface = surface;
        _selectedSurfaceScope = scope is { Count: > 0 } ? scope : [surface];
        _selectedControl = null;
        _selectedVariant = null;
        _resetAppMapZoomOnNextRender = true;
        _synchronizing = true;
        RevealHierarchyItem(surface.Id);
        _synchronizing = false;
        HighlightTopology(surface.Id);
        BuildVariants(surface);
        _appMapSummary.Text = $"{_inspectionLevel.DisplayName()} | {_viewMode}";
        RenderAppMap();
        var propertySurface = _selectedSurface ?? surface;
        ShowProperties(propertySurface.Source, propertySurface.ControlCount, _model.VariantsFor(_selectedSurfaceScope).Count);
    }

    private void SelectControlById(string id)
    {
        if (_model is null) return;
        var control = _model.Layers.SelectMany(layer => layer.Controls).FirstOrDefault(candidate => candidate.Id == id);
        if (control is null) return;
        var surface = _model.LayerFor(control.Level).Surfaces.FirstOrDefault(candidate => candidate.Id == control.OwnerSurfaceId);
        if (surface is null) return;
        _inspectionLevel = surface.Level;
        _selectedSurface = surface;
        _selectedSurfaceScope = [surface];
        _selectedControl = control;
        _selectedVariant = UiMapPresentation.ResolveControlVariant(
            control,
            _model.VariantsFor(_selectedSurfaceScope, control),
            _selectedVariant);
        // Selecting a control can move selection to an owned/child surface that is
        // rendered over the same evidence frame. That is not navigation and must
        // not discard the user's zoom. Explicit surface navigation still requests
        // fit-to-view through SelectSurface.
        _resetAppMapZoomOnNextRender = false;
        _synchronizing = true;
        if (!_hierarchyItems.ContainsKey(control.Id))
            BuildHierarchy(_search.Text.Trim(), control.Id);
        RevealHierarchyItem(control.Id);
        _synchronizing = false;
        HighlightTopology(surface.Id);
        BuildVariants(surface);
        SetViewMode("Overlay");
        _appMapSummary.Text = $"{_inspectionLevel.DisplayName()} | {_viewMode}";
        RenderAppMap();
        ShowProperties(control.Source, 0, 0);
    }

    private void RevealHierarchyItem(string id)
    {
        if (!_hierarchyItems.TryGetValue(id, out var item)) return;

        foreach (var root in _hierarchy.Items.OfType<TreeViewItem>())
        {
            if (ReferenceEquals(root, item) || ExpandHierarchyPath(root, item))
                break;
        }

        item.IsSelected = true;
        _hierarchy.UpdateLayout();
        item.BringIntoView();
        item.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (item.IsSelected) item.BringIntoView();
            }));
    }

    private static bool ExpandHierarchyPath(TreeViewItem parent, TreeViewItem target)
    {
        foreach (var child in parent.Items.OfType<TreeViewItem>())
        {
            if (!ReferenceEquals(child, target) && !ExpandHierarchyPath(child, target)) continue;
            parent.IsExpanded = true;
            return true;
        }

        return false;
    }

    private async Task ConfirmButtonCandidateAsync(string controlId)
    {
        if (_model is null || string.IsNullOrWhiteSpace(_graphPath) ||
            !_curatingControlIds.Add(controlId))
            return;

        try
        {
            var target = _model.Graph.Nodes.FirstOrDefault(node => node.Id == controlId)
                         ?? throw new InvalidOperationException("Control does not exist in this map.");
            var now = DateTimeOffset.UtcNow;
            var document = MapCurationStore.UpsertRule(
                LoadCurationDocument(), target.StableKey, "Confirm", now);
            var graphPath = _graphPath;
            _status.Text = "Confirming button candidate...";
            var sourceGraph = _model.Graph;
            var updated = await Task.Run(() =>
            {
                MapCurationStore.Save(graphPath, document);
                var curated = MapCurationStore.Reapply(sourceGraph, document);
                SaveGraph(curated, graphPath);
                return curated;
            });

            _model = new UiMappingReadModel(updated);
            UpdateCaptureSummary(graphPath);
            RefreshSurfaceKindFilter();
            RefreshAll();
            SelectControlById(controlId);
            _status.Text = "Button confirmed and saved in the map.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                                       UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = "Could not confirm the button candidate.";
            MessageBox.Show(this, ex.Message, "Confirm button", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _curatingControlIds.Remove(controlId);
        }
    }

    private async Task RemoveButtonCandidateAsync(string controlId)
    {
        if (_model is null || string.IsNullOrWhiteSpace(_graphPath) ||
            !_curatingControlIds.Add(controlId))
            return;

        try
        {
            var target = _model.Graph.Nodes.FirstOrDefault(node => node.Id == controlId);
            if (target is null) return;
            var answer = MessageBox.Show(
                this,
                $"Remove ‘{target.Label}’ from the map as an incorrect control?\n\n" +
                "The candidate will be removed from every map level.",
                "Remove incorrect control",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var document = MapCurationStore.UpsertRule(
                LoadCurationDocument(), target.StableKey, "Suppress", DateTimeOffset.UtcNow);
            var graphPath = _graphPath;
            _status.Text = "Removing incorrect control...";
            var sourceGraph = _model.Graph;
            var updated = await Task.Run(() =>
            {
                MapCurationStore.Save(graphPath, document);
                var curated = MapCurationStore.Reapply(sourceGraph, document);
                SaveGraph(curated, graphPath);
                return curated;
            });

            _model = new UiMappingReadModel(updated);
            _selectedControl = null;
            UpdateCaptureSummary(graphPath);
            RefreshSurfaceKindFilter();
            RefreshAll();
            _status.Text = "Incorrect control removed from the map.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                                       UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = "Could not remove the control candidate.";
            MessageBox.Show(this, ex.Message, "Remove incorrect control", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _curatingControlIds.Remove(controlId);
        }
    }

    private static void SaveGraph(UiKnowledgeGraph graph, string path)
    {
        if (IOPath.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            GraphJsonStore.Save(graph, path);
        else
            SqliteGraphStore.Save(graph, path);
    }

    private void HighlightTopology(string surfaceId)
    {
        foreach (var pair in _topologyShapes)
        {
            pair.Value.BorderThickness = new Thickness(pair.Key == surfaceId ? 2.5 : 1.2);
            pair.Value.Effect = null;
        }
    }

    private void BuildVariants(UiMapSurfaceView surface)
    {
        if (_model is null) return;
        var variants = _model.VariantsFor(_selectedSurfaceScope, _selectedControl);
        _visibleVariants = variants;
        var requestedFrame = _selectedVariant?.FrameSequence;
        var requestedBundle = _selectedVariant?.BundleId;
        _selectedVariant = variants.FirstOrDefault(variant =>
                variant.FrameSequence == requestedFrame &&
                string.Equals(variant.BundleId, requestedBundle, StringComparison.Ordinal))
            ?? variants
                .OrderByDescending(variant => variant.ControlCount > 0)
                .ThenByDescending(variant => variant.ControlCount)
                .FirstOrDefault();
        _selectedSurface = ResolveSurfaceForVariant(_selectedVariant) ?? _selectedSurface ?? surface;

        _refreshingVariantPicker = true;
        _variantPicker.Items.Clear();
        foreach (var variant in variants)
            _variantPicker.Items.Add(new VariantOption(variant,
                $"Frame {variant.FrameSequence}  •  {variant.ControlCount} controls"));
        _variantPicker.SelectedIndex = _selectedVariant is null
            ? -1
            : variants.ToList().FindIndex(variant => variant == _selectedVariant);
        _variantPicker.Text = _variantPicker.SelectedItem is VariantOption selected
            ? selected.Label
            : "No observed frames";
        _refreshingVariantPicker = false;
        UpdateVariantNavigatorState();
    }

    private void RestoreVariantPickerText()
    {
        if (_variantPicker.SelectedItem is VariantOption selected)
            _variantPicker.Text = selected.Label;
        else if (_visibleVariants.Count == 0)
            _variantPicker.Text = "No observed frames";
    }

    private void NavigateVariant(int offset)
    {
        if (_visibleVariants.Count == 0) return;
        var currentIndex = _selectedVariant is null
            ? -1
            : _visibleVariants.ToList().FindIndex(variant => variant == _selectedVariant);
        var targetIndex = Math.Clamp(currentIndex + offset, 0, _visibleVariants.Count - 1);
        if (targetIndex == currentIndex) return;
        _variantPicker.SelectedIndex = targetIndex;
    }

    private void SelectVariant(UiMapVariantView variant)
    {
        _selectedVariant = variant;
        _selectedSurface = ResolveSurfaceForVariant(variant) ?? _selectedSurface;
        RenderAppMap();
        if (_selectedSurface is not null)
            ShowProperties(_selectedSurface.Source, _selectedSurface.ControlCount, _visibleVariants.Count);
        UpdateVariantNavigatorState();
    }

    private void UpdateVariantNavigatorState()
    {
        var index = _selectedVariant is null
            ? -1
            : _visibleVariants.ToList().FindIndex(variant => variant == _selectedVariant);
        var hasSelection = index >= 0;
        _variantPicker.IsEnabled = _visibleVariants.Count > 0;
        if (_previousVariantButton is not null)
            _previousVariantButton.IsEnabled = hasSelection && index > 0;
        if (_nextVariantButton is not null)
            _nextVariantButton.IsEnabled = hasSelection && index < _visibleVariants.Count - 1;
        _variantPosition.Text = hasSelection ? $"{index + 1} of {_visibleVariants.Count}" : "0 frames";
    }

    private void RenderAppMap()
    {
        CancelManualButtonEditor();
        _renderedViewportBounds = null;
        _renderedEvidencePng = null;
        _renderedImageSourceOffset = new Point();
        _appMapImage.Source = null;
        _appMapImage.Opacity = 1;
        _appMapOverlay.Children.Clear();
        _traceBannerHost.Content = null;
        _traceBannerHost.Visibility = Visibility.Collapsed;
        _activeLegacyGridRepairKey = null;
        if (_selectedSurface is null || _model is null) return;
        var evidence = ResolveSelectedEvidence();
        UiEvidenceImage? evidenceImage = null;
        BitmapSource? bitmap = null;
        var contentOriginX = _selectedSurface.Bounds.X;
        var contentOriginY = _selectedSurface.Bounds.Y;
        if (evidence is not null && _evidence is not null)
        {
            try
            {
                var frame = _evidence.Read(evidence);
                if (frame is not null)
                {
                    evidenceImage = frame;
                    _renderedEvidencePng = frame.Png;
                    // Recording evidence represents an opaque window rectangle.
                    // Older WGC captures can contain valid Office RGB pixels with
                    // zero alpha, which made captions and colour swatches vanish
                    // only in this viewer. Dropping alpha repairs those maps too.
                    var sourceBitmap = WindowSnapshotCapture.DecodeOpaquePng(frame.Png);
                    bitmap = sourceBitmap;
                    if (UiMapPresentation.ShouldCropSceneToSurface(ParseProjectionMode(_viewMode)) &&
                        frame.Highlight is { } surfaceCrop && evidence.Bounds is { } absolute &&
                        TryClipBitmapRect(surfaceCrop, sourceBitmap, out var clippedCrop))
                    {
                        bitmap = new CroppedBitmap(sourceBitmap, clippedCrop);
                        _renderedImageSourceOffset = new Point(clippedCrop.X, clippedCrop.Y);
                        contentOriginX = absolute.X + clippedCrop.X - surfaceCrop.X;
                        contentOriginY = absolute.Y + clippedCrop.Y - surfaceCrop.Y;
                    }
                    else if (frame.Highlight is { } relative && evidence.Bounds is { } bounds)
                    {
                        contentOriginX = bounds.X - relative.X;
                        contentOriginY = bounds.Y - relative.Y;
                    }
                    bitmap.Freeze();
                    _appMapImage.Source = bitmap;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or
                                           FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        }
        var projectionMode = ParseProjectionMode(_viewMode);
        _appMapViewport.Background = projectionMode == UiMapProjectionMode.Routes
            ? Brush("#F8F7F4")
            : Brush("#F4F5F9");
        var width = projectionMode == UiMapProjectionMode.Routes
            ? 900
            : bitmap?.PixelWidth ?? Math.Max(1, _selectedSurface.Bounds.Width);
        var height = projectionMode == UiMapProjectionMode.Routes
            ? Math.Max(600, (_model.Routes.Count + _model.Affordances.Count(item => !item.WasObserved)) * 48 + 80)
            : bitmap?.PixelHeight ?? Math.Max(1, _selectedSurface.Bounds.Height);
        _appMapViewport.Width = width;
        _appMapViewport.Height = height;
        _appMapOverlay.Width = width;
        _appMapOverlay.Height = height;
        var projection = UiMapPresentation.PolicyFor(projectionMode);
        LegacyGridEvidenceRepair? legacyGridRepair = null;
        if (projection.ShowsControlGeometry && evidenceImage is not null)
        {
            try
            {
                var repairKey = string.Join('|',
                    _selectedVariant?.BundleId ?? string.Empty,
                    evidenceImage.Observation.Sequence,
                    evidenceImage.Entry);
                _activeLegacyGridRepairKey = repairKey;
                if (!_legacyGridRepairCache.TryGetValue(repairKey, out legacyGridRepair))
                    QueueLegacyGridEvidenceRepair(repairKey, evidenceImage);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
                                           FileFormatException or System.Runtime.InteropServices.COMException)
            {
                // A compatibility repair is optional. Invalid legacy pixels must
                // never prevent the original evidence frame from opening.
            }
        }
        _appMapImage.Visibility = projection.ShowsScene ? Visibility.Visible : Visibility.Collapsed;
        _appMapImage.Opacity = projection.SceneOpacity;
        if (bitmap is null && projection.ShowsScene)
        {
            var empty = Text(_evidence is null
                ? "No recording bundle is attached for this map yet. Open the map from the catalog flow to see screenshots."
                : "This observation intentionally contains no retained screenshot pixels.", 13, FontWeights.Normal, Muted);
            empty.TextWrapping = TextWrapping.Wrap;
            empty.MaxWidth = Math.Max(280, width - 60);
            Canvas.SetLeft(empty, 30); Canvas.SetTop(empty, 30); _appMapOverlay.Children.Add(empty);
        }
        var frameSequence = _selectedVariant?.FrameSequence;
        var frameBundleId = _selectedVariant?.BundleId;
        var activeSurfaceIds = _selectedVariant is not null
            ? new[] { _selectedSurface.Id }.ToHashSet(StringComparer.Ordinal)
            : _selectedSurfaceScope.Select(surface => surface.Id).ToHashSet(StringComparer.Ordinal);
        var scopedSurfaceById = _selectedSurfaceScope
            .Where(surface => activeSurfaceIds.Contains(surface.Id))
            .ToDictionary(surface => surface.Id, StringComparer.Ordinal);
        var scopedControls = _model.LayerFor(_inspectionLevel).Controls
            .Where(control => activeSurfaceIds.Contains(control.OwnerSurfaceId))
            .ToArray();
        var controls = scopedControls
            .Where(control => frameSequence is null || control.Evidence.Any(item =>
                item.FrameSequence == frameSequence &&
                (frameBundleId is null || string.Equals(item.BundleId, frameBundleId, StringComparison.Ordinal))))
            .Where(control => !UiMapPresentation.IsRedundantCaptionButton(
                control, frameSequence, frameBundleId, scopedControls))
            .Select(control => new
            {
                Control = control,
                Surface = scopedSurfaceById.GetValueOrDefault(control.OwnerSurfaceId),
                Bounds = UiMapPresentation.ResolveControlBounds(
                    control, frameSequence, frameBundleId, scopedControls)
            })
            .Where(item => item.Surface is not null)
            .Where(item => !UiMapPresentation.IsRedundantPopupEditor(
                item.Control,
                item.Surface!,
                frameSequence,
                frameBundleId,
                scopedControls))
            .Where(item => UiMapPresentation.ShouldRenderControl(
                item.Control,
                item.Surface!,
                projectionMode,
                item.Control.Id == _selectedControl?.Id))
            .Where(item => item.Control.Id == _selectedControl?.Id ||
                           legacyGridRepair is null || !legacyGridRepair.ContainsCenter(item.Bounds))
            .OrderBy(item => item.Control.Id == _selectedControl?.Id ? 1 : 0)
            .ThenBy(item => UiMapPresentation.ControlRenderPriority(item.Control, item.Surface!))
            .ThenByDescending(item => (long)item.Bounds.Width * item.Bounds.Height)
            .ThenBy(item => item.Control.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Control.Id, StringComparer.Ordinal)
            .ToArray();
        var viewportBounds = new RectI(contentOriginX, contentOriginY, width, height);
        _renderedViewportBounds = viewportBounds;
        if (projection.ShowsControlGeometry)
        {
            var selectedBounds = _selectedControl is null
                ? null
                : UiMapPresentation.ResolveControlBounds(
                    _selectedControl, frameSequence, frameBundleId, scopedControls);
            foreach (var item in controls)
            {
                var control = item.Control;
                var ownerSurface = item.Surface!;
                var local = UiMapPresentation.ProjectToSurface(item.Bounds, viewportBounds);
                if (local is null) continue;
                ImageBrush? controlCrop = null;
                if (projection.ShowsControlCrops && bitmap is not null &&
                    UiMapPresentation.ShouldUseControlCrop(control, ownerSurface) &&
                    TryClipBitmapRect(local, bitmap, out var cropRect))
                    controlCrop = new ImageBrush(new CroppedBitmap(bitmap, cropRect)) { Stretch = Stretch.Fill };
                var isSelected = control.Id == _selectedControl?.Id;
                var isUnverified = GraphControlConfirmation.IsUnverified(control.Source);
                var isButtonCandidate = GraphControlConfirmation.IsConfirmableButtonCandidate(control.Source);
                var isCompactPopupAction = UiMapPresentation.IsCompactPopupAction(control, ownerSurface);
                var highlightColor = isUnverified ? Color.FromRgb(0xFF, 0x8B, 0x3D) : Color.FromRgb(0x2D, 0xCE, 0x91);
                var rectangle = new Rectangle { Width = Math.Max(1, local.Width), Height = Math.Max(1, local.Height),
                    Stroke = isSelected || isButtonCandidate || isCompactPopupAction
                        ? new SolidColorBrush(highlightColor)
                        : LevelBrush(_inspectionLevel),
                    StrokeThickness = isSelected ? 4 : isButtonCandidate || isCompactPopupAction ? 2 : 1.2,
                    StrokeDashArray = !isSelected && isButtonCandidate && !isCompactPopupAction ? [4, 2] : null,
                    Fill = isSelected
                        ? new SolidColorBrush(Color.FromArgb(92, highlightColor.R, highlightColor.G, highlightColor.B))
                        : isButtonCandidate || isCompactPopupAction
                            ? new SolidColorBrush(Color.FromArgb(28, highlightColor.R, highlightColor.G, highlightColor.B))
                        : (Brush?)controlCrop ?? ResolveBlueprintFill(control, ownerSurface, _inspectionLevel, projection.BlueprintFillOpacity),
                    Effect = isSelected
                        ? new DropShadowEffect { Color = highlightColor, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 }
                        : null,
                    Cursor = Cursors.Hand,
                    ToolTip = $"{control.DisplayName} ({control.CanonicalKind})" };
                rectangle.MouseLeftButtonDown += (_, args) => { SelectControlById(control.Id); args.Handled = true; };
                Canvas.SetLeft(rectangle, local.X); Canvas.SetTop(rectangle, local.Y); _appMapOverlay.Children.Add(rectangle);
                if (projection.ShowsControlLabels || projection.ShowsControlCrops && controlCrop is null)
                {
                    var label = Text(control.DisplayName, 9, FontWeights.Normal, Ink, textTrimming: TextTrimming.CharacterEllipsis);
                    label.MaxWidth = Math.Max(30, local.Width - 4);
                    label.IsHitTestVisible = false;
                    Canvas.SetLeft(label, local.X + 2); Canvas.SetTop(label, local.Y + 1); _appMapOverlay.Children.Add(label);
                }
                if (isSelected && PropertyValue(control.Source, "manualAnnotationId") is { Length: > 0 } annotationId)
                    AddManualControlEditingAdornments(local, annotationId);
            }

            if (legacyGridRepair is not null)
            {
                foreach (var repaired in legacyGridRepair.Controls
                             .OrderByDescending(control => (long)control.Bounds.Width * control.Bounds.Height)
                             .ThenBy(control => control.TableRow)
                             .ThenBy(control => control.TableColumn))
                {
                    if (selectedBounds is { } selected && IntersectionOverUnion(selected, repaired.Bounds) >= .55)
                        continue;
                    var local = UiMapPresentation.ProjectToSurface(repaired.Bounds, viewportBounds);
                    if (local is null) continue;
                    var rectangle = new Rectangle
                    {
                        Width = Math.Max(1, local.Width),
                        Height = Math.Max(1, local.Height),
                        Stroke = LevelBrush(_inspectionLevel),
                        StrokeThickness = repaired.ControlType == "ControlType.Table" ? 1.8 : 1.2,
                        Fill = repaired.ControlType == "ControlType.Table"
                            ? Brushes.Transparent
                            : Brush("#1000A779"),
                        ToolTip = $"{repaired.Name} ({repaired.ControlType})"
                    };
                    Canvas.SetLeft(rectangle, local.X);
                    Canvas.SetTop(rectangle, local.Y);
                    _appMapOverlay.Children.Add(rectangle);
                    if (projection.ShowsControlLabels && repaired.ControlType == "ControlType.HeaderItem")
                    {
                        var label = Text(repaired.Name, 9, FontWeights.Normal, Ink,
                            textTrimming: TextTrimming.CharacterEllipsis);
                        label.MaxWidth = Math.Max(30, local.Width - 4);
                        label.IsHitTestVisible = false;
                        Canvas.SetLeft(label, local.X + 2);
                        Canvas.SetTop(label, local.Y + 1);
                        _appMapOverlay.Children.Add(label);
                    }
                }
                UpdateSelectedVariantRepairLabel(legacyGridRepair);
            }
        }
        if (projectionMode == UiMapProjectionMode.Trace)
            RenderInteractionTrace(evidenceImage, viewportBounds);
        else if (projectionMode == UiMapProjectionMode.Routes)
            RenderRoutes();

        QueueAppMapZoomBoundsUpdate(resetToFit: _resetAppMapZoomOnNextRender);
        _resetAppMapZoomOnNextRender = false;
    }

    private void QueueLegacyGridEvidenceRepair(string repairKey, UiEvidenceImage evidenceImage)
    {
        var attachmentVersion = _evidenceAttachmentVersion;
        var workKey = (attachmentVersion, repairKey);
        if (!_legacyGridRepairInFlight.Add(workKey)) return;
        var cancellationToken = _legacyGridRepairCancellation.Token;
        _ = CompleteLegacyGridEvidenceRepairAsync(
            repairKey, evidenceImage, attachmentVersion, cancellationToken);
    }

    private async Task CompleteLegacyGridEvidenceRepairAsync(
        string repairKey,
        UiEvidenceImage evidenceImage,
        int attachmentVersion,
        CancellationToken cancellationToken)
    {
        var enteredGate = false;
        try
        {
            await _legacyGridRepairGate.WaitAsync(cancellationToken);
            enteredGate = true;
            var repair = await Task.Run(
                () => LegacyGridEvidenceRepair.TryCreateAsync(evidenceImage, cancellationToken),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || attachmentVersion != _evidenceAttachmentVersion)
                return;

            _legacyGridRepairCache[repairKey] = repair;
            if (string.Equals(_activeLegacyGridRepairKey, repairKey, StringComparison.Ordinal) &&
                !_drawingManualButton && _manualButtonEditor is null && _resizingAnnotationId is null)
                RenderAppMap();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Evidence changed or the window closed while optional repair was running.
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
                                       FileFormatException or System.Runtime.InteropServices.COMException)
        {
            if (attachmentVersion == _evidenceAttachmentVersion)
                _legacyGridRepairCache[repairKey] = null;
        }
        finally
        {
            if (enteredGate) _legacyGridRepairGate.Release();
            _legacyGridRepairInFlight.Remove((attachmentVersion, repairKey));
        }
    }

    private void UpdateSelectedVariantRepairLabel(LegacyGridEvidenceRepair repair)
    {
        if (_selectedVariant is null || _variantPicker.SelectedIndex < 0 ||
            _variantPicker.SelectedItem is not VariantOption current) return;
        var gridCount = repair.Controls.Count(control => control.ControlType == "ControlType.Table");
        var cellCount = repair.Controls.Count(control => control.ControlType == "ControlType.DataItem");
        var label = gridCount > 0
            ? $"Frame {_selectedVariant.FrameSequence}  •  {gridCount} grid  •  {cellCount} cells"
            : $"Frame {_selectedVariant.FrameSequence}  •  " +
              $"{Math.Max(0, _selectedVariant.ControlCount - repair.ReplacedControlCount + repair.Controls.Count)} controls  •  controls repaired";
        if (string.Equals(current.Label, label, StringComparison.Ordinal)) return;

        var index = _variantPicker.SelectedIndex;
        _refreshingVariantPicker = true;
        _variantPicker.Items[index] = new VariantOption(current.Variant, label);
        _variantPicker.SelectedIndex = index;
        _variantPicker.Text = label;
        _refreshingVariantPicker = false;
    }

    private UiMapSurfaceView? ResolveSurfaceForVariant(UiMapVariantView? variant)
    {
        if (variant is null) return _selectedSurface ?? _selectedSurfaceScope.FirstOrDefault();
        return _selectedSurfaceScope.FirstOrDefault(candidate =>
                   candidate.Evidence.Any(evidence => MatchesVariant(evidence, variant)))
               ?? _selectedSurface
               ?? _selectedSurfaceScope.FirstOrDefault();
    }

    private EvidenceRef? ResolveSelectedEvidence()
    {
        if (_selectedVariant is not null)
        {
            var matchingEvidence = EnumerateSelectionEvidence()
                .FirstOrDefault(evidence => MatchesVariant(evidence, _selectedVariant) && !string.IsNullOrEmpty(evidence.ScreenshotEntry))
                ?? EnumerateSelectionEvidence().FirstOrDefault(evidence => MatchesVariant(evidence, _selectedVariant))
                ?? _selectedVariant.Evidence;
            if (matchingEvidence is not null) return matchingEvidence;
        }

        return _selectedSurface?.Evidence.FirstOrDefault(item => !string.IsNullOrEmpty(item.ScreenshotEntry))
            ?? _selectedControl?.Evidence.FirstOrDefault(item => !string.IsNullOrEmpty(item.ScreenshotEntry));
    }

    private IEnumerable<EvidenceRef> EnumerateSelectionEvidence()
    {
        if (_selectedSurface is not null)
        {
            foreach (var evidence in _selectedSurface.Evidence) yield return evidence;
        }

        foreach (var surface in _selectedSurfaceScope)
        {
            if (_selectedSurface is not null && string.Equals(surface.Id, _selectedSurface.Id, StringComparison.Ordinal)) continue;
            foreach (var evidence in surface.Evidence) yield return evidence;
        }
    }

    private static bool MatchesVariant(EvidenceRef evidence, UiMapVariantView variant) =>
        evidence.FrameSequence == variant.FrameSequence &&
        string.Equals(evidence.BundleId, variant.BundleId, StringComparison.Ordinal);

    private static UiMapProjectionMode ParseProjectionMode(string mode) => mode switch
    {
        "Window" => UiMapProjectionMode.Window,
        "Controls" => UiMapProjectionMode.Controls,
        "Overlay" => UiMapProjectionMode.Overlay,
        "Structure" => UiMapProjectionMode.Structure,
        "Structure Overlay" => UiMapProjectionMode.StructureOverlay,
        "Trace" => UiMapProjectionMode.Trace,
        "Routes" => UiMapProjectionMode.Routes,
        _ => throw new InvalidOperationException($"Unknown AppMap projection '{mode}'.")
    };

    private void RenderInteractionTrace(UiEvidenceImage? evidence, RectI viewportBounds)
    {
        if (_selectedSurface is null) return;
        var source = evidence?.InteractionSource;
        var popupBounds = UiMapPresentation.ProjectToSurface(_selectedSurface.Bounds, viewportBounds);
        var sourceBounds = source is null ? null : UiMapPresentation.ProjectToSurface(source.Bounds, viewportBounds);

        if (sourceBounds is not null)
        {
            var sourceOutline = new Rectangle
            {
                Width = Math.Max(1, sourceBounds.Width),
                Height = Math.Max(1, sourceBounds.Height),
                Stroke = Blue,
                StrokeThickness = 4,
                Fill = new SolidColorBrush(Color.FromArgb(30, 37, 99, 235)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(sourceOutline, sourceBounds.X);
            Canvas.SetTop(sourceOutline, sourceBounds.Y);
            _appMapOverlay.Children.Add(sourceOutline);
        }

        if (popupBounds is not null)
        {
            var popupOutline = new Rectangle
            {
                Width = Math.Max(1, popupBounds.Width),
                Height = Math.Max(1, popupBounds.Height),
                Stroke = Green,
                StrokeThickness = 4,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(popupOutline, popupBounds.X);
            Canvas.SetTop(popupOutline, popupBounds.Y);
            _appMapOverlay.Children.Add(popupOutline);
        }

        if (sourceBounds is not null && popupBounds is not null)
        {
            _appMapOverlay.Children.Add(new Line
            {
                X1 = sourceBounds.X + sourceBounds.Width / 2d,
                Y1 = sourceBounds.Y + sourceBounds.Height,
                X2 = popupBounds.X + popupBounds.Width / 2d,
                Y2 = popupBounds.Y,
                Stroke = Brush("#2563EB"),
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection([6, 4]),
                IsHitTestVisible = false
            });
        }

        var sourceName = string.IsNullOrWhiteSpace(source?.Name) ? "Source control not recorded" : source.Name;
        var popupName = string.IsNullOrWhiteSpace(_selectedSurface.DisplayName) ? "Popup" : _selectedSurface.DisplayName;
        var chain = new StackPanel { Orientation = Orientation.Horizontal };
        var previous = new Button
        {
            Content = CreateChevronIcon(pointsRight: false),
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.White,
            BorderBrush = UiBorder,
            Template = RoundedButtonTemplate(8),
            ToolTip = "Previous interaction"
        };
        AttachModernIconButtonFeedback(previous);
        previous.Click += (_, _) => NavigateInteraction(-1);
        chain.Children.Add(previous);
        var sourceFrame = evidence?.Interaction?.SourceFrameSequence;
        chain.Children.Add(Text(sourceFrame.HasValue ? $"State {sourceFrame}   →   " : "State   →   ",
            11, FontWeights.Normal, Muted));
        chain.Children.Add(Text(sourceName, 12, FontWeights.SemiBold,
            source is null ? Muted : Blue, textTrimming: TextTrimming.CharacterEllipsis));
        var action = evidence?.Interaction?.Action.ToString() ?? "opens";
        var outcome = evidence?.Interaction?.Outcome.ToString() ?? "Legacy";
        chain.Children.Add(Text($"   → {action} →   ", 13, FontWeights.Bold, Ink));
        chain.Children.Add(Text(popupName, 12, FontWeights.SemiBold, Green,
            textTrimming: TextTrimming.CharacterEllipsis));
        chain.Children.Add(Text($"   [{outcome}]", 11, FontWeights.SemiBold,
            outcome == nameof(InteractionOutcome.Succeeded) ? Green : Muted));
        var next = new Button
        {
            Content = CreateChevronIcon(pointsRight: true),
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.White,
            BorderBrush = UiBorder,
            Template = RoundedButtonTemplate(8),
            ToolTip = "Next interaction"
        };
        AttachModernIconButtonFeedback(next);
        next.Click += (_, _) => NavigateInteraction(1);
        chain.Children.Add(next);
        var banner = new Border
        {
            Background = Brush("#F8FAFF"),
            BorderBrush = source is null ? UiBorder : Brush("#C9D8FF"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new ScrollViewer
            {
                Content = chain,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            ToolTip = source is null
                ? "This older recording does not contain the button-to-popup relationship. Record it again with the current version."
                : $"{sourceName} opens {popupName}"
        };
        _traceBannerHost.Content = banner;
        _traceBannerHost.Visibility = Visibility.Visible;
    }

    private void RenderRoutes()
    {
        if (_model is null) return;
        var nodes = _model.Graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var controls = _model.Layers.SelectMany(layer => layer.Controls)
            .GroupBy(control => control.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var filteredRoutes = _model.Routes
            .Select(route => new RouteBranch(
                route.SourceStateId,
                route.SourceControlId,
                route.Action,
                route.TargetStateId,
                route.Steps.Where(MatchesInteractionFilters).ToArray()))
            .Where(route => route.Steps.Count > 0)
            .ToArray();

        // A popup capture can contain both the unchanged owner window and the new popup/dialog.
        // For the visual route, retain the causally useful new surface instead of drawing two
        // misleading destinations for the same click.
        filteredRoutes = filteredRoutes
            .GroupBy(route => (route.SourceStateId, route.SourceControlId, route.Action))
            .SelectMany(group =>
            {
                var sourceSurfaceId = nodes.GetValueOrDefault(group.Key.SourceStateId)?.ParentId;
                var crossSurfaceInteractionIds = group
                    .Where(route => !string.Equals(nodes.GetValueOrDefault(route.TargetStateId)?.ParentId,
                        sourceSurfaceId, StringComparison.Ordinal))
                    .SelectMany(route => route.Steps.Select(step => step.Id))
                    .ToHashSet(StringComparer.Ordinal);
                return group.Where(route =>
                    !string.Equals(nodes.GetValueOrDefault(route.TargetStateId)?.ParentId, sourceSurfaceId,
                        StringComparison.Ordinal) ||
                    route.Steps.Any(step => !crossSurfaceInteractionIds.Contains(step.Id)));
            })
            .ToArray();

        var outgoing = filteredRoutes
            .GroupBy(route => route.SourceStateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(route => route.Steps.Min(step => step.Sequence))
                    .ThenBy(route => ControlLabel(route.SourceControlId, nodes, controls), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(route => route.Action).ToArray(), StringComparer.Ordinal);
        var stateControls = _model.Graph.Edges
            .Where(edge => edge.Kind == "contains" && nodes.GetValueOrDefault(edge.FromId)?.Kind == GraphNodeKind.State)
            .GroupBy(edge => edge.FromId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(edge => edge.ToId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var visibleAffordances = _model.Affordances.Where(item => !item.WasObserved)
            .Where(_ => _interactionActorFilter is "All actors" or "DerivedCandidate")
            .Where(_ => _interactionOutcomeFilter is "All outcomes" or "Unobserved")
            .ToArray();
        var affordancesByState = stateControls.ToDictionary(pair => pair.Key,
            pair => visibleAffordances.Where(item => pair.Value.Contains(item.ControlId))
                .OrderBy(item => ControlLabel(item.ControlId, nodes, controls), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Action).ToArray(), StringComparer.Ordinal);

        AddRouteLegend();

        const double stateWidth = 205;
        const double actionWidth = 235;
        const double horizontalGap = 36;
        const double rowHeight = 72;
        const double contentTop = 82;
        const double cardCenterOffset = 26;
        const int renderLimit = 240;
        var row = 0;
        var renderedEntries = 0;
        var maxRight = 900d;
        var expandedStates = new HashSet<string>(StringComparer.Ordinal);
        var targetStates = filteredRoutes.Select(route => route.TargetStateId).ToHashSet(StringComparer.Ordinal);
        var routeSources = filteredRoutes.Select(route => route.SourceStateId).Distinct(StringComparer.Ordinal).ToArray();
        var affordanceSources = affordancesByState.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key)
            .Distinct(StringComparer.Ordinal).ToArray();
        var allSources = routeSources;
        long FirstSequence(string stateId) => filteredRoutes.Where(route => route.SourceStateId == stateId)
            .SelectMany(route => route.Steps).Select(step => step.Sequence).DefaultIfEmpty(long.MaxValue).Min();
        var roots = allSources.Where(stateId => !targetStates.Contains(stateId))
            .OrderBy(FirstSequence).ThenBy(stateId => StateLabel(stateId, nodes), StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0 && allSources.Length > 0)
            roots.Add(allSources.OrderBy(FirstSequence)
                .ThenBy(stateId => StateLabel(stateId, nodes), StringComparer.OrdinalIgnoreCase).First());

        void RenderStateBranch(string stateId, double stateX, double stateY, int depth, HashSet<string> ancestors,
            bool stateAlreadyDrawn = false)
        {
            if (renderedEntries >= renderLimit) return;
            if (!stateAlreadyDrawn)
                AddRouteBox(StateLabel(stateId, nodes), "Recorded UI state", stateX, stateY, stateWidth, Blue,
                    RouteCardKind.State,
                    toolTip: "Recorded UI state");
            maxRight = Math.Max(maxRight, stateX + stateWidth + 24);
            if (!expandedStates.Add(stateId) || depth >= 7) return;

            var nextAncestors = new HashSet<string>(ancestors, StringComparer.Ordinal) { stateId };
            var stateRoutes = outgoing.GetValueOrDefault(stateId) ?? [];
            foreach (var route in stateRoutes)
            {
                if (renderedEntries++ >= renderLimit) return;
                var y = contentTop + row++ * rowHeight;
                var actionX = stateX + stateWidth + horizontalGap;
                var targetX = actionX + actionWidth + horizontalGap;
                var successCount = route.Steps.Count(step => step.Outcome == InteractionOutcome.Succeeded);
                var failureCount = route.Steps.Count - successCount;
                var color = successCount > 0 && failureCount > 0 ? Brush("#D97706")
                    : successCount > 0 ? Green : Brush("#DC2626");
                AddRouteConnector(stateX + stateWidth, stateY + cardCenterOffset, actionX, y + cardCenterOffset,
                    color, dashed: false);
                var controlName = ControlLabel(route.SourceControlId, nodes, controls);
                var action = AddRouteBox(controlName,
                    $"{route.Action} · {successCount}/{route.Steps.Count} successful",
                    actionX, y, actionWidth, color, RouteCardKind.Action,
                    toolTip: "Open the recorded source and result evidence");
                var interactionId = route.Steps[0].Id;
                action.Cursor = Cursors.Hand;
                action.MouseLeftButtonDown += (_, args) =>
                {
                    SelectInteractionById(interactionId);
                    args.Handled = true;
                };
                AddRouteConnector(actionX + actionWidth, y + cardCenterOffset, targetX, y + cardCenterOffset,
                    color, dashed: false);
                var isLoop = nextAncestors.Contains(route.TargetStateId);
                var targetLabel = StateLabel(route.TargetStateId, nodes);
                AddRouteBox(targetLabel, isLoop ? "Returns to an earlier state ↩" : "Observed result",
                    targetX, y, stateWidth, color, RouteCardKind.Result,
                    toolTip: isLoop ? "This action returns to an earlier state" : "Observed result state");
                maxRight = Math.Max(maxRight, targetX + stateWidth + 24);
                if (!isLoop)
                    RenderStateBranch(route.TargetStateId, targetX, y, depth + 1, nextAncestors, stateAlreadyDrawn: true);
            }

        }

        foreach (var root in roots)
        {
            if (expandedStates.Contains(root)) continue;
            var y = contentTop + row++ * rowHeight;
            RenderStateBranch(root, 24, y, 0, []);
            row++;
        }
        foreach (var source in allSources.Where(source => !expandedStates.Contains(source)))
        {
            var y = contentTop + row++ * rowHeight;
            RenderStateBranch(source, 24, y, 0, []);
            row++;
        }

        if (renderedEntries < renderLimit && affordanceSources.Length > 0)
        {
            var headingY = contentTop + row++ * rowHeight;
            AddRouteSectionHeading("Possible actions not recorded yet", headingY);
            foreach (var stateId in affordanceSources.OrderBy(stateId => StateLabel(stateId, nodes), StringComparer.OrdinalIgnoreCase))
            {
                if (renderedEntries >= renderLimit) break;
                var stateAffordances = affordancesByState[stateId];
                var observedActions = (outgoing.GetValueOrDefault(stateId) ?? [])
                    .Select(route => (route.SourceControlId, route.Action)).ToHashSet();
                var pending = stateAffordances.Where(item => !observedActions.Contains((item.ControlId, item.Action))).ToArray();
                if (pending.Length == 0) continue;
                var stateY = contentTop + row++ * rowHeight;
                AddRouteBox(StateLabel(stateId, nodes), "State with available controls", 24, stateY, stateWidth, Blue,
                    RouteCardKind.State,
                    toolTip: "State containing these available controls");
                foreach (var affordance in pending)
                {
                    if (renderedEntries++ >= renderLimit) break;
                    var y = contentTop + row++ * rowHeight;
                    var actionX = 24 + stateWidth + horizontalGap;
                    var targetX = actionX + actionWidth + horizontalGap;
                    AddRouteConnector(24 + stateWidth, stateY + cardCenterOffset, actionX,
                        y + cardCenterOffset, Muted, dashed: true);
                    AddRouteBox(ControlLabel(affordance.ControlId, nodes, controls),
                        $"{affordance.Action} · Not recorded", actionX, y, actionWidth, Muted,
                        RouteCardKind.Action, toolTip: "Available action; result has not been recorded");
                    AddRouteConnector(actionX + actionWidth, y + cardCenterOffset, targetX,
                        y + cardCenterOffset, Muted, dashed: true);
                    AddRouteBox("Unknown result", "No recorded outcome", targetX, y, stateWidth, Muted,
                        RouteCardKind.Unknown,
                        toolTip: "This safe action has not been explored yet");
                    maxRight = Math.Max(maxRight, targetX + stateWidth + 24);
                }
                row++;
            }
        }

        if (row == 0)
        {
            var empty = Text("Trace unavailable for this legacy map.", 14, FontWeights.SemiBold, Muted);
            Canvas.SetLeft(empty, 30); Canvas.SetTop(empty, 30); _appMapOverlay.Children.Add(empty);
        }
        else if (renderedEntries >= renderLimit)
        {
            var truncated = Text($"Showing the first {renderLimit} route branches. Use the actor/outcome filters to narrow the graph.",
                11, FontWeights.SemiBold, Brush("#D97706"));
            Canvas.SetLeft(truncated, 24); Canvas.SetTop(truncated, 70 + row * rowHeight); _appMapOverlay.Children.Add(truncated);
            row++;
        }

        var width = Math.Max(900, maxRight);
        var height = Math.Max(600, 120 + row * rowHeight);
        _appMapViewport.Width = width;
        _appMapViewport.Height = height;
        _appMapOverlay.Width = width;
        _appMapOverlay.Height = height;
    }

    private void AddRouteLegend()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(RouteLegendItem(Green, "Observed route", dashed: false));
        content.Children.Add(RouteLegendItem(Brush("#A0A6B3"), "Unobserved possibility", dashed: true));
        var hint = Text("Select an action to inspect its Trace", 10, FontWeights.Normal, Muted);
        hint.Margin = new Thickness(12, 0, 0, 0);
        content.Children.Add(hint);
        var legend = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = Brush("#E7E5E1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Child = content,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(94, 89, 78),
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.09
            }
        };
        Canvas.SetLeft(legend, 24);
        Canvas.SetTop(legend, 16);
        Panel.SetZIndex(legend, 20);
        _appMapOverlay.Children.Add(legend);
    }

    private static UIElement RouteLegendItem(Brush color, string label, bool dashed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        var sample = new Line
        {
            X1 = 0,
            Y1 = 5,
            X2 = 25,
            Y2 = 5,
            Width = 25,
            Height = 10,
            Stroke = color,
            StrokeThickness = dashed ? 1.4 : 2.2,
            StrokeDashArray = dashed ? new DoubleCollection([4, 3]) : null,
            Margin = new Thickness(0, 2, 7, 0)
        };
        row.Children.Add(sample);
        row.Children.Add(Text(label, 10, FontWeights.SemiBold, Ink));
        return row;
    }

    private void AddRouteSectionHeading(string label, double y)
    {
        var heading = new Border
        {
            Background = Brush("#EFEEEA"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 6, 11, 6),
            Child = Text(label, 11, FontWeights.SemiBold, Muted)
        };
        Canvas.SetLeft(heading, 24);
        Canvas.SetTop(heading, y + 10);
        Panel.SetZIndex(heading, 10);
        _appMapOverlay.Children.Add(heading);
    }

    private Border AddRouteBox(string title, string subtitle, double x, double y, double width, Brush accent,
        RouteCardKind kind, string? toolTip = null)
    {
        var marker = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = kind == RouteCardKind.Unknown ? Brushes.White : accent,
            Stroke = accent,
            StrokeThickness = kind == RouteCardKind.Unknown ? 1.5 : 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 9, 0)
        };
        var copy = new StackPanel();
        copy.Children.Add(Text(title, 10.5, FontWeights.SemiBold, Ink,
            textTrimming: TextTrimming.CharacterEllipsis));
        var detail = Text(subtitle, 8.5, FontWeights.Normal,
            kind == RouteCardKind.Unknown ? Brush("#9CA1AD") : Muted,
            textTrimming: TextTrimming.CharacterEllipsis);
        detail.Margin = new Thickness(0, 3, 0, 0);
        copy.Children.Add(detail);
        var content = new DockPanel();
        DockPanel.SetDock(marker, Dock.Left);
        content.Children.Add(marker);
        content.Children.Add(copy);

        var box = new Border
        {
            Width = width,
            Height = 52,
            Background = kind == RouteCardKind.Unknown ? Brush("#FCFCFB") : Brushes.White,
            BorderBrush = kind == RouteCardKind.Unknown ? Brush("#D9D9D5") : Brush("#E2E2DE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 7),
            Child = content,
            ToolTip = toolTip ?? title,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(75, 70, 60),
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = kind == RouteCardKind.Unknown ? 0.05 : 0.11
            }
        };
        Panel.SetZIndex(box, 10);
        Canvas.SetLeft(box, x); Canvas.SetTop(box, y); _appMapOverlay.Children.Add(box);
        return box;
    }

    private void AddRouteConnector(double sourceX, double sourceY, double targetX, double targetY, Brush color, bool dashed)
    {
        var horizontalDistance = Math.Max(18, targetX - sourceX);
        var controlOffset = Math.Max(14, horizontalDistance * 0.48);
        var figure = new PathFigure { StartPoint = new Point(sourceX, sourceY), IsClosed = false };
        figure.Segments.Add(new BezierSegment(
            new Point(sourceX + controlOffset, sourceY),
            new Point(targetX - controlOffset, targetY),
            new Point(targetX, targetY),
            isStroked: true));
        var connector = new System.Windows.Shapes.Path
        {
            Data = new PathGeometry([figure]),
            Stroke = color,
            StrokeThickness = dashed ? 1.3 : 1.8,
            StrokeDashArray = dashed ? new DoubleCollection([4, 4]) : null,
            Opacity = dashed ? 0.48 : 0.78,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(connector, 0);
        _appMapOverlay.Children.Add(connector);
        var arrow = new Polygon
        {
            Points = new PointCollection([new Point(0, 0), new Point(6, 3.5), new Point(0, 7)]),
            Fill = color,
            Opacity = dashed ? 0.5 : 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(arrow, targetX - 6);
        Canvas.SetTop(arrow, targetY - 3.5);
        Panel.SetZIndex(arrow, 1);
        _appMapOverlay.Children.Add(arrow);
    }

    private static string ControlLabel(string controlId, IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, UiMapControlView> controls) =>
        controls.GetValueOrDefault(controlId)?.DisplayName ?? nodes.GetValueOrDefault(controlId)?.Label ?? "Control";

    private static string StateLabel(string stateId, IReadOnlyDictionary<string, GraphNode> nodes)
    {
        if (!nodes.TryGetValue(stateId, out var state)) return "State";
        var context = NodeProperty(state, "contextLabel");
        var surface = nodes.GetValueOrDefault(state.ParentId);
        var title = NodeProperty(surface, "title");
        var sourceName = NodeProperty(surface, "interactionSourceName");
        var surfaceClass = NodeProperty(surface, "surfaceClass");
        var surfaceName = !string.IsNullOrWhiteSpace(title) ? title
            : !string.IsNullOrWhiteSpace(sourceName) ? $"{sourceName} popup"
            : surface?.Label ?? "UI state";
        if (!string.IsNullOrWhiteSpace(context))
            return string.Equals(context, surfaceName, StringComparison.OrdinalIgnoreCase)
                ? context
                : $"{context} · {surfaceName}";
        if (!string.IsNullOrWhiteSpace(surfaceClass) && surfaceClass.Contains("Dialog", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(title))
            return "Dialog";
        return surfaceName;
    }

    private static string NodeProperty(GraphNode? node, string name) => node?.Properties
        .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal))?.Value ?? string.Empty;

    private sealed record RouteBranch(
        string SourceStateId,
        string SourceControlId,
        InteractionActionKind Action,
        string TargetStateId,
        IReadOnlyList<UiInteractionStepView> Steps);

    private enum RouteCardKind
    {
        State,
        Action,
        Result,
        Unknown
    }

    private bool MatchesInteractionFilters(UiRouteView route)
    {
        return route.Steps.Any(MatchesInteractionFilters);
    }

    private bool MatchesInteractionFilters(UiInteractionStepView step) =>
        (_interactionActorFilter == "All actors" || step.Actor.ToString() == _interactionActorFilter) &&
        (_interactionOutcomeFilter == "All outcomes" || step.Outcome.ToString() == _interactionOutcomeFilter);

    private void NavigateInteraction(int offset)
    {
        if (_model is null) return;
        var visibleSteps = _model.InteractionSteps.Where(MatchesInteractionFilters).ToArray();
        if (visibleSteps.Length == 0) return;
        var currentBundle = _selectedVariant?.BundleId;
        var currentFrame = _selectedVariant?.FrameSequence;
        var index = visibleSteps.ToList().FindIndex(step =>
            step.BundleId == currentBundle && step.ResultFrameSequences.Contains(currentFrame ?? -1));
        if (index < 0) index = offset > 0 ? -1 : 0;
        var target = Math.Clamp(index + offset, 0, visibleSteps.Length - 1);
        SelectInteractionById(visibleSteps[target].Id);
    }

    private void SelectInteractionById(string id)
    {
        if (_model is null) return;
        var step = _model.InteractionSteps.FirstOrDefault(item => item.Id == id);
        if (step?.Evidence is null) return;
        var layer = _model.LayerFor(UiUnderstandingLevel.RawWorld);
        var surface = layer.Surfaces.FirstOrDefault(candidate => candidate.Evidence.Any(evidence =>
            evidence.BundleId == step.Evidence.BundleId && evidence.FrameSequence == step.Evidence.FrameSequence));
        if (surface is null) return;
        _level = UiUnderstandingLevel.RawWorld;
        SelectSurface(surface);
        _selectedVariant = surface.Variants.FirstOrDefault(variant =>
            variant.BundleId == step.Evidence.BundleId && variant.FrameSequence == step.Evidence.FrameSequence);
        SetViewMode("Trace");
        RenderAppMap();
    }

    private void SetViewMode(string mode)
    {
        _viewMode = mode;
        foreach (var pair in _viewModeButtons)
        {
            pair.Value.Background = pair.Key == mode ? Brush("#EEE7FF") : Brushes.White;
            pair.Value.Foreground = pair.Key == mode ? Brush("#6D28D9") : Ink;
            pair.Value.BorderBrush = pair.Key == mode ? Brush("#6D28D9") : UiBorder;
        }
    }

    private static bool TryClipBitmapRect(RectI bounds, BitmapSource bitmap, out Int32Rect result)
    {
        var left = Math.Clamp(bounds.X, 0, bitmap.PixelWidth);
        var top = Math.Clamp(bounds.Y, 0, bitmap.PixelHeight);
        var right = Math.Clamp((long)bounds.X + bounds.Width, 0, bitmap.PixelWidth);
        var bottom = Math.Clamp((long)bounds.Y + bounds.Height, 0, bitmap.PixelHeight);
        if (right <= left || bottom <= top)
        {
            result = default;
            return false;
        }
        result = new Int32Rect(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
        return true;
    }

    private static double IntersectionOverUnion(RectI first, RectI second)
    {
        var width = Math.Max(0,
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0,
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var union = (long)first.Width * first.Height + (long)second.Width * second.Height - intersection;
        return union <= 0 ? 0 : intersection / (double)union;
    }

    private static Brush ResolveBlueprintFill(
        UiMapControlView control,
        UiMapSurfaceView surface,
        UiUnderstandingLevel level,
        double opacity)
    {
        if (opacity <= 0 || UiMapPresentation.IsLargeStructuralControl(control, surface)) return Brushes.Transparent;
        var color = level switch
        {
            UiUnderstandingLevel.RawWorld => Color.FromRgb(0xF5, 0xF0, 0xFF),
            UiUnderstandingLevel.SemanticWorld => Color.FromRgb(0xEC, 0xFD, 0xF5),
            _ => Color.FromRgb(0xEF, 0xF6, 0xFF)
        };
        var alpha = checked((byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255));
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private void ShowProperties(GraphNode node, int controlCount, int variantCount)
    {
        _properties.Children.Clear();
        _properties.Children.Add(Text(node.Label, 15, FontWeights.SemiBold, Ink, textWrapping: TextWrapping.Wrap));
        _properties.Children.Add(PropertyLabel("Level", _inspectionLevel.DisplayName()));
        _properties.Children.Add(PropertyLabel("Type", node.Kind.ToString()));
        if (controlCount > 0) _properties.Children.Add(PropertyLabel("Controls", controlCount.ToString()));
        if (variantCount > 0) _properties.Children.Add(PropertyLabel("Observed variants", variantCount.ToString()));
        _properties.Children.Add(Section("Identity"));
        _properties.Children.Add(PropertyLabel("ID", node.Id));
        if (PropertyValue(node, "manualAnnotationId") is { Length: > 0 } annotationId)
            _properties.Children.Add(BuildManualControlPropertyEditor(annotationId, node.Label));
        foreach (var property in node.Properties.Where(property => !property.Sensitive).Take(80))
            _properties.Children.Add(PropertyLabel(DisplayPropertyName(property.Name), property.Value));
        _properties.Children.Add(Section("Evidence and lineage"));
        _properties.Children.Add(PropertyLabel("Evidence records", node.Evidence.Count.ToString()));
        foreach (var evidence in node.Evidence.Take(20))
            _properties.Children.Add(PropertyLabel("Frame", $"{evidence.FrameSequence}  •  {evidence.ObservationEntry}"));
    }

    private UIElement BuildManualControlPropertyEditor(string annotationId, string label)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        panel.Children.Add(Text("Manual button", 11, FontWeights.SemiBold, Green));
        var name = new TextBox
        {
            Text = label,
            Height = 32,
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(8, 5, 8, 5),
            BorderBrush = UiBorder,
            Background = Brushes.White
        };
        panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button
        {
            Content = "Save name",
            Padding = new Thickness(9, 4, 9, 4),
            Background = Green,
            Foreground = Brushes.White,
            BorderBrush = Brush("#1FAD78"),
            Template = RoundedButtonTemplate(7)
        };
        save.Click += async (_, _) => await RenameManualButtonAsync(annotationId, name.Text);
        var delete = new Button
        {
            Content = "Delete",
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brush("#FFF1F2"),
            Foreground = Brush("#C93845"),
            BorderBrush = Brush("#FCA5A5"),
            Template = RoundedButtonTemplate(7)
        };
        delete.Click += async (_, _) => await DeleteManualButtonAsync(annotationId);
        actions.Children.Add(save);
        actions.Children.Add(delete);
        panel.Children.Add(actions);
        return panel;
    }

    private static string? PropertyValue(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;

    private void ShowEmptyProperties()
    {
        _properties.Children.Clear();
        var message = Text("Select a surface or control", 12, FontWeights.Normal, Muted, HorizontalAlignment.Center, VerticalAlignment.Center);
        message.Margin = new Thickness(0, 220, 0, 0);
        _properties.Children.Add(message);
    }

    private void ClearSelection()
    {
        _selectedSurface = null;
        _selectedSurfaceScope = [];
        _selectedControl = null;
        _selectedVariant = null;
        _visibleVariants = [];
        _refreshingVariantPicker = true;
        _variantPicker.Items.Clear();
        _variantPicker.Text = "No observed frames";
        _refreshingVariantPicker = false;
        UpdateVariantNavigatorState();
        _traceBannerHost.Content = null;
        _traceBannerHost.Visibility = Visibility.Collapsed;
        _appMapImage.Source = null;
        _appMapOverlay.Children.Clear();
        ShowEmptyProperties();
    }

    private void RefreshSurfaceKindFilter()
    {
        if (_model is null) return;
        var previous = _surfaceKindFilter.SelectedItem as string;
        var choices = _model.LayerFor(_level).Surfaces.Select(surface => surface.SurfaceKind)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Prepend("All surface kinds").ToArray();
        _refreshingFilters = true;
        _surfaceKindFilter.ItemsSource = choices;
        _surfaceKindFilter.SelectedItem = choices.Contains(previous, StringComparer.OrdinalIgnoreCase) ? previous : choices[0];
        _refreshingFilters = false;
    }

    private static TreeViewItem TreeItem(string header, SelectionRef? selection, bool expanded, Brush accent,
        FontWeight weight, Action? confirmAction = null, Action? removeAction = null) => new()
    {
        Header = HierarchyItemHeader(header, accent, weight, confirmAction, removeAction),
        Tag = selection,
        IsExpanded = expanded,
        Foreground = Ink,
        FontWeight = weight,
        ToolTip = header,
        Style = HierarchyTreeItemStyle
    };

    private static UIElement HierarchyItemHeader(
        string label,
        Brush accent,
        FontWeight weight,
        Action? confirmAction = null,
        Action? removeAction = null)
    {
        var marker = new Border
        {
            Width = weight == FontWeights.SemiBold ? 8 : 6,
            Height = weight == FontWeights.SemiBold ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 9, 0),
            Opacity = weight == FontWeights.SemiBold ? 0.95 : 0.7
        };
        var text = Text(label, weight == FontWeights.SemiBold ? 11 : 10.5, weight, Ink,
            textTrimming: TextTrimming.CharacterEllipsis);
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(marker);
        row.Children.Add(text);
        if (confirmAction is not null || removeAction is not null)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(5, 0, 0, 0),
                Opacity = 0,
                IsHitTestVisible = false
            };
            if (confirmAction is not null)
            {
                actions.Children.Add(CandidateActionButton(
                    "M2,6 L5,9 L11,2",
                    Green,
                    Brush("#1FAD78"),
                    "Confirm as a real button",
                    "Confirm button candidate",
                    confirmAction));
            }
            if (removeAction is not null)
            {
                actions.Children.Add(CandidateActionButton(
                    "M3,4 H11 M5,4 V2 H9 V4 M4,5 V12 H10 V5 M6,7 V10 M8,7 V10",
                    Brush("#E34D59"),
                    Brush("#C93845"),
                    "Remove incorrect control",
                    "Remove incorrect control candidate",
                    removeAction,
                    confirmAction is null ? new Thickness(0) : new Thickness(4, 0, 0, 0)));
            }
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            row.MouseEnter += (_, _) =>
            {
                actions.Opacity = 1;
                actions.IsHitTestVisible = true;
            };
            row.MouseLeave += (_, _) =>
            {
                actions.Opacity = 0;
                actions.IsHitTestVisible = false;
            };
        }
        return row;
    }

    private static Button CandidateActionButton(
        string geometry,
        Brush background,
        Brush border,
        string toolTip,
        string automationName,
        Action action,
        Thickness? margin = null)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var button = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(4),
            Margin = margin ?? new Thickness(0),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Content = new Viewbox { Width = 11, Height = 11, Child = icon },
            ToolTip = toolTip,
            FocusVisualStyle = null
        };
        button.Click += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static Style CreateHierarchyTreeItemStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="{x:Type TreeViewItem}">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="BorderBrush" Value="Transparent" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="Padding" Value="0" />
                <Setter Property="Margin" Value="0,1" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="FocusVisualStyle" Value="{x:Null}" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="{x:Type TreeViewItem}">
                            <StackPanel>
                                <Border x:Name="Row"
                                        MinHeight="30"
                                        Padding="4,2,7,2"
                                        Background="{TemplateBinding Background}"
                                        BorderBrush="{TemplateBinding BorderBrush}"
                                        BorderThickness="{TemplateBinding BorderThickness}"
                                        CornerRadius="7">
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="20" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <ToggleButton x:Name="Expander"
                                                      Grid.Column="0"
                                                      Width="18"
                                                      Height="22"
                                                      HorizontalAlignment="Center"
                                                      VerticalAlignment="Center"
                                                      Focusable="False"
                                                      IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}">
                                            <ToggleButton.Template>
                                                <ControlTemplate TargetType="{x:Type ToggleButton}">
                                                    <Border Background="Transparent" CornerRadius="5">
                                                        <Path x:Name="Chevron"
                                                              Width="6"
                                                              Height="10"
                                                              HorizontalAlignment="Center"
                                                              VerticalAlignment="Center"
                                                              Data="M 1 1 L 5 5 L 1 9"
                                                              Fill="Transparent"
                                                              Stroke="#7B8497"
                                                              StrokeThickness="1.5"
                                                              StrokeStartLineCap="Round"
                                                              StrokeEndLineCap="Round"
                                                              RenderTransformOrigin="0.5,0.5">
                                                            <Path.RenderTransform>
                                                                <RotateTransform Angle="0" />
                                                            </Path.RenderTransform>
                                                        </Path>
                                                    </Border>
                                                    <ControlTemplate.Triggers>
                                                        <Trigger Property="IsMouseOver" Value="True">
                                                            <Setter Property="Background" Value="#E8ECF4" />
                                                        </Trigger>
                                                        <Trigger Property="IsChecked" Value="True">
                                                            <Setter TargetName="Chevron" Property="RenderTransform">
                                                                <Setter.Value>
                                                                    <RotateTransform Angle="90" />
                                                                </Setter.Value>
                                                            </Setter>
                                                        </Trigger>
                                                    </ControlTemplate.Triggers>
                                                </ControlTemplate>
                                            </ToggleButton.Template>
                                        </ToggleButton>
                                        <ContentPresenter Grid.Column="1"
                                                          Margin="1,0,0,0"
                                                          HorizontalAlignment="Stretch"
                                                          VerticalAlignment="Center"
                                                          ContentSource="Header"
                                                          RecognizesAccessKey="True" />
                                    </Grid>
                                </Border>
                                <ItemsPresenter x:Name="ItemsHost" Margin="18,1,0,0" />
                            </StackPanel>
                            <ControlTemplate.Triggers>
                                <Trigger Property="HasItems" Value="False">
                                    <Setter TargetName="Expander" Property="Visibility" Value="Hidden" />
                                </Trigger>
                                <Trigger Property="IsExpanded" Value="False">
                                    <Setter TargetName="ItemsHost" Property="Visibility" Value="Collapsed" />
                                </Trigger>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Row" Property="Background" Value="#F5F7FB" />
                                    <Setter TargetName="Row" Property="BorderBrush" Value="#E8EBF3" />
                                </Trigger>
                                <Trigger Property="IsSelected" Value="True">
                                    <Setter TargetName="Row" Property="Background" Value="#EDF3FF" />
                                    <Setter TargetName="Row" Property="BorderBrush" Value="#B9CCFF" />
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter Property="Opacity" Value="0.55" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)XamlReader.Parse(xaml);
    }

    private static Style CreateModernComboBoxStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="{x:Type ComboBox}">
                <Setter Property="MinHeight" Value="34" />
                <Setter Property="Padding" Value="10,5,34,5" />
                <Setter Property="Background" Value="White" />
                <Setter Property="BorderBrush" Value="#E2E5EF" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="Foreground" Value="#252936" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="ItemContainerStyle">
                    <Setter.Value>
                        <Style TargetType="{x:Type ComboBoxItem}">
                            <Setter Property="Padding" Value="10,7" />
                            <Setter Property="Margin" Value="0,1" />
                            <Setter Property="Foreground" Value="#252936" />
                            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
                            <Setter Property="Template">
                                <Setter.Value>
                                    <ControlTemplate TargetType="{x:Type ComboBoxItem}">
                                        <Border x:Name="Row"
                                                Padding="{TemplateBinding Padding}"
                                                Background="Transparent"
                                                CornerRadius="6">
                                            <ContentPresenter VerticalAlignment="Center"
                                                              HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" />
                                        </Border>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property="IsHighlighted" Value="True">
                                                <Setter TargetName="Row" Property="Background" Value="#F2F5FB" />
                                            </Trigger>
                                            <Trigger Property="IsSelected" Value="True">
                                                <Setter TargetName="Row" Property="Background" Value="#EAF1FF" />
                                                <Setter Property="Foreground" Value="#315FC9" />
                                            </Trigger>
                                            <Trigger Property="IsEnabled" Value="False">
                                                <Setter Property="Opacity" Value="0.5" />
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </Setter.Value>
                </Setter>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="{x:Type ComboBox}">
                            <Grid SnapsToDevicePixels="True">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="34" />
                                </Grid.ColumnDefinitions>
                                <Border x:Name="Shell"
                                        Grid.ColumnSpan="2"
                                        Background="{TemplateBinding Background}"
                                        BorderBrush="{TemplateBinding BorderBrush}"
                                        BorderThickness="{TemplateBinding BorderThickness}"
                                        CornerRadius="8" />
                                <ToggleButton x:Name="DropDownToggle"
                                              Grid.ColumnSpan="2"
                                              Background="Transparent"
                                              BorderThickness="0"
                                              Focusable="False"
                                              HorizontalAlignment="Stretch"
                                              VerticalAlignment="Stretch"
                                              ClickMode="Press"
                                              IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                                    <ToggleButton.Template>
                                        <ControlTemplate TargetType="{x:Type ToggleButton}">
                                            <Grid Background="Transparent">
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="*" />
                                                    <ColumnDefinition Width="34" />
                                                </Grid.ColumnDefinitions>
                                                <Border x:Name="ArrowHover"
                                                        Grid.Column="1"
                                                        Width="26"
                                                        Height="26"
                                                        HorizontalAlignment="Center"
                                                        VerticalAlignment="Center"
                                                        Background="Transparent"
                                                        CornerRadius="6">
                                                    <Path x:Name="Chevron"
                                                          Width="10"
                                                          Height="6"
                                                          HorizontalAlignment="Center"
                                                          VerticalAlignment="Center"
                                                          Data="M 1 1 L 5 5 L 9 1"
                                                          Fill="Transparent"
                                                          Stroke="#6F7788"
                                                          StrokeThickness="1.5"
                                                          StrokeStartLineCap="Round"
                                                          StrokeEndLineCap="Round"
                                                          RenderTransformOrigin="0.5,0.5">
                                                        <Path.RenderTransform>
                                                            <RotateTransform Angle="0" />
                                                        </Path.RenderTransform>
                                                    </Path>
                                                </Border>
                                            </Grid>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="ArrowHover" Property="Background" Value="#F0F3F8" />
                                                </Trigger>
                                                <Trigger Property="IsChecked" Value="True">
                                                    <Setter TargetName="Chevron" Property="RenderTransform">
                                                        <Setter.Value>
                                                            <RotateTransform Angle="180" />
                                                        </Setter.Value>
                                                    </Setter>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </ToggleButton.Template>
                                </ToggleButton>
                                <ContentPresenter x:Name="SelectionPresenter"
                                                  Grid.Column="0"
                                                  Margin="10,5,4,5"
                                                  HorizontalAlignment="Stretch"
                                                  VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                                  Content="{TemplateBinding SelectionBoxItem}"
                                                  ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                                  ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"
                                                  IsHitTestVisible="False" />
                                <TextBox x:Name="PART_EditableTextBox"
                                         Grid.Column="0"
                                         Margin="10,1,4,1"
                                         Padding="0"
                                         VerticalContentAlignment="Center"
                                         Background="Transparent"
                                         BorderThickness="0"
                                         Foreground="{TemplateBinding Foreground}"
                                         Text="{Binding Text, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                         Visibility="Hidden" />
                                <Popup x:Name="PART_Popup"
                                       Grid.ColumnSpan="2"
                                       AllowsTransparency="True"
                                       Focusable="False"
                                       IsOpen="{TemplateBinding IsDropDownOpen}"
                                       Placement="Bottom"
                                       PopupAnimation="Fade">
                                    <Grid MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                          MaxHeight="{TemplateBinding MaxDropDownHeight}">
                                        <Border Margin="0,5,0,8"
                                                Padding="4"
                                                Background="White"
                                                BorderBrush="#DDE2EB"
                                                BorderThickness="1"
                                                CornerRadius="9">
                                            <Border.Effect>
                                                <DropShadowEffect BlurRadius="18"
                                                                  ShadowDepth="4"
                                                                  Opacity="0.16"
                                                                  Color="#50596A" />
                                            </Border.Effect>
                                            <ScrollViewer VerticalScrollBarVisibility="Auto"
                                                          HorizontalScrollBarVisibility="Disabled">
                                                <StackPanel IsItemsHost="True" />
                                            </ScrollViewer>
                                        </Border>
                                    </Grid>
                                </Popup>
                            </Grid>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsEditable" Value="True">
                                    <Setter TargetName="SelectionPresenter" Property="Visibility" Value="Hidden" />
                                    <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible" />
                                </Trigger>
                                <Trigger Property="IsKeyboardFocusWithin" Value="True">
                                    <Setter TargetName="Shell" Property="BorderBrush" Value="#7EA2FF" />
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter Property="Opacity" Value="0.55" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)XamlReader.Parse(xaml);
    }

    private static Border CardBorder(UIElement child) => new()
    {
        Background = Card,
        BorderBrush = UiBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Child = child
    };

    private static Button ActionButton(string label, bool primary = false) => new()
    {
        Content = label,
        MinWidth = primary ? 88 : 76,
        Height = 34,
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(primary ? 14 : 12, 5, primary ? 14 : 12, 5),
        Background = primary ? Blue : Brushes.White,
        BorderBrush = primary ? Blue : UiBorder,
        Foreground = primary ? Brushes.White : Ink,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        Template = RoundedButtonTemplate(8)
    };

    private static Button IconActionButton(UIElement icon, string compactToolTip) => new()
    {
        Content = icon,
        Width = 34,
        Height = 34,
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(0),
        Background = Brushes.White,
        BorderBrush = UiBorder,
        Foreground = Ink,
        ToolTip = compactToolTip,
        Template = RoundedButtonTemplate(8)
    };

    private static Border RoundedField(Control control)
    {
        var width = control.Width;
        var minWidth = control.MinWidth;
        var height = control.Height;
        var margin = control.Margin;
        control.Width = double.NaN;
        control.MinWidth = 0;
        control.Height = double.NaN;
        control.Margin = new Thickness(0);
        control.BorderThickness = new Thickness(0);
        control.Background = Brushes.Transparent;
        return new Border
        {
            Width = width,
            MinWidth = minWidth,
            Height = height,
            Margin = margin,
            Background = Brushes.White,
            BorderBrush = UiBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = control
        };
    }

    private static ControlTemplate RoundedButtonTemplate(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Control.Padding))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding(nameof(Control.HorizontalContentAlignment))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        presenter.SetBinding(ContentPresenter.VerticalAlignmentProperty, new System.Windows.Data.Binding(nameof(Control.VerticalContentAlignment))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
        template.Triggers.Add(pressed);
        return template;
    }

    private static ControlTemplate RoundedToggleButtonTemplate(
        double radius,
        Brush? checkedBackground = null,
        Brush? checkedBorder = null,
        Brush? checkedForeground = null)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, checkedBackground ?? Brush("#F0ECFF")));
        checkedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, checkedBorder ?? Brush("#7C3AED")));
        checkedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, checkedForeground ?? Brush("#6D28D9")));
        template.Triggers.Add(checkedTrigger);
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86));
        template.Triggers.Add(hoverTrigger);
        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
        template.Triggers.Add(pressedTrigger);
        return template;
    }

    private static Button ToolbarIconActionButton(Lazy<ImageSource?> asset, string compactToolTip) =>
        IconActionButton(CreateToolbarPngIcon(asset, 18, 18), compactToolTip);

    private static FrameworkElement CreateToolbarPngIcon(Lazy<ImageSource?> asset, double width, double height)
    {
        if (asset.Value is not ImageSource source)
            return new Border { Width = width, Height = height, Background = Ink, CornerRadius = new CornerRadius(4) };

        return new Border
        {
            Width = width,
            Height = height,
            Background = Ink,
            OpacityMask = new ImageBrush(source)
            {
                Stretch = Stretch.Uniform,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static ImageSource? LoadToolbarAssetImage(string fileName)
    {
        try
        {
            var assetPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(assetPath))
                return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(assetPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static TextBlock Text(string value, double size, FontWeight weight, Brush foreground,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment verticalAlignment = VerticalAlignment.Top,
        TextTrimming textTrimming = TextTrimming.None,
        TextWrapping textWrapping = TextWrapping.NoWrap) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        HorizontalAlignment = horizontalAlignment,
        VerticalAlignment = verticalAlignment,
        TextTrimming = textTrimming,
        TextWrapping = textWrapping
    };

    private static TextBlock Section(string value)
    {
        var text = Text(value, 11, FontWeights.SemiBold, Ink);
        text.Margin = new Thickness(0, 16, 0, 5);
        return text;
    }

    private static FrameworkElement PropertyLabel(string name, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 5) };
        panel.Children.Add(Text(name, 10, FontWeights.SemiBold, Muted));
        panel.Children.Add(Text(value, 11, FontWeights.Normal, Ink, textWrapping: TextWrapping.Wrap));
        return panel;
    }

    private static UIElement CreateSplitViewIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        var outer = new Rectangle
        {
            Width = 14,
            Height = 12,
            RadiusX = 2,
            RadiusY = 2,
            Stroke = Ink,
            StrokeThickness = 1.2,
            Fill = Brushes.Transparent
        };
        var divider = new Line
        {
            X1 = 8,
            Y1 = 2.5,
            X2 = 8,
            Y2 = 13.5,
            Stroke = Ink,
            StrokeThickness = 1.2
        };
        Canvas.SetLeft(outer, 1);
        Canvas.SetTop(outer, 2);
        canvas.Children.Add(outer);
        canvas.Children.Add(divider);
        return canvas;
    }

    private static bool Matches(UiMapSurfaceView surface, string query, string? surfaceKind)
    {
        if (!string.IsNullOrWhiteSpace(surfaceKind) && surfaceKind != "All surface kinds" &&
            !string.Equals(surface.SurfaceKind, surfaceKind, StringComparison.OrdinalIgnoreCase)) return false;
        return string.IsNullOrWhiteSpace(query) ||
               surface.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               surface.SurfaceKind.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               surface.Source.Properties.Any(property => property.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string DisplayPropertyName(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append(' ');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string LevelGlyph(UiUnderstandingLevel level) => level switch
    {
        UiUnderstandingLevel.RawDataStreams => "▤",
        UiUnderstandingLevel.RawWorld => "▧",
        _ => "▱"
    };

    private static Brush LevelBrush(UiUnderstandingLevel level) => level switch
    {
        UiUnderstandingLevel.RawDataStreams => Blue,
        UiUnderstandingLevel.RawWorld => Violet,
        _ => Green
    };

    private static Brush NodeBrush(UiPipelineNodeKind kind) => kind switch
    {
        UiPipelineNodeKind.Application => Brush("#4668D8"),
        UiPipelineNodeKind.Process => Brush("#687080"),
        UiPipelineNodeKind.NativeSurface => Blue,
        UiPipelineNodeKind.RawSurface => Violet,
        _ => Green
    };

    private static Brush NodeBackground(UiPipelineNodeKind kind) => kind switch
    {
        UiPipelineNodeKind.Application => Brush("#EEF2FF"),
        UiPipelineNodeKind.Process => Brush("#F4F5F7"),
        UiPipelineNodeKind.NativeSurface => Brush("#EEF5FF"),
        UiPipelineNodeKind.RawSurface => Brush("#F5F0FF"),
        _ => Brush("#ECFBF5")
    };

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed record SelectionRef(string? SurfaceId, string? ControlId, string? InteractionId = null);
    private sealed record VariantOption(UiMapVariantView Variant, string Label);
    private sealed record CatalogImportResult(string MapId, string MapPath, int ImportedRecordingCount, int SkippedRecordingCount);
}

internal static class UiUnderstandingLevelExtensions
{
    public static string DisplayName(this UiUnderstandingLevel level) => level switch
    {
        UiUnderstandingLevel.RawDataStreams => "Raw Data Streams",
        UiUnderstandingLevel.RawWorld => "Raw World",
        _ => "Semantic World"
    };
}

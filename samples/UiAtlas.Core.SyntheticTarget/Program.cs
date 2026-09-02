using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace UiAtlas.Core.SyntheticTarget;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        app.Run(new SyntheticWindow(args.Contains("--owned-popup", StringComparer.Ordinal)));
    }
}

internal sealed class SyntheticWindow : Window
{
    private static readonly Brush ShellBackground = Brush("#FCFCFB");
    private static readonly Brush ShellBorder = Brush("#DAD9D6");
    private static readonly Brush Hairline = Brush("#E7E6E2");
    private static readonly Brush Ink = Brush("#2F312F");
    private static readonly Brush Muted = Brush("#70716D");
    private static readonly Brush Subtle = Brush("#A2A49E");
    private static readonly Brush Accent = Brush("#4079FC");
    private static readonly Brush AccentSurface = Brush("#F3F6FF");
    private static readonly Brush AccentStroke = Brush("#D8E4FF");
    private static readonly Brush Field = Brush("#FFFFFF");
    private static readonly Brush DisabledInk = Brush("#A9AAA6");
    private static readonly Brush ButtonStroke = Brush("#E5E4E0");

    private readonly TextBlock _state = new();
    private readonly TextBlock _dpi = new();
    private readonly TextBox _nestedText = new();
    private readonly CheckBox _dynamicOption = new();
    private int _counter;

    public SyntheticWindow(bool openOwnedPopup = false)
    {
        Title = "UiAtlas Recording Target";
        Width = 1020;
        MinWidth = 980;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");

        var chrome = BuildChrome();
        Content = chrome;

        Loaded += (_, _) =>
        {
            UpdateDpi();
            Focus();
        };

        if (openOwnedPopup)
        {
            Loaded += async (_, _) =>
            {
                await Task.Delay(750);
                CreateOwnedPopup().Show();
            };
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi.Text = $"DPI: {Math.Round(newDpi.PixelsPerInchX)}";
    }

    private UIElement BuildChrome()
    {
        var root = new Grid { Background = Brushes.Transparent };

        var shell = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(18),
            Background = ShellBackground,
            BorderBrush = ShellBorder,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 0,
                Opacity = 0.18,
                Color = Color.FromRgb(42, 45, 43)
            }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var divider = new Border { Height = 1, Background = Hairline };
        Grid.SetRow(divider, 1);
        layout.Children.Add(divider);

        var helper = BuildHelperStrip();
        Grid.SetRow(helper, 2);
        layout.Children.Add(helper);

        var body = BuildBody();
        Grid.SetRow(body, 3);
        layout.Children.Add(body);

        shell.Child = layout;
        root.Children.Add(shell);
        return root;
    }

    private UIElement BuildHeader()
    {
        var header = new Grid
        {
            Margin = new Thickness(18, 10, 18, 10),
            Height = 48
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += HeaderMouseLeftButtonDown;

        var logo = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = AccentSurface,
            BorderBrush = AccentStroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new Grid
            {
                Children =
                {
                    new Ellipse { Width = 10, Height = 10, Stroke = Accent, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    new Ellipse { Width = 4, Height = 4, Fill = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        Grid.SetColumn(logo, 0);
        header.Children.Add(logo);

        var title = Text("Recording target app", 13, FontWeights.SemiBold, Ink);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        AddHeaderDivider(header, 2);

        var description = Text("This is the window you click while the separate recorder bar controls the session.", 12, FontWeights.Normal, Muted);
        description.VerticalAlignment = VerticalAlignment.Center;
        description.Margin = new Thickness(16, 0, 16, 0);
        Grid.SetColumn(description, 3);
        header.Children.Add(description);

        AddHeaderDivider(header, 4);

        var stateChip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0)
        };
        stateChip.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Accent,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        _state.Text = "State: idle";
        _state.FontSize = 12;
        _state.Foreground = Ink;
        _state.VerticalAlignment = VerticalAlignment.Center;
        stateChip.Children.Add(_state);
        Grid.SetColumn(stateChip, 5);
        header.Children.Add(stateChip);

        AddHeaderDivider(header, 6);

        _dpi.Text = "DPI: pending";
        _dpi.FontSize = 12;
        _dpi.Foreground = Ink;
        _dpi.Margin = new Thickness(16, 0, 0, 0);
        _dpi.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_dpi, 7);
        header.Children.Add(_dpi);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerActions.Children.Add(BuildOpenPopupButton());
        headerActions.Children.Add(BuildCloseButton());
        Grid.SetColumn(headerActions, 8);
        header.Children.Add(headerActions);

        return header;
    }

    private UIElement BuildHelperStrip()
    {
        var strip = new Border
        {
            Margin = new Thickness(18, 14, 18, 0),
            Padding = new Thickness(12, 10, 12, 10),
            Background = AccentSurface,
            BorderBrush = AccentStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Background = Field,
            BorderBrush = AccentStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 10, 0),
            Child = Text("TARGET APP", 11, FontWeights.SemiBold, Accent)
        };
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var helperText = new TextBlock
        {
            Text = "Click controls in this window. The floating recorder toolbar above is only for Start, Double click, Finish, Export, and Close.",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(helperText, 1);
        row.Children.Add(helperText);

        strip.Child = row;
        return strip;
    }

    private UIElement BuildBody()
    {
        var body = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var primary = BuildActionButton("Primary action", "primary-button", CreateLayeredSquaresIcon(Ink));
        primary.Click += (_, _) =>
        {
            _counter++;
            _state.Text = $"State: changed {_counter}";
        };
        SetCell(primary, 0, new Thickness(0, 0, 12, 0));
        body.Children.Add(primary);

        var secondary = BuildActionButton("Secondary action", "secondary-button", CreateLayeredSquaresIcon(Ink));
        secondary.Click += (_, _) => _state.Text = "State: secondary action";
        SetCell(secondary, 1, new Thickness(0, 0, 12, 0));
        body.Children.Add(secondary);

        var disabled = BuildActionButton("Disabled", "disabled-button", CreateDashedSquareIcon(DisabledInk), enabled: false);
        SetCell(disabled, 2, new Thickness(0, 0, 18, 0));
        body.Children.Add(disabled);

        var nestedColumn = new StackPanel { Orientation = Orientation.Vertical, Width = 300 };
        nestedColumn.Children.Add(Text("Nested accessibility tree", 12, FontWeights.Medium, Ink, new Thickness(4, 0, 0, 6)));
        nestedColumn.Children.Add(BuildNestedField());
        SetCell(nestedColumn, 3, new Thickness(0, 0, 16, 0));
        body.Children.Add(nestedColumn);

        _dynamicOption.Content = "Dynamic option";
        _dynamicOption.Margin = new Thickness(8, 24, 0, 0);
        _dynamicOption.VerticalAlignment = VerticalAlignment.Center;
        _dynamicOption.Foreground = Ink;
        _dynamicOption.Checked += (_, _) => _state.Text = "State: option enabled";
        _dynamicOption.Unchecked += (_, _) => _state.Text = "State: option disabled";
        AutomationProperties.SetAutomationId(_dynamicOption, "dynamic-option");
        Grid.SetColumn(_dynamicOption, 4);
        body.Children.Add(_dynamicOption);

        return body;
    }

    private Button BuildActionButton(string label, string automationId, UIElement icon, bool enabled = true)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(icon);
        content.Children.Add(Text(label, 12, FontWeights.Medium, enabled ? Ink : DisabledInk, new Thickness(8, 0, 0, 0)));

        var button = new Button
        {
            Width = 150,
            Height = 34,
            Background = Field,
            BorderBrush = ButtonStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0),
            IsEnabled = enabled,
            Content = content
        };
        button.Template = FlatButtonTemplate(new CornerRadius(8));
        button.Opacity = enabled ? 1.0 : 0.6;
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private UIElement BuildNestedField()
    {
        var shell = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = Field,
            BorderBrush = ButtonStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _nestedText.Text = "Synthetic non-secret text";
        _nestedText.FontSize = 12;
        _nestedText.Foreground = Ink;
        _nestedText.BorderThickness = new Thickness(0);
        _nestedText.Background = Brushes.Transparent;
        _nestedText.VerticalContentAlignment = VerticalAlignment.Center;
        _nestedText.Margin = new Thickness(0, 0, 8, 0);
        _nestedText.TextChanged += (_, _) => _state.Text = "State: nested text edited";
        AutomationProperties.SetAutomationId(_nestedText, "nested-text");
        Grid.SetColumn(_nestedText, 0);
        grid.Children.Add(_nestedText);

        var chevron = Text("v", 12, FontWeights.Medium, Subtle);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        shell.Child = grid;
        return shell;
    }

    private Button BuildOpenPopupButton()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconShell = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brush("#F3F3F0"),
            BorderBrush = ButtonStroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Dot(),
                    Dot(),
                    Dot()
                }
            }
        };
        content.Children.Add(iconShell);
        content.Children.Add(Text("Open test popup", 12, FontWeights.Medium, Ink));

        var button = new Button
        {
            Height = 34,
            Background = Field,
            BorderBrush = ButtonStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 14, 6),
            Content = content
        };
        button.Template = FlatButtonTemplate(new CornerRadius(10));
        button.Click += (_, _) =>
        {
            _state.Text = "State: popup opened";
            CreateOwnedPopup().ShowDialog();
        };
        AutomationProperties.SetAutomationId(button, "open-popup");
        return button;
    }

    private Button BuildCloseButton()
    {
        var button = new Button
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Background = Field,
            BorderBrush = ButtonStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Content = Text("x", 14, FontWeights.Medium, Muted)
        };
        button.Template = FlatButtonTemplate(new CornerRadius(10));
        button.Click += (_, _) => CloseWindow();
        AutomationProperties.SetAutomationId(button, "close-window");
        return button;
    }

    private Window CreateOwnedPopup()
    {
        var popupButton = BuildActionButton("Popup action", "popup-action", CreateLayeredSquaresIcon(Ink));
        popupButton.Width = 150;
        popupButton.Click += (_, _) => _state.Text = "State: popup action";

        return new Window
        {
            Title = "Owned synthetic popup",
            Owner = this,
            Width = 340,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Background = Brush("#F7F6F3"),
                Margin = new Thickness(18),
                Children =
                {
                    new Border
                    {
                        CornerRadius = new CornerRadius(16),
                        Background = ShellBackground,
                        BorderBrush = ShellBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(18),
                        Child = new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                Text("Popup action panel", 14, FontWeights.SemiBold, Ink, new Thickness(0, 0, 0, 10)),
                                Text("This popup stays owned by the synthetic target.", 12, FontWeights.Normal, Muted, new Thickness(0, 0, 0, 14)),
                                popupButton
                            }
                        }
                    }
                }
            }
        };
    }

    private void UpdateDpi() => _dpi.Text = $"DPI: {Math.Round(VisualTreeHelper.GetDpi(this).PixelsPerInchX)}";

    private void HeaderMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void CloseWindow() => SystemCommands.CloseWindow(this);
    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private static ControlTemplate FlatButtonTemplate(CornerRadius radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, TemplateParentBinding(nameof(Control.Background)));
        border.SetBinding(Border.BorderBrushProperty, TemplateParentBinding(nameof(Control.BorderBrush)));
        border.SetBinding(Border.BorderThicknessProperty, TemplateParentBinding(nameof(Control.BorderThickness)));
        border.SetValue(Border.CornerRadiusProperty, radius);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, TemplateParentBinding(nameof(ContentControl.Content)));
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, TemplateParentBinding(nameof(ContentControl.ContentTemplate)));
        presenter.SetBinding(ContentPresenter.MarginProperty, TemplateParentBinding(nameof(Control.Padding)));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };
        return template;
    }

    private static Binding TemplateParentBinding(string path) =>
        new(path) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) };

    private static UIElement CreateLayeredSquaresIcon(Brush brush)
    {
        var canvas = new Canvas { Width = 14, Height = 14 };
        var back = new Border
        {
            Width = 7,
            Height = 7,
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1)
        };
        Canvas.SetLeft(back, 1);
        Canvas.SetTop(back, 4);

        var front = new Border
        {
            Width = 7,
            Height = 7,
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1)
        };
        Canvas.SetLeft(front, 5);
        Canvas.SetTop(front, 1);

        canvas.Children.Add(back);
        canvas.Children.Add(front);
        return canvas;
    }

    private static UIElement CreateDashedSquareIcon(Brush brush)
    {
        return new Rectangle
        {
            Width = 11,
            Height = 11,
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = [2, 2],
            RadiusX = 1,
            RadiusY = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static void AddHeaderDivider(Grid grid, int column)
    {
        var line = new Border
        {
            Width = 1,
            Height = 24,
            Background = Hairline,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(line, column);
        grid.Children.Add(line);
    }

    private static void SetCell(FrameworkElement element, int column, Thickness margin)
    {
        element.Margin = margin;
        Grid.SetColumn(element, column);
    }

    private static Ellipse Dot() => new()
    {
        Width = 3,
        Height = 3,
        Fill = Subtle,
        Margin = new Thickness(1, 0, 1, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock Text(
        string value,
        double size,
        FontWeight weight,
        Brush brush,
        Thickness? margin = null) =>
        new()
        {
            Text = value,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            Margin = margin ?? new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}

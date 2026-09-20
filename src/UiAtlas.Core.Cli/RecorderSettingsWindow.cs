using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal sealed class RecorderSettingsWindow : Window
{
    private readonly AzureHeaderSettingsStore _store;
    private readonly ComboBox _provider;
    private readonly TextBox _endpoint, _deployment;
    private readonly PasswordBox _key;
    private readonly TextBlock _status;
    private readonly Button _save, _test;
    private readonly StackPanel _fields;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<AzureHeaderSettings, string?, CancellationToken, Task> _testConnection;
    private bool _busy, _closed;

    internal RecorderSettingsWindow(AzureHeaderSettingsStore? store = null,
        Func<AzureHeaderSettings, string?, CancellationToken, Task>? testConnection = null)
    {
        _store = store ?? new(); _testConnection = testConnection ?? TestConnectionAsync;
        Title = "UI Map Recorder settings"; Width = 560; MinWidth = 470; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Topmost = true;
        Background = MapperGridInspector.Brush("#F9F9FA"); FontFamily = new("Inter, Segoe UI"); FontSize = 13;
        var root = new StackPanel { Margin = new(24), Background = Background };
        root.Children.Add(Label("Table reading", 23));
        root.Children.Add(Label("Choose how Extract schema reads the selected column headers."));
        _provider = new ComboBox { ItemsSource = new[] { "Local OCR", "Azure OpenAI · GPT-5.6 Sol" }, SelectedIndex = 0, Margin = new(0, 8, 0, 12) };
        AutomationName(_provider, "Header reading provider"); root.Children.Add(_provider);
        _fields = new StackPanel(); root.Children.Add(_fields);
        _fields.Children.Add(Label("GPT-5.6 Sol · Light mode (low reasoning)"));
        _fields.Children.Add(Label("Azure endpoint")); _endpoint = Entry("Azure endpoint"); _fields.Children.Add(_endpoint);
        _fields.Children.Add(Label("Example: https://your-resource.openai.azure.com", 11));
        _fields.Children.Add(Label("Deployment name")); _deployment = Entry("Deployment name"); _fields.Children.Add(_deployment);
        _fields.Children.Add(Label("Use the name of your GPT-5.6 Sol deployment in Azure.", 11));
        _fields.Children.Add(Label("API key")); _key = new PasswordBox { Padding = new(6), Margin = new(0, 2, 0, 4) };
        AutomationName(_key, "Azure API key"); _fields.Children.Add(_key);
        _fields.Children.Add(Label("Stored in Windows Credential Manager. Leave blank to keep the saved key for this endpoint.", 11));
        root.Children.Add(Label("Extract schema sends cropped headers to Azure. Approved MCP table reads also send cropped table cells using these settings. Review header names before saving.", 12));
        _status = Label(""); root.Children.Add(_status);
        var buttons = new WrapPanel { Margin = new(0, 8, 0, 0) };
        _test = ActionButton("Test connection", () => _ = TestAsync()); buttons.Children.Add(_test);
        _save = ActionButton("Save settings", Save); buttons.Children.Add(_save);
        buttons.Children.Add(ActionButton("Close", Close)); root.Children.Add(buttons);
        Content = root;
        try
        {
            var settings = _store.Load(); _provider.SelectedIndex = settings.Enabled ? 1 : 0;
            _endpoint.Text = settings.Endpoint; _deployment.Text = settings.Deployment;
            if (settings.Endpoint.Length > 0 && _store.HasKey(settings)) _status.Text = "An API key is saved for this endpoint.";
        }
        catch { _deployment.Text = "gpt-5.6-sol"; _status.Text = "The saved settings could not be loaded. Enter and save them again."; }
        _provider.SelectionChanged += (_, _) => UpdateControls(); UpdateControls();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); _key.Clear(); };
    }

    private AzureHeaderSettings Current() => new(_provider.SelectedIndex == 1, _endpoint.Text.Trim(), _deployment.Text.Trim());
    private void Save()
    {
        if (_busy) return;
        try { _store.Save(Current(), _key.Password); _key.Clear(); _status.Text = "Settings saved. The next header extraction will use this provider."; }
        catch (Exception ex) { _status.Text = Error(ex); }
    }
    private async Task TestAsync()
    {
        if (_busy) return;
        _busy = true; UpdateControls(); _status.Text = "Testing GPT-5.6 Sol with a synthetic header image…";
        try { await _testConnection(Current(), _key.Password, _lifetime.Token);
            if (!_closed) _status.Text = "Connection and image reading passed. Save settings to use Azure."; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _status.Text = Error(ex); }
        finally { if (!_closed) { _busy = false; UpdateControls(); } }
    }
    private async Task TestConnectionAsync(AzureHeaderSettings settings, string? key, CancellationToken cancellation)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 200, 48));
            drawing.DrawText(new FormattedText("Order Number", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 24, Brushes.Black, 1), new Point(6, 6));
        }
        var bitmap = new RenderTargetBitmap(200, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var result = await _store.CreateReader(settings, key).ReadAsync([new("test-header", stream.ToArray())], cancellation);
        if (result.Count != 1 || result[0].Text != "Order Number")
            throw new AzureHeaderException("Azure responded, but the test header was not read correctly. Check the deployment.");
    }
    private void UpdateControls()
    {
        _provider.IsEnabled = _save.IsEnabled = !_busy;
        _fields.IsEnabled = _test.IsEnabled = !_busy && _provider.SelectedIndex == 1;
    }
    private static string Error(Exception ex) => ex is AzureHeaderException ? ex.Message : "The settings or credential store could not be accessed. Try again.";
    private static void AutomationName(DependencyObject control, string name) => System.Windows.Automation.AutomationProperties.SetName(control, name);
    private static TextBox Entry(string name) { var box = new TextBox { Padding = new(6), Margin = new(0, 2, 0, 4) }; AutomationName(box, name); return box; }
    private static TextBlock Label(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) };
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(12, 7, 12, 7), Margin = new(0, 0, 8, 0) };
        AutomationName(button, text); button.Click += (_, _) => action(); return button;
    }
}

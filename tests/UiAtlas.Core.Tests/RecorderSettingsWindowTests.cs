using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Recording.Windows;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class RecorderSettingsWindowTests
{
    [Fact]
    public void AzureSettingsCanBeTestedSavedAndReopenedWithoutDisplayingTheSecret() => Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "recorder-settings-ui-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json"); var credentials = new Credentials();
        var store = new AzureHeaderSettingsStore(path, credentials); var calls = 0;
        var window = new RecorderSettingsWindow(store, (settings, key, _) =>
        {
            calls++; Assert.True(settings.Enabled); Assert.Equal("sol-headers", settings.Deployment);
            Assert.Equal("test-secret", key); Assert.False(File.Exists(path)); return Task.CompletedTask;
        });
        try
        {
            window.Show(); Pump(20);
            var provider = Descendants<ComboBox>(window).Single();
            var test = Button(window, "Test connection"); Assert.False(test.IsEnabled);
            provider.SelectedIndex = 1; Assert.True(test.IsEnabled);
            Entry(window, "Azure endpoint").Text = "https://example.openai.azure.com";
            Entry(window, "Deployment name").Text = "sol-headers";
            Descendants<PasswordBox>(window).Single().Password = "test-secret";
            Click(test); Assert.Equal(1, calls); Assert.False(File.Exists(path));
            window.UpdateLayout(); Pump(30);
            var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
            if (!string.IsNullOrWhiteSpace(qa)) Render((FrameworkElement)window.Content, Path.Combine(qa, "azure-header-settings.png"));
            Click(Button(window, "Save settings")); Assert.True(store.Load().Enabled);
            Assert.Equal("", Descendants<PasswordBox>(window).Single().Password);
            Assert.DoesNotContain("test-secret", File.ReadAllText(path));
            window.Close();
            var reopened = new RecorderSettingsWindow(store);
            try
            {
                reopened.Show(); Pump(10); Assert.Equal(1, Descendants<ComboBox>(reopened).Single().SelectedIndex);
                Assert.Equal("sol-headers", Entry(reopened, "Deployment name").Text);
                Assert.Equal("", Descendants<PasswordBox>(reopened).Single().Password);
                Descendants<ComboBox>(reopened).Single().SelectedIndex = 0;
                Click(Button(reopened, "Save settings")); Assert.False(store.Load().Enabled);
                Assert.Equal("test-secret", Assert.Single(credentials.Values).Value);
            }
            finally { reopened.Close(); }
        }
        finally { window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public void ClosingSettingsCancelsPendingConnectionTest() => Sta(() =>
    {
        CancellationToken operation = default;
        var store = new AzureHeaderSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new Credentials());
        var window = new RecorderSettingsWindow(store, async (_, _, token) => { operation = token; await Task.Delay(Timeout.Infinite, token); });
        window.Show(); Descendants<ComboBox>(window).Single().SelectedIndex = 1;
        Click(Button(window, "Test connection")); Assert.False(Button(window, "Save settings").IsEnabled);
        window.Close(); Pump(20); Assert.True(operation.IsCancellationRequested);
    });

    private static Button Button(DependencyObject root, string name) => Descendants<Button>(root).Single(b => Equals(b.Content, name));
    private static TextBox Entry(DependencyObject root, string name) => Descendants<TextBox>(root).Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private sealed class Credentials : IAzureHeaderCredentialStore
    {
        internal Dictionary<string, string> Values { get; } = [];
        public string? Read(string target) => Values.GetValueOrDefault(target);
        public void Write(string target, string key) => Values[target] = key;
    }
}

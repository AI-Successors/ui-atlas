using System.IO;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AzureHeaderSettingsTests
{
    [Theory]
    [InlineData("https://example.openai.azure.com/")]
    [InlineData("https://example.openai.azure.com/openai/v1/")]
    [InlineData("https://example.openai.azure.com/openai/v1/responses")]
    public void EndpointNormalizesWithoutDuplicatingApiPath(string endpoint) =>
        Assert.Equal("https://example.openai.azure.com", new AzureHeaderSettings(true, endpoint).Normalize().Endpoint);

    [Theory]
    [InlineData("http://example.openai.azure.com")]
    [InlineData("https://user:password@example.openai.azure.com")]
    [InlineData("https://example.openai.azure.com?api-key=secret")]
    [InlineData("https://example.openai.azure.com/openai/deployments/deployment")]
    public void UnsafeOrIncorrectEndpointsAreRejected(string endpoint) =>
        Assert.Throws<AzureHeaderException>(() => new AzureHeaderSettings(true, endpoint).Normalize());

    [Fact]
    public void SettingsPersistNoKeyAndChangingResourceCannotReuseAnOldKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "azure-settings-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var credentials = new Credentials(); var path = Path.Combine(directory, "settings.json");
            var store = new AzureHeaderSettingsStore(path, credentials);
            Assert.False(store.Load().Enabled);
            var settings = new AzureHeaderSettings(true, "https://example.openai.azure.com/openai/v1/", "my-sol");
            Assert.Throws<AzureHeaderException>(() => store.Save(settings));
            store.Save(settings, "test-secret"); Assert.True(store.HasKey(settings));
            Assert.Equal(settings.Normalize(), store.Load()); Assert.DoesNotContain("test-secret", File.ReadAllText(path));
            store.Save(settings); Assert.Equal("test-secret", Assert.Single(credentials.Values).Value);
            var different = settings with { Endpoint = "https://other.openai.azure.com" };
            Assert.False(store.HasKey(different)); Assert.Throws<AzureHeaderException>(() => store.Save(different));
            Assert.Equal(settings.Normalize(), store.Load());
            store.Save(new(false, "invalid")); Assert.False(store.Load().Enabled);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Credentials : IAzureHeaderCredentialStore
    {
        internal Dictionary<string, string> Values { get; } = [];
        public string? Read(string target) => Values.GetValueOrDefault(target);
        public void Write(string target, string key) => Values[target] = key;
    }
}

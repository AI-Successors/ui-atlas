using System.Text.Json;
using UiAtlas.Core.Migration;

namespace UiAtlas.Core.Tests;

public sealed class MigrationPackageWriterTests
{
    [Fact]
    public async Task WritesDeterministicChecksummedCustomerPackage()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "source.fdb");
        await File.WriteAllTextAsync(database, "stable-source");
        var output = Path.Combine(temp.Path, "export");

        var result = await MigrationPackageWriter.WriteCustomersAsync(
            output,
            "test-source",
            database,
            Customers(),
            ["CLIENT.PWD"]);

        Assert.Equal(1, result.RecordCount);
        Assert.True(File.Exists(result.DataFile));
        Assert.True(File.Exists(result.ManifestFile));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestFile));
        Assert.Equal("ui-atlas.customer-migration/1", manifest.RootElement.GetProperty("formatVersion").GetString());
        Assert.True(manifest.RootElement.GetProperty("containsSensitiveData").GetBoolean());
        Assert.Equal(1, manifest.RootElement.GetProperty("recordCount").GetInt64());
        Assert.Equal(result.DataSha256, manifest.RootElement.GetProperty("dataSha256").GetString());
        Assert.Equal("CLIENT.PWD", manifest.RootElement.GetProperty("excludedSourceFields")[0].GetString());
    }

    [Fact]
    public async Task RefusesToOverwriteExistingOutput()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "source.fdb");
        var output = Path.Combine(temp.Path, "export");
        await File.WriteAllTextAsync(database, "source");
        Directory.CreateDirectory(output);

        await Assert.ThrowsAsync<IOException>(() => MigrationPackageWriter.WriteCustomersAsync(
            output,
            "test-source",
            database,
            Customers(),
            []));
    }

    private static async IAsyncEnumerable<NormalizedCustomer> Customers()
    {
        await Task.Yield();
        yield return new NormalizedCustomer(
            "17", "C-17", null, "Ada", "Lovelace", null, null,
            "ada@example.test", null, null, null, null, null, null, null,
            null, 10m, 20m, null, true, "note",
            new MigrationAddress(null, null, "London", null, null, "UK"),
            new SourceReference(null, null, null, null, null, null, null),
            new SortedDictionary<string, string?>(StringComparer.Ordinal));
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiAtlas.Core.Migration;

public static class MigrationPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonOptions)
    {
        WriteIndented = true
    };

    public static async Task<MigrationPackageResult> WriteCustomersAsync(
        string outputDirectory,
        string sourceSystem,
        string sourceDatabasePath,
        IAsyncEnumerable<NormalizedCustomer> customers,
        IReadOnlyList<string> excludedSourceFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(excludedSourceFields);

        var fullOutputPath = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullOutputPath) || File.Exists(fullOutputPath))
            throw new IOException($"Output path already exists: {fullOutputPath}");

        var parent = Directory.GetParent(fullOutputPath)?.FullName
            ?? throw new InvalidOperationException("Output directory must have a parent directory.");
        Directory.CreateDirectory(parent);

        var stagingPath = fullOutputPath + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stagingPath);
        try
        {
            const string dataFileName = "customers.jsonl";
            var dataPath = Path.Combine(stagingPath, dataFileName);
            long count = 0;
            string dataHash;

            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var stream = new FileStream(
                dataPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await foreach (var customer in customers.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(customer, JsonOptions);
                    await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(json);
                    hash.AppendData("\n"u8);
                    count++;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                dataHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var databaseHash = await ComputeSha256Async(sourceDatabasePath, cancellationToken).ConfigureAwait(false);
            var manifest = new MigrationPackageManifest(
                FormatVersion: "ui-atlas.customer-migration/1",
                SourceSystem: sourceSystem,
                ContainsSensitiveData: true,
                SourceDatabaseName: Path.GetFileName(sourceDatabasePath),
                SourceDatabaseSha256: databaseHash,
                DataFile: dataFileName,
                RecordCount: count,
                DataSha256: dataHash,
                ExcludedSourceFields: excludedSourceFields.Order(StringComparer.Ordinal).ToArray());

            var manifestPath = Path.Combine(stagingPath, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, ManifestJsonOptions) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);

            Directory.Move(stagingPath, fullOutputPath);
            return new MigrationPackageResult(
                fullOutputPath,
                Path.Combine(fullOutputPath, dataFileName),
                Path.Combine(fullOutputPath, "manifest.json"),
                count,
                dataHash);
        }
        catch
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UiAtlas.Core.Recording;

public sealed class RecordingBundleWriter : IDisposable
{
    private readonly string _staging;
    private bool _completed;
    private long _totalBytes;
    private const long MaxStagingBytes = 512L * 1024 * 1024;
    private const string OwnerMarkerName = ".ui-atlas-staging-owner";
    private const string OwnerMarkerContent = "ui-atlas-recording-staging/1";

    public RecordingBundleWriter(string stagingDirectory)
    {
        _staging = Path.GetFullPath(stagingDirectory);
        if (Directory.Exists(_staging) && Directory.EnumerateFileSystemEntries(_staging).Any())
            throw new IOException("Staging directory must be empty.");
        Directory.CreateDirectory(_staging);
        File.WriteAllText(Path.Combine(_staging, OwnerMarkerName), OwnerMarkerContent, new UTF8Encoding(false));
    }

    public string StagingDirectory => _staging;

    public void WriteJson<T>(string entry, T value) =>
        WriteBytes(entry, JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options));

    public void WriteText(string entry, string value) => WriteBytes(entry, Encoding.UTF8.GetBytes(value));

    public void WriteBytes(string entry, ReadOnlySpan<byte> bytes)
    {
        var path = ResolveEntry(entry);
        var existingLength = File.Exists(path) ? new FileInfo(path).Length : 0;
        var proposed = checked(_totalBytes - existingLength + bytes.Length);
        if (proposed > MaxStagingBytes) throw new InvalidOperationException("Recording staging quota exceeded.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray());
        _totalBytes = proposed;
    }

    public IReadOnlyList<UiAtlas.Core.Contracts.BundleFileEntry> DescribeEntries()
    {
        return Directory.EnumerateFiles(_staging, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetRelativePath(_staging, path), OwnerMarkerName, StringComparison.Ordinal))
            .Select(path => (Path: path, Entry: Path.GetRelativePath(_staging, path).Replace('\\', '/')))
            .OrderBy(x => x.Entry, StringComparer.Ordinal)
            .Select(x => new UiAtlas.Core.Contracts.BundleFileEntry(x.Entry, new FileInfo(x.Path).Length, MediaType(x.Entry),
                ComputeSha256(x.Path), x.Entry.StartsWith("raw/", StringComparison.Ordinal)))
            .ToArray();
    }

    public void Complete(string outputPath)
    {
        if (_completed) throw new InvalidOperationException("Bundle already completed.");
        var files = Directory.EnumerateFiles(_staging, "*", SearchOption.AllDirectories)
            .Where(x => !x.EndsWith("hashes.sha256", StringComparison.Ordinal) &&
                        !string.Equals(Path.GetRelativePath(_staging, x), OwnerMarkerName, StringComparison.Ordinal))
            .Select(x => (Path: x, Entry: Path.GetRelativePath(_staging, x).Replace('\\', '/')))
            .OrderBy(x => x.Entry, StringComparer.Ordinal)
            .ToArray();
        var hashLines = files.Select(x => $"{ComputeSha256(x.Path)}  {x.Entry}");
        WriteText("hashes.sha256", string.Join('\n', hashLines) + "\n");

        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var temp = fullOutput + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(_staging, "*", SearchOption.AllDirectories)
                             .Where(x => !string.Equals(Path.GetRelativePath(_staging, x), OwnerMarkerName, StringComparison.Ordinal))
                             .Select(x => (Path: x, Entry: Path.GetRelativePath(_staging, x).Replace('\\', '/')))
                             .OrderBy(x => x.Entry, StringComparer.Ordinal))
                {
                    var entry = archive.CreateEntry(file.Entry, CompressionLevel.Fastest);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var input = File.OpenRead(file.Path);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }
            File.Move(temp, fullOutput, overwrite: true);
            _completed = true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private string ResolveEntry(string entry)
    {
        var normalized = entry.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new ArgumentException("Unsafe bundle entry.", nameof(entry));
        var full = Path.GetFullPath(Path.Combine(_staging, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Entry escapes staging directory.", nameof(entry));
        return full;
    }

    private static string MediaType(string entry) => Path.GetExtension(entry).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".jsonl" => "application/x-ndjson",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_staging) && !IsReparsePoint(_staging)) Directory.Delete(_staging, recursive: true);
    }

    public static int CleanupAbandonedStaging(TimeSpan minimumAge)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(tempRoot, "ui-atlas-recording-*", SearchOption.TopDirectoryOnly))
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTime.UtcNow - Directory.GetCreationTimeUtc(full) < minimumAge) continue;
            try
            {
                if (IsReparsePoint(full) || ContainsReparsePoint(full)) continue;
                var marker = Path.Combine(full, OwnerMarkerName);
                if (!File.Exists(marker) || IsReparsePoint(marker) || new FileInfo(marker).Length > 128 ||
                    !string.Equals(File.ReadAllText(marker), OwnerMarkerContent, StringComparison.Ordinal)) continue;
                Directory.Delete(full, recursive: true);
                count++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }

    public static int CleanupOutputTemporaries(string outputPath, TimeSpan minimumAge)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("Output directory is unavailable.", nameof(outputPath));
        if (!Directory.Exists(directory)) return 0;
        var prefix = Path.GetFileName(fullOutput) + ".tmp-";
        var count = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            var full = Path.GetFullPath(candidate);
            if (!full.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTime.UtcNow - File.GetCreationTimeUtc(full) < minimumAge) continue;
            try { File.Delete(full); count++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ContainsReparsePoint(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Any(IsReparsePoint);
}

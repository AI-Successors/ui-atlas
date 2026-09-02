using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal sealed record CustomerDataCaptureResult(
    string Status,
    string? PackageDirectory = null,
    string? SourceSystem = null,
    long RecordCount = 0,
    string? DataSha256 = null,
    string? Diagnostic = null)
{
    public bool Succeeded => Status == "captured";
}

internal static partial class CustomerDataCaptureCoordinator
{
    private const string DatabaseEnvironmentVariable = "UI-ATLAS_CUSTOMER_DATABASE";
    private const string FirebirdEnvironmentVariable = "UI-ATLAS_FIREBIRD_EMBEDDED";

    public static async Task<CustomerDataCaptureResult> TryCaptureAsync(
        WindowTarget target,
        UiKnowledgeGraph graph,
        string mapPath,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(graph);
        if (!LooksLikeAbacre(target))
            return new("unsupported-source", Diagnostic: "No customer-data adapter is registered for this application.");

        var database = FindDatabase(target, graph);
        if (database is null)
            return new("source-not-found", Diagnostic: "The Abacre database could not be identified unambiguously.");
        var clientLibrary = FindFirebirdClient(target);
        if (clientLibrary is null)
            return new("adapter-runtime-not-found", Diagnostic: "The Firebird 2.1 embedded reader is not installed.");
        var helper = FindMigrationHelper();
        if (helper is null)
            return new("adapter-not-found", Diagnostic: "The Firebird migration helper is not installed.");

        var mapDirectory = Path.GetDirectoryName(Path.GetFullPath(mapPath)) ?? Environment.CurrentDirectory;
        var packageDirectory = Path.Combine(
            mapDirectory,
            Path.GetFileNameWithoutExtension(mapPath) + ".customer-data",
            SafeSegment(sessionId));
        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "ui-atlas-customer-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Directory.Exists(packageDirectory))
                return ReadExistingPackage(packageDirectory);

            Directory.CreateDirectory(snapshotDirectory);
            var snapshot = Path.Combine(snapshotDirectory, Path.GetFileName(database));
            if (!TryCreateStableSnapshot(database, snapshot))
                return new("source-busy", Diagnostic: "The source database changed while its read-only snapshot was being created.");

            Directory.CreateDirectory(Path.GetDirectoryName(packageDirectory)!);
            var startInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("export-customers");
            startInfo.ArgumentList.Add(snapshot);
            startInfo.ArgumentList.Add(clientLibrary);
            startInfo.ArgumentList.Add(packageDirectory);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new("adapter-start-failed", Diagnostic: "The customer-data adapter could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new("adapter-failed", Diagnostic: SafeDiagnostic(string.IsNullOrWhiteSpace(error) ? output : error));
            return ReadExistingPackage(packageDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new("capture-failed", Diagnostic: SafeDiagnostic(ex.Message));
        }
        finally
        {
            try
            {
                if (Directory.Exists(snapshotDirectory))
                    Directory.Delete(snapshotDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static UiKnowledgeGraph AttachMetadata(UiKnowledgeGraph graph, CustomerDataCaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(result);
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "customerDataCaptureStatus", "customerDataSourceSystem", "customerDataRecordCount",
            "customerDataSha256", "customerDataPackageId"
        };
        var packageId = string.IsNullOrWhiteSpace(result.PackageDirectory)
            ? null
            : Path.GetFileName(result.PackageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var nodes = graph.Nodes.Select(node => node.Kind != GraphNodeKind.Application
            ? node
            : node with
            {
                Properties = node.Properties.Where(property => !names.Contains(property.Name)).Concat(
                [
                    new GraphProperty("customerDataCaptureStatus", result.Status),
                    .. string.IsNullOrWhiteSpace(result.SourceSystem) ? [] : new[] { new GraphProperty("customerDataSourceSystem", result.SourceSystem) },
                    .. result.Succeeded ? new[] { new GraphProperty("customerDataRecordCount", result.RecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture)) } : [],
                    .. string.IsNullOrWhiteSpace(result.DataSha256) ? [] : new[] { new GraphProperty("customerDataSha256", result.DataSha256) },
                    .. string.IsNullOrWhiteSpace(packageId) ? [] : new[] { new GraphProperty("customerDataPackageId", packageId) }
                ]).OrderBy(property => property.Name, StringComparer.Ordinal).ToArray()
            }).ToArray();
        return graph with
        {
            Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) },
            Nodes = nodes
        };
    }

    public static UiKnowledgeGraph PreserveMetadata(UiKnowledgeGraph graph, UiKnowledgeGraph existing)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(existing);
        var properties = existing.Nodes
            .Where(node => node.Kind == GraphNodeKind.Application)
            .SelectMany(node => node.Properties)
            .Where(property => property.Name.StartsWith("customerData", StringComparison.Ordinal))
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (properties.Length == 0) return graph;

        var nodes = graph.Nodes.Select(node => node.Kind != GraphNodeKind.Application
            ? node
            : node with
            {
                Properties = node.Properties
                    .Where(property => !property.Name.StartsWith("customerData", StringComparison.Ordinal))
                    .Concat(properties)
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray()
            }).ToArray();
        return graph with
        {
            Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) },
            Nodes = nodes
        };
    }

    private static CustomerDataCaptureResult ReadExistingPackage(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            return new("package-invalid", Diagnostic: "The adapter did not create a package manifest.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var dataFileName = root.GetProperty("dataFile").GetString();
        var expectedHash = root.GetProperty("dataSha256").GetString();
        if (string.IsNullOrWhiteSpace(dataFileName) || Path.GetFileName(dataFileName) != dataFileName ||
            string.IsNullOrWhiteSpace(expectedHash))
            return new("package-invalid", Diagnostic: "The customer-data package manifest is invalid.");
        var dataPath = Path.Combine(packageDirectory, dataFileName);
        if (!File.Exists(dataPath))
            return new("package-invalid", Diagnostic: "The customer-data file is missing.");
        using var data = File.OpenRead(dataPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            return new("package-invalid", Diagnostic: "The customer-data checksum does not match its manifest.");
        return new(
            "captured",
            packageDirectory,
            root.GetProperty("sourceSystem").GetString(),
            root.GetProperty("recordCount").GetInt64(),
            expectedHash);
    }

    private static bool TryCreateStableSnapshot(string source, string destination)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = new FileInfo(source);
            var length = before.Length;
            var writeUtc = before.LastWriteTimeUtc;
            File.Copy(source, destination, overwrite: true);
            var after = new FileInfo(source);
            if (after.Length == length && after.LastWriteTimeUtc == writeUtc && new FileInfo(destination).Length == length)
                return true;
        }
        return false;
    }

    private static string? FindDatabase(WindowTarget target, UiKnowledgeGraph graph)
    {
        var configured = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var executable = Process.GetProcessById(target.ProcessId).MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
                directories.Add(Path.GetDirectoryName(executable)!);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Abacre Hotel Management System 12"));

        var candidates = directories.Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.fdb", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return null;

        var graphText = string.Join('\n', graph.Nodes.SelectMany(node =>
            node.Properties.Select(property => property.Value).Append(node.Label)));
        var referencedNames = FdbNamePattern().Matches(graphText)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = candidates.Where(candidate => referencedNames.Contains(Path.GetFileName(candidate))).ToArray();
        if (referenced.Length == 1) return referenced[0];
        if (candidates.Length == 1) return candidates[0];
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static string? FindFirebirdClient(WindowTarget target)
    {
        var configured = Environment.GetEnvironmentVariable(FirebirdEnvironmentVariable);
        var candidates = new List<string?> { configured };
        try
        {
            var executable = Process.GetProcessById(target.ProcessId).MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var directory = Path.GetDirectoryName(executable)!;
                candidates.Add(Path.Combine(directory, "fbembed.dll"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "migration-firebird", "fbembed.dll"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "Runtimes", "Firebird21", "fbembed.dll"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "ui-atlas-firebird-probe", "firebird-2.1.7-embed-extracted", "fbembed.dll"));
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string? FindMigrationHelper()
    {
        var direct = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "migration-firebird", "ui-atlas-migrate-firebird.exe"),
            Path.Combine(AppContext.BaseDirectory, "ui-atlas-migrate-firebird.exe")
        };
        var match = direct.FirstOrDefault(File.Exists);
        if (match is not null) return match;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; directory is not null && level < 8; level++, directory = directory.Parent)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(directory.FullName, "src", "UiAtlas.Core.Migration.Firebird", "bin",
                    configuration, "net10.0-windows", "ui-atlas-migrate-firebird.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static bool LooksLikeAbacre(WindowTarget target) =>
        target.ProcessName.Contains("ahms", StringComparison.OrdinalIgnoreCase) ||
        target.Title.Contains("Abacre", StringComparison.OrdinalIgnoreCase) ||
        target.ProductName.Contains("Abacre", StringComparison.OrdinalIgnoreCase);

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
    }

    private static string SafeDiagnostic(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim() is var text && text.Length > 300 ? text[..300] : text;

    [GeneratedRegex(@"[A-Za-z0-9_.-]+\.fdb", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FdbNamePattern();
}

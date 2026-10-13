using System.Diagnostics;
using System.Security.Cryptography;

namespace UiAtlas.Core.Cli;

internal static class DesktopExplorerLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string basePath, string graphPath, string? evidencePath)
    {
        var viewerDirectory = Path.Combine(basePath, "viewer");
        if (!File.Exists(Path.Combine(viewerDirectory, "UiAtlas.Core.Desktop.dll")))
            viewerDirectory = basePath; // Published packages use a shared, flat directory.
        var assembly = Path.Combine(viewerDirectory, "UiAtlas.Core.Desktop.dll");
        if (!File.Exists(assembly))
            throw new InvalidOperationException("The map viewer is missing. Rebuild the UI Atlas recorder or reinstall the complete package.");

        foreach (var name in new[] { "Contracts", "Build", "Storage", "Reader" })
        {
            var file = $"UiAtlas.Core.{name}.dll";
            if (!SameFileContent(Path.Combine(basePath, file), Path.Combine(viewerDirectory, file)))
                throw new InvalidOperationException("The recorder and map viewer are from different builds. Rebuild the UI Atlas recorder or reinstall the complete package.");
        }

        var executable = Path.Combine(viewerDirectory, "UiAtlas.Core.Desktop.exe");
        var start = new ProcessStartInfo(File.Exists(executable) ? executable : "dotnet");
        if (!File.Exists(executable)) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(Path.GetFullPath(graphPath));
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            start.ArgumentList.Add("--evidence");
            start.ArgumentList.Add(Path.GetFullPath(evidencePath));
        }
        return start;
    }

    private static bool SameFileContent(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).AsSpan().SequenceEqual(SHA256.HashData(secondStream));
    }
}

using UiAtlas.Core.Cli;

namespace UiAtlas.Core.Tests;

public sealed class DesktopExplorerLauncherTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ResumeFindsRecorderFromStagedAndPublishedViewer(bool staged, bool appHost)
    {
        using var temp = new TempDirectory();
        var viewer = CreateLayout(temp.Path, staged, appHost);
        var recorder = Path.Combine(temp.Path, appHost ? "ui-atlas.exe" : "ui-atlas.dll");
        File.WriteAllText(recorder, "recorder");
        var graph = Path.Combine(temp.Path, "saved map.db");

        var start = UiAtlas.Core.Desktop.ExplorerWindow.CreateResumeRecorderStartInfo(viewer, graph, manualReview: true);

        Assert.Equal(appHost ? recorder : "dotnet", start.FileName);
        Assert.Equal(appHost
            ? new[] { "recording", "resume", graph, "--manual-review" }
            : new[] { recorder, "recording", "resume", graph, "--manual-review" }, start.ArgumentList);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LaunchesMatchingStagedOrPublishedViewerWithSeparateArguments(bool staged, bool appHost)
    {
        using var temp = new TempDirectory();
        var viewer = CreateLayout(temp.Path, staged, appHost);
        var graph = Path.Combine(temp.Path, "saved map.db");
        var evidence = Path.Combine(temp.Path, "source recording.mlrec");

        var start = DesktopExplorerLauncher.CreateStartInfo(temp.Path, graph, evidence);

        Assert.Equal(appHost ? Path.Combine(viewer, "UiAtlas.Core.Desktop.exe") : "dotnet", start.FileName);
        Assert.Equal(appHost
            ? new[] { graph, "--evidence", evidence }
            : new[] { Path.Combine(viewer, "UiAtlas.Core.Desktop.dll"), graph, "--evidence", evidence }, start.ArgumentList);
    }

    [Theory]
    [InlineData("Contracts", false)]
    [InlineData("Build", false)]
    [InlineData("Storage", false)]
    [InlineData("Reader", false)]
    [InlineData("Contracts", true)]
    public void RejectsMismatchedOrMissingGraphAssembliesBeforeStartingViewer(string component, bool missing)
    {
        using var temp = new TempDirectory();
        var viewer = CreateLayout(temp.Path, staged: true, appHost: true);
        var assembly = Path.Combine(viewer, $"UiAtlas.Core.{component}.dll");
        if (missing) File.Delete(assembly);
        else File.WriteAllText(assembly, "older build");

        var error = Assert.Throws<InvalidOperationException>(() =>
            DesktopExplorerLauncher.CreateStartInfo(temp.Path, "map.db", null));

        Assert.Contains("different builds", error.Message);
    }

    [Fact]
    public void MissingPackagedViewerRequiresRebuildInsteadOfSearchingUnverifiedOutputs()
    {
        using var temp = new TempDirectory();
        var error = Assert.Throws<InvalidOperationException>(() =>
            DesktopExplorerLauncher.CreateStartInfo(temp.Path, "map.db", null));
        Assert.Contains("Rebuild", error.Message);
    }

    private static string CreateLayout(string root, bool staged, bool appHost)
    {
        var viewer = staged ? Path.Combine(root, "viewer") : root;
        Directory.CreateDirectory(viewer);
        File.WriteAllText(Path.Combine(viewer, "UiAtlas.Core.Desktop.dll"), "viewer");
        if (appHost) File.WriteAllText(Path.Combine(viewer, "UiAtlas.Core.Desktop.exe"), "host");
        foreach (var component in new[] { "Contracts", "Build", "Storage", "Reader" })
        {
            var name = $"UiAtlas.Core.{component}.dll";
            File.WriteAllText(Path.Combine(root, name), component);
            File.WriteAllText(Path.Combine(viewer, name), component);
        }
        return viewer;
    }
}

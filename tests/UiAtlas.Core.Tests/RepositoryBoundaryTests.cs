using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UiAtlas.Core.Tests;

public sealed partial class RepositoryBoundaryTests
{
    [Fact]
    public void AllProjectInputsResolveInsideRepository()
    {
        var root = FindRoot();
        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(project);
            foreach (var item in document.Descendants().Where(x => x.Attribute("Include") is not null))
            {
                Assert.Null(item.Attribute("Link"));
                var include = item.Attribute("Include")!.Value;
                if (include.Contains("$(", StringComparison.Ordinal) || item.Name.LocalName == "PackageReference") continue;
                var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, include));
                Assert.StartsWith(root + System.IO.Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void SourceContainsNoAbsoluteMachinePathsOrNetworkApis()
    {
        var root = FindRoot();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{System.IO.Path.DirectorySeparatorChar}.git{System.IO.Path.DirectorySeparatorChar}") &&
                        !x.Contains($"{System.IO.Path.DirectorySeparatorChar}artifacts{System.IO.Path.DirectorySeparatorChar}") &&
                        !x.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}") &&
                        !x.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"));
        foreach (var file in files.Where(x => new[] { ".cs", ".csproj", ".props", ".targets", ".md", ".json", ".yml", ".yaml" }.Contains(System.IO.Path.GetExtension(x))))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(AbsolutePathRegex(), text);
            Assert.DoesNotContain("Http" + "Client", text, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Net." + "Sockets", text, StringComparison.Ordinal);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "UiAtlas.Core.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:\\(?:Users|stuff|home)\\", RegexOptions.IgnoreCase)]
    private static partial Regex AbsolutePathRegex();
}

using System.IO;

namespace UiAtlas.Core.Mcp;

/// <summary>Owns only this host instance's generated evidence folders.</summary>
public sealed class GridEvidenceLifetime : IDisposable
{
    private readonly string _root;

    public GridEvidenceLifetime(string catalogRoot)
    {
        _root = Path.GetFullPath(Path.Combine(catalogRoot, "grid-reads", Guid.NewGuid().ToString("N")));
        EnsureUnlinkedAncestors(_root);
        Directory.CreateDirectory(_root);
    }

    public string Create(string acquisitionId)
    {
        var path = Resolve(acquisitionId);
        EnsureUnlinkedAncestors(path);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Release(string acquisitionId)
    {
        var path = Resolve(acquisitionId);
        if (!Directory.Exists(path)) return;
        EnsureUnlinkedAncestors(path);
        DeleteOwnedDirectory(path);
    }

    public void Dispose()
    {
        EnsureUnlinkedAncestors(_root);
        if (Directory.Exists(_root)) DeleteOwnedDirectory(_root);
    }

    private string Resolve(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid generated acquisition ID.", nameof(id));
        var path = Path.GetFullPath(Path.Combine(_root, id));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Evidence path escaped its host root.");
        return path;
    }

    private static void DeleteOwnedDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked evidence will not be traversed or deleted.");
            if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedDirectory(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    private static void EnsureUnlinkedAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked evidence directories are unsupported.");
    }
}

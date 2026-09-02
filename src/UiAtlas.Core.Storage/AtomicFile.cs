namespace UiAtlas.Core.Storage;

internal static class AtomicFile
{
    public static void Publish(string target, Action<string> write)
    {
        var full = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            write(temp);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

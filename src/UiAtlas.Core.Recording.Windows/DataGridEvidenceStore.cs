using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

internal sealed class DataGridEvidenceStore
{
    private readonly string _directory;
    public string ManifestPath => Path.Combine(_directory, "manifest.json");

    public DataGridEvidenceStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        if (Directory.EnumerateFileSystemEntries(_directory).Any())
            throw new InvalidOperationException("evidence-directory-not-empty");
    }

    public GridCapturedTile Save(GridViewportRequest viewport, int sequence)
    {
        var id = $"tile-{sequence:D4}";
        var path = Path.Combine(_directory, id + ".png");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            output.Write(viewport.Png);
        return new(id, path, Convert.ToHexString(SHA256.HashData(viewport.Png)).ToLowerInvariant(),
            DateTimeOffset.UtcNow, viewport.ScreenshotBounds, viewport.HeaderBounds, viewport.BodyBounds,
            0, 0, false, []);
    }

    public void SaveManifest(CapturedGrid capture)
    {
        var temporary = Path.Combine(_directory, "manifest.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(capture, JsonDefaults.Options));
        File.Move(temporary, ManifestPath, overwrite: true);
    }

    internal static GridPixelBuffer Pixels(GridViewportRequest viewport, RectI bounds)
    {
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(viewport.Png);
        if (frame.Width != viewport.ScreenshotBounds.Width || frame.Height != viewport.ScreenshotBounds.Height ||
            !DataGridTargetBinding.Contains(new(0, 0, frame.Width, frame.Height), bounds))
            throw new GridAcquisitionStoppedException("screenshot-coordinate-mismatch", GridAcquisitionStage.Capture);
        var gray = new byte[checked(bounds.Width * bounds.Height)];
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var source = ((y + bounds.Y) * frame.Width + x + bounds.X) * 4;
            gray[y * bounds.Width + x] = (byte)((frame.Pixels[source] * 29 + frame.Pixels[source + 1] * 150 + frame.Pixels[source + 2] * 77) >> 8);
        }
        return new(bounds.Width, bounds.Height, gray);
    }
}

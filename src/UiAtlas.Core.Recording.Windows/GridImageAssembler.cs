using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal static class GridImageAssembler
{
    internal static GridImageExplorationResult Assemble(CapturedGrid capture, string outputPath)
    {
        var tiles = capture.Tiles.Where(t => t.Accepted).ToArray();
        if (tiles.Length == 0) return new(capture, null, new(0, 0, 0, 0), new(0, 0, 0, 0), capture.Reasons);
        var ids = tiles.Select(t => t.TileId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != tiles.Length) throw new InvalidOperationException("duplicate-tile-identity");
        var connected = new HashSet<string>(StringComparer.Ordinal) { tiles[0].TileId };
        bool changed;
        do
        {
            changed = false;
            foreach (var join in capture.Joins.Where(j => j.Accepted && ids.Contains(j.FromTileId) && ids.Contains(j.ToTileId)))
            {
                var from = tiles.Single(t => t.TileId == join.FromTileId);
                var to = tiles.Single(t => t.TileId == join.ToTileId);
                if (to.OffsetX - from.OffsetX != join.DisplacementX || to.OffsetY - from.OffsetY != join.DisplacementY)
                    throw new InvalidOperationException("join-placement-mismatch");
                if (connected.Contains(from.TileId)) changed |= connected.Add(to.TileId);
                if (connected.Contains(to.TileId)) changed |= connected.Add(from.TileId);
            }
        } while (changed);
        if (connected.Count != tiles.Length) throw new InvalidOperationException("disconnected-image-tiles");
        var x0 = tiles.Min(t => t.OffsetX);
        var y0 = tiles.Min(t => t.OffsetY);
        var headerHeight = tiles[0].HeaderBounds.Height;
        var width = checked(tiles.Max(t => t.OffsetX + t.BodyBounds.Width) - x0);
        var height = checked(tiles.Max(t => t.OffsetY + t.BodyBounds.Height) - y0 + headerHeight);
        if (width < 1 || height < 1 || (long)width * height > 32_000_000)
            throw new InvalidOperationException("assembled-image-size-limit");
        var pixels = new byte[checked(width * height * 4)];
        // Transparent gaps remain visible in partial images; no white pixels are invented.
        foreach (var tile in tiles)
        {
            var bytes = File.ReadAllBytes(tile.PngPath);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(tile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("source-image-hash-mismatch");
            var frame = OpaqueSurfaceScanner.PixelFrame.Decode(bytes);
            if (frame.Width != tile.ScreenshotBounds.Width || frame.Height != tile.ScreenshotBounds.Height ||
                tile.HeaderBounds.Height != headerHeight || tile.HeaderBounds.Width != tile.BodyBounds.Width)
                throw new InvalidOperationException("source-image-geometry-mismatch");
            Copy(frame, tile.HeaderBounds, tile.OffsetX - x0, 0);
            Copy(frame, tile.BodyBounds, tile.OffsetX - x0, tile.OffsetY - y0 + headerHeight);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        if (capture.Status == GridCaptureStatus.Complete && Enumerable.Range(0, width * height).Any(i => pixels[i * 4 + 3] == 0))
            throw new InvalidOperationException("complete-image-has-uncovered-pixels");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);
        return new(capture, outputPath, new(0, 0, width, height), new(0, 0, width, headerHeight), capture.Reasons);

        void Copy(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds, int dx, int dy)
        {
            if (!TableCellOcrReader.Contains(frame, bounds)) throw new InvalidOperationException("source-region-outside-image");
            for (var y = 0; y < bounds.Height; y++)
            {
                var destination = ((dy + y) * width + dx) * 4;
                var source = ((bounds.Y + y) * frame.Width + bounds.X) * 4;
                // First observation wins in verified overlaps; original pixels are retained.
                for (var x = 0; x < bounds.Width; x++)
                    if (pixels[destination + x * 4 + 3] == 0)
                        Buffer.BlockCopy(frame.Pixels, source + x * 4, pixels, destination + x * 4, 4);
            }
        }
    }
}

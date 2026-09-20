using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Local OCR over one established cell. Does not infer geometry or repair text.</summary>
internal sealed class TableCellOcrReader
{
    private readonly Func<OpaqueSurfaceScanner.PixelFrame, CancellationToken, Task<LiteralOcrObservation>> _recognize;

    internal TableCellOcrReader(
        Func<OpaqueSurfaceScanner.PixelFrame, CancellationToken, Task<LiteralOcrObservation>>? recognize = null) =>
        _recognize = recognize ?? WindowsOcrTextRecognizer.RecognizeLiteralAsync;

    internal async Task<GridCellValue> ReadAsync(
        OpaqueSurfaceScanner.PixelFrame frame,
        string columnKey,
        GridCellSource source,
        bool uniformBlankCellsQualified,
        CancellationToken cancellationToken)
    {
        if (!Contains(frame, source.PixelBounds))
            return Unreadable(columnKey, source, "cell-region-outside-source");

        var crop = Crop(frame, source.PixelBounds);
        // Only exactly uniform opaque pixels can qualify as empty. OCR silence alone
        // has no empty semantics. Qualification belongs to the selected fixture.
        if (IsUniformOpaque(crop))
            return uniformBlankCellsQualified
                ? new(columnKey, GridCellReadStatus.Empty, "", null, [source])
                : Unreadable(columnKey, source, "uniform-blank-detector-unqualified");

        if (TouchesHorizontalEdge(crop))
            return Unreadable(columnKey, source, "cell-ink-touches-horizontal-edge");

        var result = await _recognize(crop, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            return Unreadable(columnKey, source, result.FailureReason ?? "ocr-failed");
        if (string.IsNullOrWhiteSpace(result.Text))
            return Unreadable(columnKey, source, "ocr-silence-is-not-empty-proof");
        return new(columnKey, GridCellReadStatus.Text, result.Text, null, [source]);
    }

    internal static bool Contains(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds) =>
        frame.Width > 0 && frame.Height > 0 &&
        frame.Pixels.LongLength == (long)frame.Width * frame.Height * 4 &&
        bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
        (long)bounds.X + bounds.Width <= frame.Width && (long)bounds.Y + bounds.Height <= frame.Height;

    internal static OpaqueSurfaceScanner.PixelFrame Crop(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        var stride = checked(bounds.Width * 4);
        var pixels = new byte[checked(stride * bounds.Height)];
        for (var y = 0; y < bounds.Height; y++)
            Buffer.BlockCopy(frame.Pixels, ((bounds.Y + y) * frame.Width + bounds.X) * 4,
                pixels, y * stride, stride);
        return new(bounds.Width, bounds.Height, pixels);
    }

    internal static bool IsUniformOpaque(OpaqueSurfaceScanner.PixelFrame frame)
    {
        if (frame.Pixels.Length < 4 || frame.Pixels[3] != 255) return false;
        for (var offset = 4; offset < frame.Pixels.Length; offset += 4)
            if (frame.Pixels[offset] != frame.Pixels[0] || frame.Pixels[offset + 1] != frame.Pixels[1] ||
                frame.Pixels[offset + 2] != frame.Pixels[2] || frame.Pixels[offset + 3] != 255)
                return false;
        return true;
    }

    internal static bool TouchesHorizontalEdge(OpaqueSurfaceScanner.PixelFrame frame)
    {
        if (frame.Width < 4 || frame.Height < 4) return true;
        var colors = new Dictionary<int, int>();
        for (var offset = 0; offset < frame.Pixels.Length; offset += 4)
        {
            var key = frame.Pixels[offset] | frame.Pixels[offset + 1] << 8 | frame.Pixels[offset + 2] << 16;
            colors[key] = colors.GetValueOrDefault(key) + 1;
        }
        var background = colors.MaxBy(pair => pair.Value);
        if (background.Value < frame.Width * frame.Height * .6) return false;
        foreach (var x in new[] { 0, frame.Width - 1 })
        {
            var ink = 0;
            for (var y = 1; y < frame.Height - 1; y++)
            {
                var offset = (y * frame.Width + x) * 4;
                if (Math.Abs(frame.Pixels[offset] - (background.Key & 255)) > 40 ||
                    Math.Abs(frame.Pixels[offset + 1] - ((background.Key >> 8) & 255)) > 40 ||
                    Math.Abs(frame.Pixels[offset + 2] - ((background.Key >> 16) & 255)) > 40) ink++;
            }
            if (ink >= 2) return true;
        }
        return false;
    }

    internal static GridCellValue Unreadable(string columnKey, GridCellSource source, string reason) =>
        new(columnKey, GridCellReadStatus.Unreadable, null, reason, [source]);
}

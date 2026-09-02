using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal sealed record VisualTextObservation(string Text, RectI Bounds, int LineIndex);

internal static class WindowsOcrTextRecognizer
{
    public static async Task<IReadOnlyDictionary<int, string>> RecognizeRegionsAsync(
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> regions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0) return new Dictionary<int, string>();

        var valid = regions
            .Select((region, index) => (Index: index, Region: Intersect(region, new RectI(0, 0, frame.Width, frame.Height))))
            .Where(item => item.Region.Width > 0 && item.Region.Height > 0)
            .ToArray();
        var labels = new Dictionary<int, string>();
        await RecognizeRegionsPassAsync(frame, valid, labels, zoom: 4, threshold: null, cancellationToken)
            .ConfigureAwait(false);
        await RecognizeRegionsPassAsync(frame, valid.Where(item => !labels.ContainsKey(item.Index)).ToArray(),
            labels, zoom: 4, threshold: 190, cancellationToken).ConfigureAwait(false);
        await RecognizeRegionsPassAsync(frame, valid.Where(item => !labels.ContainsKey(item.Index)).ToArray(),
            labels, zoom: 5, threshold: 140, cancellationToken).ConfigureAwait(false);
        return labels;
    }

    private static async Task RecognizeRegionsPassAsync(
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<(int Index, RectI Region)> candidates,
        Dictionary<int, string> labels,
        int zoom,
        int? threshold,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return;
        const int padding = 8;
        var maxDimension = (int)Math.Max(1u, OcrEngine.MaxImageDimension);
        var valid = candidates
            .Where(item => item.Region.Width * zoom + padding * 2 <= maxDimension)
            .ToArray();
        for (var start = 0; start < valid.Length;)
        {
            var batch = new List<(int Index, RectI Region)>();
            var height = padding;
            while (start < valid.Length)
            {
                var candidateHeight = valid[start].Region.Height * zoom + padding;
                if (batch.Count > 0 && height + candidateHeight > maxDimension) break;
                batch.Add(valid[start++]);
                height += candidateHeight;
            }
            if (batch.Count == 0) continue;

            var width = Math.Min(maxDimension,
                batch.Max(item => item.Region.Width * zoom) + padding * 2);
            var pixels = Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray();
            var slots = new List<(int Index, RectI Bounds)>();
            var top = padding;
            foreach (var item in batch)
            {
                var slot = new RectI(padding, top, item.Region.Width * zoom, item.Region.Height * zoom);
                for (var y = 0; y < slot.Height; y++)
                for (var x = 0; x < slot.Width; x++)
                {
                    var sourceX = item.Region.X + x / zoom;
                    var sourceY = item.Region.Y + y / zoom;
                    var sourceOffset = (sourceY * frame.Width + sourceX) * 4;
                    var targetOffset = ((slot.Y + y) * width + slot.X + x) * 4;
                    if (threshold is null)
                    {
                        Buffer.BlockCopy(frame.Pixels, sourceOffset, pixels, targetOffset, 4);
                    }
                    else
                    {
                        var luminance = (frame.Pixels[sourceOffset] * 29 +
                                         frame.Pixels[sourceOffset + 1] * 150 +
                                         frame.Pixels[sourceOffset + 2] * 77) >> 8;
                        var value = (byte)(luminance < threshold.Value ? 0 : 255);
                        pixels[targetOffset] = value;
                        pixels[targetOffset + 1] = value;
                        pixels[targetOffset + 2] = value;
                        pixels[targetOffset + 3] = 255;
                    }
                }
                slots.Add((item.Index, slot));
                top += slot.Height + padding;
            }

            var words = await RecognizeAsync(
                new OpaqueSurfaceScanner.PixelFrame(width, height, pixels), cancellationToken).ConfigureAwait(false);
            foreach (var slot in slots)
            {
                var text = string.Join(' ', words
                    .Where(word => ContainsCenter(slot.Bounds, word.Bounds))
                    .OrderBy(word => word.LineIndex)
                    .ThenBy(word => word.Bounds.X)
                    .Select(word => word.Text)
                    .Distinct(StringComparer.OrdinalIgnoreCase)).Trim();
                if (text.Length > 0) labels[slot.Index] = text;
            }
        }
    }

    public static async Task<IReadOnlyList<VisualTextObservation>> RecognizeAsync(
        OpaqueSurfaceScanner.PixelFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length < frame.Width * frame.Height * 4)
            return [];

        try
        {
            var maxDimension = Math.Max(1u, OcrEngine.MaxImageDimension);
            var scale = Math.Min(1d, maxDimension / (double)Math.Max(frame.Width, frame.Height));
            var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
            var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
            var pixels = scale >= .999
                ? frame.Pixels
                : ResizeBgra(frame.Pixels, frame.Width, frame.Height, width, height);
            var buffer = CryptographicBuffer.CreateFromByteArray(pixels);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            // Most legacy line-of-business applications expose Latin UI text
            // even on machines whose primary profile language is not English.
            // Prefer the explicit Latin model and retain the profile model as a
            // fallback for installations without that language pack.
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ??
                         OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return [];

            var result = await engine.RecognizeAsync(bitmap);
            cancellationToken.ThrowIfCancellationRequested();
            var inverse = 1d / scale;
            var words = new List<VisualTextObservation>();
            for (var lineIndex = 0; lineIndex < result.Lines.Count; lineIndex++)
            {
                foreach (var word in result.Lines[lineIndex].Words)
                {
                    var box = word.BoundingRect;
                    var text = NormalizeText(word.Text);
                    if (text.Length == 0) continue;
                    words.Add(new(
                        text,
                        new RectI(
                            (int)Math.Round(box.X * inverse),
                            (int)Math.Round(box.Y * inverse),
                            Math.Max(1, (int)Math.Round(box.Width * inverse)),
                            Math.Max(1, (int)Math.Round(box.Height * inverse))),
                        lineIndex));
                }
            }
            return words;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // OCR is enrichment. Rectangle discovery remains usable when a
            // language pack or the Windows OCR runtime is unavailable.
            return [];
        }
    }

    private static byte[] ResizeBgra(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / width);
                Buffer.BlockCopy(source, (sourceY * sourceWidth + sourceX) * 4,
                    result, (y * width + x) * 4, 4);
            }
        }
        return result;
    }

    private static RectI Intersect(RectI first, RectI second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static bool ContainsCenter(RectI outer, RectI inner)
    {
        var x = inner.X + inner.Width / 2;
        var y = inner.Y + inner.Height / 2;
        return x >= outer.X && x < outer.X + outer.Width &&
               y >= outer.Y && y < outer.Y + outer.Height;
    }

    private static string NormalizeText(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>
/// Provides OCR text only for a rectangle explicitly selected by the user.
/// It never creates or classifies controls.
/// </summary>
public static class ManualControlLabelRecognizer
{
    public static async Task<string> SuggestAsync(
        byte[] png,
        RectI imageBounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0 || imageBounds.Width <= 0 || imageBounds.Height <= 0)
            return string.Empty;

        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(png);
        var labels = await WindowsOcrTextRecognizer.RecognizeRegionsAsync(
            frame, [imageBounds], cancellationToken).ConfigureAwait(false);
        return labels.TryGetValue(0, out var label)
            ? string.Join(' ', label.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : string.Empty;
    }
}

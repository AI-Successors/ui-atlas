using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingSize = System.Drawing.Size;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public static class WindowSnapshotCapture
{
    internal const int MaxScopedWindows = RecordingContractLimits.MaxScopedWindows;
    private const int MaxCumulativeCaptureBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan MaxCumulativeCaptureDuration = TimeSpan.FromSeconds(5);
    public sealed record CaptureResult(byte[] Png, string Method, bool UsedFallback, bool IsPartial = false);

    public static WindowObservation Observe(WindowTarget target)
    {
        var hwnd = (nint)target.Hwnd;
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var placement = new NativeMethods.WindowPlacement { Length = (uint)Marshal.SizeOf<NativeMethods.WindowPlacement>() };
        NativeMethods.GetWindowPlacement(hwnd, ref placement);
        _ = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmaCloaked, out var cloaked, sizeof(int));
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return new(hwnd.ToInt64(), target.RootOwnerHwnd, target.ProcessId, WindowCatalog.GetClass(hwnd), WindowCatalog.GetText(hwnd),
            new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsWindowEnabled(hwnd), placement.ShowCmd == NativeMethods.SwShowMinimized, cloaked != 0, (int)(dpi == 0 ? 96 : dpi),
            target.OwnerHwnd, target.ZOrder, target.Style, target.ExStyle,
            (target.ExStyle & NativeMethods.WsExToolWindow) != 0,
            (target.ExStyle & NativeMethods.WsExTopmost) != 0);
    }

    public static async Task<CaptureResult> CapturePngAsync(WindowTarget target, CancellationToken cancellationToken)
    {
        var discovered = WindowCatalog.ListScopedWindows(target);
        var bounded = discovered.Take(MaxScopedWindows).ToArray();
        var result = await CapturePngAsync(bounded, cancellationToken).ConfigureAwait(false);
        return discovered.Count > bounded.Length ? result with { IsPartial = true } : result;
    }

    internal static async Task<CaptureResult> CapturePngAsync(
        IReadOnlyList<WindowTarget> windows,
        CancellationToken cancellationToken,
        bool preferScreenBounds = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (windows.Count == 0) throw new InvalidOperationException("No scoped windows are available.");
        if (windows.Count > MaxScopedWindows) throw new InvalidOperationException("Scoped window count exceeds the capture limit.");
        windows = windows.Where(IsCapturable).ToArray();
        if (windows.Count == 0) throw new InvalidOperationException("No visible scoped windows are available.");
        if (preferScreenBounds)
            return new(CaptureScreenPng(windows), "screen-bounds-explicit", true);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                if (WindowsGraphicsCapture.IsSupported)
                {
                    var frames = new List<(WindowTarget Window, byte[] Png)>(windows.Count);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var cumulativeBytes = 0;
                    var partial = false;
                    foreach (var window in windows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var remaining = MaxCumulativeCaptureDuration - timer.Elapsed;
                        if (remaining <= TimeSpan.Zero) { partial = true; break; }
                        var png = await WindowsGraphicsCapture.CaptureWindowPngAsync(
                            (nint)window.Hwnd, remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        // WGC can report success for an Office root HWND while returning
                        // a completely black frame. Validating only the final composition
                        // misses this when a small popup contributes visible pixels.
                        if (IsVisuallyBlankPng(png))
                            throw new InvalidOperationException("Windows Graphics Capture returned a blank scoped window.");
                        if (cumulativeBytes > MaxCumulativeCaptureBytes - png.Length) { partial = true; break; }
                        cumulativeBytes += png.Length;
                        frames.Add((window, png));
                    }

                    if (frames.Count > 0)
                    {
                        var composed = Compose(windows, frames);
                        // Some real Win32 / WebView-backed windows report success here but still hand back
                        // a fully transparent or otherwise blank frame. Keep falling back until we have pixels.
                        if (!IsVisuallyBlankPng(composed))
                            return new(composed, "windows-graphics-capture", false, partial || frames.Count != windows.Count);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableCaptureFailure(ex))
            {
                // The recorder intentionally falls through to its bounded per-window fallback.
            }
        }

        // Revit's accelerated/WPF surface can return a plausible but permanently stale
        // bitmap from GetWindowDC. It is not visually blank, so the generic blank-frame
        // probe cannot detect it. Once WGC is unavailable, capture the pixels that are
        // actually on screen instead of accepting that cached window surface.
        if (windows.Any(RequiresLiveScreenFallback))
            return new(CaptureScreenPng(windows), "screen-bounds-rendered-fallback", true);

        // A popup transaction is meant to preserve exactly what the user sees:
        // the foreground application with its transient surface. Once per-window
        // native capture proved unreliable, screen bounds are more faithful than
        // composing another set of potentially stale HWND surfaces.
        if (windows.Count > 1)
            return new(CaptureScreenPng(windows), "screen-bounds-fallback", true);

        var gdi = CaptureWindowDcPng(windows);
        if (!IsVisuallyBlankPng(gdi))
            return new(gdi, "gdi-window-fallback", true);

        return new(CaptureScreenPng(windows), "screen-bounds-fallback", true);
    }

    internal static bool RequiresLiveScreenFallback(WindowTarget target) =>
        target.ProcessName.Equals("Revit", StringComparison.OrdinalIgnoreCase) ||
        target.OriginalFilename.Equals("Revit.exe", StringComparison.OrdinalIgnoreCase) ||
        target.ProductName.Contains("Autodesk Revit", StringComparison.OrdinalIgnoreCase);

    internal static bool RequiresScreenBoundsCaptureForDialog(WindowTarget target)
    {
        if (target.ClassName.StartsWith("bosa_sdm_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!target.ClassName.Equals("NUIDialog", StringComparison.OrdinalIgnoreCase))
            return false;

        return target.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) ||
               target.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase) ||
               target.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) ||
               target.ProcessName.Equals("OUTLOOK", StringComparison.OrdinalIgnoreCase) ||
               target.ProcessName.Equals("MSACCESS", StringComparison.OrdinalIgnoreCase) ||
               target.ProcessName.Equals("ONENOTE", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCapturable(WindowTarget target)
    {
        var hwnd = (nint)target.Hwnd;
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd) ||
            !NativeMethods.GetWindowRect(hwnd, out var rect) ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return false;
        var result = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmaCloaked, out var cloaked, sizeof(int));
        return result != 0 || cloaked == 0;
    }

    internal static bool IsRecoverableCaptureFailure(Exception exception) => exception is
        ExternalException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or FileFormatException;

    private static byte[] CaptureWindowDcPng(IReadOnlyList<WindowTarget> windows)
    {
        var captureBounds = CaptureBounds(windows);
        var hwnd = (nint)windows[0].Hwnd;
        var source = NativeMethods.GetWindowDC(hwnd);
        if (source == 0) throw new InvalidOperationException("Cannot acquire window surface.");
        var destination = NativeMethods.CreateCompatibleDC(source);
        var bitmap = NativeMethods.CreateCompatibleBitmap(source, captureBounds.Width, captureBounds.Height);
        var old = NativeMethods.SelectObject(destination, bitmap);
        try
        {
            foreach (var window in windows)
            {
                var windowHwnd = (nint)window.Hwnd;
                var windowDc = NativeMethods.GetWindowDC(windowHwnd);
                if (windowDc == 0) continue;
                try
                {
                    if (!NativeMethods.BitBlt(destination, window.Bounds.X - captureBounds.Left, window.Bounds.Y - captureBounds.Top,
                            window.Bounds.Width, window.Bounds.Height,
                            windowDc, 0, 0, NativeMethods.Srccopy | NativeMethods.Captureblt))
                        throw new InvalidOperationException("Window capture failed.");
                }
                finally { NativeMethods.ReleaseDC(windowHwnd, windowDc); }
            }
            var sourceBitmap = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            sourceBitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sourceBitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        finally
        {
            NativeMethods.SelectObject(destination, old);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(destination);
            NativeMethods.ReleaseDC(hwnd, source);
        }
    }

    internal static bool IsVisuallyBlankPng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0) return true;
        using var input = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bitmap = converted;
        }
        else
        {
            bitmap.Freeze();
        }

        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return true;
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var hasNonBlackPixel = false;
        var hasNonWhitePixel = false;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] > 8 || pixels[offset + 1] > 8 || pixels[offset + 2] > 8)
                hasNonBlackPixel = true;
            if (pixels[offset] < 247 || pixels[offset + 1] < 247 || pixels[offset + 2] < 247)
                hasNonWhitePixel = true;
            if (hasNonBlackPixel && hasNonWhitePixel)
                return false;
        }

        return true;
    }

    internal static BitmapSource DecodeOpaquePng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var input = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.Format != PixelFormats.Bgr32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            bitmap = converted;
        }
        else
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    internal static bool AreVisuallyEquivalentPng(byte[] firstPng, byte[] secondPng)
    {
        ArgumentNullException.ThrowIfNull(firstPng);
        ArgumentNullException.ThrowIfNull(secondPng);
        if (firstPng.Length == 0 || secondPng.Length == 0) return false;

        var first = OpaqueSurfaceScanner.PixelFrame.Decode(firstPng);
        var second = OpaqueSurfaceScanner.PixelFrame.Decode(secondPng);
        if (first.Width != second.Width || first.Height != second.Height ||
            first.Pixels.Length != second.Pixels.Length)
            return false;

        // Ignore a caret, cursor, or a small animated indicator, but reject a
        // page whose cards and images are still being painted. A bounded sample
        // keeps this check cheap on large displays.
        var step = Math.Max(1, Math.Min(first.Width, first.Height) / 240);
        long samples = 0;
        long changed = 0;
        long totalDifference = 0;
        for (var y = step / 2; y < first.Height; y += step)
        {
            for (var x = step / 2; x < first.Width; x += step)
            {
                var offset = checked((y * first.Width + x) * 4);
                var difference = Math.Abs(first.Pixels[offset] - second.Pixels[offset]) +
                                 Math.Abs(first.Pixels[offset + 1] - second.Pixels[offset + 1]) +
                                 Math.Abs(first.Pixels[offset + 2] - second.Pixels[offset + 2]);
                samples++;
                totalDifference += difference;
                if (difference >= 42) changed++;
            }
        }

        if (samples == 0) return false;
        return changed / (double)samples <= 0.004 &&
               totalDifference / (double)samples <= 3.0;
    }

    internal static bool HasRenderedAutomationContentPng(
        byte[] png,
        RectI screenshotBounds,
        IReadOnlyList<AutomationObservation> automation)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(automation);
        if (png.Length == 0 || !screenshotBounds.IsValid)
            return false;

        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(png);
        var visible = automation
            .Where(control => !control.IsOffscreen && control.Bounds.IsValid &&
                              !string.IsNullOrWhiteSpace(control.Name) &&
                              Intersects(control.Bounds, screenshotBounds))
            .ToArray();
        var text = visible
            .Where(control => IsControlType(control, "Text"))
            .OrderByDescending(control => control.Name.Trim().Length)
            .ThenByDescending(control => (long)control.Bounds.Width * control.Bounds.Height)
            .FirstOrDefault();
        var actionButtons = visible
            .Where(control => IsControlType(control, "Button") && !IsWindowCloseButton(control))
            .OrderByDescending(control => control.HasKeyboardFocus)
            .ThenByDescending(control => control.Name.Trim().Length)
            .Take(4)
            .ToArray();
        var tabs = visible
            .Where(control => IsControlType(control, "TabItem"))
            .OrderBy(control => control.Bounds.X)
            .ThenBy(control => control.Bounds.Y)
            .ToArray();

        // Some dialogs only contain icon buttons, so the absence of a suitable
        // text or action candidate must not make every capture wait until the cap.
        if (text is null && actionButtons.Length == 0)
            return true;

        var textReady = text is null || RegionHasVisibleInk(frame, screenshotBounds, text.Bounds, insetPixels: 0);
        var buttonsReady = actionButtons.Length == 0 || actionButtons.All(button =>
            RegionHasVisibleInk(frame, screenshotBounds, button.Bounds, insetPixels: 4));
        var requiredRenderedTabs = tabs.Length >= 3 ? Math.Min(3, tabs.Length) : 0;
        var tabsReady = requiredRenderedTabs == 0 || tabs.Count(tab =>
            RegionHasVisibleInk(frame, screenshotBounds, tab.Bounds, insetPixels: 4)) >= requiredRenderedTabs;
        return textReady && buttonsReady && tabsReady;
    }

    private static bool RegionHasVisibleInk(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI screenshotBounds,
        RectI region,
        int insetPixels)
    {
        var left = Math.Clamp((int)Math.Floor(
            (region.X - screenshotBounds.X) * frame.Width / (double)screenshotBounds.Width), 0, frame.Width);
        var top = Math.Clamp((int)Math.Floor(
            (region.Y - screenshotBounds.Y) * frame.Height / (double)screenshotBounds.Height), 0, frame.Height);
        var right = Math.Clamp((int)Math.Ceiling(
            (region.X + region.Width - screenshotBounds.X) * frame.Width / (double)screenshotBounds.Width), 0, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(
            (region.Y + region.Height - screenshotBounds.Y) * frame.Height / (double)screenshotBounds.Height), 0, frame.Height);
        var maximumInset = Math.Max(0, Math.Min(right - left, bottom - top) / 4);
        var inset = Math.Min(insetPixels, maximumInset);
        left += inset;
        top += inset;
        right -= inset;
        bottom -= inset;
        if (right - left < 3 || bottom - top < 3)
            return false;

        var edgePixels = 0;
        var testedPixels = 0;
        for (var y = top + 1; y < bottom; y++)
        {
            for (var x = left + 1; x < right; x++)
            {
                var offset = checked((y * frame.Width + x) * 4);
                if (frame.Pixels[offset + 3] < 32)
                    continue;
                var luminance = Luminance(frame.Pixels, offset);
                var leftLuminance = Luminance(frame.Pixels, offset - 4);
                var topLuminance = Luminance(frame.Pixels, offset - frame.Width * 4);
                testedPixels++;
                if (Math.Abs(luminance - leftLuminance) >= 26 ||
                    Math.Abs(luminance - topLuminance) >= 26)
                    edgePixels++;
            }
        }

        // Anti-aliased glyphs produce many local high-contrast edges. A flat
        // unpainted label/button, even with a focus border outside the inset,
        // does not. Scale the floor for large multi-line message rectangles.
        return edgePixels >= Math.Max(6, testedPixels / 1_000);
    }

    private static int Luminance(byte[] pixels, int offset) =>
        (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;

    private static bool IsControlType(AutomationObservation control, string type) =>
        string.Equals(control.ControlType, type, StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith('.' + type, StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowCloseButton(AutomationObservation control) =>
        string.Equals(control.AutomationId, "Close", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(control.Name.Trim(), "Close", StringComparison.OrdinalIgnoreCase);

    private static bool Intersects(RectI left, RectI right) =>
        left.X < right.X + right.Width && left.X + left.Width > right.X &&
        left.Y < right.Y + right.Height && left.Y + left.Height > right.Y;

    private static byte[] CaptureScreenPng(IReadOnlyList<WindowTarget> windows)
    {
        var captureBounds = CaptureBounds(windows);
        using var bitmap = new DrawingBitmap(captureBounds.Width, captureBounds.Height, DrawingPixelFormat.Format32bppPArgb);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(captureBounds.Left, captureBounds.Top, 0, 0,
                new DrawingSize(captureBounds.Width, captureBounds.Height));
        }

        using var output = new MemoryStream();
        bitmap.Save(output, DrawingImageFormat.Png);
        if (output.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Encoded frame exceeds quota.");
        return output.ToArray();
    }

    private static byte[] Compose(IReadOnlyList<WindowTarget> windows, IReadOnlyList<(WindowTarget Window, byte[] Png)> frames)
    {
        if (frames.Count == 0) throw new InvalidOperationException("No scoped windows are available.");
        var captureBounds = CaptureBounds(windows);

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            foreach (var frame in frames)
            {
                using var input = new MemoryStream(frame.Png, writable: false);
                var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var image = decoder.Frames[0];
                image.Freeze();
                drawing.DrawImage(image, new System.Windows.Rect(
                    frame.Window.Bounds.X - captureBounds.Left, frame.Window.Bounds.Y - captureBounds.Top,
                    frame.Window.Bounds.Width, frame.Window.Bounds.Height));
            }
        }

        var bitmap = new RenderTargetBitmap(captureBounds.Width, captureBounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static (int Left, int Top, int Width, int Height) CaptureBounds(IReadOnlyList<WindowTarget> windows)
    {
        if (windows.Count == 0) throw new InvalidOperationException("No scoped windows are available.");
        var left = windows.Min(window => window.Bounds.X);
        var top = windows.Min(window => window.Bounds.Y);
        var right = windows.Max(window => window.Bounds.X + window.Bounds.Width);
        var bottom = windows.Max(window => window.Bounds.Y + window.Bounds.Height);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000)
            throw new InvalidOperationException("Window dimensions are invalid or too large.");
        return (left, top, width, height);
    }
}

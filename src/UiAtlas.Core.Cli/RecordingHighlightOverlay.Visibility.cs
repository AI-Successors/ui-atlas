using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal sealed partial class RecordingHighlightOverlay
{
    private IReadOnlyList<RectI> _mapperOccluders = [];
    private int _mapperOcclusionVersion;
    private readonly Dictionary<Window, (RectI Bounds, int Version)> _gridClipState = [];

    private void RefreshMapperOcclusion()
    {
        if (!_mapperEnabled || _window is null) return;
        var ownDrawing = new WindowInteropHelper(_window).Handle;
        var ownHits = _gridHitWindows.Values.Select(w => new WindowInteropHelper(w).Handle).ToHashSet();
        var surface = _mapperSurfaceHwnd == 0 ? _target.Hwnd : _mapperSurfaceHwnd;
        var occluders = new List<RectI>();
        // Keep the map visible when focus moves away, but do not draw or intercept input over other windows.
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd.ToInt64() == surface) return false;
            if (hwnd == ownDrawing || ownHits.Contains(hwnd) || !NativeMethods.IsWindowVisible(hwnd) ||
                (GetWindowLong(hwnd, GwlExStyle) & WsExTransparent) != 0) return true;
            _ = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmaCloaked, out var cloaked, sizeof(int));
            if (cloaked != 0 || !NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
            var bounds = new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (Intersects(bounds, _currentRootBounds)) occluders.Add(bounds);
            return true;
        }, 0);
        if (_mapperOccluders.SequenceEqual(occluders)) return;
        _mapperOccluders = occluders;
        _mapperOcclusionVersion++;
        RenderHighlights();
    }

    private void ApplyMapperDrawingClip()
    {
        if (_canvas is null) return;
        if (!_mapperEnabled || !TryProjectToOverlayRect(_currentRootBounds, out var root))
        { _canvas.Clip = null; return; }
        Geometry clip = new RectangleGeometry(root);
        foreach (var bounds in _mapperOccluders)
            if (TryProjectToOverlayRect(bounds, out var projected))
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, new RectangleGeometry(projected));
        clip.Freeze();
        _canvas.Clip = clip;
    }

    internal bool IsMapperPointVisible(Point screenPoint) => _canvas is not null && _mapperTargetVisible &&
        (_canvas.Clip?.FillContains(_canvas.PointFromScreen(screenPoint)) ?? true);

    private void ApplyGridInputClip(Window window, RectI bounds)
    {
        var state = (bounds, _mapperOcclusionVersion);
        if (_gridClipState.TryGetValue(window, out var previousState) && previousState == state) return;
        var previousDpi = SetThreadDpiAwarenessContext((nint)(-4));
        nint region = 0;
        try
        {
            region = CreateRectRgn(0, 0, bounds.Width, bounds.Height);
            if (region == 0) return;
            foreach (var occluder in _mapperOccluders.Where(r => Intersects(r, bounds)))
            {
                var cut = CreateRectRgn(occluder.X - bounds.X, occluder.Y - bounds.Y,
                    occluder.X + occluder.Width - bounds.X, occluder.Y + occluder.Height - bounds.Y);
                try { _ = CombineRgn(region, region, cut, 4); }
                finally { if (cut != 0) _ = NativeMethods.DeleteObject(cut); }
            }
            if (SetWindowRgn(new WindowInteropHelper(window).Handle, region, true) != 0)
            {
                region = 0; // Windows owns the region after a successful SetWindowRgn.
                _gridClipState[window] = state;
            }
        }
        finally
        {
            if (region != 0) _ = NativeMethods.DeleteObject(region);
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static bool Intersects(RectI a, RectI b) => a.IsValid && b.IsValid &&
        a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width &&
        a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}

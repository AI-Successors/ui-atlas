namespace UiAtlas.Core.Contracts;

/// <summary>Source classification and coordinate arithmetic shared by capture and inspection.</summary>
public static class RawObservationSemantics
{
    public const string Version = "raw-observation/2";

    public static string Kind(string source) => source switch
    {
        "pixels" => "pixels",
        "win32" => "native-windows",
        "ocr" or "visual.geometry" => "image-derived",
        _ when source.StartsWith("vision.", StringComparison.Ordinal) => "image-derived",
        "capture" => "diagnostic",
        _ => "native-automation"
    };

    public static bool IsImageSource(string source) => Kind(source) == "image-derived";

    public static RectI? Intersection(RectI? first, RectI? second)
    {
        if (!ControlGeometry.IsRenderable(first) || !ControlGeometry.IsRenderable(second)) return null;
        var left = Math.Max((long)first!.X, second!.X);
        var top = Math.Max((long)first.Y, second.Y);
        var right = Math.Min((long)first.X + first.Width, (long)second.X + second.Width);
        var bottom = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height);
        return right > left && bottom > top
            ? new((int)left, (int)top, (int)(right - left), (int)(bottom - top)) : null;
    }

    public static string Coverage(RectI window, RectI? image)
    {
        var overlap = Intersection(window, image);
        return overlap is null ? "missing" : overlap == window ? "full" : "partial";
    }

    public static string NativeKind(WindowObservation window)
    {
        if (window.ClassName.Equals("#32770", StringComparison.OrdinalIgnoreCase) ||
            window.ClassName.StartsWith("bosa_", StringComparison.OrdinalIgnoreCase)) return "dialog-window";
        var tool = window.IsToolWindow || (window.ExStyle & 0x80) != 0 || window.ClassName.Contains("tool window", StringComparison.OrdinalIgnoreCase);
        var framed = (window.Style & 0x00C40000) != 0;
        if (tool) return framed ? "tool-window" : "borderless-tool-window";
        return !framed && (window.Style & 0x80000000L) != 0 ? "borderless-window" : "normal-window";
    }
}

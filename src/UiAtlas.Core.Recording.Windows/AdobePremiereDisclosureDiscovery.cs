using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public static class AdobePremiereDisclosureDiscovery
{
    private const string OverflowClass = "AdobeVisualOverflow";
    private const string DisclosureClass = "AdobeVisualDisclosure";
    private const string VisualControlClass = "AdobeVisualControl";
    private const string PanelHeaderClass = "AdobePanelHeader";
    private const string PanelTabClass = "AdobePanelTab";
    private const string PanelMenuClass = "AdobePanelMenu";
    private const string ToolDisclosureClass = "AdobeToolDisclosure";
    private const string ScrollRegionClass = "AdobePanelScrollRegion";
    private const string ApplicationMenuClass = "AdobeApplicationMenu";
    private const string WorkspaceTabClass = "AdobeWorkspaceTab";
    private const string TreeItemButtonClass = "AdobeTreeItemButton";

    public static bool IsSupported(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.Title} {target.ProductName} {target.CompanyName}";
        return identity.Contains("Premiere", StringComparison.OrdinalIgnoreCase) &&
               identity.Contains("Adobe", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyList<AutomationObservation>> DiscoverAsync(
        WindowTarget target,
        IReadOnlyList<AutomationObservation> legacyControls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(legacyControls);
        if (!IsSupported(target)) return [];

        var capture = await WindowSnapshotCapture.CapturePngAsync(target, cancellationToken).ConfigureAwait(false);
        return Discover(target, legacyControls, capture.Png);
    }

    internal static IReadOnlyList<AutomationObservation> Discover(
        WindowTarget target,
        IReadOnlyList<AutomationObservation> legacyControls,
        byte[] png)
    {
        if (png.Length == 0 || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return [];
        using var stream = new MemoryStream(png, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bitmap = converted;
        }

        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return Discover(target, legacyControls, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride);
    }

    internal static IReadOnlyList<AutomationObservation> Discover(
        WindowTarget target,
        IReadOnlyList<AutomationObservation> legacyControls,
        byte[] bgra,
        int pixelWidth,
        int pixelHeight,
        int stride)
    {
        if (pixelWidth < 80 || pixelHeight < 80 || stride < pixelWidth * 4 ||
            bgra.Length < checked(stride * pixelHeight)) return [];

        var scaleX = pixelWidth / (double)target.Bounds.Width;
        var scaleY = pixelHeight / (double)target.Bounds.Height;
        var panelBounds = legacyControls
            .Where(control => !control.IsOffscreen && control.Bounds.Width >= 32 && control.Bounds.Height >= 30)
            .Where(control => control.ClassName.Contains("DroverLord", StringComparison.OrdinalIgnoreCase) ||
                              control.Name.Equals("OS_ViewContainer", StringComparison.OrdinalIgnoreCase) ||
                              control.Name.Equals("SubWindow", StringComparison.OrdinalIgnoreCase))
            .Select(control => control.Bounds)
            .Distinct()
            .ToArray();

        var mask = new bool[checked(pixelWidth * pixelHeight)];
        var topStart = Math.Clamp((int)Math.Round(pixelHeight * 0.055), 1, pixelHeight - 2);
        var topEnd = Math.Clamp((int)Math.Round(pixelHeight * 0.12), topStart + 1, pixelHeight - 1);
        MarkRegion(mask, pixelWidth, pixelHeight, 0, topStart, pixelWidth, topEnd);
        var scrollCandidates = new List<RectI>();
        foreach (var panel in panelBounds)
        {
            var left = ToPixelX(panel.X, target.Bounds, scaleX, pixelWidth);
            var right = ToPixelX(panel.X + panel.Width, target.Bounds, scaleX, pixelWidth);
            var top = ToPixelY(panel.Y, target.Bounds, scaleY, pixelHeight);
            var bottom = ToPixelY(panel.Y + panel.Height, target.Bounds, scaleY, pixelHeight);
            var leftEdge = Math.Clamp((int)Math.Round(30 * scaleX), 16, 48);
            var rightEdge = Math.Clamp((int)Math.Round(72 * scaleX), 36, 110);
            MarkRegion(mask, pixelWidth, pixelHeight, left, top, Math.Min(right, left + leftEdge), bottom);
            MarkRegion(mask, pixelWidth, pixelHeight, Math.Max(left, right - rightEdge), top, right, bottom);
        }

        var components = FindRightChevrons(bgra, pixelWidth, pixelHeight, stride, mask);
        var paired = new HashSet<int>();
        var candidates = new List<(RectI Bounds, string ClassName, string Name)>();
        var applicationMenuIndex = 0;
        foreach (var command in FindTopTextRegions(
                     bgra, pixelWidth, pixelHeight, stride,
                     0.028, 0.061, darkForeground: true))
        {
            var expanded = Expand(command.Bounds, 7, pixelWidth, pixelHeight);
            candidates.Add((expanded, ApplicationMenuClass,
                $"Adobe application menu {++applicationMenuIndex} {VisualFingerprint(bgra, stride, expanded)}"));
        }
        var workspaceTabIndex = 0;
        foreach (var command in FindTopTextRegions(
                     bgra, pixelWidth, pixelHeight, stride,
                     0.061, 0.112, darkForeground: false))
        {
            var expanded = Expand(command.Bounds, 8, pixelWidth, pixelHeight);
            candidates.Add((expanded, WorkspaceTabClass,
                $"Adobe workspace tab {++workspaceTabIndex} {VisualFingerprint(bgra, stride, expanded)}"));
        }
        foreach (var header in legacyControls
                     .Where(control => control.ControlType == "ControlType.Window" &&
                                       control.Name.Equals("OS_ViewContainer", StringComparison.OrdinalIgnoreCase) &&
                                       control.Bounds.Width >= 180 && control.Bounds.Height is >= 28 and <= 48)
                     .Select(control => control.Bounds)
                     .Distinct())
        {
            var pixelHeader = ToPixelRect(header, target.Bounds, scaleX, scaleY, pixelWidth, pixelHeight);
            if (pixelHeader.Width <= 0 || pixelHeader.Height <= 0) continue;
            candidates.Add((pixelHeader, PanelHeaderClass,
                "Adobe collapsible panel " + VisualFingerprint(bgra, stride, pixelHeader)));
        }
        for (var first = 0; first < components.Count; first++)
        {
            for (var second = first + 1; second < components.Count; second++)
            {
                if (!IsDoubleChevronPair(components[first], components[second])) continue;
                paired.Add(first);
                paired.Add(second);
                var union = Union(components[first].Bounds, components[second].Bounds);
                candidates.Add((Expand(union, 5, pixelWidth, pixelHeight), OverflowClass, "More panels"));
                break;
            }
        }

        for (var index = 0; index < components.Count; index++)
        {
            if (paired.Contains(index)) continue;
            var centerX = components[index].Bounds.X + components[index].Bounds.Width / 2;
            var centerY = components[index].Bounds.Y + components[index].Bounds.Height / 2;
            var matchesPanelEdge = panelBounds.Any(panel =>
            {
                var left = ToPixelX(panel.X, target.Bounds, scaleX, pixelWidth);
                var right = ToPixelX(panel.X + panel.Width, target.Bounds, scaleX, pixelWidth);
                var top = ToPixelY(panel.Y, target.Bounds, scaleY, pixelHeight);
                var bottom = ToPixelY(panel.Y + panel.Height, target.Bounds, scaleY, pixelHeight);
                var leftEdge = Math.Clamp((int)Math.Round(30 * scaleX), 16, 48);
                var rightEdge = Math.Clamp((int)Math.Round(72 * scaleX), 36, 110);
                return centerY >= top && centerY < bottom &&
                       (centerX >= left && centerX <= left + leftEdge || centerX >= right - rightEdge && centerX <= right);
            });
            if (!matchesPanelEdge) continue;
            var matchesNarrowToolPanel = panelBounds.Any(panel =>
            {
                var pixelPanel = ToPixelRect(panel, target.Bounds, scaleX, scaleY, pixelWidth, pixelHeight);
                return pixelPanel.Width is >= 35 and <= 100 && pixelPanel.Height >= 100 &&
                       centerX >= pixelPanel.X && centerX < pixelPanel.X + pixelPanel.Width &&
                       centerY >= pixelPanel.Y && centerY < pixelPanel.Y + pixelPanel.Height;
            });
            var direction = components[index].Direction.ToString().ToLowerInvariant();
            candidates.Add((
                Expand(components[index].Bounds, 5, pixelWidth, pixelHeight),
                matchesNarrowToolPanel ? ToolDisclosureClass : DisclosureClass,
                matchesNarrowToolPanel ? $"Tool flyout chevron {direction}" : $"Disclosure chevron {direction}"));
        }

        var collapsedHeaders = legacyControls
            .Where(control => control.ControlType == "ControlType.Window" &&
                              control.Name.Equals("OS_ViewContainer", StringComparison.OrdinalIgnoreCase) &&
                              control.Bounds.Width >= 180 && control.Bounds.Height is >= 28 and <= 48)
            .Select(control => ToPixelRect(control.Bounds, target.Bounds, scaleX, scaleY, pixelWidth, pixelHeight))
            .ToArray();
        foreach (var panel in panelBounds)
        {
            if (panel.Width < 150 || panel.Height < 220) continue;
            var pixelPanel = ToPixelRect(panel, target.Bounds, scaleX, scaleY, pixelWidth, pixelHeight);
            var disclosureCount = components.Count(component => Contains(pixelPanel, component.Bounds));
            var headerCount = collapsedHeaders.Count(header => Contains(pixelPanel, header));
            if (disclosureCount < 3 && headerCount < 3) continue;
            var scrollBounds = new RectI(
                pixelPanel.X + 6,
                pixelPanel.Y + Math.Clamp((int)Math.Round(42 * scaleY), 30, 62),
                Math.Max(1, pixelPanel.Width - 12),
                Math.Max(1, pixelPanel.Height - Math.Clamp((int)Math.Round(52 * scaleY), 38, 76)));
            if (scrollBounds.Width <= 0 || scrollBounds.Height < 80) continue;
            scrollCandidates.Add(scrollBounds);
        }
        var retainedScrollRegions = new List<RectI>();
        foreach (var scrollBounds in scrollCandidates.OrderBy(bounds => (long)bounds.Width * bounds.Height))
        {
            if (retainedScrollRegions.Any(existing => OverlapRatio(existing, scrollBounds) >= 0.75)) continue;
            retainedScrollRegions.Add(scrollBounds);
            candidates.Add((scrollBounds, ScrollRegionClass,
                "Adobe scrollable panel " + VisualFingerprint(bgra, stride, scrollBounds)));
        }

        // Owner-drawn trees in Premiere do not expose their row chevrons through
        // UIA. A cluster of three or more disclosure glyphs inside one panel is a
        // tree/section list: retain every glyph as a Button, but never auto-click it.
        var treePanels = panelBounds
            .Select(panel => ToPixelRect(panel, target.Bounds, scaleX, scaleY, pixelWidth, pixelHeight))
            .Where(panel => candidates.Count(candidate =>
                candidate.ClassName == DisclosureClass && Contains(panel, candidate.Bounds)) >= 3)
            .ToArray();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.ClassName != DisclosureClass ||
                !treePanels.Any(panel => Contains(panel, candidate.Bounds))) continue;
            candidates[index] = (candidate.Bounds, TreeItemButtonClass,
                candidate.Name.Replace("Disclosure chevron", "Tree item button", StringComparison.Ordinal));
        }

        var visualMask = BuildVisualControlMask(panelBounds, target.Bounds, pixelWidth, pixelHeight, scaleX, scaleY);
        var rawVisualComponents = FindComponents(bgra, pixelWidth, pixelHeight, stride, visualMask, 70);
        var visualComponents = MergeNearbyComponents(
            rawVisualComponents,
            Math.Clamp((int)Math.Round(4 * Math.Max(scaleX, scaleY)), 3, 8));
        var textRuns = MergeHorizontalTextRuns(
                rawVisualComponents,
                Math.Clamp((int)Math.Round(9 * scaleX), 6, 14))
            .Concat(FindPanelTabRegions(
                bgra, stride, panelBounds, target.Bounds,
                pixelWidth, pixelHeight, scaleX, scaleY))
            .GroupBy(component => $"{component.Bounds.X / 6}:{component.Bounds.Y / 6}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(component => component.Bounds.Width).First())
            .ToArray();
        var tabIndex = 0;
        foreach (var run in textRuns.Where(component => IsPanelTabRun(
                     component.Bounds, component.Area, panelBounds, target.Bounds,
                     pixelWidth, pixelHeight, scaleX, scaleY)))
        {
            var expanded = Expand(run.Bounds, 5, pixelWidth, pixelHeight);
            // A panel's overflow chevrons are often only a few pixels after the final tab label.
            // Their padded hit targets may overlap even though the label remains a separate control.
            // Deduplicate tabs only against other tab/header text, never against the adjacent overflow.
            if (candidates.Any(candidate =>
                    candidate.ClassName is PanelTabClass or PanelHeaderClass &&
                    OverlapRatio(candidate.Bounds, expanded) >= 0.30)) continue;
            candidates.Add((expanded, PanelTabClass,
                $"Adobe panel tab {++tabIndex} {VisualFingerprint(bgra, stride, expanded)}"));
        }

        foreach (var component in FindPanelMenuRegions(
                     bgra, stride, panelBounds, target.Bounds,
                     pixelWidth, pixelHeight, scaleX, scaleY))
        {
            var expanded = Expand(component.Bounds, 6, pixelWidth, pixelHeight);
            if (candidates.Any(candidate => OverlapRatio(candidate.Bounds, expanded) >= 0.30)) continue;
            candidates.Add((expanded, PanelMenuClass, "Panel menu"));
        }

        var visualIndex = 0;
        foreach (var component in visualComponents)
        {
            var maxWidth = Math.Clamp((int)Math.Round(30 * scaleX), 22, 48);
            var maxHeight = Math.Clamp((int)Math.Round(32 * scaleY), 22, 52);
            if (component.Bounds.Width is < 4 || component.Bounds.Height is < 4 ||
                component.Bounds.Width > maxWidth || component.Bounds.Height > maxHeight || component.Area < 5)
                continue;
            var expanded = Expand(component.Bounds, 4, pixelWidth, pixelHeight);
            if (candidates.Any(candidate => OverlapRatio(candidate.Bounds, expanded) >= 0.30)) continue;
            candidates.Add((expanded, VisualControlClass, $"Adobe panel control {++visualIndex}"));
        }

        return candidates
            .Select(candidate => ToObservation(
                target,
                candidate.Bounds,
                candidate.ClassName,
                candidate.ClassName == PanelHeaderClass ? "Adobe collapsible panel" : candidate.Name,
                candidate.ClassName == PanelHeaderClass ? candidate.Name : null,
                pixelWidth, pixelHeight, scaleX, scaleY))
            .GroupBy(control => $"{control.ClassName}:{control.Bounds.X / 6}:{control.Bounds.Y / 6}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(control => control.ClassName == OverflowClass ? 0 : 1)
            .ThenBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .ToArray();
    }

    public static bool IsOverflow(AutomationObservation observation) =>
        observation.ClassName.Equals(OverflowClass, StringComparison.Ordinal);

    public static bool IsSafeDisclosure(AutomationObservation observation) =>
        observation.ClassName.Equals(OverflowClass, StringComparison.Ordinal) ||
        IsCollapsedDisclosure(observation) ||
        observation.ClassName.Equals(PanelHeaderClass, StringComparison.Ordinal) ||
        observation.ClassName.Equals(PanelTabClass, StringComparison.Ordinal) ||
        observation.ClassName.Equals(PanelMenuClass, StringComparison.Ordinal) ||
        IsApplicationMenu(observation) ||
        IsWorkspaceTab(observation);

    public static bool IsTreeItemButton(AutomationObservation observation) =>
        observation.ClassName.Equals(TreeItemButtonClass, StringComparison.Ordinal);

    public static bool IsCollapsedDisclosure(AutomationObservation observation) =>
        observation.ClassName.Equals(DisclosureClass, StringComparison.Ordinal) &&
        observation.Name.EndsWith(" right", StringComparison.OrdinalIgnoreCase);

    public static bool IsPanelHeader(AutomationObservation observation) =>
        observation.ClassName.Equals(PanelHeaderClass, StringComparison.Ordinal);

    public static bool IsPanelTab(AutomationObservation observation) =>
        observation.ClassName.Equals(PanelTabClass, StringComparison.Ordinal);

    public static bool IsApplicationMenu(AutomationObservation observation) =>
        observation.ClassName.Equals(ApplicationMenuClass, StringComparison.Ordinal);

    public static bool IsWorkspaceTab(AutomationObservation observation) =>
        observation.ClassName.Equals(WorkspaceTabClass, StringComparison.Ordinal);

    public static bool IsTransientMenu(AutomationObservation observation) =>
        IsOverflow(observation) ||
        observation.ClassName.Equals(PanelMenuClass, StringComparison.Ordinal) ||
        IsApplicationMenu(observation);

    public static bool IsScrollRegion(AutomationObservation observation) =>
        observation.ClassName.Equals(ScrollRegionClass, StringComparison.Ordinal);

    private static AutomationObservation ToObservation(
        WindowTarget target,
        RectI pixelBounds,
        string className,
        string name,
        string? stableIdentity,
        int pixelWidth,
        int pixelHeight,
        double scaleX,
        double scaleY)
    {
        var x = target.Bounds.X + (int)Math.Round(pixelBounds.X / scaleX);
        var y = target.Bounds.Y + (int)Math.Round(pixelBounds.Y / scaleY);
        var width = Math.Max(10, (int)Math.Round(pixelBounds.Width / scaleX));
        var height = Math.Max(14, (int)Math.Round(pixelBounds.Height / scaleY));
        var runtimeSuffix = string.IsNullOrWhiteSpace(stableIdentity)
            ? $"{x:x}.{y:x}"
            : stableIdentity[(stableIdentity.LastIndexOf(' ') + 1)..];
        var runtime = $"{target.Hwnd:x}.adobe-disclosure.{runtimeSuffix}";
        return new(runtime, "", runtime, name, "ControlType.Button", className,
            new RectI(x, y, width, height), true, false, "Visual", target.Hwnd,
            ["ExpandCollapsePatternIdentifiers.Pattern"]);
    }

    private static List<Component> FindRightChevrons(
        byte[] pixels, int width, int height, int stride, bool[] mask)
    {
        var bright = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!mask[index]) continue;
                var offset = row + x * 4;
                var luminance = (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
                var topBand = y >= (int)Math.Round(height * 0.055) && y < (int)Math.Round(height * 0.12);
                bright[index] = luminance >= (topBand ? 82 : 105);
            }
        }

        var visited = new bool[mask.Length];
        var result = new List<Component>();
        var queue = new Queue<int>();
        for (var start = 0; start < bright.Length; start++)
        {
            if (!bright[start] || visited[start]) continue;
            visited[start] = true;
            queue.Enqueue(start);
            var points = new List<(int X, int Y)>(24);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                points.Add((x, y));
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (!bright[next] || visited[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            if (TryCreateChevron(points, out var component)) result.Add(component);
        }
        return result;
    }

    private static List<Component> FindComponents(
        byte[] pixels, int width, int height, int stride, bool[] mask, int threshold, bool darkForeground = false)
    {
        var bright = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!mask[index]) continue;
                var offset = row + x * 4;
                var luminance = (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
                bright[index] = darkForeground ? luminance <= threshold : luminance >= threshold;
            }
        }

        var visited = new bool[mask.Length];
        var result = new List<Component>();
        var queue = new Queue<int>();
        for (var start = 0; start < bright.Length; start++)
        {
            if (!bright[start] || visited[start]) continue;
            visited[start] = true;
            queue.Enqueue(start);
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            var area = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                area++;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (!bright[next] || visited[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            if (area >= 2 && maxX - minX < 80 && maxY - minY < 80)
                result.Add(new(new RectI(minX, minY, maxX - minX + 1, maxY - minY + 1), area));
        }
        return result;
    }

    private static bool[] BuildVisualControlMask(
        IReadOnlyList<RectI> panels,
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight,
        double scaleX,
        double scaleY)
    {
        var mask = new bool[checked(pixelWidth * pixelHeight)];
        foreach (var panel in panels)
        {
            var left = ToPixelX(panel.X, windowBounds, scaleX, pixelWidth);
            var right = ToPixelX(panel.X + panel.Width, windowBounds, scaleX, pixelWidth);
            var top = ToPixelY(panel.Y, windowBounds, scaleY, pixelHeight);
            var bottom = ToPixelY(panel.Y + panel.Height, windowBounds, scaleY, pixelHeight);
            if (right - left is >= 35 and <= 100 && bottom - top >= 100)
            {
                MarkRegion(mask, pixelWidth, pixelHeight, left + 2, top + 2, right - 2, bottom - 2);
                continue;
            }
            if (right - left < 150 || bottom - top < 100) continue;
            var topBandHeight = Math.Clamp((int)Math.Round(72 * scaleY), 42, 110);
            var bottomBandHeight = Math.Clamp((int)Math.Round(56 * scaleY), 34, 90);
            MarkRegion(mask, pixelWidth, pixelHeight, left + 3, top + 3, right - 3, Math.Min(bottom, top + topBandHeight));
            MarkRegion(mask, pixelWidth, pixelHeight, left + 3, Math.Max(top, bottom - bottomBandHeight), right - 3, bottom - 3);
        }
        return mask;
    }

    private static IReadOnlyList<Component> MergeNearbyComponents(IReadOnlyList<Component> input, int gap)
    {
        var remaining = new HashSet<int>(Enumerable.Range(0, input.Count));
        var merged = new List<Component>();
        while (remaining.Count > 0)
        {
            var seed = remaining.First();
            remaining.Remove(seed);
            var bounds = input[seed].Bounds;
            var area = input[seed].Area;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var index in remaining.ToArray())
                {
                    if (!AreNear(bounds, input[index].Bounds, gap)) continue;
                    bounds = Union(bounds, input[index].Bounds);
                    area += input[index].Area;
                    remaining.Remove(index);
                    changed = true;
                }
            }
            merged.Add(new(bounds, area));
        }
        return merged;
    }

    private static IReadOnlyList<Component> MergeHorizontalTextRuns(IReadOnlyList<Component> input, int gap)
    {
        var fragments = input
            .Where(component => component.Bounds.Width is >= 1 and <= 48 &&
                                component.Bounds.Height is >= 3 and <= 30)
            .OrderBy(component => component.Bounds.X)
            .ThenBy(component => component.Bounds.Y)
            .ToList();
        var remaining = new HashSet<int>(Enumerable.Range(0, fragments.Count));
        var merged = new List<Component>();
        while (remaining.Count > 0)
        {
            var seed = remaining
                .OrderBy(index => fragments[index].Bounds.X)
                .ThenBy(index => fragments[index].Bounds.Y)
                .First();
            remaining.Remove(seed);
            var bounds = fragments[seed].Bounds;
            var area = fragments[seed].Area;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var index in remaining.ToArray())
                {
                    var candidate = fragments[index].Bounds;
                    var horizontalGap = Math.Max(0, candidate.X - (bounds.X + bounds.Width));
                    var verticalOverlap = Math.Max(0,
                        Math.Min(bounds.Y + bounds.Height, candidate.Y + candidate.Height) - Math.Max(bounds.Y, candidate.Y));
                    var minimumHeight = Math.Max(1, Math.Min(bounds.Height, candidate.Height));
                    var baselineDifference = Math.Abs(
                        bounds.Y + bounds.Height - (candidate.Y + candidate.Height));
                    if (candidate.X < bounds.X || horizontalGap > gap ||
                        verticalOverlap < minimumHeight * 0.20 && baselineDifference > 5) continue;
                    bounds = Union(bounds, candidate);
                    area += fragments[index].Area;
                    remaining.Remove(index);
                    changed = true;
                }
            }
            merged.Add(new(bounds, area));
        }
        return merged;
    }

    private static IReadOnlyList<Component> FindTopTextRegions(
        byte[] pixels,
        int width,
        int height,
        int stride,
        double topRatio,
        double bottomRatio,
        bool darkForeground)
    {
        var top = Math.Clamp((int)Math.Round(height * topRatio), 0, height - 1);
        var bottom = Math.Clamp((int)Math.Round(height * bottomRatio), top + 1, height);
        var mask = new bool[checked(width * height)];
        MarkRegion(mask, width, height, 0, top, width, bottom);
        var fragments = FindComponents(
            pixels, width, height, stride, mask,
            darkForeground ? 175 : 70,
            darkForeground);
        var gap = Math.Clamp((int)Math.Round(width / 190d), 6, 14);
        return MergeHorizontalTextRuns(fragments, gap)
            .Where(component =>
                component.Bounds.Width is >= 18 and <= 240 &&
                component.Bounds.Height is >= 5 and <= 28 &&
                component.Bounds.Width >= component.Bounds.Height * 1.25 &&
                component.Area >= 10)
            .OrderBy(component => component.Bounds.X)
            .ToArray();
    }

    private static bool IsPanelTabRun(
        RectI bounds,
        int area,
        IReadOnlyList<RectI> panels,
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight,
        double scaleX,
        double scaleY)
    {
        if (bounds.Width is < 24 or > 230 || bounds.Height is < 6 or > 28 ||
            bounds.Width < bounds.Height * 1.35 || area < 14)
            return false;

        return panels.Any(panel =>
        {
            if (panel.Width < 150 || panel.Height < 100) return false;
            var pixelPanel = ToPixelRect(panel, windowBounds, scaleX, scaleY, pixelWidth, pixelHeight);
            var headerBottom = pixelPanel.Y + Math.Clamp((int)Math.Round(48 * scaleY), 32, 70);
            return bounds.X >= pixelPanel.X + 5 && bounds.X + bounds.Width <= pixelPanel.X + pixelPanel.Width - 30 &&
                   bounds.Y >= pixelPanel.Y + 4 && bounds.Y + bounds.Height <= headerBottom;
        });
    }

    private static IReadOnlyList<Component> FindPanelTabRegions(
        byte[] pixels,
        int stride,
        IReadOnlyList<RectI> panels,
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight,
        double scaleX,
        double scaleY)
    {
        var result = new List<Component>();
        foreach (var panel in panels)
        {
            if (panel.Width < 150 || panel.Height < 100) continue;
            var pixelPanel = ToPixelRect(panel, windowBounds, scaleX, scaleY, pixelWidth, pixelHeight);
            var left = Math.Max(0, pixelPanel.X + 5);
            var right = Math.Min(pixelWidth, pixelPanel.X + pixelPanel.Width - 30);
            var top = Math.Max(0, pixelPanel.Y + 4);
            var bottom = Math.Min(pixelHeight, pixelPanel.Y +
                Math.Clamp((int)Math.Round(38 * scaleY), 28, 54));
            if (right <= left || bottom <= top) continue;

            var activeColumns = new bool[right - left];
            for (var x = left; x < right; x++)
            {
                var bright = 0;
                for (var y = top; y < bottom; y++)
                {
                    var offset = y * stride + x * 4;
                    var luminance = (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
                    if (luminance >= 62) bright++;
                }
                activeColumns[x - left] = bright >= 2;
            }

            var start = -1;
            var lastActive = -1;
            for (var localX = 0; localX <= activeColumns.Length; localX++)
            {
                if (localX < activeColumns.Length && activeColumns[localX])
                {
                    if (start < 0) start = localX;
                    lastActive = localX;
                    continue;
                }
                if (start < 0 || localX - lastActive <= 10) continue;
                AddTabRun(start, lastActive);
                start = -1;
                lastActive = -1;
            }
            if (start >= 0) AddTabRun(start, lastActive);

            void AddTabRun(int localStart, int localEnd)
            {
                var runLeft = left + localStart;
                var runRight = left + localEnd + 1;
                if (runRight - runLeft is < 24 or > 230) return;
                var points = new List<(int X, int Y)>();
                for (var y = top; y < bottom; y++)
                for (var x = runLeft; x < runRight; x++)
                {
                    var offset = y * stride + x * 4;
                    var luminance = (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
                    if (luminance >= 62) points.Add((x, y));
                }
                if (points.Count < 14) return;
                var bounds = new RectI(
                    runLeft,
                    points.Min(point => point.Y),
                    runRight - runLeft,
                    points.Max(point => point.Y) - points.Min(point => point.Y) + 1);
                if (bounds.Height is >= 4 and <= 28) result.Add(new(bounds, points.Count));
            }
        }
        return result;
    }

    private static IReadOnlyList<Component> FindPanelMenuRegions(
        byte[] pixels,
        int stride,
        IReadOnlyList<RectI> panels,
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight,
        double scaleX,
        double scaleY)
    {
        var result = new List<Component>();
        foreach (var panel in panels)
        {
            if (panel.Width < 150 || panel.Height < 100) continue;
            var pixelPanel = ToPixelRect(panel, windowBounds, scaleX, scaleY, pixelWidth, pixelHeight);
            var left = Math.Max(pixelPanel.X, pixelPanel.X + pixelPanel.Width -
                Math.Clamp((int)Math.Round(42 * scaleX), 28, 58));
            var right = Math.Min(pixelWidth, pixelPanel.X + pixelPanel.Width - 2);
            var top = Math.Max(0, pixelPanel.Y + 3);
            var bottom = Math.Min(pixelHeight, pixelPanel.Y +
                Math.Clamp((int)Math.Round(44 * scaleY), 30, 64));
            if (right <= left || bottom <= top) continue;

            var lineGroups = 0;
            var insideLine = false;
            var menuPoints = new List<(int X, int Y)>();
            for (var y = top; y < bottom; y++)
            {
                var bestStart = -1;
                var bestLength = 0;
                var runStart = -1;
                for (var x = left; x < right; x++)
                {
                    var offset = y * stride + x * 4;
                    var luminance = (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
                    if (luminance >= 52)
                    {
                        if (runStart < 0) runStart = x;
                        continue;
                    }
                    if (runStart < 0) continue;
                    if (x - runStart > bestLength) (bestStart, bestLength) = (runStart, x - runStart);
                    runStart = -1;
                }
                if (runStart >= 0 && right - runStart > bestLength)
                    (bestStart, bestLength) = (runStart, right - runStart);
                var line = bestLength is >= 4 and <= 24;
                if (line && !insideLine) lineGroups++;
                if (line)
                    for (var x = bestStart; x < bestStart + bestLength; x++) menuPoints.Add((x, y));
                insideLine = line;
            }
            if (lineGroups < 3 || menuPoints.Count < 12) continue;
            var bounds = new RectI(
                menuPoints.Min(point => point.X),
                menuPoints.Min(point => point.Y),
                menuPoints.Max(point => point.X) - menuPoints.Min(point => point.X) + 1,
                menuPoints.Max(point => point.Y) - menuPoints.Min(point => point.Y) + 1);
            if (bounds.Width > 30 || bounds.Height > 30) continue;
            result.Add(new(bounds, menuPoints.Count));
        }
        return result;
    }

    private static bool AreNear(RectI first, RectI second, int gap)
    {
        var horizontalGap = Math.Max(0, Math.Max(first.X, second.X) - Math.Min(first.X + first.Width, second.X + second.Width));
        var verticalGap = Math.Max(0, Math.Max(first.Y, second.Y) - Math.Min(first.Y + first.Height, second.Y + second.Height));
        return horizontalGap <= gap && verticalGap <= gap;
    }

    private static double OverlapRatio(RectI first, RectI second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var smaller = Math.Min((long)first.Width * first.Height, (long)second.Width * second.Height);
        return smaller == 0 ? 0 : intersection / (double)smaller;
    }

    private static bool TryCreateRightChevron(IReadOnlyList<(int X, int Y)> points, out Component component)
    {
        component = default;
        if (points.Count is < 7 or > 90) return false;
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        if (width is < 3 or > 14 || height is < 5 or > 20 || height < width * 0.85 || height > width * 3.5)
            return false;

        var middle = minY + height / 2.0;
        var top = points.Where(point => point.Y <= middle - 1).Average(point => point.X);
        var centerPoints = points.Where(point => Math.Abs(point.Y - middle) <= Math.Max(1, height / 5.0)).ToArray();
        var bottom = points.Where(point => point.Y >= middle + 1).Average(point => point.X);
        if (centerPoints.Length == 0) return false;
        var center = centerPoints.Average(point => point.X);
        var leftEdgeRows = points.Where(point => point.X == minX).Select(point => point.Y).Distinct().Count();
        if (center < top + Math.Max(0.45, width * 0.08) || center < bottom + Math.Max(0.45, width * 0.08) ||
            leftEdgeRows > height * 0.78)
            return false;

        component = new(new RectI(minX, minY, width, height), points.Count);
        return true;
    }

    private static bool TryCreateChevron(IReadOnlyList<(int X, int Y)> points, out Component component)
    {
        component = default;
        if (points.Count == 0) return false;
        var bounds = new RectI(
            points.Min(point => point.X),
            points.Min(point => point.Y),
            points.Max(point => point.X) - points.Min(point => point.X) + 1,
            points.Max(point => point.Y) - points.Min(point => point.Y) + 1);
        (IReadOnlyList<(int X, int Y)> Points, ChevronDirection Direction)[] orientations =
        [
            (points, ChevronDirection.Right),
            (points.Select(point => (-point.X, point.Y)).ToArray(), ChevronDirection.Left),
            (points.Select(point => (point.Y, point.X)).ToArray(), ChevronDirection.Down),
            (points.Select(point => (-point.Y, point.X)).ToArray(), ChevronDirection.Up)
        ];
        foreach (var orientation in orientations)
        {
            if (!TryCreateRightChevron(orientation.Points, out _)) continue;
            component = new(bounds, points.Count, orientation.Direction);
            return true;
        }
        return false;
    }

    private static bool IsDoubleChevronPair(Component first, Component second)
    {
        if (first.Direction != ChevronDirection.Right || second.Direction != ChevronDirection.Right) return false;
        var left = first.Bounds.X <= second.Bounds.X ? first.Bounds : second.Bounds;
        var right = first.Bounds.X <= second.Bounds.X ? second.Bounds : first.Bounds;
        var gap = right.X - (left.X + left.Width);
        var centerDifference = Math.Abs((left.Y + left.Height / 2.0) - (right.Y + right.Height / 2.0));
        return gap is >= 0 and <= 14 && centerDifference <= 4 &&
               Math.Abs(left.Width - right.Width) <= 4 && Math.Abs(left.Height - right.Height) <= 4;
    }

    private static RectI Union(RectI first, RectI second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
        return new(left, top, right - left, bottom - top);
    }

    private static bool Contains(RectI outer, RectI inner)
    {
        var centerX = inner.X + inner.Width / 2;
        var centerY = inner.Y + inner.Height / 2;
        return centerX >= outer.X && centerX < outer.X + outer.Width &&
               centerY >= outer.Y && centerY < outer.Y + outer.Height;
    }

    private static RectI Expand(RectI value, int padding, int width, int height)
    {
        var left = Math.Max(0, value.X - padding);
        var top = Math.Max(0, value.Y - padding);
        var right = Math.Min(width, value.X + value.Width + padding);
        var bottom = Math.Min(height, value.Y + value.Height + padding);
        return new(left, top, right - left, bottom - top);
    }

    private static void MarkRegion(bool[] mask, int width, int height, int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, width);
        right = Math.Clamp(right, left, width);
        top = Math.Clamp(top, 0, height);
        bottom = Math.Clamp(bottom, top, height);
        for (var y = top; y < bottom; y++)
            Array.Fill(mask, true, y * width + left, right - left);
    }

    private static int ToPixelX(int screenX, RectI bounds, double scale, int width) =>
        Math.Clamp((int)Math.Round((screenX - bounds.X) * scale), 0, width);

    private static int ToPixelY(int screenY, RectI bounds, double scale, int height) =>
        Math.Clamp((int)Math.Round((screenY - bounds.Y) * scale), 0, height);

    private static RectI ToPixelRect(
        RectI screenBounds,
        RectI windowBounds,
        double scaleX,
        double scaleY,
        int pixelWidth,
        int pixelHeight)
    {
        var left = ToPixelX(screenBounds.X, windowBounds, scaleX, pixelWidth);
        var top = ToPixelY(screenBounds.Y, windowBounds, scaleY, pixelHeight);
        var right = ToPixelX(screenBounds.X + screenBounds.Width, windowBounds, scaleX, pixelWidth);
        var bottom = ToPixelY(screenBounds.Y + screenBounds.Height, windowBounds, scaleY, pixelHeight);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static string VisualFingerprint(byte[] pixels, int stride, RectI bounds)
    {
        using var data = new MemoryStream();
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y += 2)
        for (var x = bounds.X; x < bounds.X + bounds.Width; x += 2)
        {
            var offset = y * stride + x * 4;
            var luminance = (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
            data.WriteByte((byte)(luminance / 24));
        }
        return Convert.ToHexString(SHA256.HashData(data.ToArray()))[..12].ToLowerInvariant();
    }

    private enum ChevronDirection { None, Right, Left, Down, Up }

    private readonly record struct Component(
        RectI Bounds,
        int Area,
        ChevronDirection Direction = ChevronDirection.None);
}

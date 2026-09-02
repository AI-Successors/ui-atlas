using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AdobePremiereDisclosureDiscoveryTests
{
    [Fact]
    public void DiscoverFindsWorkspaceOverflowAndPanelDisclosureWithoutPromotingText()
    {
        const int width = 240;
        const int height = 200;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, 32);
        DrawRightChevron(pixels, width, stride, 150, 14);
        DrawRightChevron(pixels, width, stride, 156, 14);
        DrawRightChevron(pixels, width, stride, 126, 72);

        var target = PremiereTarget(new RectI(0, 0, width, height));
        var pane = new AutomationObservation(
            "pane", "", "", "OS_ViewContainer", "ControlType.Window", "DroverLord - Window Class",
            new RectI(115, 50, 110, 120), true, false, "Win32", target.Hwnd);
        var collapsedPanel = new AutomationObservation(
            "collapsed", "", "", "OS_ViewContainer", "ControlType.Window", "MSAA.Role9",
            new RectI(10, 145, 200, 38), true, false, "Win32", target.Hwnd);

        var result = AdobePremiereDisclosureDiscovery.Discover(
            target, [pane, collapsedPanel], pixels, width, height, stride);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, AdobePremiereDisclosureDiscovery.IsOverflow);
        Assert.Contains(result, AdobePremiereDisclosureDiscovery.IsPanelHeader);
        Assert.Contains(result, item => item.ClassName == "AdobeVisualDisclosure");
        Assert.All(result, item => Assert.Equal("ControlType.Button", item.ControlType));
    }

    [Fact]
    public void IsSupportedRequiresAdobePremiereIdentity()
    {
        Assert.True(AdobePremiereDisclosureDiscovery.IsSupported(PremiereTarget(new RectI(0, 0, 200, 100))));
        Assert.False(AdobePremiereDisclosureDiscovery.IsSupported(PremiereTarget(
            new RectI(0, 0, 200, 100)) with
        {
            ProcessName = "excel", Title = "Book1 - Excel", CompanyName = "Microsoft", ProductName = "Microsoft Excel"
        }));
    }

    [Fact]
    public void DiscoverClassifiesPanelTabsMenusAndPassiveToolButtons()
    {
        const int width = 360;
        const int height = 220;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, 32);

        // Disconnected letter-like strokes in a panel title/tab.
        DrawRect(pixels, stride, 28, 31, 5, 12);
        DrawRect(pixels, stride, 38, 31, 4, 12);
        DrawRect(pixels, stride, 47, 31, 7, 12);
        DrawRect(pixels, stride, 59, 31, 5, 12);
        DrawRect(pixels, stride, 69, 31, 8, 12);
        // Premiere's three-line panel menu.
        DrawRect(pixels, stride, 242, 31, 12, 2);
        DrawRect(pixels, stride, 242, 36, 12, 2);
        DrawRect(pixels, stride, 242, 41, 12, 2);
        // A tool icon in a narrow vertical toolbar: retain, but never auto-click.
        DrawRect(pixels, stride, 294, 92, 14, 14);
        DrawRightChevron(pixels, width, stride, 312, 130);

        var target = PremiereTarget(new RectI(0, 0, width, height));
        AutomationObservation[] panels =
        [
            Panel("main", new RectI(10, 20, 255, 180), target.Hwnd),
            Panel("tools", new RectI(280, 20, 45, 180), target.Hwnd)
        ];

        var result = AdobePremiereDisclosureDiscovery.Discover(
            target, panels, pixels, width, height, stride);

        var tab = Assert.Single(result, AdobePremiereDisclosureDiscovery.IsPanelTab);
        Assert.True(AdobePremiereDisclosureDiscovery.IsSafeDisclosure(tab));
        var menu = Assert.Single(result, item =>
            AdobePremiereDisclosureDiscovery.IsTransientMenu(item) &&
            !AdobePremiereDisclosureDiscovery.IsOverflow(item));
        Assert.True(AdobePremiereDisclosureDiscovery.IsSafeDisclosure(menu));
        Assert.Contains(result, item =>
            item.ClassName == "AdobeVisualControl" &&
            !AdobePremiereDisclosureDiscovery.IsSafeDisclosure(item));
        Assert.Contains(result, item =>
            item.ClassName == "AdobeToolDisclosure" &&
            !AdobePremiereDisclosureDiscovery.IsSafeDisclosure(item));
    }

    [Fact]
    public void DiscoverFindsVerticalDisclosureChevron()
    {
        const int width = 240;
        const int height = 200;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, 32);
        DrawDownChevron(pixels, stride, 202, 88);
        var target = PremiereTarget(new RectI(0, 0, width, height));

        var result = AdobePremiereDisclosureDiscovery.Discover(
            target,
            [Panel("panel", new RectI(80, 45, 140, 130), target.Hwnd)],
            pixels, width, height, stride);

        var expanded = Assert.Single(result, item => item.ClassName == "AdobeVisualDisclosure");
        Assert.False(AdobePremiereDisclosureDiscovery.IsSafeDisclosure(expanded));
    }

    [Fact]
    public void DiscoverCreatesScrollableRegionForNestedTree()
    {
        const int width = 320;
        const int height = 300;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, 32);
        DrawRightChevron(pixels, width, stride, 266, 90);
        DrawRightChevron(pixels, width, stride, 266, 130);
        DrawRightChevron(pixels, width, stride, 266, 170);
        var target = PremiereTarget(new RectI(0, 0, width, height));

        var result = AdobePremiereDisclosureDiscovery.Discover(
            target,
            [Panel("tree", new RectI(40, 30, 250, 250), target.Hwnd)],
            pixels, width, height, stride);

        Assert.Equal(3, result.Count(AdobePremiereDisclosureDiscovery.IsTreeItemButton));
        Assert.All(result.Where(AdobePremiereDisclosureDiscovery.IsTreeItemButton), item =>
        {
            Assert.Equal("ControlType.Button", item.ControlType);
            Assert.False(AdobePremiereDisclosureDiscovery.IsSafeDisclosure(item));
        });
        Assert.Single(result, AdobePremiereDisclosureDiscovery.IsScrollRegion);
    }

    [Fact]
    public void DiscoverFindsTopApplicationMenusAndWorkspaceTabs()
    {
        const int width = 800;
        const int height = 400;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, 32);
        DrawRect(pixels, stride, 0, 10, width, 16, 245);
        DrawTextWord(pixels, stride, 14, 14, 4, 32);
        DrawTextWord(pixels, stride, 62, 14, 5, 32);
        DrawTextWord(pixels, stride, 250, 30, 7, 180);
        DrawTextWord(pixels, stride, 340, 30, 8, 180);
        DrawTextWord(pixels, stride, 450, 30, 6, 180);
        var target = PremiereTarget(new RectI(0, 0, width, height));

        var result = AdobePremiereDisclosureDiscovery.Discover(
            target, [], pixels, width, height, stride);

        Assert.Equal(2, result.Count(AdobePremiereDisclosureDiscovery.IsApplicationMenu));
        Assert.Equal(3, result.Count(AdobePremiereDisclosureDiscovery.IsWorkspaceTab));
        Assert.All(result, item => Assert.True(AdobePremiereDisclosureDiscovery.IsSafeDisclosure(item)));
        Assert.All(result.Where(AdobePremiereDisclosureDiscovery.IsApplicationMenu),
            item => Assert.True(AdobePremiereDisclosureDiscovery.IsTransientMenu(item)));
        Assert.All(result.Where(AdobePremiereDisclosureDiscovery.IsWorkspaceTab),
            item => Assert.False(AdobePremiereDisclosureDiscovery.IsTransientMenu(item)));
    }

    [Fact]
    public void DiscoverKeepsAdjacentPanelTabsAndOverflowAsSeparateControls()
    {
        const int width = 360;
        const int height = 240;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, 32);
        DrawTextWord(pixels, stride, 24, 92, 7, 180);
        DrawTextWord(pixels, stride, 105, 92, 14, 180);
        DrawRightChevron(pixels, width, stride, 190, 92);
        DrawRightChevron(pixels, width, stride, 196, 92);
        var target = PremiereTarget(new RectI(0, 0, width, height));

        var result = AdobePremiereDisclosureDiscovery.Discover(
            target,
            [Panel("project", new RectI(10, 80, 220, 140), target.Hwnd)],
            pixels, width, height, stride);

        Assert.True(result.Count(AdobePremiereDisclosureDiscovery.IsPanelTab) >= 2);
        Assert.Contains(result, AdobePremiereDisclosureDiscovery.IsOverflow);
    }

    private static WindowTarget PremiereTarget(RectI bounds) => new(
        100, 100, 7, "Adobe Premiere Pro", DateTimeOffset.UnixEpoch,
        "Adobe Premiere Pro 2022", "Premiere Pro", bounds,
        CompanyName: "Adobe Inc.", ProductName: "Adobe Premiere Pro");

    private static AutomationObservation Panel(string id, RectI bounds, long hwnd) => new(
        id, "", "", "OS_ViewContainer", "ControlType.Window", "DroverLord - Window Class",
        bounds, true, false, "Win32", hwnd);

    private static void Fill(byte[] pixels, byte value)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
    }

    private static void DrawRightChevron(byte[] pixels, int width, int stride, int x, int y)
    {
        var rows = new[] { "##...", "###..", ".###.", "..###", "..###", ".###.", "###..", "##...", "#...." };
        for (var row = 0; row < rows.Length; row++)
        for (var column = 0; column < rows[row].Length; column++)
        {
            if (rows[row][column] != '#') continue;
            var offset = (y + row) * stride + (x + column) * 4;
            pixels[offset] = 180;
            pixels[offset + 1] = 180;
            pixels[offset + 2] = 180;
            pixels[offset + 3] = 255;
        }
    }

    private static void DrawDownChevron(byte[] pixels, int stride, int x, int y)
    {
        var rows = new[] { "#.......#", "##.....##", ".##...##.", "..##.##..", "...###..." };
        for (var row = 0; row < rows.Length; row++)
        for (var column = 0; column < rows[row].Length; column++)
        {
            if (rows[row][column] != '#') continue;
            DrawPixel(pixels, stride, x + column, y + row, 180);
        }
    }

    private static void DrawTextWord(byte[] pixels, int stride, int x, int y, int glyphCount, byte value)
    {
        for (var glyph = 0; glyph < glyphCount; glyph++)
            DrawRect(pixels, stride, x + glyph * 5, y, 3, 9, value);
    }

    private static void DrawRect(
        byte[] pixels, int stride, int x, int y, int width, int height, byte value = 180)
    {
        for (var row = y; row < y + height; row++)
        for (var column = x; column < x + width; column++)
            DrawPixel(pixels, stride, column, row, value);
    }

    private static void DrawPixel(byte[] pixels, int stride, int x, int y, byte value)
    {
        var offset = y * stride + x * 4;
        pixels[offset] = value;
        pixels[offset + 1] = value;
        pixels[offset + 2] = value;
        pixels[offset + 3] = 255;
    }
}

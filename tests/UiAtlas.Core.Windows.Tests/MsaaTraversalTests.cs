using Accessibility;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class MsaaTraversalTests
{
    [Fact]
    public void ScrollingContainerPreservesVisibleAndOffscreenDescendantsWithOriginalBounds()
    {
        var visible = new Accessible("Abadi", new(110, 210, 180, 20));
        var offscreen = new Accessible("Later font", new(110, 3000, 180, 20), state: 0x10000);
        var fonts = new Accessible("All Fonts", new(100, 200, 200, 8000), children: [visible, offscreen]);
        var root = new Accessible("Font", new(100, 100, 220, 400), children: [fonts]);
        var records = Collect(root);
        Assert.Equal(3, records.Count);
        var container = Assert.Single(records, item => item.Name == "All Fonts");
        Assert.Equal(fonts.Bounds, container.Bounds);
        Assert.All(records.Where(item => item.Name != "All Fonts"),
            item => Assert.Equal(container.RuntimeId, item.ParentRuntimeId));
        Assert.False(Assert.Single(records, item => item.Name == "Abadi").IsOffscreen);
        Assert.True(Assert.Single(records, item => item.Name == "Later font").IsOffscreen);
    }

    [Fact]
    public void EmptyContainerGeometryDoesNotSuppressItsVisibleChildAndTraversalStaysBounded()
    {
        var child = new Accessible("Visible", new(110, 210, 180, 20));
        var root = new Accessible("Font", new(100, 100, 220, 400), children:
            [new Accessible("Container", new(0, 0, 0, 0), children: [child])]);
        Assert.Contains(Collect(root), item => item.Name == "Visible");
        Assert.Single(Collect(root, 1));
        Assert.Single(Collect(root, depth: 1));
    }

    [Fact]
    public void UnreadableAncestorKeepsItsChildWithoutInventingAnAncestorEdge()
    {
        var child = new Accessible("Visible", new(110, 210, 180, 20));
        var root = new Accessible("Font", new(100, 100, 220, 400), children:
            [new Accessible("Unavailable", new(0, 0, 0, 0), children: [child], failLocation: true)]);
        var record = Assert.Single(Collect(root));
        Assert.Equal("Visible", record.Name);
        Assert.Equal("test.1", record.ParentRuntimeId);
    }

    [Fact]
    public void FailedCyclicProviderStopsAtVisitBudgetEvenWithoutAnyRetainedRecords()
    {
        var root = new Accessible("Unavailable", new(0, 0, 0, 0), failLocation: true);
        root.Children = [root];
        var records = new List<AutomationObservation>();
        var sequence = 0;
        var reads = 0;
        MsaaDialogCollector.AppendTree(root, "test", "root", 42, default, records, 3, 64,
            ref sequence, item => { reads++; return ((Accessible)item).Children.Cast<object>().ToArray(); });
        Assert.Empty(records);
        Assert.Equal(3, reads);
    }

    private static List<AutomationObservation> Collect(Accessible root, int limit = 100, int depth = 64)
    {
        var result = new List<AutomationObservation>();
        var sequence = 0;
        MsaaDialogCollector.AppendTree(root, "test", "root", 42,
            new NativeMethods.Rect { Left = 100, Top = 100, Right = 320, Bottom = 500 },
            result, limit, depth, ref sequence, item => ((Accessible)item).Children.Cast<object>().ToArray());
        return result;
    }

    // A provider double: exercises the actual property-reading/traversal path;
    // only COM child enumeration is substituted, so no desktop input is required.
    private sealed class Accessible(string name, RectI bounds, int state = 0, Accessible[]? children = null,
        bool failLocation = false) : IAccessible
    {
        public RectI Bounds => bounds;
        public Accessible[] Children { get; set; } = children ?? [];
        public object accParent => null!;
        public int accChildCount => Children.Length;
        public object accFocus => null!;
        public object accSelection => null!;
        public object get_accChild(object child) => Children[(int)child - 1];
        public string get_accName(object child) => name;
        public string get_accValue(object child) => "";
        public string get_accDescription(object child) => "";
        public object get_accRole(object child) => Children.Length > 0 ? 20 : 34;
        public object get_accState(object child) => state;
        public string get_accHelp(object child) => "";
        public int get_accHelpTopic(out string helpFile, object child) { helpFile = ""; return 0; }
        public string get_accKeyboardShortcut(object child) => "";
        public string get_accDefaultAction(object child) => "";
        public void accSelect(int flags, object child) { }
        public void accLocation(out int x, out int y, out int width, out int height, object child)
        {
            if (failLocation) throw new System.Runtime.InteropServices.COMException("Unavailable");
            x = bounds.X; y = bounds.Y; width = bounds.Width; height = bounds.Height;
        }
        public object accNavigate(int direction, object child) => null!;
        public object accHitTest(int x, int y) => null!;
        public void accDoDefaultAction(object child) { }
        public void set_accName(object child, string value) { }
        public void set_accValue(object child, string value) { }
    }
}

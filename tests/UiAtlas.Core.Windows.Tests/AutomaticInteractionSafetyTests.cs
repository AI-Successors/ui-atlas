using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AutomaticInteractionSafetyTests
{
    [Fact]
    public void TreeItemsRemainObservableButCannotBeAutoActivated()
    {
        var tree = Control("tree", "", "ControlType.Tree", new RectI(20, 80, 300, 500));
        var item = Control("item", "tree", "ControlType.TreeItem", new RectI(40, 110, 240, 24));

        Assert.False(AutomaticInteractionSafety.CanActivate(item, [tree, item]));
        Assert.Equal("ControlType.TreeItem", item.ControlType);
    }

    [Fact]
    public void ButtonUnderTreeItemCannotBeAutoActivated()
    {
        var tree = Control("tree", "", "ControlType.Tree", new RectI(20, 80, 300, 500));
        var item = Control("item", "tree", "ControlType.TreeItem", new RectI(40, 110, 240, 24));
        var expander = Control("plus", "item", "ControlType.Button", new RectI(42, 114, 16, 16));

        Assert.False(AutomaticInteractionSafety.CanActivate(expander, [tree, item, expander]));
        Assert.Equal("ControlType.Button", expander.ControlType);
    }

    [Fact]
    public void OrphanButtonInsideTreeBoundsCannotBeAutoActivated()
    {
        var tree = Control("tree", "", "ControlType.Tree", new RectI(20, 80, 300, 500));
        var expander = Control("plus", "", "ControlType.Button", new RectI(42, 114, 16, 16));

        Assert.False(AutomaticInteractionSafety.CanActivate(expander, [tree, expander]));
    }

    [Fact]
    public void OrdinaryButtonOutsideTreeCanBeAutoActivated()
    {
        var tree = Control("tree", "", "ControlType.Tree", new RectI(20, 80, 300, 500));
        var button = Control("save", "", "ControlType.Button", new RectI(400, 20, 80, 30));

        Assert.True(AutomaticInteractionSafety.CanActivate(button, [tree, button]));
    }

    private static AutomationObservation Control(string id, string parentId, string type, RectI bounds) =>
        new(id, parentId, id, id, type, "Test", bounds, true, false, "UIA", 100,
            ["InvokePatternIdentifiers.Pattern"]);
}

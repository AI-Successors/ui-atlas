using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class ApplicationSurfaceCacheStoreTests
{
    [Fact]
    public void CacheUsesVersionedIdentityAndProjectsWithoutMakingControlsClickable()
    {
        using var temp = new TempDirectory();
        var store = new ApplicationSurfaceCacheStore(temp.Path);
        var key = new ApplicationPlanningProfileKey("REVIT", "Autodesk Revit", "2027", "HwndWrapper");
        var root = new RectI(100, 50, 1000, 800);
        var control = new AutomationObservation(
            "runtime-1", "parent", "Save", "Save", "ControlType.Button", "Button",
            new RectI(600, 90, 100, 40), true, false, "WPF", 44, ["Invoke"]);

        _ = store.Observe(key, root, [control], DateTimeOffset.UnixEpoch);
        var cache = store.Observe(key, root, [control], DateTimeOffset.UnixEpoch.AddMinutes(1));
        var projected = Assert.Single(store.Project(cache, new RectI(0, 0, 2000, 1600), 99));

        Assert.Equal(2, Assert.Single(cache.Controls).Observations);
        Assert.Equal(new RectI(1000, 80, 200, 80), projected.Bounds);
        Assert.True(projected.IsOffscreen);
        Assert.False(projected.IsEnabled);
        Assert.Equal("UiAtlas.Cached", projected.FrameworkId);
        Assert.Equal(99, projected.WindowHwnd);
    }

    [Fact]
    public void HoverOnlyShadowGeometrySurvivesAsDisabledCacheHint()
    {
        using var temp = new TempDirectory();
        var store = new ApplicationSurfaceCacheStore(temp.Path);
        var key = new ApplicationPlanningProfileKey("REVIT", "Autodesk Revit", "2027", "HwndWrapper");
        var root = new RectI(0, 0, 1000, 800);
        var shadow = new AutomationObservation(
            "shadow-hover:one", "", "shadow:one", "Unverified control", "ControlType.Custom",
            "UiAtlas.HoverRegion", new RectI(500, 50, 30, 30), false, true,
            "UiAtlas.Shadow.Hover", 44, []);

        var cache = store.Observe(key, root, [shadow], DateTimeOffset.UnixEpoch);
        var projected = Assert.Single(store.Project(cache, root, 99));

        Assert.False(projected.IsEnabled);
        Assert.True(projected.IsOffscreen);
        Assert.Equal("UiAtlas.HoverRegion", projected.ClassName);
    }
}

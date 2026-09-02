using UiAtlas.Core.Cli;

namespace UiAtlas.Core.Tests;

public sealed class AutoPassBudgetPolicyTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    public void CanCaptureNextAutoStep_PreservesFinalizationReserve(int remainingFrameBudget, bool expected)
    {
        Assert.Equal(expected, AutoPassBudgetPolicy.CanCaptureNextAutoStep(remainingFrameBudget));
        Assert.Equal(
            AutoPassBudgetPolicy.FramesPerAutoStep + AutoPassBudgetPolicy.FramesReservedForFinalize,
            AutoPassBudgetPolicy.MinimumFramesRequiredForNextAutoStep);
    }
}

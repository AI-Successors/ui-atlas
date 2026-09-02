using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal static class AutoPassBudgetPolicy
{
    public const int FramesPerAutoStep = 1;
    public const int FramesReservedForFinalize = 1;

    public static int MinimumFramesRequiredForNextAutoStep => FramesPerAutoStep + FramesReservedForFinalize;

    public static bool CanCaptureNextAutoStep(ManualRecordingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return CanCaptureNextAutoStep(session.RemainingFrameBudget);
    }

    public static bool CanCaptureNextAutoStep(int remainingFrameBudget) =>
        remainingFrameBudget >= MinimumFramesRequiredForNextAutoStep;
}

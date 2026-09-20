namespace UiAtlas.Core.Contracts;

/// <summary>Image geometry, not a qualified transcription or a saved data schema.</summary>
public sealed record GridImageExplorationResult(
    CapturedGrid Capture, string? ImagePath, RectI TableBounds, RectI HeaderBounds,
    IReadOnlyList<string> Reasons);

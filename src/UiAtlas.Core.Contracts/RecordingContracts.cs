using System.Text.Json.Serialization;

namespace UiAtlas.Core.Contracts;

public static class RecordingContractLimits
{
    public const int MaxEvents = 50_000;
    public const int MaxInteractions = 10_000;
    public const int MaxControlsPerFrame = 100_000;
    public const int MaxScopedWindows = 32;
    public const int MaxBundleEntries = 100_000;
}

[JsonConverter(typeof(JsonStringEnumConverter<RecordingOutcome>))]
public enum RecordingOutcome { Complete, Partial, Cancelled, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<InputEventKind>))]
public enum InputEventKind { PointerMove, PointerDown, PointerUp, Wheel, KeyDown, KeyUp, Marker }

[JsonConverter(typeof(JsonStringEnumConverter<InteractionActor>))]
public enum InteractionActor { User, AutoExplorer, DerivedCandidate }

[JsonConverter(typeof(JsonStringEnumConverter<InteractionGestureKind>))]
public enum InteractionGestureKind { Click, DoubleClick, Drag, Wheel, Keyboard, ProgrammaticInvoke, ProgrammaticSelect }

[JsonConverter(typeof(JsonStringEnumConverter<InteractionActionKind>))]
public enum InteractionActionKind { Invoke, Select, Expand, Collapse, Toggle, SetValue, Scroll, MoveResize, Dismiss, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<InteractionOutcome>))]
public enum InteractionOutcome { Succeeded, NoChange, Failed, TimedOut, Cancelled, Unobserved }

[JsonConverter(typeof(JsonStringEnumConverter<ControlEvidenceSource>))]
public enum ControlEvidenceSource
{
    Win32,
    UiaRaw,
    UiaControl,
    UiaContent,
    Msaa,
    UiaFromPoint,
    ChildWindow,
    Dom,
    JavaAccessBridge,
    Focus,
    Visual,
    AtSpi
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractionCoverageStatus>))]
public enum ExtractionCoverageStatus { Confirmed, Observed, Partial, Unavailable, LimitReached }

[JsonConverter(typeof(JsonStringEnumConverter<CoverageGapKind>))]
public enum CoverageGapKind
{
    EmptyContainer,
    EmptyPopup,
    LargeContainer,
    EmptyBounds,
    UnknownFocus,
    ViewDivergence,
    Timeout,
    NodeLimit,
    ChildWindowUncovered
}

public sealed record PrivacyProfile(
    bool LiteralTypedTextCaptured = false,
    bool ExecutablePathsCaptured = false,
    bool ScreenshotsRetained = true,
    string Name = "default-redacted/1");

public sealed record TargetScope(
    long SelectedHwnd,
    long RootOwnerHwnd,
    int ProcessId,
    string ProcessName,
    DateTimeOffset ProcessStartedUtc,
    string Policy = "selected-root-owner-and-owned-popups/1",
    string ProductVersion = "",
    string OriginalFilename = "",
    string CompanyName = "",
    string ProductName = "");

public sealed record RetentionPolicy(bool RetainOnCancel = true, string Name = "retain-until-user-deletes/1");

public sealed record BundleFileEntry(string Path, long Length, string MediaType, string Sha256, bool Immutable);

public sealed record RecordingManifest(
    string FormatVersion,
    string ToolVersion,
    string SessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    RecordingOutcome Outcome,
    TargetScope Target,
    PrivacyProfile Privacy,
    RetentionPolicy Retention,
    bool ExplicitConsent,
    int EventCount,
    int FrameCount,
    string HashAlgorithm = "SHA-256",
    IReadOnlyList<BundleFileEntry>? Files = null);

public sealed record InputEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    InputEventKind Kind,
    int X,
    int Y,
    int VirtualKey,
    string Text = "[redacted]",
    long WindowFromPointHwnd = 0,
    long RootOwnerHwnd = 0,
    long ForegroundHwnd = 0);

public sealed record WindowObservation(
    long Hwnd,
    long RootOwnerHwnd,
    int ProcessId,
    string ClassName,
    string Title,
    RectI Bounds,
    bool IsVisible,
    bool IsEnabled,
    bool IsMinimized,
    bool IsCloaked,
    int Dpi,
    long OwnerHwnd = 0,
    int ZOrder = 0,
    long Style = 0,
    long ExStyle = 0,
    bool IsToolWindow = false,
    bool IsTopMost = false);

public sealed record AutomationObservation(
    string RuntimeId,
    string ParentRuntimeId,
    string AutomationId,
    string Name,
    string ControlType,
    string ClassName,
    RectI Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    string FrameworkId,
    long WindowHwnd = 0,
    IReadOnlyList<string>? SupportedPatterns = null,
    bool HasKeyboardFocus = false,
    bool IsSelected = false,
    string? ToggleState = null,
    string? ExpandCollapseState = null,
    string? VisualRole = null,
    string? OcrText = null,
    string? VisualGroupId = null,
    int? TableRow = null,
    int? TableColumn = null);

public sealed record ControlEvidenceObservation(
    string EvidenceId,
    ControlEvidenceSource Source,
    string SurfaceId,
    AutomationObservation Control,
    double Confidence,
    string DiagnosticCode = "");

public sealed record MergedControlCandidate(
    string CandidateId,
    string SurfaceId,
    AutomationObservation Control,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<ControlEvidenceSource> Sources,
    double Confidence,
    ExtractionCoverageStatus CoverageStatus,
    bool HasConflict = false);

public sealed record CoverageGapObservation(
    string GapId,
    string SurfaceId,
    CoverageGapKind Kind,
    RectI Bounds,
    double Potential,
    string NextProbe,
    string RelatedRuntimeId = "",
    string DiagnosticCode = "");

public sealed record ExtractionSourceResult(
    ControlEvidenceSource Source,
    string SurfaceId,
    IReadOnlyList<ControlEvidenceObservation> Evidence,
    string Status,
    int DurationMs);

public sealed record AdaptiveExtractionSnapshot(
    string FormatVersion,
    IReadOnlyList<ExtractionSourceResult> Sources,
    IReadOnlyList<MergedControlCandidate> Candidates,
    IReadOnlyList<CoverageGapObservation> Gaps,
    ExtractionCoverageStatus CoverageStatus,
    string StopReason,
    int DurationMs,
    int ProbeCount);

public sealed record InteractionObservation(
    string InteractionId,
    string OperationId,
    int Attempt,
    long Sequence,
    InteractionActor Actor,
    InteractionGestureKind Gesture,
    InteractionActionKind Action,
    long SourceFrameSequence,
    AutomationObservation? SourceControl,
    IReadOnlyList<long> InputSequences,
    IReadOnlyList<long> ResultFrameSequences,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    InteractionOutcome Outcome,
    string DiagnosticCode = "");

public sealed record FrameObservation(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string FrameEntry,
    WindowObservation Window,
    IReadOnlyList<AutomationObservation> Automation,
    bool AutomationTimedOut,
    string AutomationStatus,
    string Trigger,
    IReadOnlyList<WindowObservation>? ScopedWindows = null,
    long? EpisodeSequence = null,
    int? PostTriggerDelayMs = null,
    string CapturePhase = "materialized",
    DateTimeOffset? ActionObservedUtc = null,
    string ObservationScope = "full-root",
    IReadOnlyList<long>? ObservedWindowHwnds = null,
    RectI? ScreenshotBounds = null,
    long? BaseFrameSequence = null,
    AutomationObservation? InteractionSource = null,
    string? InteractionId = null,
    AdaptiveExtractionSnapshot? Extraction = null);

public sealed record CaptureHealthEvent(
    DateTimeOffset TimestampUtc,
    string Component,
    string Status,
    string Detail,
    bool Recoverable);

public sealed record Episode(
    string EpisodeId,
    long? InputSequence,
    long StartFrameSequence,
    long EndFrameSequence,
    string Trigger,
    string Outcome,
    DateTimeOffset? ArmedUtc = null,
    DateTimeOffset? ActionObservedUtc = null,
    DateTimeOffset? StreamsSettledUtc = null,
    int ExpectedClickCount = 1,
    string ObservationStatus = "complete");

public sealed record DerivedStatebook(
    string DerivationVersion,
    IReadOnlyList<long> RepresentativeFrames,
    IReadOnlyList<Episode> Episodes);

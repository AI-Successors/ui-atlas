using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

public sealed class RecordingBundle : IDisposable
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    private RecordingBundle(FileStream stream, ZipArchive archive)
    {
        _stream = stream;
        _archive = archive;
        _entries = archive.Entries.ToDictionary(x => x.FullName, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Entries => _entries.Keys;

    public static RecordingBundle Open(string path, BundleLimits? limits = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var issues = BundleSecurity.Inspect(archive, limits);
            if (issues.Count > 0)
                throw new InvalidDataException("Unsafe recording bundle: " + string.Join(", ", issues));
            return new RecordingBundle(stream, archive);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public T ReadJson<T>(string name)
    {
        using var stream = OpenEntry(name);
        return JsonSerializer.Deserialize<T>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException($"Entry {name} is empty.");
    }

    public string ReadText(string name)
    {
        using var reader = new StreamReader(OpenEntry(name), new UTF8Encoding(false, true), true, 1_024, leaveOpen: false);
        return reader.ReadToEnd();
    }

    public byte[] ReadBytes(string name, int maxBytes = 64 * 1024 * 1024)
    {
        var entry = GetEntry(name);
        if (entry.Length > maxBytes) throw new InvalidDataException("Entry exceeds read limit.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        input.CopyTo(output);
        return output.ToArray();
    }

    public Stream OpenEntry(string name) => GetEntry(name).Open();

    private ZipArchiveEntry GetEntry(string name) =>
        _entries.TryGetValue(name, out var entry) ? entry : throw new InvalidDataException($"Missing entry: {name}");

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }
}

public static class RecordingBundleValidator
{
    public static ValidationReport Validate(string path)
    {
        try
        {
            using var bundle = RecordingBundle.Open(path);
            return Validate(bundle);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or DecoderFallbackException or UnauthorizedAccessException or NullReferenceException or OverflowException)
        {
            return new(false, [new("bundle.invalid", "error", path, ex.Message)]);
        }
    }

    public static ValidationReport Validate(RecordingBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var issues = new List<ValidationIssue>();
        try
        {
            foreach (var required in new[] { "manifest.json", "raw/input-events.jsonl", "raw/capture-health.jsonl", "derived/statebook.json", "hashes.sha256" })
                if (!bundle.Entries.Contains(required))
                    issues.Add(new("bundle.missing", "error", required, "Required entry is missing."));

            if (issues.Count > 0) return new(false, issues);
            ValidateJsonEntries(bundle, issues);
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            if (manifest.Target is null || manifest.Privacy is null || manifest.Retention is null)
            {
                issues.Add(new("bundle.required", "error", "manifest.json", "Required manifest object is null."));
                return new(false, issues);
            }
            if (manifest.FormatVersion != FormatVersions.RecordingBundle)
                issues.Add(new("bundle.version", "error", "manifest.json", "Unsupported recording format version."));
            if (!manifest.ExplicitConsent || manifest.EndedUtc < manifest.StartedUtc || manifest.SessionId.Length is < 1 or > 200)
                issues.Add(new("bundle.manifest", "error", "manifest.json", "Manifest consent, time range, or session identifier is invalid."));
            if (manifest.FrameCount < 0 || manifest.EventCount is < 0 or > RecordingContractLimits.MaxEvents)
                issues.Add(new("bundle.manifest", "error", "manifest.json", "Manifest frame or event count is invalid."));
            if (manifest.Target.ProcessId < 0 || manifest.Target.SelectedHwnd == 0 || manifest.Target.RootOwnerHwnd == 0 ||
                manifest.Target.ProcessName.Length > 512 || manifest.Target.Policy != "selected-root-owner-and-owned-popups/1")
                issues.Add(new("bundle.target", "error", "manifest.json", "Target scope is invalid."));
            if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > RecordingContractLimits.MaxBundleEntries)
                issues.Add(new("bundle.file-table", "error", "manifest.json", "Manifest file table is missing or exceeds the entry limit."));
            else
            {
                var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var declared in manifest.Files)
                {
                    if (!declaredPaths.Add(declared.Path)) issues.Add(new("bundle.file-table", "error", declared.Path, "File is declared more than once."));
                    if (!bundle.Entries.Contains(declared.Path)) issues.Add(new("bundle.file-table", "error", declared.Path, "Declared file is missing."));
                    else
                    {
                        var bytes = bundle.ReadBytes(declared.Path);
                        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                        if (bytes.LongLength != declared.Length || !string.Equals(actual, declared.Sha256, StringComparison.Ordinal))
                            issues.Add(new("bundle.file-table", "error", declared.Path, "Declared length or hash does not match."));
                    }
                    if (declared.Immutable != declared.Path.StartsWith("raw/", StringComparison.Ordinal))
                        issues.Add(new("bundle.file-table", "error", declared.Path, "Immutable flag does not match the raw/derived boundary."));
                }
                foreach (var extra in bundle.Entries.Where(x => x is not ("manifest.json" or "hashes.sha256") && !declaredPaths.Contains(x)))
                    issues.Add(new("bundle.extra", "error", extra, "Archive entry is not declared by the manifest."));
            }
            if (manifest.Privacy.LiteralTypedTextCaptured)
                issues.Add(new("privacy.literal-text", "warning", "manifest.json", "Literal typed text is retained."));

            var expected = ParseHashes(bundle.ReadText("hashes.sha256"), issues);
            foreach (var entry in bundle.Entries.Where(x => x != "hashes.sha256"))
            {
                if (!expected.TryGetValue(entry, out var wanted))
                {
                    issues.Add(new("hash.missing", "error", entry, "Entry has no declared hash."));
                    continue;
                }
                using var content = bundle.OpenEntry(entry);
                var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(wanted)))
                    issues.Add(new("hash.mismatch", "error", entry, "Entry hash does not match."));
            }
            foreach (var extraHash in expected.Keys.Where(x => !bundle.Entries.Contains(x)))
                issues.Add(new("hash.extra", "error", extraHash, "Hash declaration has no archive entry."));

            var observationCount = bundle.Entries.Count(x => x.StartsWith("raw/observations/frame-", StringComparison.Ordinal) && x.EndsWith(".json", StringComparison.Ordinal));
            var eventCount = bundle.ReadText("raw/input-events.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (eventCount > RecordingContractLimits.MaxEvents)
            {
                issues.Add(new("bundle.count-limit", "error", "manifest.json", "Recording streams exceed the v1 count limit."));
                return new(false, issues);
            }
            if (observationCount != manifest.FrameCount) issues.Add(new("bundle.frame-count", "error", "manifest.json", "Frame count does not match observations."));
            if (eventCount != manifest.EventCount) issues.Add(new("bundle.event-count", "error", "manifest.json", "Event count does not match input stream."));
            ValidateContracts(bundle, manifest, issues);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or DecoderFallbackException or UnauthorizedAccessException or NullReferenceException or OverflowException)
        {
            issues.Add(new("bundle.invalid", "error", "bundle", ex.Message));
        }
        return new(!issues.Any(x => x.Severity == "error"), issues);
    }

    private static void ValidateContracts(RecordingBundle bundle, RecordingManifest manifest, List<ValidationIssue> issues)
    {
        var options = JsonDefaults.Options;
        long expectedSequence = 1;
        var inputLines = bundle.ReadText("raw/input-events.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (inputLines.Length > RecordingContractLimits.MaxEvents)
        {
            issues.Add(new("bundle.input", "error", "raw/input-events.jsonl", "Input event count exceeds limit."));
            return;
        }
        foreach (var line in inputLines)
        {
            var item = JsonSerializer.Deserialize<InputEvent>(line, options) ?? throw new InvalidDataException("Null input event.");
            if (item.Sequence != expectedSequence++ || !Enum.IsDefined(item.Kind) || item.Text is null || item.Text.Length > 256 ||
                (!manifest.Privacy.LiteralTypedTextCaptured && item.Kind != InputEventKind.Marker && item.Text != "[redacted]"))
                issues.Add(new("bundle.input", "error", "raw/input-events.jsonl", "Input event violates sequence or privacy contract."));
        }
        var interactions = new List<InteractionObservation>();
        if (bundle.Entries.Contains("raw/interactions.jsonl"))
        {
            var interactionLines = bundle.ReadText("raw/interactions.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (interactionLines.Length > RecordingContractLimits.MaxInteractions)
                issues.Add(new("bundle.interaction", "error", "raw/interactions.jsonl", "Interaction count exceeds limit."));
            foreach (var line in interactionLines)
            {
                var item = JsonSerializer.Deserialize<InteractionObservation>(line, options)
                    ?? throw new InvalidDataException("Null interaction observation.");
                interactions.Add(item);
                if (string.IsNullOrWhiteSpace(item.InteractionId) || item.InteractionId.Length > 128 ||
                    string.IsNullOrWhiteSpace(item.OperationId) || item.OperationId.Length > 256 ||
                    item.Attempt < 1 || item.Sequence < 1 || !Enum.IsDefined(item.Actor) ||
                    !Enum.IsDefined(item.Gesture) || !Enum.IsDefined(item.Action) || !Enum.IsDefined(item.Outcome) ||
                    item.SourceFrameSequence < 1 || item.InputSequences is null || item.ResultFrameSequences is null ||
                    item.DiagnosticCode is null || item.DiagnosticCode.Length > 256 || item.CompletedUtc < item.StartedUtc)
                    issues.Add(new("bundle.interaction", "error", "raw/interactions.jsonl", "Interaction observation is invalid."));
                if (item.SourceControl is not null && !ValidAutomation(item.SourceControl))
                    issues.Add(new("bundle.interaction", "error", "raw/interactions.jsonl", "Interaction source control is invalid."));
            }
            if (interactions.Select(item => item.InteractionId).Distinct(StringComparer.Ordinal).Count() != interactions.Count ||
                interactions.Select(item => item.Sequence).Distinct().Count() != interactions.Count)
                issues.Add(new("bundle.interaction", "error", "raw/interactions.jsonl", "Interaction IDs and sequences must be unique."));
        }
        var healthLines = bundle.ReadText("raw/capture-health.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (healthLines.Length > 10_000)
        {
            issues.Add(new("bundle.health", "error", "raw/capture-health.jsonl", "Capture health event count exceeds limit."));
            return;
        }
        foreach (var line in healthLines)
        {
            var item = JsonSerializer.Deserialize<CaptureHealthEvent>(line, options) ?? throw new InvalidDataException("Null health event.");
            if (item.Component is null || item.Status is null || item.Detail is null || item.Component.Length > 128 || item.Status.Length > 128 || item.Detail.Length > 4_096)
                issues.Add(new("bundle.health", "error", "raw/capture-health.jsonl", "Capture health field exceeds limit."));
        }
        var statebook = bundle.ReadJson<DerivedStatebook>("derived/statebook.json");
        if (statebook.RepresentativeFrames is null || statebook.Episodes is null)
            issues.Add(new("bundle.statebook", "error", "derived/statebook.json", "Derived statebook is invalid."));
        else if (statebook.DerivationVersion is null || statebook.DerivationVersion.Length > 128 || statebook.Episodes.Any(x => x is null ||
                     x.EpisodeId is null || x.EpisodeId.Length > 128 || x.Trigger is null || x.Trigger.Length > 256 || x.Outcome is null || x.Outcome.Length > 128))
            issues.Add(new("bundle.statebook", "error", "derived/statebook.json", "Derived statebook contains an invalid episode."));
        else if (statebook.Episodes.Any(x => x.ExpectedClickCount is < 1 or > 2 || x.ObservationStatus is null || x.ObservationStatus.Length > 128 ||
                     x.EndFrameSequence < x.StartFrameSequence || (x.ActionObservedUtc.HasValue && x.ArmedUtc.HasValue && x.ActionObservedUtc < x.ArmedUtc) ||
                     (x.StreamsSettledUtc.HasValue && x.ActionObservedUtc.HasValue && x.StreamsSettledUtc < x.ActionObservedUtc)))
            issues.Add(new("bundle.statebook", "error", "derived/statebook.json", "Derived statebook episode timing or capture status is invalid."));

        var referencedFrames = new HashSet<string>(StringComparer.Ordinal);
        var observedFrameSequences = new HashSet<long>();
        foreach (var entry in bundle.Entries.Where(x => x.StartsWith("raw/observations/frame-", StringComparison.Ordinal) && x.EndsWith(".json", StringComparison.Ordinal)))
        {
            var frame = bundle.ReadJson<FrameObservation>(entry);
            if (frame.Window is null || !ValidWindow(frame.Window) || frame.Automation is null || frame.Automation.Count > RecordingContractLimits.MaxControlsPerFrame ||
                frame.FrameEntry is null || frame.FrameEntry.Length > 512 || frame.Trigger is null || frame.Trigger.Length > 256 ||
                frame.AutomationStatus is null || frame.AutomationStatus.Length > 128 ||
                frame.InteractionId is { Length: > 128 } || frame.InteractionSource is not null && !ValidAutomation(frame.InteractionSource))
            { issues.Add(new("bundle.observation", "error", entry, "Frame observation exceeds limits.")); continue; }
            if (frame.Sequence <= 0 || !string.Equals(entry, $"raw/observations/frame-{frame.Sequence:D6}.json", StringComparison.Ordinal))
                issues.Add(new("bundle.observation", "error", entry, "Frame observation path does not match its sequence."));
            observedFrameSequences.Add(frame.Sequence);
            if (frame.EpisodeSequence is <= 0 || frame.PostTriggerDelayMs is < 0 or > 60_000 ||
                frame.CapturePhase is null || frame.CapturePhase is not ("baseline" or "action" or "post-trigger" or "materialized" or "final") ||
                (frame.PostTriggerDelayMs.HasValue && frame.EpisodeSequence is null) ||
                (frame.ActionObservedUtc.HasValue && frame.ActionObservedUtc > frame.TimestampUtc))
                issues.Add(new("bundle.observation", "error", entry, "Frame episode linkage or capture timing is invalid."));
            var scopedHwnds = (frame.ScopedWindows ?? [frame.Window]).Select(window => window.Hwnd).ToHashSet();
            var isDeltaScope = frame.ObservationScope is "popup-delta" or "control-delta";
            var observedWindowScopeMismatch = frame.ObservedWindowHwnds is not null &&
                                              frame.ObservedWindowHwnds.Any(hwnd => !scopedHwnds.Contains(hwnd));
            var recoverableLegacyEmbeddedDelta = observedWindowScopeMismatch &&
                                                 IsRecoverableLegacyEmbeddedDelta(frame, scopedHwnds);
            if (recoverableLegacyEmbeddedDelta)
                issues.Add(new("bundle.observation.legacy-scope", "warning", entry,
                    "Recovered a legacy embedded-window delta whose observed child window was omitted from scopedWindows."));
            if (frame.ObservationScope is not ("full-root" or "popup-delta" or "control-delta") ||
                frame.ObservedWindowHwnds is { Count: > RecordingContractLimits.MaxScopedWindows } ||
                frame.ObservedWindowHwnds is not null && (frame.ObservedWindowHwnds.Distinct().Count() != frame.ObservedWindowHwnds.Count ||
                    observedWindowScopeMismatch && !recoverableLegacyEmbeddedDelta) ||
                isDeltaScope && (frame.ObservedWindowHwnds is not { Count: > 0 } || frame.BaseFrameSequence is null) ||
                frame.BaseFrameSequence is <= 0 || frame.BaseFrameSequence >= frame.Sequence ||
                !string.IsNullOrEmpty(frame.FrameEntry) && (frame.ScreenshotBounds is { Width: <= 0 } or { Height: <= 0 }))
                issues.Add(new("bundle.observation", "error", entry, "Frame delta scope or screenshot bounds are invalid."));
            var expectedFrameEntry = $"raw/frames/frame-{frame.Sequence:D6}.png";
            if (!string.IsNullOrEmpty(frame.FrameEntry))
            {
                if (!manifest.Privacy.ScreenshotsRetained || !string.Equals(frame.FrameEntry, expectedFrameEntry, StringComparison.Ordinal) ||
                    !bundle.Entries.Contains(frame.FrameEntry))
                    issues.Add(new("bundle.observation", "error", entry, "Frame image reference is not canonical or is missing."));
                else
                    referencedFrames.Add(frame.FrameEntry);
            }
            if (frame.ScopedWindows is { Count: > RecordingContractLimits.MaxScopedWindows }) issues.Add(new("bundle.observation", "error", entry, "Scoped window count exceeds limit."));
            if (frame.ScopedWindows is not null && frame.ScopedWindows.Any(x => x is null || !ValidWindow(x)))
                issues.Add(new("bundle.observation", "error", entry, "Scoped window observation is invalid."));
            foreach (var item in frame.Automation)
                if (item is null || !ValidAutomation(item))
                    issues.Add(new("bundle.observation", "error", entry, "Automation field exceeds limit."));
            if (frame.Extraction is not null && !ValidExtraction(frame.Extraction))
                issues.Add(new("bundle.extraction", "error", entry, "Adaptive extraction evidence exceeds limits."));
        }

        if (statebook.RepresentativeFrames is not null && statebook.Episodes is not null &&
            (statebook.RepresentativeFrames.Distinct().Count() != statebook.RepresentativeFrames.Count ||
             statebook.RepresentativeFrames.Any(sequence => !observedFrameSequences.Contains(sequence)) ||
             statebook.Episodes.Any(episode => !observedFrameSequences.Contains(episode.StartFrameSequence) || !observedFrameSequences.Contains(episode.EndFrameSequence))))
            issues.Add(new("bundle.statebook", "error", "derived/statebook.json", "Derived statebook references missing or duplicate frame sequences."));
        if (interactions.Any(interaction =>
                !observedFrameSequences.Contains(interaction.SourceFrameSequence) ||
                interaction.ResultFrameSequences.Any(sequence => !observedFrameSequences.Contains(sequence)) ||
                interaction.InputSequences.Any(sequence => sequence < 1 || sequence > inputLines.Length) ||
                interaction.Outcome == InteractionOutcome.Succeeded &&
                (interaction.SourceControl is null || interaction.ResultFrameSequences.Count == 0)))
            issues.Add(new("bundle.interaction", "error", "raw/interactions.jsonl",
                "Interaction references missing evidence or a successful interaction has no source and result."));

        var interactionIds = interactions.Select(interaction => interaction.InteractionId).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in bundle.Entries.Where(x => x.StartsWith("raw/observations/frame-", StringComparison.Ordinal) && x.EndsWith(".json", StringComparison.Ordinal)))
        {
            var frame = bundle.ReadJson<FrameObservation>(entry);
            if (!string.IsNullOrWhiteSpace(frame.InteractionId) && !interactionIds.Contains(frame.InteractionId))
                issues.Add(new("bundle.interaction", "error", entry, "Frame references a missing interaction observation."));
        }

        foreach (var entry in bundle.Entries.Where(x => x.StartsWith("raw/frames/", StringComparison.Ordinal)))
        {
            if (!referencedFrames.Contains(entry))
                issues.Add(new("bundle.image", "error", entry, "Frame image is not referenced by its canonical observation."));
            var bytes = bundle.ReadBytes(entry, 16 * 1024 * 1024);
            if (!IsBoundedPng(bytes)) issues.Add(new("bundle.image", "error", entry, "Frame is not a bounded PNG image."));
        }
        foreach (var entry in bundle.Entries.Where(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("raw/frames/", StringComparison.Ordinal)))
            issues.Add(new("bundle.image", "error", entry, "PNG image is outside the canonical frame namespace."));
    }

    private static bool ValidWindow(WindowObservation value) =>
        value.ProcessId > 0 && value.ClassName is not null && value.ClassName.Length <= 512 && value.Title is not null &&
        value.Title.Length <= 4_096 && value.Bounds.Width >= 0 && value.Bounds.Height >= 0 &&
        (long)value.Bounds.Width * value.Bounds.Height <= 16_000_000 && value.Dpi is >= 48 and <= 960;

    private static bool ValidAutomation(AutomationObservation item) =>
        item.RuntimeId is not null && item.ParentRuntimeId is not null && item.AutomationId is not null && item.Name is not null &&
        item.ControlType is not null && item.ClassName is not null && item.FrameworkId is not null && item.RuntimeId.Length <= 4_096 &&
        item.ParentRuntimeId.Length <= 4_096 && item.AutomationId.Length <= 512 && item.Name.Length <= 4_096 &&
        item.ControlType.Length <= 512 && item.ClassName.Length <= 512 && item.FrameworkId.Length <= 512;

    private static bool ValidExtraction(AdaptiveExtractionSnapshot value)
    {
        if (value.FormatVersion is null or { Length: > 128 } || value.StopReason is null or { Length: > 256 } ||
            value.DurationMs is < 0 or > 120_000 || value.ProbeCount is < 0 or > 128 ||
            value.Sources is null or { Count: > 256 } || value.Candidates is null or { Count: > RecordingContractLimits.MaxControlsPerFrame } ||
            value.Gaps is null or { Count: > 4_096 } || !Enum.IsDefined(value.CoverageStatus)) return false;
        if (value.Sources.Sum(source => source?.Evidence?.Count ?? 0) > RecordingContractLimits.MaxControlsPerFrame * 8) return false;
        if (value.Sources.Any(source => source is null || !Enum.IsDefined(source.Source) ||
                source.SurfaceId is null or { Length: > 256 } || source.Status is null or { Length: > 128 } ||
                source.DurationMs is < 0 or > 120_000 || source.Evidence is null || source.Evidence.Any(evidence =>
                    evidence is null || evidence.EvidenceId is null or { Length: > 256 } ||
                    evidence.SurfaceId is null or { Length: > 256 } || !Enum.IsDefined(evidence.Source) ||
                    !double.IsFinite(evidence.Confidence) || evidence.Confidence is < 0 or > 1 ||
                    evidence.DiagnosticCode is null or { Length: > 256 } || evidence.Control is null || !ValidAutomation(evidence.Control)))) return false;
        if (value.Candidates.Any(candidate => candidate is null || candidate.CandidateId is null or { Length: > 256 } ||
                candidate.SurfaceId is null or { Length: > 256 } || candidate.Control is null || !ValidAutomation(candidate.Control) ||
                candidate.EvidenceIds is null or { Count: > 64 } || candidate.Sources is null or { Count: > 16 } ||
                !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1 ||
                !Enum.IsDefined(candidate.CoverageStatus))) return false;
        return !value.Gaps.Any(gap => gap is null || gap.GapId is null or { Length: > 256 } ||
            gap.SurfaceId is null or { Length: > 256 } || gap.NextProbe is null or { Length: > 128 } ||
            gap.RelatedRuntimeId is null or { Length: > 4_096 } || gap.DiagnosticCode is null or { Length: > 256 } ||
            !Enum.IsDefined(gap.Kind) || !double.IsFinite(gap.Potential) || gap.Potential is < 0 or > 4);
    }

    private static bool IsRecoverableLegacyEmbeddedDelta(FrameObservation frame, IReadOnlySet<long> scopedHwnds)
    {
        if (frame.ObservationScope != "popup-delta" || frame.ObservedWindowHwnds is not { Count: > 0 } ||
            frame.ScreenshotBounds is not { Width: > 0, Height: > 0 } screenshotBounds ||
            !scopedHwnds.Contains(frame.Window.Hwnd) || !Contains(screenshotBounds, frame.Window.Bounds))
            return false;

        return frame.ObservedWindowHwnds
            .Where(hwnd => !scopedHwnds.Contains(hwnd))
            .All(hwnd => frame.Automation.Any(control => control.WindowHwnd == hwnd && Contains(screenshotBounds, control.Bounds)));
    }

    private static bool Contains(RectI outer, RectI inner) =>
        inner.Width >= 0 && inner.Height >= 0 &&
        inner.X >= outer.X && inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static bool IsBoundedPng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return width is > 0 and <= 16_384 && height is > 0 and <= 16_384 && (ulong)width * height <= 16_000_000;
    }

    private static void ValidateJsonEntries(RecordingBundle bundle, List<ValidationIssue> issues)
    {
        foreach (var entry in bundle.Entries.Where(x => x.EndsWith(".json", StringComparison.Ordinal)))
        {
            try { StrictJsonValidator.Validate(bundle.ReadBytes(entry, 8 * 1024 * 1024)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { issues.Add(new("json.strict", "error", entry, ex.Message)); }
        }
        foreach (var entry in bundle.Entries.Where(x => x.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            var text = bundle.ReadText(entry);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 2 * 1024 * 1024) { issues.Add(new("jsonl.line-limit", "error", entry, "JSON line exceeds limit.")); break; }
                try { StrictJsonValidator.Validate(Encoding.UTF8.GetBytes(line)); }
                catch (JsonException ex) { issues.Add(new("json.strict", "error", entry, ex.Message)); break; }
            }
        }
    }

    private static Dictionary<string, string> ParseHashes(string text, List<ValidationIssue> issues)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) || !result.TryAdd(parts[1], parts[0].ToLowerInvariant()))
                issues.Add(new("hash.list", "error", "hashes.sha256", "Malformed or duplicate hash declaration."));
        }
        return result;
    }
}

public static class StrictJsonValidator
{
    public static void Validate(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        Visit(document.RootElement);
    }

    private static void Visit(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate object property.");
                Visit(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Visit(item);
    }
}

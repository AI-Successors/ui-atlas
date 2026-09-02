using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Cli;

internal sealed record MapQualityFrame(FrameObservation Frame, string PixelFingerprint);

internal sealed record MapQualityEvidence(
    IReadOnlyList<MapQualityFrame> Frames,
    IReadOnlyList<InteractionObservation> Interactions,
    IReadOnlyList<CaptureHealthEvent> Health);

internal sealed record MapQualityReport(
    int ScreenCount,
    int SemanticSurfaceCount,
    int SemanticControlCount,
    int TableCellCount,
    int ScreenshotFrameCount,
    int DuplicateScreenshotCount,
    int PartialFrameCount,
    int MissingScreenshotCount,
    int InteractionCount,
    int SuccessfulInteractionCount,
    int InteractionWithoutResultCount,
    int CriticalCaptureIssueCount,
    IReadOnlyList<string> ReviewReasons)
{
    public bool NeedsReview => ReviewReasons.Count > 0;

    public string UserSummary()
    {
        var summary = $"Map ready: {ScreenCount} screen{Plural(ScreenCount)}, " +
                      $"{SemanticControlCount} control{Plural(SemanticControlCount)}, " +
                      $"{TableCellCount} table cell{Plural(TableCellCount)}.";
        return NeedsReview
            ? summary + " Review needed: " + string.Join("; ", ReviewReasons.Take(2)) + "."
            : summary + (InteractionCount == 0
                ? " No click gaps were recorded."
                : " Every recorded interaction has a result screen.");
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}

internal static class MapQualityInspector
{
    private static readonly HashSet<string> CriticalCaptureStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "delta-failed",
        "popup-failed",
        "queue-full",
        "root-change-missed",
        "root-state-changing",
        "timeout",
        "unavailable",
        "visual-root-snapshot-failed"
    };

    public static MapQualityReport Inspect(
        UiKnowledgeGraph graph,
        IEnumerable<string> recordingPaths)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(recordingPaths);

        var frames = new List<MapQualityFrame>();
        var interactions = new List<InteractionObservation>();
        var health = new List<CaptureHealthEvent>();
        foreach (var recordingPath in recordingPaths
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var bundle = RecordingBundle.Open(recordingPath);
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            var hashes = (manifest.Files ?? [])
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
            foreach (var entry in bundle.Entries
                         .Where(IsObservationEntry)
                         .OrderBy(entry => entry, StringComparer.Ordinal))
            {
                var frame = bundle.ReadJson<FrameObservation>(entry);
                var fingerprint = !string.IsNullOrWhiteSpace(frame.FrameEntry) &&
                                  hashes.TryGetValue(frame.FrameEntry, out var hash)
                    ? hash
                    : string.Empty;
                frames.Add(new(frame, fingerprint));
            }

            interactions.AddRange(ReadJsonLines<InteractionObservation>(bundle, "raw/interactions.jsonl"));
            health.AddRange(ReadJsonLines<CaptureHealthEvent>(bundle, "raw/capture-health.jsonl"));
        }

        return Evaluate(graph, new(frames, interactions, health));
    }

    internal static MapQualityReport Evaluate(UiKnowledgeGraph graph, MapQualityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(evidence);

        var semanticNodes = graph.Nodes.Where(node => HasProperty(node, "layer", "semantic-world")).ToArray();
        var semanticSurfaces = semanticNodes.Count(node =>
            node.Kind == GraphNodeKind.Surface && !HasProperty(node, "semanticSurfaceKind", "PopupFamily"));
        var semanticControls = semanticNodes.Where(node => node.Kind == GraphNodeKind.Control).ToArray();
        var tableCells = semanticControls.Count(node =>
            HasProperty(node, "tableRow") && HasProperty(node, "tableColumn"));
        var screens = graph.Nodes.Count(node =>
            node.Kind == GraphNodeKind.State &&
            HasProperty(node, "layer", "raw-world") &&
            node.Evidence.Any(item => !string.IsNullOrWhiteSpace(item.ScreenshotEntry)));

        var visualFrames = evidence.Frames
            .Where(item => IsVisualFrame(item.Frame))
            .ToArray();
        var screenshotFrames = visualFrames.Count(item => !string.IsNullOrWhiteSpace(item.Frame.FrameEntry));
        var duplicateScreenshots = visualFrames
            .Where(item => !string.IsNullOrWhiteSpace(item.PixelFingerprint))
            .GroupBy(item => item.PixelFingerprint, StringComparer.OrdinalIgnoreCase)
            .Sum(group => Math.Max(0, group.Count() - 1));
        var partialFrames = visualFrames.Count(item =>
            item.Frame.AutomationTimedOut ||
            item.Frame.AutomationStatus is not ("ok" or "node-limit" or "not-requested"));
        var missingScreenshots = visualFrames.Count(item => string.IsNullOrWhiteSpace(item.Frame.FrameEntry));
        var successfulInteractions = evidence.Interactions.Count(interaction =>
            interaction.Outcome is InteractionOutcome.Succeeded or InteractionOutcome.NoChange);
        var interactionsWithoutResult = evidence.Interactions.Count(interaction =>
            (interaction.Outcome != InteractionOutcome.NoChange && interaction.ResultFrameSequences.Count == 0) ||
            interaction.Outcome is InteractionOutcome.Failed or InteractionOutcome.TimedOut or
                InteractionOutcome.Cancelled or InteractionOutcome.Unobserved);
        var criticalIssues = evidence.Health.Count(item =>
            !item.Recoverable || CriticalCaptureStatuses.Contains(item.Status));

        var reasons = new List<string>();
        if (screens == 0)
            reasons.Add("no complete screen was promoted to the map");
        if (interactionsWithoutResult > 0)
            reasons.Add($"{interactionsWithoutResult} interaction{Plural(interactionsWithoutResult)} without a confirmed result screen");
        if (missingScreenshots > 0)
            reasons.Add($"{missingScreenshots} visual frame{Plural(missingScreenshots)} without pixels");
        if (partialFrames > 0)
            reasons.Add($"{partialFrames} screen scan{Plural(partialFrames)} returned partial controls");
        if (criticalIssues > 0)
            reasons.Add($"{criticalIssues} capture warning{Plural(criticalIssues)} require review");
        if (semanticControls.Length == 0)
            reasons.Add("no semantic controls were produced");

        return new(
            screens,
            semanticSurfaces,
            semanticControls.Length,
            tableCells,
            screenshotFrames,
            duplicateScreenshots,
            partialFrames,
            missingScreenshots,
            evidence.Interactions.Count,
            successfulInteractions,
            interactionsWithoutResult,
            criticalIssues,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static void Print(MapQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Console.WriteLine(report.NeedsReview ? "STATUS\tNEEDS REVIEW" : "STATUS\tREADY");
        Console.WriteLine($"SCREENS\t{report.ScreenCount}");
        Console.WriteLine($"SEMANTIC SURFACES\t{report.SemanticSurfaceCount}");
        Console.WriteLine($"CONTROLS\t{report.SemanticControlCount}");
        Console.WriteLine($"TABLE CELLS\t{report.TableCellCount}");
        Console.WriteLine($"SCREENSHOTS\t{report.ScreenshotFrameCount}");
        Console.WriteLine($"EXACT DUPLICATE SCREENSHOTS\t{report.DuplicateScreenshotCount}");
        Console.WriteLine($"INTERACTIONS\t{report.SuccessfulInteractionCount}/{report.InteractionCount} complete");
        Console.WriteLine($"PARTIAL SCREEN SCANS\t{report.PartialFrameCount}");
        Console.WriteLine($"MISSING SCREENSHOTS\t{report.MissingScreenshotCount}");
        if (report.ReviewReasons.Count == 0)
        {
            Console.WriteLine("REVIEW\tNo known capture gaps.");
            return;
        }

        foreach (var reason in report.ReviewReasons)
            Console.WriteLine("REVIEW\t" + reason);
    }

    private static IReadOnlyList<T> ReadJsonLines<T>(RecordingBundle bundle, string entry)
    {
        if (!bundle.Entries.Contains(entry)) return [];
        return bundle.ReadText(entry)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options)
                ?? throw new InvalidDataException($"Null value in {entry}."))
            .ToArray();
    }

    private static bool IsObservationEntry(string entry) =>
        entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
        entry.EndsWith(".json", StringComparison.Ordinal);

    private static bool IsVisualFrame(FrameObservation frame) =>
        !string.Equals(frame.ObservationScope, "control-delta", StringComparison.Ordinal);

    private static bool HasProperty(GraphNode node, string name, string? value = null) =>
        node.Properties.Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (value is null || property.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;
using System.Xml.Linq;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;
using Microsoft.Win32;

namespace UiAtlas.Core.Cli;

internal sealed record SvgAsset(
    double ViewBoxWidth,
    double ViewBoxHeight,
    IReadOnlyList<SvgPathAsset> Paths);

internal sealed record SvgPathAsset(
    System.Windows.Media.Geometry Geometry,
    System.Windows.Media.Brush? Fill,
    System.Windows.Media.Brush? Stroke,
    double StrokeThickness,
    System.Windows.Media.PenLineCap StrokeLineCap,
    System.Windows.Media.PenLineJoin StrokeLineJoin);

internal sealed record RecordingLaunchOptions(
    bool EnableHoverAndFocusDiscovery = true,
    bool CaptureCustomerData = false);

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string PanelCloseCommand = "CLOSE";
    private const string AutoPassQuotaStopMessage = "Auto labels reached the capture limit. Saving a partial map...";
    private const string AutoPassQuotaSavedMessage = "Auto labels reached the capture limit. Partial map saved. Resume to continue.";
    private const int AutoCaptureMaxAttempts = 2;

    private enum SessionLaunchMode
    {
        RescanCurrentScreen,
        Manual,
        AutoTabs
    }

    private enum RecordingPhaseOutcome
    {
        Completed,
        PartialCompleted,
        Cancelled,
        Failed
    }

    private enum AutoTabsOutcome
    {
        ContinueManual,
        SavePartialMap,
        FinishMap,
        Cancelled,
        PanelClosed
    }

    private enum AutoPassRefreshOutcomeKind
    {
        NoStructuralChange,
        StructuralChangePersisted,
        CaptureUnavailableAfterActivation,
        InvocationFailed
    }

    private sealed record AutoPassRefreshOutcome(AutoPassRefreshOutcomeKind Kind, FrameObservation? Frame = null)
    {
        public static AutoPassRefreshOutcome CreateInvocationFailed() =>
            new(AutoPassRefreshOutcomeKind.InvocationFailed);

        public static AutoPassRefreshOutcome CreateNoStructuralChange() =>
            new(AutoPassRefreshOutcomeKind.NoStructuralChange);

        public static AutoPassRefreshOutcome CreateCaptureUnavailableAfterActivation() =>
            new(AutoPassRefreshOutcomeKind.CaptureUnavailableAfterActivation);

        public static AutoPassRefreshOutcome CreateStructuralChangePersisted(FrameObservation frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return new(AutoPassRefreshOutcomeKind.StructuralChangePersisted, frame);
        }
    }

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // Window rectangles, UI Automation bounds and captured pixels must share
        // physical screen coordinates when Excel moves between differently scaled
        // monitors. The manifest covers the published executable; this early call
        // also protects development and alternate hosting paths when Windows still
        // allows the process awareness to be selected at runtime.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
            _ = NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
        // Quick Edit suspends the console process while text is selected. The WPF
        // recorder remains clickable on its UI thread, but its commands can no
        // longer reach the workflow. Never let console selection freeze recording.
        ConsoleInputMode.DisableQuickEdit();
        if (args.Length == 0) return await InteractiveShell().ConfigureAwait(false);
        return await Execute(args).ConfigureAwait(false);
    }

    private static async Task<int> Execute(string[] args)
    {
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => List(args[1..]),
                "recording" => await RecordingCommand(args[1..]),
                "map" => await MapCommand(args[1..]).ConfigureAwait(false),
                "record" => await Record(args[1..]),
                "list-windows" => ListWindows(),
                "synthetic-record" => SyntheticRecord(args[1..]),
                "build" => Build(args[1..]),
                "validate" => Validate(args[1..]),
                "inspect" => Inspect(args[1..]),
                "export" => ExportCommand(args[1..]),
                "export-ui-atlas" => ExportUiAtlas(args[1..]),
                "validate-ui-atlas-export" => ValidateUiAtlasExport(args[1..]),
                "diff" => Diff(args[1..]),
                "open" => Open(args[1..]),
                UiaWorkerHost.Command => UiaWorkerHost.Run(args[1..]),
                "help" or "--help" or "-h" => Help(args[1..]),
                _ => Fail("Unknown command. Run 'ui-atlas help'.")
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"error: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
            return 2;
        }
    }

    private static async Task<int> InteractiveShell()
    {
        Console.WriteLine($"UiAtlas Core {FormatVersions.Tool}");
        Console.WriteLine("Type HELP for a list of commands, type R and press Enter to start recording, or EXIT to leave.");
        while (true)
        {
            Console.WriteLine();
            Console.Write("UI-ATLAS> ");
            var line = ReadInteractiveLine();
            if (line is null) return 0;
            line = ExpandInteractiveShortcut(line.TrimStart('\uFEFF'));
            string[] args;
            try { args = ParseInteractiveCommand(line); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine($"error: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
                continue;
            }
            if (args.Length == 0) continue;
            if (args[0].Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("quit", StringComparison.OrdinalIgnoreCase)) return 0;
            var result = await Execute(args).ConfigureAwait(false);
            if (result != 0) Console.WriteLine($"Command completed with status {result}.");
        }
    }

    private static int Help(string[]? topic = null)
    {
        topic ??= [];
        if (topic.Length > 0)
        {
            var name = topic[0].ToLowerInvariant();
            return name switch
            {
                "list" => HelpList(),
                "recording" => HelpRecording(),
                "map" => HelpMap(),
                "export" => HelpExport(),
                _ => Fail("No help is available for that command.")
            };
        }
        PrintHelpHeader();
        Console.WriteLine("""
            LIST        - Display windows, recordings, or maps.
            RECORDING   - Start, validate, or delete a recording.
            MAP         - Build, validate, inspect, check quality, open, export, or delete a map.
            EXPORT      - Create or manage JSON map exports.
            R           - Shortcut for RECORDING START after you press Enter.
            HELP        - Display command help. For example, HELP MAP.
            EXIT        - Exit the interactive shell.

            Type HELP <command> for its subcommands.
            """);
        return 0;
    }

    private static int HelpList()
    {
        PrintHelpHeader();
        Console.WriteLine("""
            WINDOWS      - Display selectable top-level windows. For example, LIST WINDOWS.
            RECORDINGS   - Display recordings in the local catalog. For example, LIST RECORDINGS.
            MAPS         - Display maps in the local catalog. For example, LIST MAPS.
            """);
        return 0;
    }

    private static int HelpRecording()
    {
        PrintHelpHeader();
        Console.WriteLine("""
            START       - Start attended recording. RECORDING START [HWND].
            RESUME      - Resume recording for an existing map. RECORDING RESUME <map-path>.
            VALIDATE    - Validate a catalog recording. RECORDING VALIDATE <recording-id>.
            DELETE      - Move one or all recordings to catalog trash. RECORDING DELETE <recording-id|ALL> [--yes].
            """);
        return 0;
    }

    private static int HelpMap()
    {
        PrintHelpHeader();
        Console.WriteLine("""
            BUILD       - Build or rebuild a map. MAP BUILD <recording-id>.
            VALIDATE    - Validate a catalog map. MAP VALIDATE <map-id>.
            INSPECT     - Inspect one layer. MAP INSPECT <map-id> WORLD <streams|raw|semantic|shared>.
            QUALITY     - Explain capture coverage. MAP QUALITY <map-id> [--strict].
            OPEN        - Open a map in the visual explorer. MAP OPEN <map-id>.
            EXPORT      - Export safe JSON. MAP EXPORT JSON <map-id>.
            DELETE      - Move one or all maps to catalog trash. MAP DELETE <map-id|ALL> [--yes].
        """);
        return 0;
    }

    private static bool IsHelpKey(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.F1 or ConsoleKey.H;

    private static void PrintRecorderStartConsoleHelp(bool targetSelectionRequired)
    {
        Console.WriteLine("Console controls remain available:");
        if (targetSelectionRequired)
            Console.WriteLine("  Select a window in the toolbar first, or use LIST WINDOWS plus RECORDING START <HWND> to stay fully in the console.");
        else
            Console.WriteLine("  The target window is already selected, so you can keep working from the console if you prefer.");
        Console.WriteLine("  S or Enter  - open the start chooser.");
        Console.WriteLine("  1           - start manual recording after the chooser opens.");
        Console.WriteLine("  2           - start auto labels after the chooser opens.");
        Console.WriteLine("  C or Esc    - cancel and close the recorder.");
        Console.WriteLine("  F1 or H     - show these controls again.");
        Console.WriteLine("  Run ui-atlas with no arguments to return to the full HELP shell.");
    }

    private static void PrintRecorderActiveConsoleHelp()
    {
        Console.WriteLine("Console controls remain available during recording:");
        Console.WriteLine("  Single clicks on the target are still captured automatically.");
        Console.WriteLine("  N           - return to automatic single-click capture.");
        Console.WriteLine("  T           - arm a double-click capture.");
        Console.WriteLine("  A           - bring the target app to the front again.");
        Console.WriteLine("  P           - pause the recording.");
        Console.WriteLine("  F           - finish and build the map.");
        Console.WriteLine("  C or Esc    - cancel this recording.");
        Console.WriteLine("  F1 or H     - show these controls again.");
    }

    private static void PrintRecorderAutoTabsConsoleHelp()
    {
        Console.WriteLine("Console controls remain available during auto tabs:");
        Console.WriteLine("  S           - skip the rest of auto tabs and return to manual capture.");
        Console.WriteLine("  F           - finish and build the map now.");
        Console.WriteLine("  C or Esc    - cancel this recording.");
        Console.WriteLine("  F1 or H     - show these controls again.");
    }

    private static void PrintRecorderPausedConsoleHelp()
    {
        Console.WriteLine("Console controls remain available while paused:");
        Console.WriteLine("  R           - resume recording and reactivate the target window.");
        Console.WriteLine("  F           - finish and build the map.");
        Console.WriteLine("  C or Esc    - cancel this recording.");
        Console.WriteLine("  F1 or H     - show these controls again.");
    }

    private static int HelpExport()
    {
        PrintHelpHeader();
        Console.WriteLine("""
            JSON VALIDATE       - Validate graph JSON. EXPORT JSON VALIDATE <path>.
            JSON DELETE         - Trash managed graph JSON. EXPORT JSON DELETE <catalog-path> [--yes].
            MAP                 - Export human-readable JSON. EXPORT MAP <map-id> [format=json|ui-atlas_flat|sqlite].
            MAP VALIDATE        - Validate any exported map format and its hash. EXPORT MAP VALIDATE <path>.
            MAP DELETE          - Trash any managed map export and its hash. EXPORT MAP DELETE <catalog-path> [--yes].
            """);
        return 0;
    }

    private static int List(string[] args)
    {
        if (args.Length == 0 || args.Length == 1 && IsCommand(args[0], "help")) return HelpList();
        if (args.Length != 1) return Fail("list requires windows, recordings, or maps.");
        return args[0].ToLowerInvariant() switch
        {
            "windows" => ListWindows(),
            "recordings" => ListRecordings(),
            "maps" => ListMaps(),
            _ => Fail("list requires windows, recordings, or maps.")
        };
    }

    private static int ListWindows()
    {
        var windows = WindowCatalog.ListTopLevelWindows();
        Console.WriteLine("HWND\tPROCESS\tTITLE");
        foreach (var window in windows)
            Console.WriteLine($"0x{window.Hwnd:X}\t{window.ProcessName}\t{SanitizeConsole(window.Title)}");
        if (windows.Count == 0) Console.WriteLine("(none)");
        return 0;
    }

    private static int ListRecordings()
    {
        var recordings = Catalog().ListRecordings();
        Console.WriteLine("ID\tSTARTED UTC\tOUTCOME\tFRAMES\tPROCESS");
        foreach (var item in recordings)
            Console.WriteLine($"{item.Id}\t{item.StartedUtc:O}\t{item.Outcome}\t{item.FrameCount}\t{SanitizeConsole(item.ProcessName)}");
        if (recordings.Count == 0) Console.WriteLine("(none)");
        return 0;
    }

    private static int ListMaps()
    {
        var catalog = Catalog();
        CatalogMapRecovery.RecoverCompletedMaps(catalog);
        var maps = catalog.ListMaps();
        Console.WriteLine("ID\tBUILT UTC\tSTATUS\tNODES\tEDGES");
        foreach (var item in maps)
            Console.WriteLine($"{item.Id}\t{item.BuiltUtc:O}\t{item.Status}\t{item.NodeCount}\t{item.EdgeCount}");
        if (maps.Count == 0) Console.WriteLine("(none)");
        return 0;
    }

    private static async Task<int> RecordingCommand(string[] args)
    {
        if (args.Length == 0 || args.Length == 1 && IsCommand(args[0], "help")) return HelpRecording();
        if (IsCommand(args[0], "start"))
        {
            return args.Length switch
            {
                1 => await StartCatalogRecordingWithSelector().ConfigureAwait(false),
                2 => await StartCatalogRecording(WindowCatalog.Resolve(ParseHwnd(args[1]))).ConfigureAwait(false),
                _ => Fail("recording start accepts zero or one window HWND.")
            };
        }
        if (IsCommand(args[0], "resume"))
        {
            if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--manual-review")
                return Fail("recording resume requires one map path and optional --manual-review.");
            return await ResumeRecordingFromMapAsync(args[1], args.Length == 3).ConfigureAwait(false);
        }
        if (IsCommand(args[0], "validate"))
        {
            if (args.Length != 2) return Fail("recording validate requires one recording ID.");
            Catalog().EnsureSafe();
            return ValidateRecording(Catalog().RecordingPath(args[1]));
        }
        if (IsCommand(args[0], "delete"))
        {
            if (args.Length is < 2 or > 3) return Fail("recording delete requires one recording ID, ALL, and optional --yes.");
            if (args.Length == 3 && args[2] != "--yes") return Fail("The only recording delete option is --yes.");
            var catalog = Catalog();
            var confirmed = args.Contains("--yes", StringComparer.Ordinal);
            if (IsCommand(args[1], "all"))
            {
                var count = catalog.ListRecordings().Count;
                if (count == 0) { Console.WriteLine("No recordings to delete."); return 0; }
                if (!ConfirmDeleteAll("recordings", count, confirmed)) return Fail("Delete cancelled.");
                var archived = catalog.ArchiveAllRecordings();
                Console.WriteLine($"{archived} recording{(archived == 1 ? string.Empty : "s")} moved to the catalog trash.");
                return 0;
            }
            if (!ConfirmDelete("recording", args[1], confirmed)) return Fail("Delete cancelled.");
            catalog.ArchiveRecording(args[1]);
            Console.WriteLine("Recording moved to the catalog trash.");
            return 0;
        }
        return Fail("recording requires start, validate, or delete.");
    }

    private static async Task<int> StartCatalogRecording(WindowTarget target)
    {
        var catalog = Catalog();
        var id = catalog.CreateId(target.ProcessName);
        var workspace = RecorderWorkspace.CreateCatalogWorkspace(catalog, id, target.ProcessName);
        Console.WriteLine($"Map ID: {id}");
        using var panel = new RecordingControlPanel(id, target.ProcessName, target, allowTargetSelection: false);
        panel.Start();
        return await RunRecorderWorkflow(
            target,
            workspace,
            panel,
            catalog.EnsureSafe,
            createNewWorkspace: CreateNewCatalogWorkspace).ConfigureAwait(false);
    }

    private static async Task<int> StartCatalogRecordingWithSelector()
    {
        using var panel = new RecordingControlPanel("select-window", "Choose a window", initialTarget: null, allowTargetSelection: true);
        panel.Start();
        panel.ShowPreStartState();
        Console.WriteLine("Choose a window from the recorder toolbar. Start stays disabled until a target window is selected.");
        PrintRecorderStartConsoleHelp(targetSelectionRequired: true);
        var startCommand = (await WaitForStartCommandAsync(panel, CancellationToken.None).ConfigureAwait(false)).Trim().ToUpperInvariant();
        if (!TryResolveLaunchMode(startCommand, out var launchMode))
        {
            panel.SetStatus("Recording cancelled before start.");
            Console.WriteLine("Recording cancelled.");
            return 3;
        }

        var selectedTarget = panel.GetSelectedTarget();
        if (selectedTarget is null)
        {
            var message = panel.CanStartRecording
                ? "The selected window is no longer available."
                : "No target window was selected.";
            panel.SetStatus(message);
            Console.WriteLine(message);
            return 3;
        }

        WindowTarget target;
        try
        {
            target = WindowCatalog.Resolve(selectedTarget.Hwnd);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            panel.SetStatus("The selected window is no longer available.");
            Console.WriteLine("The selected window is no longer available.");
            return 3;
        }

        var catalog = Catalog();
        var id = catalog.CreateId(target.ProcessName);
        var workspace = RecorderWorkspace.CreateCatalogWorkspace(catalog, id, target.ProcessName);
        panel.UpdateRecordingContext(id, target.ProcessName, target);
        Console.WriteLine($"Map ID: {id}");
        return await RunRecorderWorkflow(
            target,
            workspace,
            panel,
            catalog.EnsureSafe,
            launchMode,
            createNewWorkspace: CreateNewCatalogWorkspace).ConfigureAwait(false);
    }

    private static async Task<int> ResumeRecordingFromMapAsync(string mapPath, bool manualReview = false)
    {
        var fullMapPath = Path.GetFullPath(mapPath);
        if (!File.Exists(fullMapPath))
            return Fail("The requested map file was not found.");

        var sessionManifestPath = Path.Combine(
            Path.GetDirectoryName(fullMapPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(fullMapPath) + ".session.json");
        if (!File.Exists(sessionManifestPath))
            return Fail("This map does not have a logical session manifest yet, so resume is not available.");

        var sessionManifest = LogicalMapSessionStore.Load(sessionManifestPath);
        var target = ResolveResumeTarget(sessionManifest.ProcessName);
        if (target is null)
        {
            Console.WriteLine($"Open the target application first and try again. Expected process: {sessionManifest.ProcessName}.");
            return 3;
        }

        var defaultExportPath = Path.ChangeExtension(fullMapPath, ".json");
        var workspace = RecorderWorkspace.CreateExistingWorkspace(fullMapPath, defaultExportPath, sessionManifestPath, sessionManifest);
        // Resume is a hand-off, not a second recorder. Close any older control panel
        // before this process exposes the resumed panel.
        if (!RecordingPanelCoordinator.CloseOtherRecorderPanels(TimeSpan.FromSeconds(2)))
            return Fail("The existing UiAtlas recorder is still busy. Resume was not opened, so a duplicate recorder was not created.");
        using var panel = new RecordingControlPanel(sessionManifest.LogicalMapId, sessionManifest.ProcessName, target, allowTargetSelection: false);
        panel.Start();
        panel.SetStatus("Resuming recording on the selected map...");
        Console.WriteLine($"Resuming map {sessionManifest.LogicalMapId} on {target.ProcessName} (HWND 0x{target.RootOwnerHwnd:X}).");
        return await RunRecorderWorkflow(
            target,
            workspace,
            panel,
            () => { },
            initialMode: manualReview ? SessionLaunchMode.Manual : null,
            restoreExistingHighlights: true,
            createNewWorkspace: CreateNewCatalogWorkspace,
            showResumeChooserInitially: !manualReview).ConfigureAwait(false);
    }

    private static RecorderWorkspace CreateNewCatalogWorkspace(WindowTarget target)
    {
        var catalog = Catalog();
        catalog.EnsureSafe();
        var id = catalog.CreateId(target.ProcessName);
        return RecorderWorkspace.CreateCatalogWorkspace(catalog, id, target.ProcessName);
    }

    private static WindowTarget? ResolveResumeTarget(string processName)
    {
        var candidates = WindowCatalog.ListTopLevelWindows()
            .Where(window => string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var foregroundRoot = WindowCatalog.ForegroundRootOwnerHwnd();
        return candidates
            .OrderByDescending(window => window.RootOwnerHwnd == foregroundRoot)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static WindowTarget? RefreshRecordingTarget(WindowTarget current, string processName)
    {
        try
        {
            // Preserve the visible representative. Legacy Delphi applications
            // often use a hidden zero-sized TApplication window as root owner.
            var refreshed = WindowCatalog.Resolve(current.Hwnd);
            if (string.Equals(refreshed.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                return refreshed;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The original HWND may have been replaced while the previous map was being built.
        }

        return ResolveResumeTarget(processName);
    }

    private static async Task<int> MapCommand(string[] args)
    {
        if (args.Length == 0 || args.Length == 1 && IsCommand(args[0], "help")) return HelpMap();
        var catalog = Catalog();
        if (IsCommand(args[0], "build"))
        {
            if (args.Length != 2) return Fail("map build requires one recording ID.");
            var recordingPath = catalog.RecordingPath(args[1]);
            var sessionPath = catalog.MapSessionPath(args[1]);
            var existingSession = File.Exists(sessionPath)
                ? LogicalMapSessionStore.Load(sessionPath)
                : null;
            var recordingPaths = existingSession?.Recordings
                .Where(recording => recording is not null && File.Exists(recording.RecordingPath))
                .Select(recording => recording.RecordingPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            if (recordingPaths.Length == 0)
                recordingPaths = [recordingPath];
            catalog.EnsureSafe();
            foreach (var sourcePath in recordingPaths)
            {
                var recordingReport = RecordingBundleValidator.Validate(sourcePath);
                if (!recordingReport.IsValid) return PrintValidation(recordingReport);
            }
            catalog.EnsureSafe();
            var inputs = await Task.WhenAll(recordingPaths.Select(path =>
                LoadEnrichedRecordingGraphInputAsync(path, CancellationToken.None))).ConfigureAwait(false);
            var graph = new RecordingGraphBuilder().Build(inputs, args[1]);
            catalog.EnsureSafe();
            SqliteGraphStore.Save(graph, catalog.MapPath(args[1]));
            if (existingSession is null)
            {
                using var bundle = RecordingBundle.Open(recordingPath);
                var recordingManifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                var sessionManifest = LogicalMapSessionStore.AddRecording(
                    LogicalMapSessionStore.Create(
                        args[1], recordingManifest.Target?.ProcessName ?? string.Empty, recordingManifest.StartedUtc),
                    recordingManifest.SessionId,
                    recordingPath,
                    recordingManifest.EndedUtc);
                LogicalMapSessionStore.Save(sessionPath, sessionManifest);
            }
            Console.WriteLine($"Built map {args[1]}: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges. Semantic hash: {graph.Metadata.SemanticHash}");
            return 0;
        }
        if (IsCommand(args[0], "validate"))
        {
            if (args.Length != 2) return Fail("map validate requires one map ID.");
            catalog.EnsureSafe();
            return ValidateGraph(catalog.MapPath(args[1]));
        }
        if (IsCommand(args[0], "quality"))
        {
            if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--strict")
                return Fail("map quality requires one map ID and optional --strict.");
            catalog.EnsureSafe();
            var graph = new UiGraphReader().Load(catalog.MapPath(args[1]));
            var report = MapQualityInspector.Inspect(graph, catalog.MatchingRecordingPaths(args[1]));
            MapQualityInspector.Print(report);
            return args.Length == 3 && report.NeedsReview ? 4 : 0;
        }
        if (IsCommand(args[0], "inspect"))
        {
            if (args.Length is < 4 or > 6 || !IsCommand(args[2], "world") ||
                (args.Length > 4 && (args.Length != 6 || !IsCommand(args[4], "--query"))))
                return Fail("map inspect requires: <map-id> world <streams|raw|semantic|shared> [--query <text>].");
            var inspectArgs = new List<string> { catalog.MapPath(args[1]), "--world", args[3].ToLowerInvariant() };
            if (args.Length == 6) { inspectArgs.Add("--query"); inspectArgs.Add(args[5]); }
            catalog.EnsureSafe();
            return Inspect(inspectArgs.ToArray());
        }
        if (IsCommand(args[0], "open"))
        {
            if (args.Length != 2) return Fail("map open requires one map ID.");
            catalog.EnsureSafe();
            var recordingPath = catalog.MatchingRecordingPath(args[1]);
            return Open(recordingPath is not null
                ? [catalog.MapPath(args[1]), recordingPath]
                : [catalog.MapPath(args[1])]);
        }
        if (IsCommand(args[0], "delete"))
        {
            if (args.Length is < 2 or > 3) return Fail("map delete requires one map ID, ALL, and optional --yes.");
            if (args.Length == 3 && args[2] != "--yes") return Fail("The only map delete option is --yes.");
            var confirmed = args.Contains("--yes", StringComparer.Ordinal);
            if (IsCommand(args[1], "all"))
            {
                var count = catalog.ListMaps().Count;
                if (count == 0) { Console.WriteLine("No maps to delete."); return 0; }
                if (!ConfirmDeleteAll("maps", count, confirmed)) return Fail("Delete cancelled.");
                var archived = catalog.ArchiveAllMaps();
                Console.WriteLine($"{archived} map{(archived == 1 ? string.Empty : "s")} moved to the catalog trash.");
                return 0;
            }
            if (!ConfirmDelete("map", args[1], confirmed)) return Fail("Delete cancelled.");
            catalog.ArchiveMap(args[1]);
            Console.WriteLine("Map moved to the catalog trash.");
            return 0;
        }
        if (!IsCommand(args[0], "export") || args.Length < 2)
            return Fail("map requires build, validate, inspect, quality, open, export, or delete.");
        if (IsCommand(args[1], "json"))
        {
            if (args.Length < 3) return Fail("map export json requires one map ID.");
            return ExportJsonFromCatalog(args[2..]);
        }

        // Compatibility alias retained for the original catalog CLI shape.
        return args.Contains("--ui-atlas", StringComparer.Ordinal)
            ? ExportMapFromCatalog([.. args.Where(argument => argument != "--ui-atlas"), "format=ui-atlas_flat"])
            : ExportJsonFromCatalog(args);
    }

    private static async Task<RecordingGraphInput> LoadEnrichedRecordingGraphInputAsync(
        string recordingPath,
        CancellationToken cancellationToken)
    {
        using var bundle = RecordingBundle.Open(recordingPath);
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var statebook = bundle.ReadJson<DerivedStatebook>("derived/statebook.json");
        var interactions = bundle.Entries.Contains("raw/interactions.jsonl")
            ? bundle.ReadText("raw/interactions.jsonl")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<InteractionObservation>(line, JsonDefaults.Options)
                    ?? throw new InvalidDataException("Null interaction observation."))
                .OrderBy(interaction => interaction.Sequence)
                .ToArray()
            : [];
        var representativeFrames = statebook.RepresentativeFrames
            .Concat(interactions.Select(interaction => interaction.SourceFrameSequence))
            .Concat(interactions.SelectMany(interaction => interaction.ResultFrameSequences))
            .ToHashSet();
        var observations = bundle.Entries
            .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                            entry.EndsWith(".json", StringComparison.Ordinal))
            .Select(bundle.ReadJson<FrameObservation>)
            .Where(frame => representativeFrames.Contains(frame.Sequence))
            .OrderBy(frame => frame.Sequence)
            .ToArray();
        var repaired = await OfflineRecordingEnricher.RepairAsync(
            bundle, observations, cancellationToken).ConfigureAwait(false);
        return new(manifest, repaired, interactions);
    }

    private static int ExportJsonFromCatalog(string[] args)
    {
        if (args.Length == 0) return Fail("map export json requires one map ID.");
        ValidateOptions(args, 1, ["--full", "--acknowledge-sensitive-evidence", "--overwrite"], ["--out"]);
        var catalog = Catalog();
        var id = args[0];
        catalog.EnsureSafe();
        var graph = SqliteGraphStore.Load(catalog.MapPath(id));
        var full = args.Contains("--full", StringComparer.Ordinal);
        var output = OptionalOption(args, "--out") ?? catalog.DefaultExportPath(id);
        if (File.Exists(output) && !args.Contains("--overwrite", StringComparer.Ordinal))
            return Fail("Export already exists. Use --out with a new path or pass --overwrite.");
        if (full && !args.Contains("--acknowledge-sensitive-evidence", StringComparer.Ordinal))
            return Fail("Full export requires --acknowledge-sensitive-evidence.");
        catalog.EnsureSafe();
        GraphJsonStore.Save(GraphExport.ApplyProfile(graph, full), output);
        Console.WriteLine($"Exported {Path.GetFullPath(output)} using {(full ? FormatVersions.FullEvidenceProfile : FormatVersions.SafeExportProfile)}.");
        return 0;
    }

    private static bool ConfirmDelete(string kind, string id, bool confirmed)
    {
        if (confirmed) return true;
        Console.Write($"Move {kind} '{SanitizeConsole(id)}' to catalog trash? Type DELETE: ");
        return string.Equals(Console.ReadLine(), "DELETE", StringComparison.Ordinal);
    }

    private static bool ConfirmDeleteAll(string kind, int count, bool confirmed)
    {
        if (confirmed) return true;
        Console.Write($"Move ALL {count} {kind} to catalog trash? Type DELETE ALL: ");
        return string.Equals(Console.ReadLine(), "DELETE ALL", StringComparison.Ordinal);
    }

    private static int ExportCommand(string[] args)
    {
        if (args.Length == 0 || args.Length == 1 && IsCommand(args[0], "help")) return HelpExport();
        if (IsCommand(args[0], "json"))
        {
            if (args.Length < 3) return Fail("export json requires validate or delete followed by a file-system path.");
            return args[1].ToLowerInvariant() switch
            {
                "validate" when args.Length == 3 => ValidateJsonExport(args[2]),
                "delete" when args.Length == 3 => DeleteExport(args[2], "JSON", confirmed: false, includeHashSidecar: false),
                "delete" when args.Length == 4 && args[3] == "--yes" => DeleteExport(args[2], "JSON", confirmed: true, includeHashSidecar: false),
                _ => Fail("export json requires: validate <path> or delete <path> [--yes].")
            };
        }
        if (IsCommand(args[0], "map") || IsCommand(args[0], "ui-atlas"))
        {
            if (args.Length < 2) return Fail("export map requires a map ID, validate, or delete.");
            if (IsCommand(args[1], "validate"))
                return args.Length == 3 ? ValidateMapExportFile(args[2]) : Fail("export map validate requires one file-system path.");
            if (IsCommand(args[1], "delete"))
                return args.Length switch
                {
                    3 => DeleteExport(args[2], "map", confirmed: false, includeHashSidecar: true, allowDatabase: true),
                    4 when args[3] == "--yes" => DeleteExport(args[2], "map", confirmed: true, includeHashSidecar: true, allowDatabase: true),
                    _ => Fail("export map delete requires one file-system path and optional --yes.")
                };
            return ExportMapFromCatalog(args[1..]);
        }
        return Export(args);
    }

    private static int ExportMapFromCatalog(string[] args)
    {
        if (args.Length == 0) return Fail("export map requires one map ID.");
        var formatArguments = args.Where(argument => argument.StartsWith("format=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (formatArguments.Length > 1) return Fail("export map accepts only one format value.");
        var format = formatArguments.Length == 0 ? "json" : formatArguments[0]["format=".Length..].ToLowerInvariant();
        if (format is not ("json" or "ui-atlas_flat" or "sqlite"))
            return Fail("export map format must be json, ui-atlas_flat, or sqlite.");
        var options = args.Where(argument => !argument.StartsWith("format=", StringComparison.OrdinalIgnoreCase)).ToArray();
        ValidateOptions(options, 1, ["--acknowledge-sensitive-identities", "--overwrite"], ["--out", "--project-id"]);
        var catalog = Catalog();
        var id = options[0];
        var defaultSuffix = format switch { "ui-atlas_flat" => "-ui-atlas-flat.json", "sqlite" => "-map.db", _ => "-map.json" };
        var output = OptionalOption(options, "--out") ?? Path.Combine(catalog.ExportsDirectory, id + defaultSuffix);
        if (File.Exists(output) && !options.Contains("--overwrite", StringComparer.Ordinal))
            return Fail("Export already exists. Use --out with a new path or pass --overwrite.");
        var acknowledged = options.Contains("--acknowledge-sensitive-identities", StringComparer.Ordinal);
        if (!acknowledged)
        {
            Console.Write("This export retains identity-bearing UI data. Type EXPORT to continue: ");
            acknowledged = string.Equals(Console.ReadLine(), "EXPORT", StringComparison.Ordinal);
        }
        if (!acknowledged) return Fail("UiAtlas export cancelled.");
        catalog.EnsureSafe();
        var graph = SqliteGraphStore.Load(catalog.MapPath(id));
        catalog.EnsureSafe();
        var hash = format switch
        {
            "json" => HumanReadableMapExporter.Publish(graph, output, true),
            "ui-atlas_flat" => UiAtlasVNextCompatibilityExporter.Publish(graph, output, OptionalOption(options, "--project-id") ?? id, true),
            _ => SqliteMapExporter.Publish(graph, output)
        };
        Console.WriteLine($"Exported {Path.GetFullPath(output)} using format={format}.");
        Console.WriteLine($"SHA-256: {hash}");
        return 0;
    }

    private static int ValidateMapExportFile(string path)
    {
        var resolved = ResolveExistingFile(path);
        if (Path.GetExtension(resolved).Equals(".db", StringComparison.OrdinalIgnoreCase))
            return PrintValidation(SqliteMapExporter.ValidateFile(resolved));
        try
        {
            using var input = File.OpenRead(resolved);
            using var document = JsonDocument.Parse(input);
            return document.RootElement.TryGetProperty("formatVersion", out var format) && format.GetString() == HumanReadableMapExporter.FormatVersion
                ? PrintValidation(HumanReadableMapExporter.ValidateFile(resolved))
                : PrintValidation(UiAtlasVNextCompatibilityValidator.ValidateFile(resolved));
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"error: export.invalid at file: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
            Console.WriteLine("invalid");
            return 4;
        }
    }

    private static int ValidateJsonExport(string path)
    {
        try { return PrintValidation(GraphValidator.Validate(new UiGraphReader().Load(ResolveJsonFile(path)))); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"error: export.invalid at file: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
            Console.WriteLine("invalid");
            return 4;
        }
    }

    private static int ValidateUiAtlasExportFile(string path)
    {
        try
        {
            var resolved = ResolveJsonFile(path);
            return PrintValidation(UiAtlasVNextCompatibilityValidator.ValidateFile(resolved));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"error: export.invalid at file: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
            Console.WriteLine("invalid");
            return 4;
        }
    }

    private static int DeleteExport(string path, string kind, bool confirmed, bool includeHashSidecar, bool allowDatabase = false)
    {
        var resolved = allowDatabase ? ResolveExistingFile(path) : ResolveJsonFile(path);
        if (allowDatabase && !new[] { ".json", ".db" }.Contains(Path.GetExtension(resolved), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Map export path must identify a JSON or SQLite file.", nameof(path));
        var catalog = Catalog();
        if (!catalog.IsCatalogExport(resolved))
            throw new InvalidOperationException("Only a top-level catalog export can be deleted by this command.");
        var sidecar = includeHashSidecar && File.Exists(resolved + ".sha256")
            ? ResolveExistingFile(resolved + ".sha256")
            : null;
        if (!confirmed)
        {
            Console.Write($"Move the {kind} export '{SanitizeConsole(resolved)}' to recoverable storage? Type DELETE: ");
            confirmed = string.Equals(Console.ReadLine(), "DELETE", StringComparison.Ordinal);
        }
        if (!confirmed) return Fail("Delete cancelled.");
        catalog.EnsureSafe();
        catalog.ArchiveExport(resolved);
        if (sidecar is not null)
        {
            try
            {
                catalog.EnsureSafe();
                catalog.ArchiveExport(sidecar);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"partial: primary export was moved, but its checksum was not: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
                return 3;
            }
        }
        Console.WriteLine($"{kind} export moved to catalog trash.");
        return 0;
    }

    private static string ResolveJsonFile(string path)
    {
        var resolved = ResolveExistingFile(path);
        if (!Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Export path must identify a JSON file.", nameof(path));
        return resolved;
    }

    private static string ResolveExistingFile(string path)
    {
        var resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved)) throw new FileNotFoundException("Export file was not found.");
        for (FileSystemInfo? item = new FileInfo(resolved); item is not null; item = item switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null
        })
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Export paths cannot contain links or reparse points.");
        return resolved;
    }

    private static LocalArtifactCatalog Catalog() =>
        new(Environment.GetEnvironmentVariable("UI-ATLAS_DATA_HOME"));

    private static async Task<int> Record(string[] args)
    {
        var hwnd = ParseHwnd(RequiredOption(args, "--hwnd"));
        var output = RequiredOption(args, "--out");
        var target = WindowCatalog.Resolve(hwnd);
        var workspace = RecorderWorkspace.CreateStandaloneWorkspace(output, target.ProcessName);
        using var panel = new RecordingControlPanel(Path.GetFileNameWithoutExtension(output), target.ProcessName, target, allowTargetSelection: false);
        panel.Start();
        return await RunRecorderWorkflow(target, workspace, panel).ConfigureAwait(false);
    }

    private static async Task<int> RunRecorderWorkflow(
        WindowTarget target,
        RecorderWorkspace workspace,
        RecordingControlPanel panel,
        Action? beforeStart = null,
        SessionLaunchMode? initialMode = null,
        bool restoreExistingHighlights = false,
        Func<WindowTarget, RecorderWorkspace>? createNewWorkspace = null,
        bool showResumeChooserInitially = false)
    {
        SessionLaunchMode? nextMode = initialMode;
        var restoreHighlightsWithNextSession = restoreExistingHighlights;
        var resumeChooserPending = showResumeChooserInitially;
        while (true)
        {
            if (nextMode is null)
            {
                if (!HasBuiltMap(workspace))
                {
                    Console.WriteLine($"Target: {target.ProcessName} (HWND 0x{target.RootOwnerHwnd:X})");
                    Console.WriteLine("Review the target and choose Manual or Auto labels from the Start control. Both begin with one quick surface scan. Press C to cancel.");
                    PrintRecorderStartConsoleHelp(targetSelectionRequired: false);
                    panel.ShowPreStartState();
                    var startCommand = (await WaitForStartCommandAsync(panel, CancellationToken.None).ConfigureAwait(false)).Trim().ToUpperInvariant();
                    if (startCommand == PanelCloseCommand)
                        return 0;
                    if (!TryResolveLaunchMode(startCommand, out var selectedMode))
                    {
                        panel.SetStatus("Recording cancelled before start.");
                        Console.WriteLine("Recording cancelled.");
                        return 3;
                    }

                    nextMode = selectedMode;
                }
                else
                {
                    panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                    if (resumeChooserPending)
                    {
                        panel.ShowSessionModeChooser(resumeMode: true);
                        resumeChooserPending = false;
                    }

                    var mapReadyCommand = (await WaitForMapReadyCommandAsync(panel, CancellationToken.None).ConfigureAwait(false)).Trim().ToUpperInvariant();
                    if (mapReadyCommand == PanelCloseCommand)
                        return 0;
                    if (mapReadyCommand == "FOCUS_READY")
                    {
                        var focused = await ActivateReadyTargetWindowAsync(target, CancellationToken.None).ConfigureAwait(false);
                        panel.SetStatus(focused ? "Target app focused." : "Focus failed for the target app.");
                        continue;
                    }
                    if (!TryResolveLaunchMode(mapReadyCommand, out var selectedMode))
                        return 0;

                    if (IsNewMapLaunchCommand(mapReadyCommand))
                    {
                        if (createNewWorkspace is null)
                        {
                            panel.SetStatus("Start a new recording from the main recorder window.");
                            continue;
                        }

                        var selectedNewTarget = panel.GetSelectedTarget();
                        if (selectedNewTarget is null)
                        {
                            panel.SetStatus("Choose an available window before starting a new map.");
                            panel.ShowPreStartState();
                            continue;
                        }

                        target = selectedNewTarget;
                        workspace = createNewWorkspace(target);
                        panel.UpdateRecordingContext(workspace.LogicalMapId, target.ProcessName, target);
                        Console.WriteLine($"Starting new map {workspace.LogicalMapId}.");
                        restoreHighlightsWithNextSession = false;
                    }
                    else
                    {
                        restoreHighlightsWithNextSession = true;
                    }

                    nextMode = selectedMode;
                }
            }

            var selectedLaunchMode = nextMode.Value;
            var launchOptions = panel.SelectedLaunchOptions;
            nextMode = null;
            var restoreHighlightsForSession = restoreHighlightsWithNextSession;
            restoreHighlightsWithNextSession = false;
            var refreshedTarget = RefreshRecordingTarget(target, workspace.ProcessName);
            if (refreshedTarget is null)
            {
                panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                panel.SetStatus("Recording could not restart because the selected app window is no longer available.");
                Console.WriteLine("The selected application window is no longer available. Open it and try again.");
                continue;
            }

            target = refreshedTarget;
            panel.UpdateRecordingContext(workspace.LogicalMapId, target.ProcessName, target);

            RecorderSessionTarget sessionTarget;
            RecordingPhaseOutcome outcome;
            try
            {
                sessionTarget = workspace.CreateNextSession();
                Console.WriteLine($"Recording session ID: {sessionTarget.SessionId}");
                using var overlay = new RecordingHighlightOverlay(target);
                overlay.Start();
                if (restoreHighlightsForSession)
                {
                    var restoredHighlights = RecordingHighlightHistory.Load(workspace.RecordingPaths());
                    foreach (var highlight in restoredHighlights)
                    {
                        overlay.AddHistoricalHighlights(
                            highlight.CapturedRootBounds,
                            highlight.LayerKey,
                            [highlight.Bounds],
                            visibleLayerKey: null);
                    }
                    if (restoredHighlights.Count > 0)
                        Console.WriteLine($"Restored {restoredHighlights.Count} previous click highlights in lilac.");
                }
                beforeStart?.Invoke();
                outcome = await RunManualRecording(
                    target, sessionTarget.RecordingPath, panel, overlay, selectedLaunchMode,
                    workspace, sessionTarget.SessionId, launchOptions).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecorderWorkflowRecoverable(ex))
            {
                panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                panel.SetStatus("Recording could not restart. The recorder is still open; choose Manual or Auto labels to try again.");
                Console.Error.WriteLine($"error: recording.restart-failed: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
                continue;
            }

            var panelClosed = panel.IsClosed;
            if (outcome is RecordingPhaseOutcome.Completed or RecordingPhaseOutcome.PartialCompleted)
            {
                var isPartialCompletion = outcome == RecordingPhaseOutcome.PartialCompleted;
                var mergedRecordingPaths = workspace.RecordingPaths().Append(sessionTarget.RecordingPath).ToArray();
                try
                {
                    if (selectedLaunchMode == SessionLaunchMode.AutoTabs)
                    {
                        var quality = AutoCaptureQualityGate.Inspect(mergedRecordingPaths, workspace.AutoMapping);
                        if (!quality.IsSufficient)
                            throw new InvalidDataException(
                                $"Automatic capture is incomplete: {quality.ControlCount} controls across {quality.FrameCount} captured frames " +
                                $"({quality.EmptyFrameCount} empty). The bundle was retained, but an empty map was not built.");
                    }
                    panel.SetStatus(isPartialCompletion
                        ? "Finalizing the partial recording bundle. Large maps can take a minute; keep UiAtlas open."
                        : "Finalizing the new recording bundle. Large maps can take a minute; keep UiAtlas open.");
                    Console.WriteLine(isPartialCompletion
                        ? "Finalizing the partial recording bundle..."
                        : "Finalizing the new recording bundle...");
                    beforeStart?.Invoke();
                    panel.SetStatus("Merging this recording into the current map. Large maps can take a minute; keep UiAtlas open.");
                    Console.WriteLine("Merging the new recording bundle into the current map...");
                    var graph = SpeculativeGraphProjector.Apply(
                        new RecordingGraphBuilder().Build(mergedRecordingPaths, workspace.LogicalMapId),
                        workspace.SpeculativePlanning);
                    if (launchOptions.CaptureCustomerData)
                    {
                        panel.SetStatus("Creating the separate customer-data package...");
                        Console.WriteLine("Customer-data capture was explicitly enabled. Creating a read-only source snapshot and a separate package...");
                        var customerData = await CustomerDataCaptureCoordinator.TryCaptureAsync(
                            target, graph, workspace.MapPath, sessionTarget.SessionId).ConfigureAwait(false);
                        if (!customerData.Succeeded && File.Exists(workspace.MapPath))
                            graph = CustomerDataCaptureCoordinator.PreserveMetadata(
                                graph, SqliteGraphStore.Load(workspace.MapPath));
                        else
                            graph = CustomerDataCaptureCoordinator.AttachMetadata(graph, customerData);
                        if (customerData.Succeeded)
                            Console.WriteLine($"Customer package: {customerData.PackageDirectory} ({customerData.RecordCount} records, SHA-256 {customerData.DataSha256})");
                        else
                            Console.Error.WriteLine($"warning: customer-data.{customerData.Status}: {customerData.Diagnostic}");
                    }
                    else if (File.Exists(workspace.MapPath))
                    {
                        graph = CustomerDataCaptureCoordinator.PreserveMetadata(
                            graph, SqliteGraphStore.Load(workspace.MapPath));
                    }
                    beforeStart?.Invoke();
                    panel.SetStatus("Saving the updated map. Almost done—keep UiAtlas open.");
                    Console.WriteLine("Saving the updated map...");
                    SqliteGraphStore.Save(graph, workspace.MapPath);
                    var qualityReport = MapQualityInspector.Inspect(graph, mergedRecordingPaths);
                    try
                    {
                        beforeStart?.Invoke();
                        workspace.AddCompletedSession(sessionTarget.SessionId, sessionTarget.RecordingPath);
                    }
                    catch (Exception ex) when (IsRecorderWorkflowRecoverable(ex))
                    {
                        panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                        panel.SetStatus("Map updated, but session history could not be saved. Resume may be unavailable.");
                        Console.Error.WriteLine($"error: session-manifest.failed at {BundleSecurity.SafeDiagnostic(Path.GetFullPath(workspace.SessionManifestPath))}: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
                        Console.WriteLine($"Map ID: {workspace.LogicalMapId} ({graph.Nodes.Count} nodes, {graph.Edges.Count} edges)");
                        Console.WriteLine("The map was saved, but the session history could not be updated. Resume may be unavailable until this is fixed.");
                        if (panelClosed)
                            return 0;
                        continue;
                    }
                    if (selectedLaunchMode == SessionLaunchMode.RescanCurrentScreen)
                    {
                        var snapshot = workspace.QuickMapSnapshots.Last(item => item.SessionId == sessionTarget.SessionId);
                        panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                        panel.SetStatus($"Current screen rescanned. Visible: {snapshot.VisibleControlCount}. Confirmed: {snapshot.ConfirmedControlCount}. Coverage gaps: {snapshot.CoverageGapCount}.");
                        Console.WriteLine($"Current screen rescanned. Visible: {snapshot.VisibleControlCount}. Confirmed: {snapshot.ConfirmedControlCount}. Observed: {snapshot.ObservedControlCount}. Coverage gaps: {snapshot.CoverageGapCount}. Coverage: {snapshot.CoverageStatus}.");
                        try
                        {
                            OpenMapViewer(workspace.MapPath, sessionTarget.RecordingPath);
                        }
                        catch (Exception ex) when (IsRecorderWorkflowRecoverable(ex))
                        {
                            Console.Error.WriteLine($"warning: rescan.open-failed: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
                            panel.SetStatus("The rescan was saved. Use Open map to view it.");
                        }
                    }
                    else if (isPartialCompletion)
                    {
                        panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                        panel.SetStatus(AutoPassQuotaSavedMessage + " " + qualityReport.UserSummary());
                        Console.WriteLine("Auto labels reached the capture limit. The partial map was saved. Use Resume to continue this session.");
                    }
                    else
                    {
                        panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                        panel.SetStatus(qualityReport.UserSummary());
                    }
                    MapQualityInspector.Print(qualityReport);
                    Console.WriteLine($"Map ID: {workspace.LogicalMapId} ({graph.Nodes.Count} nodes, {graph.Edges.Count} edges)");
                    Console.WriteLine("The recording panel now has Open map, Resume, and Export controls.");
                }
                catch (Exception ex) when (IsRecorderWorkflowRecoverable(ex))
                {
                    workspace.DiscardQuickMapSnapshot(sessionTarget.SessionId);
                    if (HasBuiltMap(workspace))
                        panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                    else
                        panel.ShowPreStartState();
                    panel.SetStatus(ex is InvalidDataException && ex.Message.StartsWith("Automatic capture is incomplete:", StringComparison.Ordinal)
                        ? "Auto capture contained no usable controls. The recording was retained, but an empty map was not built."
                        : "Finish map failed. The recording bundle was retained.");
                    Console.Error.WriteLine($"error: finish-map.failed at {BundleSecurity.SafeDiagnostic(Path.GetFullPath(sessionTarget.RecordingPath))}: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
                    Console.WriteLine($"Retained recording bundle: {Path.GetFullPath(sessionTarget.RecordingPath)}");
                    Console.WriteLine("Map build failed. The toolbar stayed open so you can try again.");
                }
                if (panelClosed)
                    return 0;
                continue;
            }

            if (outcome == RecordingPhaseOutcome.Failed)
            {
                workspace.DiscardQuickMapSnapshot(sessionTarget.SessionId);
                if (HasBuiltMap(workspace))
                    panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
                else
                    panel.ShowPreStartState();
                if (panelClosed)
                    return 0;
                continue;
            }

            workspace.DiscardQuickMapSnapshot(sessionTarget.SessionId);
            if (!HasBuiltMap(workspace))
            {
                panel.ShowPreStartState();
                Console.WriteLine("Recording session cancelled. The toolbar remains open so you can start again.");
                if (panelClosed)
                    return 0;
                continue;
            }

            panel.MarkMapReady(workspace.MapPath, null, workspace.DefaultExportPath);
            if (panelClosed)
                return 0;
        }
    }

    private static async Task<RecordingPhaseOutcome> RunManualRecording(
        WindowTarget target,
        string output,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        SessionLaunchMode launchMode,
        RecorderWorkspace workspace,
        string sessionId,
        RecordingLaunchOptions launchOptions)
    {
        // Switch the visible shell before any disk history or provider setup.
        // Those operations are bounded, but the user must never be left looking
        // at the mode chooser after recording has already been requested.
        panel.ShowActiveRecordingState();
        panel.SetAutoPassActive(launchMode == SessionLaunchMode.AutoTabs);
        panel.SetStatus("Stage 1 of 5: capturing the current screen before scanning controls.");
        await using var session = new ManualRecordingSession(
            target,
            output,
            overlay.HideForScreenshotAsync,
            overlay.RestoreAfterScreenshot);
        session.Start(explicitConsent: true);
        // The click that chose a recording mode and any user input used while the
        // initial bounded scan is running must never become the first recorded
        // application action. Arm input only after the target has been restored.
        session.SetInputCapturePaused(true);
        using var speculative = new SpeculativePlanningCoordinator(workspace, target);
        AdaptiveCaptureCoordinator? adaptive = null;
        AutoMappingCampaignTracker? autoMapping = null;
        if (launchMode == SessionLaunchMode.AutoTabs || workspace.AutoMapping is not null)
        {
            var recordingEvidence = workspace.RecordingEvidence();
            workspace.AdoptReferencedRecordingEvidence(recordingEvidence);
            var recovered = AutoMappingCampaignRecovery.Recover(
                workspace.AutoMapping, workspace.Recordings, DateTimeOffset.UtcNow);
            workspace.SaveAutoMapping(recovered);
            autoMapping = new AutoMappingCampaignTracker(
                recovered, workspace.SaveAutoMapping, DateTimeOffset.UtcNow);
        }
        using var cancellation = new CancellationTokenSource();
        var pauseRequested = 0;
        var panelCancelRequested = 0;
        var autoStopRequested = 0;
        var autoPassGate = new object();
        CancellationTokenSource? activeAutoPassCancellation = null;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        void PauseInputCapture()
        {
            Volatile.Write(ref pauseRequested, 1);
            session.SetInputCapturePaused(true);
        }
        void CancelFromPanel()
        {
            Volatile.Write(ref panelCancelRequested, 1);
            cancellation.Cancel();
        }
        void StopAutoPassFromPanel()
        {
            Volatile.Write(ref autoStopRequested, 1);
            lock (autoPassGate)
                activeAutoPassCancellation?.Cancel();
        }
        void ReportAdaptiveStatus(string message)
        {
            Console.WriteLine(message);
            panel.SetStatus(message);
        }
        async Task<AutoTabsOutcome> RunCancelableAutoPassAsync(FrameObservation frame, AdaptiveCaptureCoordinator coordinator)
        {
            using var autoCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            lock (autoPassGate)
                activeAutoPassCancellation = autoCancellation;
            if (Volatile.Read(ref autoStopRequested) != 0)
                autoCancellation.Cancel();

            try
            {
                return await RunAutoTabsPassAsync(
                    session, panel, overlay, frame, coordinator, autoCancellation.Token,
                    autoMapping!, sessionId, speculative).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                autoCancellation.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                panel.SetStatus("Auto stopped. Manual single-click capture is starting...");
                Console.WriteLine("Auto tabs stopped immediately. No further automatic clicks will be sent.");
                return AutoTabsOutcome.ContinueManual;
            }
            finally
            {
                lock (autoPassGate)
                {
                    if (ReferenceEquals(activeAutoPassCancellation, autoCancellation))
                        activeAutoPassCancellation = null;
                }
                Volatile.Write(ref autoStopRequested, 0);
            }
        }
        Console.CancelKeyPress += cancelHandler;
        panel.PauseRequested += PauseInputCapture;
        panel.CancelRequested += CancelFromPanel;
        panel.AutoPassStopRequested += StopAutoPassFromPanel;
        try
        {
            var manualClickCursorUtc = DateTimeOffset.UtcNow;
            Console.WriteLine("Recording active. Target clicks advance automatically after each completed capture.");
            if (launchMode == SessionLaunchMode.RescanCurrentScreen)
            {
                panel.SetStatus("Rescanning the current screen...");
                if (!await ActivateTargetWithOutlineAsync(session, overlay, cancellation.Token).ConfigureAwait(false))
                {
                    session.Cancel(retain: false);
                    panel.SetStatus("Rescan could not focus the selected app.");
                    return RecordingPhaseOutcome.Failed;
                }

                var scan = await overlay.RunHiddenAsync(
                    () => CaptureAndRegisterInitialSurfaceAsync(
                        session, panel, workspace, speculative, sessionId, campaign: null,
                        "rescan-current-screen", cancellation.Token, launchOptions.EnableHoverAndFocusDiscovery),
                    cancellation.Token).ConfigureAwait(false);
                ShowObservedSurfaceHighlights(overlay, scan.Frame);
                if (!scan.HasUsableControls)
                {
                    session.Cancel(retain: false);
                    panel.SetStatus("Rescan found no usable controls. The existing map was not changed.");
                    return RecordingPhaseOutcome.Failed;
                }

                session.SetInputCapturePaused(true);
                panel.BeginMapBuild();
                if (scan.Status == QuickMapCaptureStatus.Partial)
                    session.CompletePartial();
                else
                    session.Complete();
                return RecordingPhaseOutcome.Completed;
            }
            if (launchMode == SessionLaunchMode.AutoTabs)
            {
                Console.WriteLine("Bringing the selected application to the front before the auto pass starts...");
                panel.SetStatus("Bringing the target app to the front...");
                var targetActivated = await ActivateTargetWithOutlineAsync(
                    session, overlay, cancellation.Token).ConfigureAwait(false);
                if (!targetActivated)
                {
                    panel.SetAutoPassActive(false);
                    Console.WriteLine("The selected application could not be brought to the front automatically. Auto labels will not click it; manual capture remains available.");
                }

                // Auto labels can spend several seconds on its first visual/OCR
                // pass. Keep real user clicks armed during that interval so the
                // mode never appears frozen and no early click is discarded.
                manualClickCursorUtc = DateTimeOffset.UtcNow;
                if (Volatile.Read(ref pauseRequested) == 0)
                    session.SetInputCapturePaused(false);
                QuickSurfaceScanResult initialScan;
                var initialSurfaceInvalidated = 0;
                using (var feedbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    var feedbackTask = ShowInitialManualClickFeedbackAsync(
                        session,
                        target,
                        panel,
                        overlay,
                        manualClickCursorUtc,
                        () => Interlocked.Exchange(ref initialSurfaceInvalidated, 1),
                        feedbackCancellation.Token);
                    try
                    {
                        initialScan = await overlay.RunHiddenAsync(
                            () => CaptureAndRegisterInitialSurfaceAsync(
                                session, panel, workspace, speculative, sessionId, autoMapping!.Snapshot(),
                                "auto-tabs-initial-surface", cancellation.Token, launchOptions.EnableHoverAndFocusDiscovery),
                            cancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        feedbackCancellation.Cancel();
                        try { await feedbackTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    }
                }
                // The provider walk and bounded visual probes can outlive a
                // transient foreground switch. Validate focus again immediately
                // before auto traversal, and retry one passive scan when the first
                // provider read returned nothing. Without this retry a temporary
                // UIA timeout silently degraded Auto labels into an empty Manual
                // session even though the selected application was still healthy.
                if (Volatile.Read(ref initialSurfaceInvalidated) == 0)
                {
                    targetActivated = await ReactivateTargetWindowAsync(
                        session, overlay, cancellation.Token).ConfigureAwait(false);
                    if (targetActivated && !initialScan.HasUsableControls)
                    {
                        session.AddCaptureHealth(
                            "auto-tabs",
                            "initial-scan-retry",
                            "The first provider scan returned no controls; the target was refocused and one passive retry was started.");
                        panel.SetStatus("The first scan returned no controls. Refocusing and retrying once...");
                        using var retryFeedbackCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        var retryFeedbackTask = ShowInitialManualClickFeedbackAsync(
                            session,
                            target,
                            panel,
                            overlay,
                            manualClickCursorUtc,
                            () => Interlocked.Exchange(ref initialSurfaceInvalidated, 1),
                            retryFeedbackCancellation.Token);
                        try
                        {
                            initialScan = await overlay.RunHiddenAsync(
                                () => CaptureAndRegisterInitialSurfaceAsync(
                                    session, panel, workspace, speculative, sessionId, autoMapping!.Snapshot(),
                                    "auto-tabs-initial-surface-retry", cancellation.Token,
                                    enableHoverAndFocusDiscovery: false),
                                cancellation.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            retryFeedbackCancellation.Cancel();
                            try { await retryFeedbackTask.ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                        }

                        if (Volatile.Read(ref initialSurfaceInvalidated) == 0)
                            targetActivated = await ReactivateTargetWindowAsync(
                                session, overlay, cancellation.Token).ConfigureAwait(false);
                        session.AddMarker(initialScan.HasUsableControls
                            ? "auto-tabs:initial-scan-retry-recovered"
                            : "auto-tabs:initial-scan-retry-empty");
                    }
                }
                if (Volatile.Read(ref initialSurfaceInvalidated) == 0)
                    ShowObservedSurfaceHighlights(overlay, initialScan.Frame);
                else
                    overlay.ClearObservedSurfaceHighlights();
                var initialFrame = initialScan.Frame;
                adaptive = new AdaptiveCaptureCoordinator(session, target, ReportAdaptiveStatus);
                SynchronizeOverlayWithAdaptiveFrames(adaptive, overlay);
                adaptive.Start(initialFrame);

                if (targetActivated && initialScan.HasUsableControls &&
                    Volatile.Read(ref initialSurfaceInvalidated) == 0)
                {
                    Console.WriteLine("Auto tabs is active. The recorder will walk top-level ribbon/navigation tabs, visit File/backstage last, and then return to manual capture.");
                    PrintRecorderAutoTabsConsoleHelp();
                    panel.SetStatus("Auto tabs is preparing the first pass...");
                    session.SetInputCapturePaused(true);
                    var autoOutcome = await RunCancelableAutoPassAsync(initialFrame, adaptive).ConfigureAwait(false);
                    panel.SetAutoPassActive(false);
                    switch (autoOutcome)
                    {
                        case AutoTabsOutcome.SavePartialMap:
                            return await FinishPartialRecordingAsync(session, adaptive, panel, output, cancellation.Token).ConfigureAwait(false);

                        case AutoTabsOutcome.FinishMap:
                            return await FinishRecordingAsync(session, adaptive, panel, output, cancellation.Token).ConfigureAwait(false);

                        case AutoTabsOutcome.PanelClosed:
                            await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                            return CancelRecordingForPanelClose(session, panel);

                        case AutoTabsOutcome.Cancelled:
                            await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                            return CancelRecording(session, panel);
                    }

                    manualClickCursorUtc = DateTimeOffset.UtcNow;
                    await RestoreManualSingleClickCaptureAsync(
                        session, panel, overlay, resumeInputCapture: Volatile.Read(ref pauseRequested) == 0,
                        cancellationToken: cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    panel.SetAutoPassActive(false);
                    session.SetInputCapturePaused(false);
                    panel.SetStatus(Volatile.Read(ref initialSurfaceInvalidated) != 0
                        ? "A click was detected during scanning. Manual capture is active."
                        : initialScan.HasUsableControls
                        ? "Auto labels could not safely activate the app. Manual capture is active."
                        : "The initial scan found no safe controls. Manual capture is active; Auto labels sent no clicks.");
                    Console.WriteLine(Volatile.Read(ref initialSurfaceInvalidated) != 0
                        ? "Auto labels yielded to the user's click. Manual capture is active."
                        : initialScan.HasUsableControls
                        ? "Auto labels did not start because the app could not be activated safely. Manual capture is active."
                        : "The initial scan found no usable controls. Auto labels sent no clicks; manual capture is active.");
                    PrintRecorderActiveConsoleHelp();
                }
            }
            else
            {
                Console.WriteLine("Preparing manual capture and bringing the target app to the front...");
                panel.SetStatus("Bringing the target app to the front...");
                var targetActivated = await ActivateTargetWithOutlineAsync(
                    session, overlay, cancellation.Token).ConfigureAwait(false);
                if (!targetActivated)
                {
                    Console.WriteLine("The selected application could not be brought to the front automatically; focus it and continue clicking.");
                    panel.SetStatus("The target app could not be brought to the front automatically. Focus it manually.");
                }
                else
                {
                    Console.WriteLine("Single-click capture is armed automatically. Use Pause to hold this session, Double or T for a double-click, Stop or F to finish, or C to cancel. Press A only if you need to refocus the app.");
                    PrintRecorderActiveConsoleHelp();
                    panel.SetStatus("Recording is active. The next single click is armed automatically.");
                }

                // Arm after the chooser click has finished but before the slow
                // UIA/OCR pass. A lightweight watcher paints the clicked control
                // immediately while the complete map continues in background.
                manualClickCursorUtc = DateTimeOffset.UtcNow;
                if (Volatile.Read(ref pauseRequested) == 0)
                {
                    session.SetInputCapturePaused(false);
                    session.AddMarker("manual-mode:armed-before-initial-scan");
                }
                QuickSurfaceScanResult initialScan;
                var initialSurfaceInvalidated = 0;
                using (var feedbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    var feedbackTask = ShowInitialManualClickFeedbackAsync(
                        session,
                        target,
                        panel,
                        overlay,
                        manualClickCursorUtc,
                        () => Interlocked.Exchange(ref initialSurfaceInvalidated, 1),
                        feedbackCancellation.Token);
                    try
                    {
                        initialScan = await overlay.RunHiddenAsync(
                            () => CaptureAndRegisterInitialSurfaceAsync(
                                session, panel, workspace, speculative, sessionId, autoMapping?.Snapshot(),
                                "manual-initial-surface", cancellation.Token, launchOptions.EnableHoverAndFocusDiscovery),
                            cancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        feedbackCancellation.Cancel();
                        try { await feedbackTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    }
                }
                if (Volatile.Read(ref initialSurfaceInvalidated) == 0)
                    ShowObservedSurfaceHighlights(overlay, initialScan.Frame);
                else
                    overlay.ClearObservedSurfaceHighlights();
                var baseline = initialScan.Frame;
                adaptive = new AdaptiveCaptureCoordinator(session, target, ReportAdaptiveStatus);
                SynchronizeOverlayWithAdaptiveFrames(adaptive, overlay);
                adaptive.Start(baseline);
                panel.SetStatus("Initial scan finished. Returning focus to the selected app...");
                var targetRefocused = await ReactivateTargetWindowAsync(
                    session, overlay, cancellation.Token).ConfigureAwait(false);
                if (Volatile.Read(ref pauseRequested) == 0)
                {
                    session.SetInputCapturePaused(false);
                    session.AddMarker("manual-mode:armed-after-initial-scan");
                }
                panel.SetStatus(!targetRefocused
                    ? "Recording is ready, but the target app could not be focused. Focus it once to continue."
                    : initialScan.HasUsableControls
                        ? $"Initial scan saved {initialScan.VisibleControlCount} visible controls ({initialScan.ConfirmedControlCount} confirmed, {initialScan.CoverageGapCount} coverage gaps). Manual capture is active."
                        : "The initial scan found no controls. Manual capture is still active.");
                ShowNextManualReview(autoMapping, baseline, panel, overlay);
            }

            var requestedClicks = 1;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var clickCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                var pendingInteraction = adaptive is null ? null : session.CreateInteractionContext(
                    "manual-input-" + Guid.NewGuid().ToString("N"),
                    1,
                    InteractionActor.User,
                    requestedClicks == 1 ? InteractionGestureKind.Click : InteractionGestureKind.DoubleClick,
                    InteractionActionKind.Unknown,
                    adaptive.LatestFullFrame.Sequence);
                var captureCheckpoint = adaptive?.CreateClickCheckpoint(pendingInteraction?.InteractionId) ?? default;
                var dialogCheckpoint = adaptive?.CreateDialogCheckpoint();
                var clickTask = session.WaitForRecordedTargetClicksAsync(manualClickCursorUtc, requestedClicks,
                    (observed, total) => Console.WriteLine($"Detected click {observed} of {total}."), clickCancellation.Token);
                var commandTask = WaitForRecordingCommandAsync(panel, commandCancellation.Token);
                if (Volatile.Read(ref pauseRequested) == 0)
                {
                    panel.SetStatus(requestedClicks == 1
                        ? "Ready for the next single click."
                        : "Double-click capture is armed.");
                }
                await Task.WhenAny(commandTask, clickTask).ConfigureAwait(false);

                if (commandTask.IsCompleted)
                {
                    clickCancellation.Cancel();
                    try { await clickTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    var command = (await commandTask.ConfigureAwait(false)).Trim().ToUpperInvariant();
                    if (command is "N" or "T")
                    {
                        requestedClicks = command == "T" ? 2 : 1;
                        Console.WriteLine("Activating the selected application before arming capture...");
                        panel.SetStatus(requestedClicks == 1
                            ? "Returning to automatic single-click capture and activating the target app..."
                            : "Arming a double-click capture and activating the target app...");
                        if (!await ActivateTargetWithOutlineAsync(session, overlay, cancellation.Token).ConfigureAwait(false))
                        {
                            Console.WriteLine("The selected application could not be activated automatically; focus it and continue clicking.");
                            panel.SetStatus("The target app was not activated automatically. Focus it manually.");
                        }
                        else
                        {
                            panel.SetStatus(requestedClicks == 1
                                ? "Automatic single-click capture is active."
                                : "Double-click capture is armed.");
                        }
                        continue;
                    }
                    if (command == "A")
                    {
                        Console.WriteLine("Activating the selected application...");
                        if (!await ActivateTargetWithOutlineAsync(session, overlay, cancellation.Token).ConfigureAwait(false))
                        {
                            Console.WriteLine("The selected application could not be activated automatically; focus it manually.");
                            panel.SetStatus("The target app was not activated automatically. Focus it manually.");
                        }
                        else
                        {
                            panel.SetStatus("The target app is active. Arm the next capture when ready.");
                        }
                        continue;
                    }
                    if (command == "CONTINUE_AUTO")
                    {
                        if (adaptive is null)
                        {
                            panel.SetStatus("Auto tabs are unavailable because the baseline is missing.");
                            continue;
                        }

                        Volatile.Write(ref autoStopRequested, 0);
                        panel.SetAutoPassActive(true);
                        panel.SetStatus("Continuing automatic Ribbon tab capture...");
                        if (!await ActivateTargetWithOutlineAsync(session, overlay, cancellation.Token).ConfigureAwait(false))
                        {
                            panel.SetAutoPassActive(false);
                            panel.SetStatus("Could not activate Excel. Focus it and press Continue Auto again.");
                            continue;
                        }

                        session.SetInputCapturePaused(true);
                        var autoOutcome = await RunCancelableAutoPassAsync(
                            adaptive.LatestFullFrame, adaptive).ConfigureAwait(false);
                        panel.SetAutoPassActive(false);
                        switch (autoOutcome)
                        {
                            case AutoTabsOutcome.SavePartialMap:
                                return await FinishPartialRecordingAsync(session, adaptive, panel, output, cancellation.Token).ConfigureAwait(false);
                            case AutoTabsOutcome.FinishMap:
                                return await FinishRecordingAsync(session, adaptive, panel, output, cancellation.Token).ConfigureAwait(false);
                            case AutoTabsOutcome.PanelClosed:
                                await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                                return CancelRecordingForPanelClose(session, panel);
                            case AutoTabsOutcome.Cancelled:
                                await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                                return CancelRecording(session, panel);
                        }

                        manualClickCursorUtc = DateTimeOffset.UtcNow;
                        await RestoreManualSingleClickCaptureAsync(
                            session, panel, overlay, resumeInputCapture: Volatile.Read(ref pauseRequested) == 0,
                            cancellationToken: cancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (command == "P")
                    {
                        PauseInputCapture();
                        overlay.HideTargetFocusOutline();
                        panel.ShowPausedRecordingState();
                        panel.SetStatus("Recording paused. Resume to continue or Finish to build the map.");
                        Console.WriteLine("Recording paused. Use Resume to continue this session or Finish to build the map.");
                        PrintRecorderPausedConsoleHelp();
                        var pausedCommand = await WaitForPausedRecordingCommandAsync(panel, cancellation.Token).ConfigureAwait(false);
                        if (pausedCommand == "R")
                        {
                            panel.ShowActiveRecordingState();
                            panel.SetAutoPassActive(false);
                            Console.WriteLine("Resuming recording and bringing the target app to the front again...");
                            panel.SetStatus("Bringing the target app to the front...");
                            var targetReactivated = await ReactivateTargetWindowAsync(session, overlay, cancellation.Token).ConfigureAwait(false);
                            Volatile.Write(ref pauseRequested, 0);
                            manualClickCursorUtc = DateTimeOffset.UtcNow;
                            session.SetInputCapturePaused(false);
                            if (!targetReactivated)
                            {
                                Console.WriteLine("The selected application could not be brought to the front automatically after pause; focus it manually.");
                                panel.SetStatus("The target app could not be brought to the front automatically. Focus it manually.");
                            }
                            else
                            {
                                panel.SetStatus("Recording resumed. The next click is armed automatically.");
                                PrintRecorderActiveConsoleHelp();
                            }
                            continue;
                        }
                        if (pausedCommand == "F")
                            return await FinishRecordingAsync(session, adaptive, panel, output, cancellation.Token).ConfigureAwait(false);
                        if (pausedCommand == "C")
                        {
                            await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                            return CancelRecording(session, panel);
                        }
                        if (pausedCommand == PanelCloseCommand)
                        {
                            await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                            return CancelRecordingForPanelClose(session, panel);
                        }
                        continue;
                    }
                    if (command == "F")
                        return await FinishRecordingAsync(session, adaptive, panel, output, cancellation.Token).ConfigureAwait(false);
                    if (command == "C")
                    {
                        await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                        return CancelRecording(session, panel);
                    }
                    if (command == PanelCloseCommand)
                    {
                        await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
                        return CancelRecordingForPanelClose(session, panel);
                    }
                    Console.WriteLine("Unknown recording command. Use P, T, A, F, or C; single clicks continue automatically. Press F1 or H for help.");
                    continue;
                }

                // Do not leave a second command-reader running after every manual click.
                // A stale reader can dequeue Pause/Finish/Continue Auto before the next
                // loop owns it, making the live recorder appear to stop responding.
                commandCancellation.Cancel();
                try { await commandTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                var recordedClicks = await clickTask.ConfigureAwait(false);
                var recordedClick = recordedClicks[^1];
                manualClickCursorUtc = recordedClick.TimestampUtc;
                session.AddMarker(requestedClicks == 1 ? "manual-click" : "manual-double-click");
                var clickCaptureOutcome = AdaptiveClickCaptureOutcome.Failed;
                if (adaptive is not null)
                {
                    var clickPoint = new RectI(recordedClick.X, recordedClick.Y, 1, 1);
                    var immediateFrame = adaptive.LatestFullFrame;
                    var interactionSourceFrameSequence = adaptive.ResolveInteractionSourceFrameSequence(
                        recordedClick.WindowFromPointHwnd);
                    var immediateRootBounds = ResolveOverlayRootBounds(overlay, immediateFrame);
                    var immediateBounds = adaptive.ResolveManualHighlightBounds(clickPoint) ??
                                          new RectI(recordedClick.X - 9, recordedClick.Y - 9, 18, 18);
                    // Keep the current surface visible until the post-click frame is
                    // ready. That refresh replaces only observed (green) geometry;
                    // confirmed and historical highlights must survive every click.
                    overlay.ShowConfirmedClickPulse(
                        immediateRootBounds,
                        immediateBounds,
                        TimeSpan.FromSeconds(45));
                    panel.SetStatus("Click detected. Writing it to the map...");
                    var immediateVisualFeedback = RefineManualClickFeedbackAsync(
                        target,
                        clickPoint,
                        overlay,
                        cancellation.Token);
                    clickCaptureOutcome = await adaptive.CaptureClickAsync(
                        clickPoint, captureCheckpoint, cancellation.Token,
                        recordedClick.WindowFromPointHwnd, dialogCheckpoint).ConfigureAwait(false);
                    await immediateVisualFeedback.ConfigureAwait(false);
                    var highlightControl = adaptive.LastManualHighlightControl;
                    var highlightBounds = adaptive.ResolveManualHighlightBounds(clickPoint) ??
                                          new RectI(recordedClick.X - 9, recordedClick.Y - 9, 18, 18);
                    var frame = adaptive.LatestFullFrame;
                    try
                    {
                        // The HWND can stay unchanged while Outlook switches from
                        // Mail to Calendar or People. Refresh the cheap window title
                        // so the click is assigned to the visible module, not the
                        // previous full-frame layer.
                        var currentWindow = WindowSnapshotCapture.Observe(
                            WindowCatalog.Resolve(session.TargetRootOwnerHwnd));
                        frame = frame with { Window = currentWindow };
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                                     System.ComponentModel.Win32Exception)
                    {
                    }
                    var visibleLayerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(
                        frame.Window, frame.Automation, overlay.CurrentVisibleLayerKey);
                    var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
                        frame, [highlightBounds], visibleLayerKey);
                    if (highlightControl is not null)
                    {
                        overlay.AddControlHighlight(
                            ResolveOverlayRootBounds(overlay, frame), layerKey, highlightControl, visibleLayerKey);
                    }
                    else
                    {
                        overlay.AddHighlights(
                            ResolveOverlayRootBounds(overlay, frame), layerKey, [highlightBounds], visibleLayerKey);
                    }
                    if (pendingInteraction is not null)
                    {
                        var resultFrames = session.LatestFrameSequence > pendingInteraction.SourceFrameSequence
                            ? new[] { session.LatestFrameSequence }
                            : [];
                        var interactionOutcome = ResolveManualInteractionOutcome(
                            clickCaptureOutcome,
                            highlightControl is not null,
                            resultFrames.Length > 0);
                        var clickSucceeded = interactionOutcome == InteractionOutcome.Succeeded;
                        session.CompleteInteraction(
                            pendingInteraction with
                            {
                                Action = ResolveInteractionAction(highlightControl),
                                SourceFrameSequence = interactionSourceFrameSequence
                            },
                            interactionOutcome,
                            resultFrames,
                            clickCaptureOutcome.ToString(),
                            highlightControl,
                            recordedClicks.Select(click => click.Sequence).ToArray());
                        var resolvedManualReview = clickSucceeded && autoMapping is not null && highlightControl is not null &&
                            autoMapping.ConfirmManual(
                                highlightControl,
                                adaptive.LatestFullFrame.Window.Bounds,
                                sessionId,
                                pendingInteraction.InteractionId,
                                resultFrames,
                                ResolveSelectedTabFingerprint(adaptive.LatestFullFrame));
                        if (highlightControl is not null)
                        {
                            speculative.RecordOutcome(
                                highlightControl,
                                frame.Window.Bounds,
                                clickSucceeded,
                                sessionId,
                                resultFrames.Length > 0 ? resultFrames[^1] : null);
                            if (clickSucceeded)
                            {
                                await speculative.PrepareAsync(
                                    adaptive.LatestFullFrame,
                                    autoMapping?.Snapshot(),
                                    sessionId,
                                    null,
                                    cancellation.Token).ConfigureAwait(false);
                            }
                        }
                        if (resolvedManualReview)
                            ShowNextManualReview(autoMapping, adaptive.LatestFullFrame, panel, overlay);
                    }
                }
                requestedClicks = 1;
                if (Volatile.Read(ref pauseRequested) != 0)
                {
                    overlay.HideTargetFocusOutline();
                    panel.SetStatus("Recording paused. Resume to continue or Finish to build the map.");
                }
                else
                {
                    panel.SetStatus(clickCaptureOutcome switch
                    {
                        AdaptiveClickCaptureOutcome.PopupCaptured => "Click and popup recorded. Ready for the next single click.",
                        AdaptiveClickCaptureOutcome.DialogCaptured => "Click and dialog controls recorded. The dialog remains open for manual clicks.",
                        AdaptiveClickCaptureOutcome.RootCaptured => "Click and complete screen recorded. Ready for the next single click.",
                        AdaptiveClickCaptureOutcome.ControlCaptured => "Click target saved, but no changed screen was confirmed. Ready for the next click.",
                        AdaptiveClickCaptureOutcome.PopupFailed => "The click was recorded, but the popup was not captured. Keep it open and click again.",
                        AdaptiveClickCaptureOutcome.DialogFailed => "The dialog opened, but its controls were not captured. Keep it open and click a control again.",
                        _ => "The click was detected, but its UI state was not captured. Click the control again."
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
            if (Volatile.Read(ref panelCancelRequested) != 0)
                return CancelRecordingFromPanel(session, panel);

            session.Cancel(retain: true);
            panel.SetStatus("Recording interrupted. The partial bundle was retained.");
            Console.WriteLine("Cancelled bundle retained after console interruption.");
            return RecordingPhaseOutcome.Cancelled;
        }
        catch (Exception ex) when (IsRecorderWorkflowRecoverable(ex))
        {
            await DrainAdaptiveForCancelAsync(adaptive).ConfigureAwait(false);
            session.Fail();
            panel.SetStatus("Finish map failed. The partial bundle was retained.");
            Console.Error.WriteLine($"error: recording.failed at {BundleSecurity.SafeDiagnostic(Path.GetFullPath(output))}: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
            Console.WriteLine($"Retained recording bundle: {Path.GetFullPath(output)}");
            Console.WriteLine("Recording did not finish cleanly, but the toolbar stayed open so you can continue.");
            return RecordingPhaseOutcome.Failed;
        }
        finally
        {
            if (adaptive is not null)
                await adaptive.DisposeAsync().ConfigureAwait(false);
            panel.CancelRequested -= CancelFromPanel;
            panel.PauseRequested -= PauseInputCapture;
            panel.AutoPassStopRequested -= StopAutoPassFromPanel;
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void ShowNextManualReview(
        AutoMappingCampaignTracker? campaign,
        FrameObservation frame,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay)
    {
        var pending = campaign?.NeedsManualItems() ?? [];
        if (pending.Count == 0)
        {
            panel.SetStatus("Recording is active. Each click is confirmed after its control or popup is written to the map.");
            return;
        }

        var next = pending[0];
        var matches = frame.Automation.Where(control =>
            control.IsEnabled && !control.IsOffscreen &&
            string.Equals(
                AutoMappingTargetFingerprint.Create(control, frame.Window.Bounds),
                next.TargetFingerprint,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 1)
        {
            var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
                frame, [matches[0].Bounds], overlay.CurrentVisibleLayerKey);
            overlay.AddControlHighlight(frame.Window.Bounds, layerKey, matches[0], overlay.CurrentVisibleLayerKey);
            panel.SetStatus($"Manual review {pending.Count}: click {next.DisplayName}. It is highlighted in the app.");
            return;
        }

        panel.SetStatus($"Manual review {pending.Count}: open the relevant tab and click {next.DisplayName}.");
    }

    private static string ResolveSelectedTabFingerprint(FrameObservation frame)
    {
        var selected = AutoTabDiscovery.Discover(frame).Where(candidate => candidate.IsSelected).ToArray();
        return selected.Length == 1
            ? AutoMappingTargetFingerprint.Create(selected[0].Observation, frame.Window.Bounds)
            : string.Empty;
    }

    private static async Task<RecordingPhaseOutcome> FinishRecordingAsync(
        ManualRecordingSession session,
        AdaptiveCaptureCoordinator? adaptive,
        RecordingControlPanel panel,
        string output,
        CancellationToken cancellationToken)
    {
        // Finish is a hard boundary: clicks made while popup work drains or the
        // graph is built must never enter this bundle or start another session.
        session.SetInputCapturePaused(true);
        panel.BeginMapBuild();
        if (adaptive is not null)
            await adaptive.DrainAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return await FinalizeRecordingAsync(
            session,
            panel,
            output,
            cancellationToken,
            completePartial: false,
            finalizingMessage: "Finalizing the recording bundle...",
            phaseOutcome: RecordingPhaseOutcome.Completed).ConfigureAwait(false);
    }

    private static async Task<RecordingPhaseOutcome> FinishPartialRecordingAsync(
        ManualRecordingSession session,
        AdaptiveCaptureCoordinator? adaptive,
        RecordingControlPanel panel,
        string output,
        CancellationToken cancellationToken)
    {
        session.SetInputCapturePaused(true);
        panel.BeginMapBuild();
        if (adaptive is not null)
            await adaptive.DrainAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return await FinalizeRecordingAsync(
            session,
            panel,
            output,
            cancellationToken,
            completePartial: true,
            finalizingMessage: "Finalizing the partial recording bundle...",
            phaseOutcome: RecordingPhaseOutcome.PartialCompleted).ConfigureAwait(false);
    }

    private static async Task DrainAdaptiveForCancelAsync(AdaptiveCaptureCoordinator? adaptive)
    {
        if (adaptive is not null)
            await adaptive.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<RecordingPhaseOutcome> FinalizeRecordingAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        string output,
        CancellationToken cancellationToken,
        bool completePartial,
        string finalizingMessage,
        RecordingPhaseOutcome phaseOutcome)
    {
        panel.SetStatus(finalizingMessage);
        Console.WriteLine(finalizingMessage);
        if (completePartial)
            session.CompletePartial();
        else
            session.Complete();
        Console.WriteLine($"{(completePartial ? "Completed partial bundle" : "Completed")}: {Path.GetFullPath(output)}");
        return phaseOutcome;
    }

    private static RecordingPhaseOutcome CancelRecording(ManualRecordingSession session, RecordingControlPanel panel)
    {
        Console.Write("Retain the cancelled partial bundle? [y/N]: ");
        var retain = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        session.Cancel(retain);
        panel.SetStatus(retain ? "Recording cancelled. The partial bundle was retained." : "Recording cancelled. The partial bundle was discarded.");
        Console.WriteLine(retain ? "Cancelled bundle retained." : "Cancelled capture discarded.");
        return RecordingPhaseOutcome.Cancelled;
    }

    private static RecordingPhaseOutcome CancelRecordingForPanelClose(ManualRecordingSession session, RecordingControlPanel panel)
    {
        session.Cancel(retain: true);
        panel.SetStatus("Recording cancelled. The partial bundle was retained.");
        Console.WriteLine("Recording panel closed. Cancelled bundle retained.");
        return RecordingPhaseOutcome.Cancelled;
    }

    private static RecordingPhaseOutcome CancelRecordingFromPanel(ManualRecordingSession session, RecordingControlPanel panel)
    {
        session.Cancel(retain: false);
        panel.SetStatus("Recording cancelled. Choose a window to start a new recording.");
        Console.WriteLine("Recording cancelled from the panel. The recorder remains open.");
        return RecordingPhaseOutcome.Cancelled;
    }

    private static async Task<AutoTabsOutcome> RunAutoTabsPassAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        FrameObservation initialFrame,
        AdaptiveCaptureCoordinator adaptive,
        CancellationToken cancellationToken,
        AutoMappingCampaignTracker campaign,
        string campaignSessionId,
        SpeculativePlanningCoordinator speculative,
        bool includeOutlookNavigation = true,
        string? navigationContextKey = null,
        string? campaignParentFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(campaign);
        var campaignExecutor = new AutoMappingCampaignExecutor(campaign, campaignSessionId);

        var visitedTabs = navigationContextKey is null
            ? new HashSet<string>(adaptive.RecordedTabKeys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var visitedCommandsByTab = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ribbonCandidatesByTab = new Dictionary<string, AutoRibbonCommandCandidate[]>(StringComparer.Ordinal);
        var visitedDialogLaunchersByTab = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dialogLauncherCandidatesByTab = new Dictionary<string, AutoRibbonDialogLauncherCandidate[]>(StringComparer.Ordinal);
        var verifiedCandidateScans = new HashSet<string>(StringComparer.Ordinal);
        var knownCommandTotalsByTab = new Dictionary<string, int>(StringComparer.Ordinal);
        var knownTabs = new Dictionary<string, AutoTabCandidate>(StringComparer.Ordinal);
        var tabCampaignIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var tabInteractionEvidence = new Dictionary<string, (string InteractionId, long[] ResultFrames)>(StringComparer.Ordinal);
        var commandSweptTabs = new HashSet<string>(StringComparer.Ordinal);
        var currentFrame = initialFrame;
        var currentVisibleLayerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(initialFrame.Window, initialFrame.Automation);
        var rootOwnerHwnd = initialFrame.Window.RootOwnerHwnd;
        var targetProcessId = initialFrame.Window.ProcessId;
        var knownTotal = 0;
        var countedInitialSelection = false;
        var discoveredAnyTabs = false;
        AutoTabCandidate? activeTab = null;

        if (AutoTabDiscovery.Discover(currentFrame).Count == 0)
        {
            panel.SetStatus("Finding top-level menus and navigation tabs...");
            var navigationProfile = ResolveRibbonCaptureProfile(session, RibbonSurfaceCapturePolicy.Fast);
            var navigationProbe = await session.CollectNavigationAutomationAsync(
                rootOwnerHwnd,
                navigationProfile.NavigationTimeout,
                navigationProfile.NavigationMaxNodes,
                cancellationToken).ConfigureAwait(false);
            if (navigationProbe.Items.Count > 0)
            {
                currentFrame = currentFrame with
                {
                    Automation = navigationProbe.Items,
                    AutomationTimedOut = navigationProbe.TimedOut,
                    AutomationStatus = navigationProbe.Status
                };
                adaptive.RegisterFullFrame(currentFrame);
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await speculative.PrepareAsync(
                currentFrame, campaign.Snapshot(), campaignSessionId, null, cancellationToken).ConfigureAwait(false);
            if (TryReadActiveAutoPassCommand(panel, out var command))
            {
                switch (command)
                {
                    case "SKIP_AUTO":
                        panel.SetStatus("Auto pass skipped. The next single click is armed automatically.");
                        Console.WriteLine("Auto tabs skipped by request.");
                        return AutoTabsOutcome.ContinueManual;

                    case "F":
                        return AutoTabsOutcome.FinishMap;

                    case "C":
                        return AutoTabsOutcome.Cancelled;

                    case PanelCloseCommand:
                        return AutoTabsOutcome.PanelClosed;
                }
            }

            if (!IsTargetForeground(rootOwnerHwnd, targetProcessId))
            {
                panel.SetStatus("Auto temporarily lost the target app. Closing the popup and continuing...");
                session.DismissTransientPopup();
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                if (!await ActivateTargetWithOutlineAsync(session, overlay, cancellationToken).ConfigureAwait(false))
                {
                    panel.SetStatus("Auto paused because Excel could not be reactivated. Use Continue Auto to resume.");
                    Console.WriteLine("Auto tabs paused because the target could not be reactivated.");
                    return AutoTabsOutcome.ContinueManual;
                }
                continue;
            }

            var discovered = AutoPassTraversalPolicy.OrderTabsInVisualSequence(
                AutoTabDiscovery.Discover(currentFrame));
            if (discovered.Count == 0 && !discoveredAnyTabs)
            {
                var visualButtons = DiscoverVisualMainButtons(
                    currentFrame,
                    ResolveOverlayRootBounds(overlay, currentFrame));
                if (visualButtons.Length > 0)
                {
                    Console.WriteLine($"Auto labels found {visualButtons.Length} safe visual navigation buttons.");
                    return await RunVisualMainButtonPassAsync(
                        session, panel, overlay, adaptive, currentFrame, visualButtons,
                        cancellationToken).ConfigureAwait(false);
                }
                panel.SetStatus("Auto labels found no safe top-level menus or tabs. The next single click is armed automatically.");
                Console.WriteLine("Auto labels found no safe top-level menus or tabs.");
                return AutoTabsOutcome.ContinueManual;
            }

            discoveredAnyTabs |= discovered.Count > 0;
            foreach (var candidate in discovered)
            {
                knownTabs[candidate.StableKey] = candidate;
                tabCampaignIds[candidate.StableKey] = campaign.Register(
                    candidate.IsBackstage ? AutoMappingWorkKind.Backstage : AutoMappingWorkKind.Tab,
                    candidate.Observation,
                    currentFrame.Window.Bounds,
                    campaignParentFingerprint ?? string.Empty);
            }
            foreach (var ambiguous in discovered
                         .GroupBy(candidate => tabCampaignIds[candidate.StableKey], StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
                campaign.MarkAmbiguous([ambiguous.Key]);
            foreach (var candidate in discovered)
            {
                var campaignId = tabCampaignIds[candidate.StableKey];
                if (campaign.IsTerminal(campaignId))
                {
                    visitedTabs.Add(candidate.StableKey);
                    commandSweptTabs.Add(candidate.StableKey);
                }
            }
            knownTotal = Math.Max(knownTotal, discovered.Count);

            if (!countedInitialSelection)
            {
                countedInitialSelection = true;
                var initialActiveTab = ResolveActiveAutoPassTab(discovered, currentVisibleLayerKey, fallbackStableKey: null);
                if (initialActiveTab is not null)
                {
                    activeTab = initialActiveTab;
                    // The page that happened to be active when recording began is
                    // revisited in its normal left-to-right position. It must not
                    // jump ahead of navigation controls located to its left.
                    if (!campaign.IsTerminal(tabCampaignIds[initialActiveTab.StableKey]))
                    {
                        visitedTabs = AutoPassTraversalPolicy.PrepareVisitedTabsForExplicitSweep(
                            visitedTabs, initialActiveTab.StableKey);
                        Console.WriteLine($"Auto tabs will explicitly click the initially active tab: {SanitizeConsole(initialActiveTab.DisplayName)}.");
                    }
                    session.AddMarker("auto-tabs:initial-active:" + initialActiveTab.StableKey);
                }
            }

            activeTab = ResolveActiveAutoPassTab(discovered, currentVisibleLayerKey, activeTab?.StableKey);

            // Finish one tab completely before moving to the next: activate Home,
            // capture every eligible chevron on Home, then activate Insert, and so on.
            if (activeTab?.IsBackstage != true && AutoPassTraversalPolicy.ShouldSweepCommandsForActiveTab(
                    activeTab?.StableKey, visitedTabs, commandSweptTabs))
            {
                var commandTab = activeTab!;
                if (!visitedCommandsByTab.TryGetValue(commandTab.StableKey, out var visitedCommands))
                {
                    visitedCommands = new HashSet<string>(StringComparer.Ordinal);
                    visitedCommandsByTab[commandTab.StableKey] = visitedCommands;
                }
                if (!visitedDialogLaunchersByTab.TryGetValue(commandTab.StableKey, out var visitedDialogLaunchers))
                {
                    visitedDialogLaunchers = new HashSet<string>(StringComparer.Ordinal);
                    visitedDialogLaunchersByTab[commandTab.StableKey] = visitedDialogLaunchers;
                }

                if (!ribbonCandidatesByTab.TryGetValue(commandTab.StableKey, out var commandCandidates))
                {
                    panel.SetStatus($"Scanning all chevrons on {commandTab.DisplayName}...");
                    var scan = await DiscoverAutoRibbonTargetsAsync(
                        session, rootOwnerHwnd, currentFrame, commandTab,
                        RibbonSurfaceCapturePolicy.CommandScan, cancellationToken).ConfigureAwait(false);
                    commandCandidates = AutoPassTraversalPolicy.OrderCommandsInVisualSequence(scan.Commands).ToArray();
                    ribbonCandidatesByTab[commandTab.StableKey] = commandCandidates;
                    dialogLauncherCandidatesByTab[commandTab.StableKey] =
                        AutoPassTraversalPolicy.OrderDialogLaunchersInVisualSequence(scan.DialogLaunchers).ToArray();
                    Console.WriteLine($"Auto tabs found {commandCandidates.Length} chevrons and {scan.DialogLaunchers.Length} dialog launchers on {SanitizeConsole(commandTab.DisplayName)}.");
                }
                commandCandidates = AutoPassTraversalPolicy.OrderCommandsInVisualSequence(commandCandidates).ToArray();
                var commandParentFingerprint = AutoMappingTargetFingerprint.Create(
                    commandTab.Observation, currentFrame.Window.Bounds);
                var commandCampaignIds = commandCandidates.ToDictionary(
                    candidate => candidate.StableKey,
                    candidate => campaign.Register(
                        AutoMappingWorkKind.Command,
                        candidate.Observation,
                        currentFrame.Window.Bounds,
                        commandParentFingerprint),
                    StringComparer.Ordinal);
                foreach (var candidate in commandCandidates.Where(candidate =>
                             campaign.IsTerminal(commandCampaignIds[candidate.StableKey])))
                    visitedCommands.Add(candidate.StableKey);
                var commandPlan = AutoMappingCampaignPlanner.Plan(
                    commandCandidates,
                    candidate => commandCampaignIds[candidate.StableKey],
                    campaign);
                var nextCommands = commandPlan.Ready
                    .Where(candidate => !visitedCommands.Contains(candidate.StableKey))
                    .Where(candidate => !adaptive.IsCommandRecorded(
                        AutoCommandScopeKey(navigationContextKey, commandTab.StableKey), candidate.StableKey))
                    .ToArray();
                var existingKnownCommandTotal = knownCommandTotalsByTab.TryGetValue(commandTab.StableKey, out var knownCommandTotal)
                    ? knownCommandTotal
                    : 0;
                knownCommandTotal = Math.Max(existingKnownCommandTotal, visitedCommands.Count + nextCommands.Length);
                knownCommandTotalsByTab[commandTab.StableKey] = knownCommandTotal;

                if (nextCommands.Length > 0)
                {
                    var nextCommand = nextCommands[0];
                    speculative.MarkReused(nextCommand.Observation, currentFrame.Window.Bounds);
                    var commandCampaignId = commandCampaignIds[nextCommand.StableKey];
                    var commandMarkerKey = commandTab.StableKey + ":" + nextCommand.StableKey;
                    panel.SetStatus($"Auto-clicking {commandTab.DisplayName}: {nextCommand.DisplayName} ({visitedCommands.Count + 1}/{knownCommandTotal})... · {campaign.ProgressSummary()}");
                    Console.WriteLine($"Auto tabs opening {SanitizeConsole(commandTab.DisplayName)} -> {SanitizeConsole(nextCommand.DisplayName)} ({visitedCommands.Count + 1}/{knownCommandTotal}).");
                    overlay.ShowClickPulse(currentFrame.Window.Bounds, nextCommand.Observation.Bounds);
                    await Task.Delay(160, cancellationToken).ConfigureAwait(false);
                    var commandInteraction = session.CreateInteractionContext(
                        "auto-command:" + commandMarkerKey,
                        campaign.Attempts(commandCampaignId) + 1,
                        InteractionActor.AutoExplorer,
                        InteractionGestureKind.ProgrammaticInvoke,
                        ResolveInteractionAction(nextCommand.Observation),
                        currentFrame.Sequence,
                        nextCommand.Observation);
                    campaignExecutor.Begin(commandCampaignId, commandInteraction.InteractionId);
                    var popupCheckpoint = adaptive.CreateClickCheckpoint(commandInteraction.InteractionId);
                    var commandDialogCheckpoint = adaptive.CreateDialogCheckpoint();
                    adaptive.ArmPopupSource(nextCommand.Observation, commandInteraction.InteractionId);
                    var invoked = await TryOpenAutoPopupCommandAsync(
                        session,
                        nextCommand.Observation,
                        "auto-tabs:command:" + commandMarkerKey,
                        cancellationToken,
                        overlay).ConfigureAwait(false);
                    if (!invoked)
                    {
                        speculative.RecordOutcome(
                            nextCommand.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                        session.CompleteInteraction(commandInteraction, InteractionOutcome.Failed,
                            diagnosticCode: "activation-failed");
                        session.AddMarker("auto-tabs:command:activation-failed:" + commandMarkerKey);
                        var needsManual = campaignExecutor.Reject(
                            commandCampaignId, commandInteraction.InteractionId, "activation-failed");
                        if (!needsManual)
                        {
                            panel.SetStatus($"{nextCommand.DisplayName} did not open. Retrying it before continuing...");
                            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        session.AddCaptureHealth("auto-tabs", "command-blocked",
                            $"{nextCommand.DisplayName} could not be activated after {AutoCaptureMaxAttempts} attempts. It was recorded as unmapped and auto traversal continued.");
                        visitedCommands.Add(nextCommand.StableKey);
                        session.AddMarker("auto-tabs:command:unmapped-activation:" + commandMarkerKey);
                        panel.SetStatus($"Could not open {nextCommand.DisplayName}. Continuing with the remaining controls...");
                        Console.WriteLine($"Auto tabs could not activate {SanitizeConsole(nextCommand.DisplayName)}; continuing instead of ending the pass.");
                        continue;
                    }

                    // Some Office controls reported as chevrons actually open a
                    // modal dialog. Treat that as a dialog transaction instead of
                    // waiting for a popup and then leaving the dialog uncaptured.
                    var commandDialog = await adaptive.WaitForDialogCaptureAsync(
                        commandDialogCheckpoint, TimeSpan.FromMilliseconds(180), cancellationToken).ConfigureAwait(false);
                    var commandOpenedDialog = commandDialog.Outcome != AdaptiveDialogCaptureOutcome.NotObserved;
                    var commandPersisted = commandDialog.Outcome == AdaptiveDialogCaptureOutcome.Captured;
                    if (!commandOpenedDialog)
                    {
                        if (LooksLikeRevitWindow(currentFrame.Window))
                        {
                            // Revit renders Ribbon flyouts inside the main WPF window
                            // instead of exposing a reliable owned popup HWND. Capture
                            // the materialized root while the flyout is still open.
                            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
                            commandPersisted = await overlay.RunHiddenAsync(async () =>
                            {
                                var capturedCommandFrame = await CaptureRibbonSurfaceAsync(
                                    session,
                                    "auto-tabs:inline-revit-command:" + commandMarkerKey,
                                    adaptive.BaseFrameSequence,
                                    cancellationToken,
                                    RibbonSurfaceCapturePolicy.CommandScan,
                                    reportEmpty: true,
                                    preferScreenBoundsScreenshot: true,
                                    requiredInlineChangeFrom: currentFrame,
                                    activatedControl: nextCommand.Observation).ConfigureAwait(false);
                                if (capturedCommandFrame is null)
                                {
                                    await Task.Delay(260, cancellationToken).ConfigureAwait(false);
                                    capturedCommandFrame = await CaptureRibbonSurfaceAsync(
                                        session,
                                        "auto-tabs:inline-revit-command-retry:" + commandMarkerKey,
                                        adaptive.BaseFrameSequence,
                                        cancellationToken,
                                        RibbonSurfaceCapturePolicy.DenseRetry,
                                        reportEmpty: true,
                                        preferScreenBoundsScreenshot: true,
                                        requiredInlineChangeFrom: currentFrame,
                                        activatedControl: nextCommand.Observation).ConfigureAwait(false);
                                }
                                return capturedCommandFrame is not null;
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        else if (IsInlineTopChromeCommand(nextCommand.Observation))
                        {
                            // Comments opens an in-window pane rather than a popup
                            // HWND. Persist the changed surface directly; requiring a
                            // popup transaction here would incorrectly retry a
                            // successful click and leave the pane open.
                            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
                            commandPersisted = await CaptureRibbonSurfaceAsync(
                                session,
                                "auto-tabs:inline-command:" + commandMarkerKey,
                                adaptive.BaseFrameSequence,
                                cancellationToken,
                                RibbonSurfaceCapturePolicy.Fast,
                                reportEmpty: true).ConfigureAwait(false) is not null;
                        }
                        else
                        {
                            var popupOutcome = await adaptive.WaitForPopupCapturesAsync(
                                popupCheckpoint, TimeSpan.FromMilliseconds(1_800), cancellationToken).ConfigureAwait(false);
                            commandPersisted = IsAutoPopupCaptureConfirmed(popupOutcome);
                        }
                    }
                    if (commandPersisted)
                    {
                        overlay.HideClickPulse();
                        overlay.AddControlHighlight(
                            currentFrame.Window.Bounds,
                            TabHighlightLayerResolver.ResolveLayerKey(
                                currentFrame, [nextCommand.Observation.Bounds], commandTab.StableKey),
                            nextCommand.Observation,
                            TabHighlightLayerResolver.ResolveVisibleLayerKey(
                                currentFrame.Window, currentFrame.Automation, commandTab.StableKey));
                        session.AddMarker(commandOpenedDialog
                            ? "auto-tabs:command:mapped-dialog:" + commandMarkerKey
                            : "auto-tabs:command:mapped:" + commandMarkerKey);
                    }
                    else
                    {
                        session.AddMarker(commandOpenedDialog
                            ? "auto-tabs:command:dialog-not-captured:" + commandMarkerKey
                            : "auto-tabs:command:popup-not-captured:" + commandMarkerKey);
                        session.AddCaptureHealth("auto-tabs", "popup-not-captured",
                            commandOpenedDialog
                                ? "The command opened a dialog, but its controls were not confirmed. It will not be activated again in this pass."
                            : "The popup was not confirmed. The already-activated command will not be clicked again in this pass.");
                    }
                    var commandResults = session.LatestFrameSequence > commandInteraction.SourceFrameSequence
                        ? new[] { session.LatestFrameSequence }
                        : Array.Empty<long>();
                    session.CompleteInteraction(
                        commandInteraction,
                        commandPersisted && commandResults.Length > 0
                            ? InteractionOutcome.Succeeded
                            : commandPersisted ? InteractionOutcome.NoChange : InteractionOutcome.Failed,
                        commandResults,
                        commandOpenedDialog ? "dialog" : IsInlineTopChromeCommand(nextCommand.Observation) ? "inline-surface" : "popup");
                    var commandNeedsRetry = false;
                    if (commandPersisted && commandResults.Length > 0)
                    {
                        speculative.RecordOutcome(
                            nextCommand.Observation, currentFrame.Window.Bounds, true,
                            campaignSessionId, commandResults[0]);
                        campaignExecutor.Confirm(
                            commandCampaignId, commandInteraction.InteractionId, commandResults);
                        visitedCommands.Add(nextCommand.StableKey);
                        adaptive.MarkCommandRecorded(
                            AutoCommandScopeKey(navigationContextKey, commandTab.StableKey), nextCommand.StableKey);
                    }
                    else
                    {
                        speculative.RecordOutcome(
                            nextCommand.Observation, currentFrame.Window.Bounds, false,
                            campaignSessionId, commandResults.FirstOrDefault() > 0 ? commandResults[0] : null);
                        var diagnostic = commandPersisted ? "checkpoint-no-frame" :
                            commandOpenedDialog ? "dialog-not-confirmed" : "popup-not-confirmed";
                        commandNeedsRetry = !campaignExecutor.Reject(
                            commandCampaignId, commandInteraction.InteractionId, diagnostic);
                    }
                    if (commandOpenedDialog && commandDialog.Hwnd != 0)
                    {
                        if (!await session.DismissOwnedDialogAsync(commandDialog.Hwnd, cancellationToken).ConfigureAwait(false))
                        {
                            panel.SetStatus("The dialog is still open. Close or cancel it before continuing.");
                            return AutoTabsOutcome.ContinueManual;
                        }
                    }
                    else if (IsInlineTopChromeCommand(nextCommand.Observation))
                    {
                        // Toggle the transient in-window pane back out so the
                        // remaining Ribbon controls keep their original geometry.
                        _ = await session.TryInvokeControlAsync(
                                nextCommand.Observation, cancellationToken).ConfigureAwait(false) ||
                            await session.TryClickControlAsync(
                                nextCommand.Observation, 1, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await session.DismissTransientPopupAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await Task.Delay(90, cancellationToken).ConfigureAwait(false);
                    if (commandNeedsRetry)
                    {
                        panel.SetStatus($"{nextCommand.DisplayName} was not confirmed. Retrying once...");
                        continue;
                    }
                    if (!commandPersisted)
                    {
                        session.AddCaptureHealth("auto-tabs", "popup-blocked",
                            $"{nextCommand.DisplayName} was activated once, but its popup was not confirmed. It was recorded as unmapped without clicking it again.");
                        visitedCommands.Add(nextCommand.StableKey);
                        session.AddMarker("auto-tabs:command:unmapped-popup:" + commandMarkerKey);
                        panel.SetStatus($"Could not save {nextCommand.DisplayName}. Continuing with the remaining controls...");
                        Console.WriteLine($"Auto tabs could not confirm {SanitizeConsole(nextCommand.DisplayName)}; continuing instead of ending the pass.");
                        continue;
                    }

                    panel.SetStatus($"Saved {nextCommand.DisplayName}. Continuing automatically...");
                    continue;
                }

                if (!verifiedCandidateScans.Contains(commandTab.StableKey))
                {
                    panel.SetStatus($"Verifying all controls on {commandTab.DisplayName} before leaving the tab...");
                    var verification = await DiscoverAutoRibbonTargetsAsync(
                        session, rootOwnerHwnd, currentFrame, commandTab,
                        RibbonSurfaceCapturePolicy.DenseRetry, cancellationToken).ConfigureAwait(false);
                    commandCandidates = ribbonCandidatesByTab[commandTab.StableKey] =
                        AutoPassTraversalPolicy.OrderCommandsInVisualSequence(
                            MergeAutoTargets(commandCandidates, verification.Commands, candidate => candidate.StableKey)).ToArray();
                    var existingLaunchers = dialogLauncherCandidatesByTab.GetValueOrDefault(commandTab.StableKey) ?? [];
                    dialogLauncherCandidatesByTab[commandTab.StableKey] =
                        AutoPassTraversalPolicy.OrderDialogLaunchersInVisualSequence(
                            MergeAutoTargets(existingLaunchers, verification.DialogLaunchers, candidate => candidate.StableKey)).ToArray();
                    verifiedCandidateScans.Add(commandTab.StableKey);
                    session.AddMarker($"auto-tabs:verified-scan:{commandTab.StableKey}:commands={commandCandidates.Length}:dialogs={dialogLauncherCandidatesByTab[commandTab.StableKey].Length}");
                    continue;
                }

                var launcherCandidates = AutoPassTraversalPolicy.OrderDialogLaunchersInVisualSequence(
                    dialogLauncherCandidatesByTab.GetValueOrDefault(commandTab.StableKey) ?? []);
                var dialogCampaignIds = launcherCandidates.ToDictionary(
                    candidate => candidate.StableKey,
                    candidate => campaign.Register(
                        AutoMappingWorkKind.DialogLauncher,
                        candidate.Observation,
                        currentFrame.Window.Bounds,
                        commandParentFingerprint),
                    StringComparer.Ordinal);
                foreach (var ambiguous in launcherCandidates
                             .GroupBy(candidate => dialogCampaignIds[candidate.StableKey], StringComparer.Ordinal)
                             .Where(group => group.Count() > 1))
                    campaign.MarkAmbiguous([ambiguous.Key]);
                foreach (var candidate in launcherCandidates.Where(candidate =>
                             campaign.IsTerminal(dialogCampaignIds[candidate.StableKey])))
                    visitedDialogLaunchers.Add(candidate.StableKey);
                var nextDialogLauncher = launcherCandidates.FirstOrDefault(candidate =>
                    !visitedDialogLaunchers.Contains(candidate.StableKey) &&
                    campaign.CanAttempt(dialogCampaignIds[candidate.StableKey]));
                if (nextDialogLauncher is not null)
                {
                    speculative.MarkReused(nextDialogLauncher.Observation, currentFrame.Window.Bounds);
                    var dialogCampaignId = dialogCampaignIds[nextDialogLauncher.StableKey];
                    var launcherMarkerKey = commandTab.StableKey + ":" + nextDialogLauncher.StableKey;
                    panel.SetStatus($"Opening {commandTab.DisplayName}: {nextDialogLauncher.DisplayName} dialog...");
                    Console.WriteLine($"Auto tabs opening dialog launcher {SanitizeConsole(commandTab.DisplayName)} -> {SanitizeConsole(nextDialogLauncher.DisplayName)}.");
                    overlay.ShowClickPulse(currentFrame.Window.Bounds, nextDialogLauncher.Observation.Bounds);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    var dialogInteraction = session.CreateInteractionContext(
                        "auto-dialog:" + launcherMarkerKey,
                        campaign.Attempts(dialogCampaignId) + 1,
                        InteractionActor.AutoExplorer,
                        InteractionGestureKind.Click,
                        InteractionActionKind.Invoke,
                        currentFrame.Sequence,
                        nextDialogLauncher.Observation);
                    campaign.Start(dialogCampaignId, campaignSessionId, dialogInteraction.InteractionId);
                    var dialogCheckpoint = adaptive.CreateDialogCheckpoint();
                    var invoked = await TryOpenAutoDialogLauncherAsync(
                        session,
                        nextDialogLauncher.Observation,
                        "auto-tabs:dialog-launcher:" + launcherMarkerKey,
                        cancellationToken,
                        overlay).ConfigureAwait(false);
                    if (!invoked)
                    {
                        speculative.RecordOutcome(
                            nextDialogLauncher.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                        session.CompleteInteraction(dialogInteraction, InteractionOutcome.Failed,
                            diagnosticCode: "activation-failed");
                        session.AddMarker("auto-tabs:dialog-launcher:activation-failed:" + launcherMarkerKey);
                        var needsManual = campaign.Fail(
                            dialogCampaignId, campaignSessionId, dialogInteraction.InteractionId, "activation-failed");
                        if (!needsManual)
                        {
                            panel.SetStatus($"{nextDialogLauncher.DisplayName} did not open. Retrying it...");
                            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        session.AddCaptureHealth("auto-tabs", "dialog-launcher-blocked",
                            $"{nextDialogLauncher.DisplayName} could not be activated after {AutoCaptureMaxAttempts} attempts. It was recorded as unmapped and auto traversal continued.");
                        visitedDialogLaunchers.Add(nextDialogLauncher.StableKey);
                        session.AddMarker("auto-tabs:dialog-launcher:unmapped-activation:" + launcherMarkerKey);
                        panel.SetStatus($"Could not open {nextDialogLauncher.DisplayName}. Continuing with the remaining controls...");
                        Console.WriteLine($"Auto tabs could not activate dialog launcher {SanitizeConsole(nextDialogLauncher.DisplayName)}; continuing.");
                        continue;
                    }

                    overlay.AddControlHighlight(
                        currentFrame.Window.Bounds,
                        TabHighlightLayerResolver.ResolveLayerKey(
                            currentFrame, [nextDialogLauncher.Observation.Bounds], commandTab.StableKey),
                        nextDialogLauncher.Observation,
                        TabHighlightLayerResolver.ResolveVisibleLayerKey(
                            currentFrame.Window, currentFrame.Automation, commandTab.StableKey));
                    var dialogResult = await adaptive.WaitForDialogCaptureAsync(
                        dialogCheckpoint, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    session.CompleteInteraction(
                        dialogInteraction,
                        dialogResult.Outcome == AdaptiveDialogCaptureOutcome.Captured && dialogResult.Frame is not null
                            ? InteractionOutcome.Succeeded
                            : dialogResult.Outcome == AdaptiveDialogCaptureOutcome.NotObserved
                                ? InteractionOutcome.NoChange
                                : InteractionOutcome.Failed,
                        dialogResult.Frame is null ? [] : [dialogResult.Frame.Sequence],
                        dialogResult.Outcome.ToString());
                    var dialogResultFrames = dialogResult.Frame is null
                        ? Array.Empty<long>()
                        : new[] { dialogResult.Frame.Sequence };
                    var dialogNeedsRetry = false;
                    if (dialogResult.Outcome == AdaptiveDialogCaptureOutcome.Captured && dialogResultFrames.Length > 0)
                    {
                        speculative.RecordOutcome(
                            nextDialogLauncher.Observation, currentFrame.Window.Bounds, true,
                            campaignSessionId, dialogResultFrames[0]);
                        campaign.Succeed(
                            dialogCampaignId, campaignSessionId, dialogInteraction.InteractionId, dialogResultFrames);
                    }
                    else
                    {
                        speculative.RecordOutcome(
                            nextDialogLauncher.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                        dialogNeedsRetry = !campaign.Fail(
                            dialogCampaignId, campaignSessionId, dialogInteraction.InteractionId, "dialog-not-confirmed");
                    }
                    if (dialogResult.Outcome == AdaptiveDialogCaptureOutcome.Captured)
                    {
                        visitedDialogLaunchers.Add(nextDialogLauncher.StableKey);
                        session.AddMarker("auto-tabs:dialog-launcher:mapped:" + launcherMarkerKey);
                        panel.SetStatus($"Saved {dialogResult.Title} dialog. Continuing automatically...");
                    }
                    else
                    {
                        session.AddMarker("auto-tabs:dialog-launcher:not-captured:" + launcherMarkerKey);
                        session.AddCaptureHealth("auto-tabs", "dialog-not-captured",
                            "A Ribbon dialog launcher was invoked, but no stable owned dialog frame was persisted.");
                        panel.SetStatus($"Could not preserve {nextDialogLauncher.DisplayName} dialog; continuing automatically...");
                    }
                    if (dialogResult.Hwnd == 0)
                    {
                        await session.DismissTransientPopupAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else if (!await session.DismissOwnedDialogAsync(
                                 dialogResult.Hwnd, cancellationToken).ConfigureAwait(false))
                    {
                        panel.SetStatus("The dialog is still open. Close or cancel it to continue recording.");
                        Console.WriteLine("Auto tabs paused because the captured dialog could not be closed safely.");
                        return AutoTabsOutcome.ContinueManual;
                    }
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    if (dialogNeedsRetry)
                    {
                        panel.SetStatus($"{nextDialogLauncher.DisplayName} was not confirmed. Retrying once...");
                        continue;
                    }
                    if (dialogResult.Outcome != AdaptiveDialogCaptureOutcome.Captured)
                    {
                        session.AddCaptureHealth("auto-tabs", "dialog-blocked",
                            $"{nextDialogLauncher.DisplayName} was activated once, but its dialog was not confirmed. It was recorded as unmapped without clicking it again.");
                        visitedDialogLaunchers.Add(nextDialogLauncher.StableKey);
                        session.AddMarker("auto-tabs:dialog-launcher:unmapped-dialog:" + launcherMarkerKey);
                        panel.SetStatus($"Could not save {nextDialogLauncher.DisplayName}. Continuing with the remaining controls...");
                        Console.WriteLine($"Auto tabs could not confirm dialog launcher {SanitizeConsole(nextDialogLauncher.DisplayName)}; continuing.");
                        continue;
                    }
                    continue;
                }

                commandSweptTabs.Add(commandTab.StableKey);
                if (tabCampaignIds.TryGetValue(commandTab.StableKey, out var completedTabCampaignId) &&
                    tabInteractionEvidence.TryGetValue(commandTab.StableKey, out var tabEvidence))
                {
                    campaign.CompleteParent(
                        completedTabCampaignId, campaignSessionId, tabEvidence.InteractionId, tabEvidence.ResultFrames);
                }
                session.AddMarker("auto-tabs:chevrons-complete:" + commandTab.StableKey);
                session.AddMarker("auto-tabs:dialog-launchers-complete:" + commandTab.StableKey);
                panel.SetStatus($"Finished all chevrons and dialog launchers on {commandTab.DisplayName}. Moving to the next tab...");
            }

            var nextCandidates = AutoPassTraversalPolicy.OrderTabsInVisualSequence(knownTabs.Values
                .Where(candidate => !visitedTabs.Contains(candidate.StableKey))
                .Where(candidate => tabCampaignIds.TryGetValue(candidate.StableKey, out var itemId) &&
                                    campaign.CanAttempt(itemId))).ToArray();
            knownTotal = Math.Max(knownTotal, visitedTabs.Count + nextCandidates.Length);
            if (nextCandidates.Length == 0)
            {
                if (includeOutlookNavigation)
                {
                    var outlookNavigation = await RunOutlookNavigationPassAsync(
                        session, panel, overlay, currentFrame, adaptive, cancellationToken,
                        campaign, campaignSessionId, speculative).ConfigureAwait(false);
                    currentFrame = outlookNavigation.Frame;
                    if (outlookNavigation.TerminalOutcome is { } terminalOutcome)
                        return terminalOutcome;
                }
                await RunAdobePremiereDisclosurePassAsync(
                    session, panel, overlay, currentFrame, adaptive, cancellationToken,
                    campaign, campaignSessionId).ConfigureAwait(false);
                panel.SetStatus("Finishing the last popup captures before manual mode...");
                _ = await adaptive.WaitForPopupCapturesAsync(
                    adaptive.CreateClickCheckpoint(), TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
                panel.SetStatus("Auto-mapping complete. The next single click is armed automatically.");
                Console.WriteLine("Auto labels completed each safe top-level menu or tab in visual order. Manual single-click capture is active again.");
                return AutoTabsOutcome.ContinueManual;
            }

            var next = nextCandidates[0];
            speculative.MarkReused(next.Observation, currentFrame.Window.Bounds);
            var tabCampaignId = tabCampaignIds[next.StableKey];
            if (!AutoPassBudgetPolicy.CanCaptureNextAutoStep(session))
                return StopAutoPassForQuota(panel);
            var isApplicationMenu = AutoTabDiscovery.IsApplicationMenu(next, currentFrame.Window.Bounds);
            var isBackstage = next.IsBackstage;
            panel.SetStatus(isBackstage
                ? "Auto-mapping File/backstage last..."
                : isApplicationMenu
                ? $"Auto-mapping menus {visitedTabs.Count + 1}/{knownTotal}..."
                : $"Auto-mapping tabs {visitedTabs.Count + 1}/{knownTotal}...");
            Console.WriteLine($"Auto labels activating {SanitizeConsole(next.DisplayName)} ({visitedTabs.Count + 1}/{knownTotal}).");
            overlay.ShowClickPulse(currentFrame.Window.Bounds, next.Observation.Bounds);
            panel.SetStatus($"Auto-clicking {next.DisplayName} ({visitedTabs.Count + 1}/{knownTotal})... · {campaign.ProgressSummary()}");
            await Task.Delay(160, cancellationToken).ConfigureAwait(false);

            AutoPassRefreshOutcome tabResult;
            var tabInteraction = session.CreateInteractionContext(
                (isBackstage ? "auto-backstage:" : isApplicationMenu ? "auto-menu:" : "auto-tab:") + next.StableKey,
                campaign.Attempts(tabCampaignId) + 1,
                InteractionActor.AutoExplorer,
                isApplicationMenu || isBackstage ? InteractionGestureKind.ProgrammaticInvoke : InteractionGestureKind.ProgrammaticSelect,
                isApplicationMenu || isBackstage ? InteractionActionKind.Expand : InteractionActionKind.Select,
                currentFrame.Sequence,
                next.Observation);
            campaign.Start(tabCampaignId, campaignSessionId, tabInteraction.InteractionId);
            if (isBackstage)
            {
                FrameObservation? backstageFrame;
                try
                {
                    backstageFrame = await CaptureBackstageAsync(
                        session, next, adaptive.BaseFrameSequence, tabInteraction, () => overlay.AddControlHighlight(
                            currentFrame.Window.Bounds,
                            TabHighlightLayerResolver.GlobalLayerKey,
                            next.Observation,
                            currentVisibleLayerKey), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (IsRecordingQuotaReached(ex))
                {
                    return StopAutoPassForQuota(panel);
                }

                if (backstageFrame is null)
                {
                    speculative.RecordOutcome(next.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                    session.CompleteInteraction(tabInteraction, InteractionOutcome.Failed,
                        diagnosticCode: "backstage-capture-failed");
                    var needsManual = campaign.Fail(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, "backstage-capture-failed");
                    if (needsManual)
                    {
                        visitedTabs.Add(next.StableKey);
                        commandSweptTabs.Add(next.StableKey);
                    }
                    session.AddMarker("auto-backstage:unmapped:" + next.StableKey);
                    panel.SetStatus("File/backstage could not be captured. Continuing to manual mode...");
                }
                else
                {
                    adaptive.RegisterFullFrame(backstageFrame);
                    speculative.RecordOutcome(
                        next.Observation, currentFrame.Window.Bounds, true, campaignSessionId, backstageFrame.Sequence);
                    session.CompleteInteraction(tabInteraction, InteractionOutcome.Succeeded,
                        [backstageFrame.Sequence], "backstage-captured");
                    campaign.Succeed(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, [backstageFrame.Sequence]);
                    visitedTabs.Add(next.StableKey);
                    commandSweptTabs.Add(next.StableKey);
                    session.AddMarker("auto-backstage:mapped:" + next.StableKey);
                    await CaptureSafeBackstageNavigationAsync(
                        session, adaptive, panel, overlay, backstageFrame, cancellationToken).ConfigureAwait(false);
                    panel.SetStatus("File/backstage and its safe sections were saved. Closing it...");
                }

                await session.DismissTransientPopupAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(140, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (isApplicationMenu)
            {
                var popupCheckpoint = adaptive.CreateClickCheckpoint(tabInteraction.InteractionId);
                adaptive.ArmPopupSource(next.Observation, tabInteraction.InteractionId);
                var invoked = await TryOpenAutoPopupCommandAsync(
                    session,
                    next.Observation,
                    "auto-menus:item:" + next.StableKey,
                    cancellationToken,
                    overlay).ConfigureAwait(false);
                if (!invoked)
                {
                    speculative.RecordOutcome(next.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                    session.CompleteInteraction(tabInteraction, InteractionOutcome.Failed,
                        diagnosticCode: "activation-failed");
                    var needsManual = campaign.Fail(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, "activation-failed");
                    if (needsManual)
                    {
                        visitedTabs.Add(next.StableKey);
                        commandSweptTabs.Add(next.StableKey);
                    }
                    session.AddMarker("auto-menus:skipped:" + next.StableKey);
                    panel.SetStatus($"Could not open {next.DisplayName}. Continuing with the remaining menus...");
                    continue;
                }

                overlay.AddControlHighlight(
                    currentFrame.Window.Bounds,
                    TabHighlightLayerResolver.GlobalLayerKey,
                    next.Observation,
                    currentVisibleLayerKey);
                var popupOutcome = await adaptive.WaitForPopupCapturesAsync(
                    popupCheckpoint, TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
                FrameObservation? menuFrame = null;
                var menuCaptured = IsAutoPopupCaptureConfirmed(popupOutcome);
                if (!menuCaptured)
                {
                    menuFrame = await session.CaptureAsync(
                        "auto-menus:item:" + next.StableKey + ":opened",
                        cancellationToken).ConfigureAwait(false);
                    menuCaptured = HasExpandedApplicationMenu(currentFrame, menuFrame, next);
                    adaptive.RegisterFullFrame(menuFrame);
                }

                var resultSequence = menuCaptured
                    ? menuFrame?.Sequence ?? session.LatestFrameSequence
                    : 0;
                session.CompleteInteraction(
                    tabInteraction,
                    menuCaptured ? InteractionOutcome.Succeeded : InteractionOutcome.NoChange,
                    resultSequence > tabInteraction.SourceFrameSequence ? [resultSequence] : [],
                    menuCaptured ? "menu-captured" : "menu-content-unavailable");
                if (menuCaptured && resultSequence > tabInteraction.SourceFrameSequence)
                {
                    speculative.RecordOutcome(
                        next.Observation, currentFrame.Window.Bounds, true, campaignSessionId, resultSequence);
                    campaign.Succeed(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, [resultSequence]);
                    visitedTabs.Add(next.StableKey);
                    commandSweptTabs.Add(next.StableKey);
                }
                else if (campaign.Fail(
                             tabCampaignId, campaignSessionId, tabInteraction.InteractionId, "menu-not-confirmed"))
                {
                    speculative.RecordOutcome(next.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                    visitedTabs.Add(next.StableKey);
                    commandSweptTabs.Add(next.StableKey);
                }
                session.AddMarker((menuCaptured ? "auto-menus:mapped:" : "auto-menus:no-change:") + next.StableKey);
                await session.DismissTransientPopupAsync(cancellationToken).ConfigureAwait(false);
                panel.SetStatus(menuCaptured
                    ? $"Saved {next.DisplayName} menu. Continuing automatically..."
                    : $"Opened {next.DisplayName}, but its menu items were unavailable. Continuing...");
                continue;
            }
            try
            {
                tabResult = AutoTabDiscovery.IsLegacyNavigationButton(next)
                    ? await CaptureFirstVisitToLegacyNavigationAsync(
                        session,
                        adaptive,
                        next.Observation,
                        "auto-tabs:legacy-navigation:" + next.StableKey,
                        () => overlay.AddControlHighlight(
                            ResolveOverlayRootBounds(overlay, currentFrame),
                            TabHighlightLayerResolver.GlobalLayerKey,
                            next.Observation,
                            currentVisibleLayerKey),
                        cancellationToken,
                        overlay).ConfigureAwait(false)
                    : await CaptureFirstVisitToTabAsync(
                        session,
                        next.Observation,
                        currentFrame.Window.Bounds,
                        "auto-tabs:tab:" + next.StableKey,
                        adaptive.BaseFrameSequence,
                        tabInteraction.Attempt,
                        () => overlay.AddControlHighlight(
                            currentFrame.Window.Bounds,
                            TabHighlightLayerResolver.GlobalLayerKey,
                            next.Observation,
                            currentVisibleLayerKey),
                        cancellationToken,
                        overlay).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsRecordingQuotaReached(ex))
            {
                return StopAutoPassForQuota(panel);
            }
            if (tabResult.Kind == AutoPassRefreshOutcomeKind.InvocationFailed)
            {
                speculative.RecordOutcome(next.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                session.CompleteInteraction(tabInteraction, InteractionOutcome.Failed,
                    diagnosticCode: "activation-failed");
                if (campaign.Fail(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, "activation-failed"))
                    visitedTabs.Add(next.StableKey);
                session.AddMarker("auto-tabs:skipped:" + next.StableKey);
                panel.SetStatus($"Skipped {next.DisplayName}. Continuing auto tabs...");
                Console.WriteLine($"Auto tabs skipped {SanitizeConsole(next.DisplayName)} because it could not be activated.");
                continue;
            }

            if (tabResult.Kind == AutoPassRefreshOutcomeKind.CaptureUnavailableAfterActivation)
            {
                speculative.RecordOutcome(next.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                session.CompleteInteraction(tabInteraction, InteractionOutcome.TimedOut,
                    diagnosticCode: "capture-unavailable");
                if (campaign.Fail(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, "capture-unavailable"))
                    visitedTabs.Add(next.StableKey);
                session.AddMarker("auto-tabs:capture-unavailable:" + next.StableKey);
                session.AddCaptureHealth("auto-tabs", "tab-capture-unavailable",
                    $"{next.DisplayName} was activated, but its Ribbon did not return a materialized control tree after the bounded retry. It was recorded as unmapped and auto traversal continued.");
                panel.SetStatus($"{next.DisplayName} opened, but its controls were unavailable. Continuing auto mapping...");
                Console.WriteLine($"Auto tabs could not read {SanitizeConsole(next.DisplayName)}; continuing instead of ending the pass.");
                continue;
            }

            if (tabResult.Kind == AutoPassRefreshOutcomeKind.NoStructuralChange)
            {
                speculative.RecordOutcome(next.Observation, currentFrame.Window.Bounds, false, campaignSessionId, null);
                session.CompleteInteraction(tabInteraction, InteractionOutcome.NoChange,
                    diagnosticCode: "no-structural-change");
                session.AddMarker("auto-tabs:mapped-no-change:" + next.StableKey);
                if (campaign.Fail(
                        tabCampaignId, campaignSessionId, tabInteraction.InteractionId, "no-structural-change"))
                {
                    visitedTabs.Add(next.StableKey);
                    commandSweptTabs.Add(next.StableKey);
                }
                else
                {
                    panel.SetStatus($"{next.DisplayName} did not switch. Retrying once with another activation method...");
                }
                Console.WriteLine($"Auto tabs checked {SanitizeConsole(next.DisplayName)} without a structural UI change.");
                continue;
            }

            session.AddMarker("auto-tabs:mapped:" + next.StableKey);
            visitedTabs.Add(next.StableKey);
            var refreshedTabFrame = tabResult.Frame!;
            speculative.RecordOutcome(
                next.Observation, currentFrame.Window.Bounds, true, campaignSessionId, refreshedTabFrame.Sequence);
            session.CompleteInteraction(tabInteraction, InteractionOutcome.Succeeded,
                [refreshedTabFrame.Sequence], "captured");
            tabInteractionEvidence[next.StableKey] =
                (tabInteraction.InteractionId, [refreshedTabFrame.Sequence]);
            adaptive.RegisterFullFrame(refreshedTabFrame);
            currentVisibleLayerKey = ResolveAutoPassVisibleLayerKey(refreshedTabFrame, currentVisibleLayerKey, next.StableKey);
            currentFrame = refreshedTabFrame;
            activeTab = ResolveActiveAutoPassTab(AutoTabDiscovery.Discover(currentFrame), currentVisibleLayerKey, next.StableKey);
        }
    }

    private static AutoTabCandidate[] DiscoverVisualMainButtons(
        FrameObservation frame,
        RectI rootBounds)
    {
        var visualButtons = frame.Automation
            .Where(control => control.FrameworkId.Equals("UiAtlas.Visual.Ocr", StringComparison.OrdinalIgnoreCase) &&
                              control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
                              control.ControlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase))
            .Where(control => control.Bounds.Width is >= 44 and <= 260 &&
                              control.Bounds.Height is >= 28 and <= 90)
            .Where(control => control.Bounds.X >= rootBounds.X - 4 &&
                              control.Bounds.X + control.Bounds.Width <= rootBounds.X + rootBounds.Width + 4 &&
                              control.Bounds.Y >= rootBounds.Y - 4 &&
                              control.Bounds.Y < rootBounds.Y + Math.Max(140, rootBounds.Height / 4))
            .OrderBy(control => control.Bounds.Y + control.Bounds.Height / 2)
            .ThenBy(control => control.Bounds.X)
            .ToArray();
        if (visualButtons.Length < 3)
            return [];

        var navigationRow = visualButtons
            .Select(seed => visualButtons
                .Where(control => Math.Abs(
                    control.Bounds.Y + control.Bounds.Height / 2 -
                    (seed.Bounds.Y + seed.Bounds.Height / 2)) <= 18)
                .ToArray())
            .Where(row => row.Length >= 3)
            .OrderByDescending(row => row.Length)
            .ThenBy(row => row.Min(control => control.Bounds.Y))
            .FirstOrDefault();
        if (navigationRow is null)
            return [];

        return navigationRow
            .Where(control => IsSafeVisualNavigationLabel(control.Name))
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(control => control.Bounds.X)
            .Select(control => new AutoTabCandidate(
                control.RuntimeId,
                control.Name.Trim(),
                false,
                false,
                control))
            .Take(16)
            .ToArray();
    }

    private static bool IsSafeVisualNavigationLabel(string? value)
    {
        var label = value?.Trim() ?? string.Empty;
        if (label.Length < 2 || label.StartsWith("Unlabelled", StringComparison.OrdinalIgnoreCase))
            return false;

        var unsafeWords = new[]
        {
            "add", "buy", "cancel", "clear", "close", "create", "delete", "email",
            "end of day", "exit", "log off", "log out", "lcg off", "new order",
            "pay", "payment", "post", "remove", "save", "send", "submit"
        };
        return !unsafeWords.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<AutoTabsOutcome> RunVisualMainButtonPassAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        AdaptiveCaptureCoordinator adaptive,
        FrameObservation initialFrame,
        IReadOnlyList<AutoTabCandidate> buttons,
        CancellationToken cancellationToken)
    {
        var currentFrame = initialFrame;
        for (var index = 0; index < buttons.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadActiveAutoPassCommand(panel, out var command))
            {
                if (command == "F") return AutoTabsOutcome.FinishMap;
                if (command == "C") return AutoTabsOutcome.Cancelled;
                if (command == PanelCloseCommand) return AutoTabsOutcome.PanelClosed;
                if (command == "SKIP_AUTO") return AutoTabsOutcome.ContinueManual;
            }
            if (!AutoPassBudgetPolicy.CanCaptureNextAutoStep(session))
                return StopAutoPassForQuota(panel);

            var button = buttons[index];
            var rootBounds = ResolveOverlayRootBounds(overlay, currentFrame);
            panel.SetStatus($"Auto-clicking {button.DisplayName} ({index + 1}/{buttons.Count})...");
            Console.WriteLine($"Auto labels activating visual navigation -> {SanitizeConsole(button.DisplayName)} ({index + 1}/{buttons.Count}).");
            overlay.ShowClickPulse(rootBounds, button.Observation.Bounds);
            await Task.Delay(140, cancellationToken).ConfigureAwait(false);

            var interaction = session.CreateInteractionContext(
                "auto-visual-navigation:" + button.StableKey,
                1,
                InteractionActor.AutoExplorer,
                InteractionGestureKind.Click,
                InteractionActionKind.Select,
                currentFrame.Sequence,
                button.Observation);
            var clicked = await overlay.RunHiddenAsync(
                () => session.TryClickControlAsync(button.Observation, 1, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (!clicked)
            {
                session.CompleteInteraction(interaction, InteractionOutcome.Failed,
                    diagnosticCode: "activation-failed");
                continue;
            }

            var refreshed = await adaptive.RefreshRootSurfaceAsync(
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
            {
                session.CompleteInteraction(interaction, InteractionOutcome.TimedOut,
                    diagnosticCode: "visual-refresh-timeout");
                continue;
            }

            session.CompleteInteraction(
                interaction,
                InteractionOutcome.Succeeded,
                [refreshed.Sequence],
                "visual-surface-captured");
            currentFrame = refreshed;
        }

        panel.SetStatus("Auto labels finished the safe visual navigation buttons. Manual clicks are active.");
        Console.WriteLine("Auto labels finished the safe visual navigation buttons. Manual single-click capture is active again.");
        return AutoTabsOutcome.ContinueManual;
    }

    private static string AutoCommandScopeKey(string? navigationContextKey, string tabKey) =>
        string.IsNullOrWhiteSpace(navigationContextKey)
            ? tabKey
            : navigationContextKey + "\n" + tabKey;

    private static async Task<(FrameObservation Frame, AutoTabsOutcome? TerminalOutcome)> RunOutlookNavigationPassAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        FrameObservation sourceFrame,
        AdaptiveCaptureCoordinator adaptive,
        CancellationToken cancellationToken,
        AutoMappingCampaignTracker campaign,
        string campaignSessionId,
        SpeculativePlanningCoordinator speculative)
    {
        var discoveryFrame = sourceFrame;
        var candidates = OutlookNavigationDiscovery.Discover(discoveryFrame).ToArray();
        if (candidates.Length == 0 && OutlookNavigationDiscovery.IsSupported(sourceFrame.Window))
        {
            panel.SetStatus("Finding Outlook navigation buttons...");
            var body = await session.CollectWindowAutomationAsync(
                session.TargetRootOwnerHwnd,
                TimeSpan.FromSeconds(20),
                RecordingContractLimits.MaxControlsPerFrame,
                cancellationToken).ConfigureAwait(false);
            if (!body.TimedOut && body.Status is "ok" or "node-limit")
            {
                discoveryFrame = sourceFrame with
                {
                    Automation = sourceFrame.Automation.Concat(body.Items)
                        .GroupBy(control => string.IsNullOrWhiteSpace(control.RuntimeId)
                            ? $"bounds:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}:{control.AutomationId}"
                            : control.RuntimeId, StringComparer.Ordinal)
                        .Select(group => group.Last())
                        .ToArray()
                };
                candidates = OutlookNavigationDiscovery.Discover(discoveryFrame).ToArray();
            }
        }
        if (candidates.Length == 0)
            return (sourceFrame, null);

        var navigationCampaignIds = candidates.ToDictionary(
            candidate => candidate.StableKey,
            candidate => campaign.Register(
                AutoMappingWorkKind.NavigationItem,
                candidate.Observation,
                discoveryFrame.Window.Bounds),
            StringComparer.Ordinal);
        foreach (var ambiguous in candidates
                     .GroupBy(candidate => navigationCampaignIds[candidate.StableKey], StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            campaign.MarkAmbiguous([ambiguous.Key]);

        var latestFrame = discoveryFrame;
        var initialModuleKey = OutlookNavigationDiscovery.ResolveActive(discoveryFrame)?.StableKey;
        var completed = 0;
        foreach (var originalCandidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navigationCampaignId = navigationCampaignIds[originalCandidate.StableKey];
            if (!campaign.CanAttempt(navigationCampaignId))
                continue;
            if (!AutoPassBudgetPolicy.CanCaptureNextAutoStep(session))
                return (latestFrame, StopAutoPassForQuota(panel));

            var refreshedCandidate = OutlookNavigationDiscovery.Discover(latestFrame)
                .FirstOrDefault(candidate => candidate.StableKey.Equals(
                    originalCandidate.StableKey, StringComparison.Ordinal));
            var candidate = refreshedCandidate ?? originalCandidate;
            var control = candidate.Observation;
            panel.SetStatus($"Auto-clicking Outlook navigation: {candidate.DisplayName} ({completed + 1}/{candidates.Length})...");
            Console.WriteLine($"Auto labels activating Outlook navigation -> {SanitizeConsole(candidate.DisplayName)} ({completed + 1}/{candidates.Length}).");
            overlay.ShowClickPulse(latestFrame.Window.Bounds, control.Bounds);
            await Task.Delay(140, cancellationToken).ConfigureAwait(false);

            var markerKey = candidate.StableKey;
            var interaction = session.CreateInteractionContext(
                "auto-outlook-navigation:" + markerKey,
                campaign.Attempts(navigationCampaignId) + 1,
                InteractionActor.AutoExplorer,
                InteractionGestureKind.Click,
                candidate.OpensPopup ? InteractionActionKind.Expand : InteractionActionKind.Select,
                latestFrame.Sequence,
                control);
            campaign.Start(navigationCampaignId, campaignSessionId, interaction.InteractionId);
            var popupCheckpoint = candidate.OpensPopup
                ? adaptive.CreateClickCheckpoint(interaction.InteractionId)
                : default;
            if (candidate.OpensPopup)
                adaptive.ArmPopupSource(control, interaction.InteractionId);

            var clicked = await session.TryClickControlAsync(control, 1, cancellationToken).ConfigureAwait(false);
            if (!clicked)
            {
                session.CompleteInteraction(interaction, InteractionOutcome.Failed,
                    diagnosticCode: "activation-failed");
                campaign.Fail(
                    navigationCampaignId, campaignSessionId, interaction.InteractionId, "activation-failed");
                session.AddMarker("auto-outlook-navigation:activation-failed:" + markerKey);
                completed++;
                continue;
            }

            overlay.AddControlHighlight(
                latestFrame.Window.Bounds,
                TabHighlightLayerResolver.GlobalLayerKey,
                control,
                TabHighlightLayerResolver.GlobalLayerKey);
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);

            FrameObservation? capturedFrame = null;
            var popupCaptured = false;
            if (candidate.OpensPopup)
            {
                var popupOutcome = await adaptive.WaitForPopupCapturesAsync(
                    popupCheckpoint, TimeSpan.FromMilliseconds(1_500), cancellationToken).ConfigureAwait(false);
                popupCaptured = IsAutoPopupCaptureConfirmed(popupOutcome);
            }

            // Module buttons replace Outlook's main body; the ellipsis may expose
            // either an owned NetUI popup or an inline menu depending on version.
            // A full visible-body capture preserves both variants.
            try
            {
                capturedFrame = await CaptureRibbonSurfaceAsync(
                    session,
                    "auto-outlook-navigation:opened:" + markerKey,
                    adaptive.BaseFrameSequence,
                    cancellationToken,
                    RibbonSurfaceCapturePolicy.Fast,
                    reportEmpty: false,
                    includeWorksheet: true).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsRecordingQuotaReached(ex))
            {
                session.CompleteInteraction(interaction, InteractionOutcome.Failed,
                    diagnosticCode: "capture-quota-reached");
                return (latestFrame, StopAutoPassForQuota(panel));
            }
            catch (InvalidOperationException ex) when (!IsRecordingQuotaReached(ex))
            {
                session.AddCaptureHealth("auto-outlook-navigation", "capture-failed", ex.Message);
            }

            if (capturedFrame is not null)
            {
                latestFrame = capturedFrame;
                adaptive.RegisterFullFrame(capturedFrame);
            }

            var succeeded = capturedFrame is not null || popupCaptured;
            var resultSequence = session.LatestFrameSequence > interaction.SourceFrameSequence
                ? session.LatestFrameSequence
                : 0;
            session.CompleteInteraction(
                interaction,
                succeeded ? InteractionOutcome.Succeeded : InteractionOutcome.Failed,
                resultSequence > 0 ? [resultSequence] : [],
                candidate.OpensPopup
                    ? popupCaptured ? "navigation-popup" : capturedFrame is not null ? "inline-navigation-menu" : "popup-not-captured"
                    : capturedFrame is not null ? "module-selected" : "surface-not-captured");
            if (succeeded && resultSequence > interaction.SourceFrameSequence)
                campaign.Succeed(
                    navigationCampaignId, campaignSessionId, interaction.InteractionId, [resultSequence]);
            else
                campaign.Fail(
                    navigationCampaignId, campaignSessionId, interaction.InteractionId, "navigation-not-confirmed");
            session.AddMarker((succeeded
                ? "auto-outlook-navigation:mapped:"
                : "auto-outlook-navigation:unmapped:") + markerKey);
            if (succeeded)
                adaptive.MarkCommandRecorded("outlook-navigation", markerKey);

            if (candidate.OpensPopup)
                await session.DismissTransientPopupAsync(cancellationToken).ConfigureAwait(false);
            else if (capturedFrame is not null &&
                     !string.Equals(candidate.StableKey, initialModuleKey, StringComparison.Ordinal))
            {
                panel.SetStatus($"Scanning the Ribbon and chevrons in Outlook {candidate.DisplayName}...");
                var moduleOutcome = await RunAutoTabsPassAsync(
                    session,
                    panel,
                    overlay,
                    capturedFrame,
                    adaptive,
                    cancellationToken,
                    campaign,
                    campaignSessionId,
                    speculative,
                    includeOutlookNavigation: false,
                    navigationContextKey: OutlookNavigationDiscovery.ModuleLayerKey(candidate),
                    campaignParentFingerprint: AutoMappingTargetFingerprint.Create(
                        candidate.Observation, capturedFrame.Window.Bounds)).ConfigureAwait(false);
                if (moduleOutcome != AutoTabsOutcome.ContinueManual)
                    return (latestFrame, moduleOutcome);
                latestFrame = adaptive.LatestFullFrame;
            }
            completed++;
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        session.AddMarker("auto-outlook-navigation:complete");
        panel.SetStatus("Finished all Outlook navigation buttons. Continuing automatically...");
        return (latestFrame, null);
    }

    private static async Task RunAdobePremiereDisclosurePassAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        FrameObservation currentFrame,
        AdaptiveCaptureCoordinator adaptive,
        CancellationToken cancellationToken,
        AutoMappingCampaignTracker campaign,
        string campaignSessionId)
    {
        WindowTarget target;
        try { target = WindowCatalog.Resolve(session.TargetRootOwnerHwnd); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (!AdobePremiereDisclosureDiscovery.IsSupported(target)) return;

        var visitedThisPass = new HashSet<string>(StringComparer.Ordinal);
        var scrollPages = new Dictionary<string, int>(StringComparer.Ordinal);
        var scrollRestores = new Dictionary<string, (RectI Bounds, int Attempts)>(StringComparer.Ordinal);
        var scrollGeneration = 0;
        var latestFrame = currentFrame;
        string VisitKey(AutomationObservation control) =>
            AdobePremiereDisclosureDiscovery.IsCollapsedDisclosure(control)
                ? $"tree:{scrollGeneration}:{control.RuntimeId}"
                : control.RuntimeId;
        try
        {
            for (var pass = 0; pass < 48; pass++)
            {
                panel.SetStatus("Finding Adobe panel controls and collapsible sections...");
                var scan = await session.CollectAdobeDisclosureAutomationAsync(
                    TimeSpan.FromSeconds(4), 256, cancellationToken).ConfigureAwait(false);
                if (scan.Items.Count == 0)
                {
                    if (pass == 0) session.AddMarker("auto-adobe-disclosures:none");
                    break;
                }

                var sourceFrame = await session.CaptureAutomationDeltaAsync(
                    "auto-adobe-controls:source",
                    latestFrame.Automation.Concat(scan.Items)
                        .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
                        .Select(group => group.Last())
                        .ToArray(),
                    cancellationToken,
                    latestFrame.Sequence).ConfigureAwait(false);
                Console.WriteLine($"Adobe visual scan found {scan.Items.Count} controls.");

                var safeDisclosures = scan.Items
                    .Where(AdobePremiereDisclosureDiscovery.IsSafeDisclosure)
                    .Where(control => AutomaticInteractionSafety.CanActivate(control, sourceFrame.Automation))
                    .Where(control => !AdobePremiereDisclosureDiscovery.IsPanelHeader(control))
                    .Where(control => !visitedThisPass.Contains(VisitKey(control)))
                    .Where(control => AdobePremiereDisclosureDiscovery.IsCollapsedDisclosure(control) ||
                                      !adaptive.IsCommandRecorded("adobe-owner-drawn", control.RuntimeId))
                    .ToArray();
                var disclosureRegistrations = safeDisclosures
                    .Select(control => (Control: control, ItemId: campaign.Register(
                        AutoMappingWorkKind.Disclosure, control, sourceFrame.Window.Bounds)))
                    .ToArray();
                foreach (var ambiguous in disclosureRegistrations
                             .GroupBy(item => item.ItemId, StringComparer.Ordinal)
                             .Where(group => group.Count() > 1))
                    campaign.MarkAmbiguous([ambiguous.Key]);
                var disclosures = disclosureRegistrations
                    .Where(item => campaign.CanAttempt(item.ItemId))
                    .ToArray();
                if (disclosures.Length > 0)
                {
                    var candidate = disclosures[0].Control;
                    var result = await CaptureAdobeVisualControlAsync(
                        session, panel, overlay, sourceFrame, adaptive, candidate,
                        campaign, campaignSessionId, disclosures[0].ItemId,
                        1, disclosures.Length,
                        restoreInline: !AdobePremiereDisclosureDiscovery.IsPanelTab(candidate) &&
                                       !AdobePremiereDisclosureDiscovery.IsWorkspaceTab(candidate) &&
                                       !AdobePremiereDisclosureDiscovery.IsCollapsedDisclosure(candidate),
                        cancellationToken).ConfigureAwait(false);
                    visitedThisPass.Add(VisitKey(candidate));
                    if (result is not null) latestFrame = result;
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var header = scan.Items
                    .Where(AdobePremiereDisclosureDiscovery.IsPanelHeader)
                    .Where(control => AutomaticInteractionSafety.CanActivate(control, sourceFrame.Automation))
                    .FirstOrDefault(control =>
                        !visitedThisPass.Contains(control.RuntimeId) &&
                        !adaptive.IsCommandRecorded("adobe-owner-drawn", control.RuntimeId));
                if (header is not null)
                {
                    var headerCampaignId = campaign.Register(
                        AutoMappingWorkKind.Disclosure, header, sourceFrame.Window.Bounds);
                    if (!campaign.CanAttempt(headerCampaignId))
                    {
                        visitedThisPass.Add(header.RuntimeId);
                        continue;
                    }
                    var headerResult = await CaptureAdobeVisualControlAsync(
                        session, panel, overlay, sourceFrame, adaptive, header,
                        campaign, campaignSessionId, headerCampaignId,
                        visitedThisPass.Count + 1, scan.Items.Count, restoreInline: false, cancellationToken).ConfigureAwait(false);
                    visitedThisPass.Add(header.RuntimeId);
                    if (headerResult is not null) latestFrame = headerResult;
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var scrollRegion = scan.Items
                    .Where(AdobePremiereDisclosureDiscovery.IsScrollRegion)
                    .Where(control => scrollPages.GetValueOrDefault(control.RuntimeId) < 4)
                    .OrderBy(control => control.Bounds.Width * control.Bounds.Height)
                    .FirstOrDefault();
                if (scrollRegion is null) break;

                var page = scrollPages.GetValueOrDefault(scrollRegion.RuntimeId) + 1;
                var scrollResult = await CaptureAdobePanelScrollAsync(
                    session, panel, overlay, sourceFrame, scrollRegion, page, cancellationToken).ConfigureAwait(false);
                scrollPages[scrollRegion.RuntimeId] = scrollResult.ContinueScrolling ? page : 4;
                if (scrollResult.ScrollInjected)
                {
                    var restore = scrollRestores.GetValueOrDefault(scrollRegion.RuntimeId);
                    scrollRestores[scrollRegion.RuntimeId] = (scrollRegion.Bounds, restore.Attempts + 1);
                }
                scrollGeneration++;
                if (scrollResult.Frame is not null) latestFrame = scrollResult.Frame;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && scrollRestores.Count > 0)
                await RestoreAdobePanelScrollAsync(session, panel, scrollRestores.Values, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RestoreAdobePanelScrollAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        IEnumerable<(RectI Bounds, int Attempts)> scrollRestores,
        CancellationToken cancellationToken)
    {
        panel.SetStatus("Returning Adobe panels to the top...");
        foreach (var restore in scrollRestores.Reverse())
        {
            for (var attempt = 0; attempt < restore.Attempts; attempt++)
            {
                if (!await session.TryScrollBoundsAsync(restore.Bounds, 720, cancellationToken).ConfigureAwait(false))
                    break;
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<FrameObservation?> CaptureAdobeVisualControlAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        FrameObservation sourceFrame,
        AdaptiveCaptureCoordinator adaptive,
        AutomationObservation candidate,
        AutoMappingCampaignTracker campaign,
        string campaignSessionId,
        string campaignItemId,
        int index,
        int total,
        bool restoreInline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AutoPassBudgetPolicy.CanCaptureNextAutoStep(session)) return null;
        var isOverflow = AdobePremiereDisclosureDiscovery.IsOverflow(candidate);
        var isPanelHeader = AdobePremiereDisclosureDiscovery.IsPanelHeader(candidate);
        var isPanelTab = AdobePremiereDisclosureDiscovery.IsPanelTab(candidate);
        var isApplicationMenu = AdobePremiereDisclosureDiscovery.IsApplicationMenu(candidate);
        var isWorkspaceTab = AdobePremiereDisclosureDiscovery.IsWorkspaceTab(candidate);
        var isTransientMenu = AdobePremiereDisclosureDiscovery.IsTransientMenu(candidate);
        var label = isOverflow
            ? "workspace/panel overflow"
            : isApplicationMenu
                ? "application menu"
                : isWorkspaceTab
                    ? "workspace tab"
            : isPanelTab
                ? "panel tab"
                : isTransientMenu
                    ? "panel menu"
                    : isPanelHeader
                        ? "collapsible panel"
                        : "disclosure arrow";
        panel.SetStatus($"Opening Adobe {label} ({index}/{total})...");
        overlay.ShowClickPulse(sourceFrame.Window.Bounds, candidate.Bounds);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        var interaction = session.CreateInteractionContext(
            "auto-adobe-disclosure:" + candidate.RuntimeId,
            campaign.Attempts(campaignItemId) + 1,
            InteractionActor.AutoExplorer,
            InteractionGestureKind.Click,
            isPanelTab || isWorkspaceTab ? InteractionActionKind.Select : InteractionActionKind.Expand,
            sourceFrame.Sequence,
            candidate);
        campaign.Start(campaignItemId, campaignSessionId, interaction.InteractionId);
        var clicked = await session.TryClickControlAsync(candidate, 1, cancellationToken).ConfigureAwait(false);
        if (!clicked)
        {
            session.CompleteInteraction(interaction, InteractionOutcome.Failed, diagnosticCode: "activation-failed");
            campaign.Fail(campaignItemId, campaignSessionId, interaction.InteractionId, "activation-failed");
            session.AddMarker("auto-adobe-disclosure:activation-failed:" + candidate.RuntimeId);
            return null;
        }

        await Task.Delay(180, cancellationToken).ConfigureAwait(false);
        FrameObservation? resultFrame = null;
        try
        {
            resultFrame = await session.CaptureAsync(
                "auto-adobe-disclosure:opened:" + candidate.RuntimeId,
                cancellationToken,
                new FrameCaptureOptions(
                    InteractionSource: candidate,
                    InteractionId: interaction.InteractionId)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            session.AddCaptureHealth("auto-adobe-disclosures", "capture-failed", ex.Message);
        }

        session.CompleteInteraction(
            interaction,
            resultFrame is null ? InteractionOutcome.Failed : InteractionOutcome.Succeeded,
            resultFrame is null ? [] : [resultFrame.Sequence],
            resultFrame is null
                ? "capture-failed"
                : isOverflow
                    ? "overflow-menu"
                    : isApplicationMenu
                        ? "application-menu"
                        : isWorkspaceTab
                            ? "workspace-selected"
                    : isPanelTab
                        ? "panel-tab-selected"
                        : isTransientMenu
                            ? "panel-menu"
                            : isPanelHeader
                                ? "panel-expanded"
                                : "inline-expansion");
        if (resultFrame is not null)
            campaign.Succeed(
                campaignItemId, campaignSessionId, interaction.InteractionId, [resultFrame.Sequence]);
        else
            campaign.Fail(campaignItemId, campaignSessionId, interaction.InteractionId, "capture-failed");
        if (resultFrame is not null)
        {
            adaptive.MarkCommandRecorded("adobe-owner-drawn", candidate.RuntimeId);
            session.AddMarker("auto-adobe-disclosure:mapped:" + candidate.RuntimeId);
        }

        if (isTransientMenu)
            await session.DismissTransientPopupAsync(cancellationToken).ConfigureAwait(false);
        else if (restoreInline)
            _ = await session.TryClickControlAsync(candidate, 1, cancellationToken).ConfigureAwait(false);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        return resultFrame;
    }

    private static async Task<(FrameObservation? Frame, bool ContinueScrolling, bool ScrollInjected)> CaptureAdobePanelScrollAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        FrameObservation sourceFrame,
        AutomationObservation scrollRegion,
        int page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AutoPassBudgetPolicy.CanCaptureNextAutoStep(session)) return (null, false, false);
        panel.SetStatus($"Scrolling Adobe panel to discover hidden controls (page {page}/4)...");
        overlay.ShowClickPulse(sourceFrame.Window.Bounds, scrollRegion.Bounds);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        var interaction = session.CreateInteractionContext(
            "auto-adobe-scroll:" + scrollRegion.RuntimeId,
            page,
            InteractionActor.AutoExplorer,
            InteractionGestureKind.Wheel,
            InteractionActionKind.Scroll,
            sourceFrame.Sequence,
            scrollRegion);
        var scrolled = await session.TryScrollBoundsAsync(
            scrollRegion.Bounds, -720, cancellationToken).ConfigureAwait(false);
        if (!scrolled)
        {
            session.CompleteInteraction(interaction, InteractionOutcome.Failed, diagnosticCode: "wheel-injection-failed");
            return (null, false, false);
        }

        await Task.Delay(220, cancellationToken).ConfigureAwait(false);
        FrameObservation? resultFrame = null;
        try
        {
            resultFrame = await session.CaptureAsync(
                $"auto-adobe-scroll:page-{page}",
                cancellationToken,
                new FrameCaptureOptions(
                    InteractionSource: scrollRegion,
                    InteractionId: interaction.InteractionId)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            session.AddCaptureHealth("auto-adobe-scroll", "capture-failed", ex.Message);
        }

        var verification = await session.CollectAdobeDisclosureAutomationAsync(
            TimeSpan.FromSeconds(4), 256, cancellationToken).ConfigureAwait(false);
        var updatedRegion = verification.Items.FirstOrDefault(control =>
            AdobePremiereDisclosureDiscovery.IsScrollRegion(control) &&
            control.RuntimeId.Equals(scrollRegion.RuntimeId, StringComparison.Ordinal));
        var changed = updatedRegion is not null &&
                      !updatedRegion.Name.Equals(scrollRegion.Name, StringComparison.Ordinal);
        var outcome = resultFrame is null
            ? InteractionOutcome.Failed
            : updatedRegion is null
                ? InteractionOutcome.Unobserved
                : changed
                    ? InteractionOutcome.Succeeded
                    : InteractionOutcome.NoChange;
        session.CompleteInteraction(
            interaction,
            outcome,
            resultFrame is null ? [] : [resultFrame.Sequence],
            resultFrame is null
                ? "capture-failed"
                : updatedRegion is null
                    ? "scroll-region-unobserved"
                    : changed
                        ? $"panel-scroll-page-{page}"
                        : "scroll-bottom-reached");
        if (resultFrame is not null)
            session.AddMarker($"auto-adobe-scroll:mapped:{scrollRegion.RuntimeId}:page-{page}");
        return (resultFrame, updatedRegion is null || changed, true);
    }

    private static async Task<(AutoRibbonCommandCandidate[] Commands, AutoRibbonDialogLauncherCandidate[] DialogLaunchers)>
        DiscoverAutoRibbonTargetsAsync(
            ManualRecordingSession session,
            long rootOwnerHwnd,
            FrameObservation currentFrame,
            AutoTabCandidate commandTab,
            RibbonSurfaceCaptureProfile profile,
            CancellationToken cancellationToken)
    {
        profile = ResolveRibbonCaptureProfile(session, profile);
        var ribbonProbe = await session.CollectRibbonAutomationAsync(
            rootOwnerHwnd,
            profile.RibbonTimeout,
            profile.RibbonMaxNodes,
            cancellationToken).ConfigureAwait(false);
        var ribbonFrame = currentFrame with
        {
            Automation = currentFrame.Automation
                .Concat(ribbonProbe.Items)
                .GroupBy(control => string.IsNullOrWhiteSpace(control.RuntimeId)
                    ? $"bounds:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}:{control.AutomationId}"
                    : control.RuntimeId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray()
        };
        return (
            AutoRibbonCommandDiscovery.Discover(ribbonFrame, commandTab).ToArray(),
            AutoRibbonDialogLauncherDiscovery.Discover(ribbonFrame, commandTab).ToArray());
    }

    internal static T[] MergeAutoTargets<T>(
        IEnumerable<T> existing,
        IEnumerable<T> discovered,
        Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(keySelector);
        return existing.Concat(discovered)
            .GroupBy(keySelector, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
    }

    internal static bool ShouldRetryAutoCapture(
        IDictionary<string, int> failureAttempts,
        string targetKey,
        int maxAttempts = AutoCaptureMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(failureAttempts);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var attempts = failureAttempts.TryGetValue(targetKey, out var previous) ? previous + 1 : 1;
        failureAttempts[targetKey] = attempts;
        return attempts < maxAttempts;
    }

    private static async Task<bool> TryOpenAutoPopupCommandAsync(
        ManualRecordingSession session,
        AutomationObservation control,
        string markerPrefix,
        CancellationToken cancellationToken,
        RecordingHighlightOverlay? overlay = null)
    {
        session.AddMarker(markerPrefix + ":armed");
        session.AddMarker($"{markerPrefix}:target:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}");
        // ExpandCollapse is the safe operation for SplitButton/ComboBox hosts.
        // Clicking their center first can execute the primary command and open a
        // modal dialog (for example Insert Pictures) instead of the popup chevron.
        async Task<bool> ActivateAsync() => ProgrammaticControlInvoker.PrefersDirectMouseClick(control)
            ? await session.TryClickControlAsync(control, 1, cancellationToken).ConfigureAwait(false) ||
              await session.TryInvokeControlAsync(control, cancellationToken).ConfigureAwait(false)
            : await session.TryInvokeControlAsync(control, cancellationToken).ConfigureAwait(false) ||
              await session.TryClickControlAsync(control, 1, cancellationToken).ConfigureAwait(false);
        var invoked = overlay is null
            ? await ActivateAsync().ConfigureAwait(false)
            : await overlay.RunHiddenAsync(ActivateAsync, cancellationToken).ConfigureAwait(false);
        if (invoked)
            session.AddMarker(markerPrefix + ":opened");
        return invoked;
    }

    private static async Task<bool> TryOpenAutoDialogLauncherAsync(
        ManualRecordingSession session,
        AutomationObservation control,
        string markerPrefix,
        CancellationToken cancellationToken,
        RecordingHighlightOverlay? overlay = null)
    {
        session.AddMarker(markerPrefix + ":armed");
        session.AddMarker($"{markerPrefix}:target:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}");
        // InvokePattern can synchronously wait for a modal Office dialog to close.
        // A physical click returns as soon as the launcher receives input, leaving
        // the recorder free to observe and capture the new owned HWND.
        var invoked = overlay is null
            ? await session.TryClickControlAsync(control, 1, cancellationToken).ConfigureAwait(false)
            : await overlay.RunHiddenAsync(
                () => session.TryClickControlAsync(control, 1, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        if (invoked)
            session.AddMarker(markerPrefix + ":opened");
        return invoked;
    }

    internal static bool IsAutoPopupCaptureConfirmed(AdaptivePopupCaptureOutcome outcome) =>
        outcome == AdaptivePopupCaptureOutcome.Captured;

    internal static bool HasExpandedApplicationMenu(
        FrameObservation before,
        FrameObservation after,
        AutoTabCandidate menu)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(menu);
        if (after.ScopedWindows?.Any(window => window.Hwnd != after.Window.RootOwnerHwnd) == true)
            return true;

        var beforeKeys = before.Automation.Select(AutomationShapeKey).ToHashSet(StringComparer.Ordinal);
        var menuBottom = menu.Observation.Bounds.Y + menu.Observation.Bounds.Height;
        return after.Automation.Any(control =>
            control.IsEnabled && !control.IsOffscreen &&
            control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            control.Bounds.Y >= menuBottom - 2 &&
            control.ControlType.EndsWith(".MenuItem", StringComparison.OrdinalIgnoreCase) &&
            !beforeKeys.Contains(AutomationShapeKey(control)));
    }

    internal static bool HasMaterializedInlineFlyout(
        FrameObservation before,
        IReadOnlyList<AutomationObservation> afterControls,
        AutomationObservation activatedControl)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(afterControls);
        ArgumentNullException.ThrowIfNull(activatedControl);

        var beforeKeys = before.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Select(AutomationShapeKey)
            .ToHashSet(StringComparer.Ordinal);
        var flyoutTop = activatedControl.Bounds.Y + activatedControl.Bounds.Height - 6;
        var flyoutBottom = flyoutTop + 420;
        var horizontalReach = Math.Max(320, activatedControl.Bounds.Width * 6);
        var flyoutLeft = activatedControl.Bounds.X - horizontalReach;
        var flyoutRight = activatedControl.Bounds.X + activatedControl.Bounds.Width + horizontalReach;

        return afterControls.Any(control =>
            control.IsEnabled && !control.IsOffscreen &&
            control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            control.Bounds.Y >= flyoutTop && control.Bounds.Y < flyoutBottom &&
            control.Bounds.X < flyoutRight && control.Bounds.X + control.Bounds.Width > flyoutLeft &&
            !beforeKeys.Contains(AutomationShapeKey(control)) &&
            (control.SupportedPatterns?.Count > 0 ||
             control.ControlType.EndsWith(".MenuItem", StringComparison.OrdinalIgnoreCase) ||
             control.ControlType.EndsWith(".ListItem", StringComparison.OrdinalIgnoreCase) ||
             control.ControlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase)));
    }

    private static string AutomationShapeKey(AutomationObservation control) => string.Join('|',
        control.RuntimeId,
        control.AutomationId,
        control.Name,
        control.ControlType,
        control.Bounds.X,
        control.Bounds.Y,
        control.Bounds.Width,
        control.Bounds.Height);

    internal static InteractionOutcome ResolveManualInteractionOutcome(
        AdaptiveClickCaptureOutcome outcome,
        bool hasControl,
        bool hasResultFrame)
    {
        if (!hasControl || !hasResultFrame)
            return InteractionOutcome.Failed;
        return outcome is AdaptiveClickCaptureOutcome.RootCaptured or
            AdaptiveClickCaptureOutcome.PopupCaptured or
            AdaptiveClickCaptureOutcome.DialogCaptured
                ? InteractionOutcome.Succeeded
                : outcome == AdaptiveClickCaptureOutcome.ControlCaptured
                    ? InteractionOutcome.Unobserved
                    : InteractionOutcome.Failed;
    }

    private static InteractionActionKind ResolveInteractionAction(AutomationObservation? control)
    {
        if (control is null) return InteractionActionKind.Unknown;
        if (!string.IsNullOrWhiteSpace(control.ToggleState)) return InteractionActionKind.Toggle;
        if (control.ExpandCollapseState?.Equals("Collapsed", StringComparison.OrdinalIgnoreCase) == true)
            return InteractionActionKind.Expand;
        if (control.ExpandCollapseState?.Equals("Expanded", StringComparison.OrdinalIgnoreCase) == true)
            return InteractionActionKind.Collapse;
        var type = control.ControlType;
        if (type.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".ListItem", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".TreeItem", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith("CanvasItem", StringComparison.OrdinalIgnoreCase))
            return InteractionActionKind.Select;
        if (type.EndsWith(".Edit", StringComparison.OrdinalIgnoreCase))
            return InteractionActionKind.SetValue;
        return InteractionActionKind.Invoke;
    }

    internal static bool IsInlineTopChromeCommand(AutomationObservation control) =>
        control.Name.Equals("Comments", StringComparison.OrdinalIgnoreCase);

    private static async Task<FrameObservation?> CaptureBackstageAsync(
        ManualRecordingSession session,
        AutoTabCandidate backstage,
        long baseFrameSequence,
        InteractionCaptureContext interaction,
        Action onInvoked,
        CancellationToken cancellationToken)
    {
        session.AddMarker("auto-backstage:armed:" + backstage.StableKey);
        var invoked = await session.TryClickControlAsync(backstage.Observation, 1, cancellationToken).ConfigureAwait(false) ||
                      await session.TryInvokeControlAsync(backstage.Observation, cancellationToken).ConfigureAwait(false);
        if (!invoked) return null;

        onInvoked();
        session.AddMarker("auto-backstage:clicked:" + backstage.StableKey);
        await Task.Delay(260, cancellationToken).ConfigureAwait(false);
        try
        {
            return await session.CaptureAsync(
                "auto-backstage:opened:" + backstage.StableKey,
                cancellationToken,
                new FrameCaptureOptions(
                    CapturePhase: "materialized",
                    ObservationScope: "full-root",
                    BaseFrameSequence: baseFrameSequence,
                    InteractionSource: backstage.Observation,
                    InteractionId: interaction.InteractionId,
                    AutomationBeforeScreenshot: true,
                    WaitForDeferredVisualContent: true)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                                   ex is InvalidOperationException && !IsRecordingQuotaReached(ex))
        {
            session.AddCaptureHealth("auto-backstage", "capture-failed", ex.Message);
            return null;
        }
    }

    private static async Task CaptureSafeBackstageNavigationAsync(
        ManualRecordingSession session,
        AdaptiveCaptureCoordinator adaptive,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        FrameObservation backstageFrame,
        CancellationToken cancellationToken)
    {
        var candidates = AutoTabDiscovery.DiscoverBackstageNavigation(backstageFrame)
            .Where(candidate => !candidate.IsSelected)
            .ToArray();
        if (candidates.Length == 0)
        {
            session.AddMarker("auto-backstage:navigation:none");
            return;
        }

        var sourceFrame = backstageFrame;
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AutoPassBudgetPolicy.CanCaptureNextAutoStep(session)) break;

            var candidate = candidates[index];
            var currentCandidate = AutoTabDiscovery.DiscoverBackstageNavigation(sourceFrame)
                .FirstOrDefault(item => item.StableKey == candidate.StableKey) ?? candidate;
            panel.SetStatus($"Checking File: {candidate.DisplayName} ({index + 1}/{candidates.Length})...");
            overlay.ShowClickPulse(sourceFrame.Window.Bounds, currentCandidate.Observation.Bounds);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            var interaction = session.CreateInteractionContext(
                "auto-backstage:navigation:" + candidate.StableKey,
                1,
                InteractionActor.AutoExplorer,
                InteractionGestureKind.Click,
                InteractionActionKind.Select,
                sourceFrame.Sequence,
                currentCandidate.Observation);
            var clicked = await session.TryClickControlAsync(currentCandidate.Observation, 1, cancellationToken).ConfigureAwait(false);
            if (!clicked)
            {
                session.CompleteInteraction(interaction, InteractionOutcome.Failed, diagnosticCode: "activation-failed");
                session.AddMarker("auto-backstage:navigation:skipped:" + candidate.StableKey);
                continue;
            }

            await Task.Delay(220, cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await session.CaptureAsync(
                    "auto-backstage:navigation:opened:" + candidate.StableKey,
                    cancellationToken,
                    new FrameCaptureOptions(
                        CapturePhase: "materialized",
                        ObservationScope: "full-root",
                        BaseFrameSequence: sourceFrame.Sequence,
                        InteractionSource: currentCandidate.Observation,
                        InteractionId: interaction.InteractionId,
                        AutomationBeforeScreenshot: true,
                        WaitForDeferredVisualContent: true)).ConfigureAwait(false);
                session.CompleteInteraction(interaction, InteractionOutcome.Succeeded,
                    [result.Sequence], "backstage-section-captured");
                session.AddMarker("auto-backstage:navigation:mapped:" + candidate.StableKey);
                adaptive.RegisterFullFrame(result);
                sourceFrame = result;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                session.CompleteInteraction(interaction, InteractionOutcome.Failed, diagnosticCode: "capture-failed");
                session.AddCaptureHealth("auto-backstage", "section-capture-failed", ex.Message);
            }
        }
    }

    private static async Task<AutoPassRefreshOutcome> CaptureFirstVisitToTabAsync(
        ManualRecordingSession session,
        AutomationObservation control,
        RectI sourceRootBounds,
        string markerPrefix,
        long baseFrameSequence,
        int attempt,
        Action onInvoked,
        CancellationToken cancellationToken,
        RecordingHighlightOverlay? overlay = null)
    {
        session.AddMarker(markerPrefix + ":armed");
        session.AddMarker($"{markerPrefix}:target:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}");
        var invokeFirst = ShouldInvokeAutoTabBeforeClick(control, attempt);
        async Task<AutoPassRefreshOutcome> ActivateAndCaptureAsync()
        {
            var invoked = invokeFirst
                ? await session.TryInvokeControlAsync(control, cancellationToken).ConfigureAwait(false) ||
                  await session.TryClickControlAsync(control, 1, cancellationToken).ConfigureAwait(false)
                : await session.TryClickControlAsync(control, 1, cancellationToken).ConfigureAwait(false) ||
                  await session.TryInvokeControlAsync(control, cancellationToken).ConfigureAwait(false);
            if (!invoked)
                return AutoPassRefreshOutcome.CreateInvocationFailed();

            session.AddMarker(markerPrefix + ":clicked");
            onInvoked();
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
            var frame = await CaptureRibbonSurfaceAsync(
                session,
                markerPrefix + ":first-visit",
                baseFrameSequence,
                cancellationToken,
                RibbonSurfaceCapturePolicy.Fast,
                reportEmpty: false).ConfigureAwait(false);
            if (frame is null)
            {
                // A successful click and an unavailable provider read are different
                // states. Home used to be falsely marked as an activation failure at
                // this point, so its chevrons were never visited.
                session.AddMarker(markerPrefix + ":capture-retry");
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                frame = await CaptureRibbonSurfaceAsync(
                    session,
                    markerPrefix + ":first-visit-dense",
                    baseFrameSequence,
                    cancellationToken,
                    RibbonSurfaceCapturePolicy.DenseRetry,
                    reportEmpty: true).ConfigureAwait(false);
            }
            if (frame is null)
                return AutoPassRefreshOutcome.CreateCaptureUnavailableAfterActivation();
            if (!IsRequestedTabSelected(control, sourceRootBounds, frame))
                return AutoPassRefreshOutcome.CreateNoStructuralChange();
            return AutoPassRefreshOutcome.CreateStructuralChangePersisted(frame);
        }

        // Keep the transparent highlight surface out of both the physical hit-test
        // and the UIA read that verifies the selected tab. Showing it between the
        // click and capture made Revit's FromPoint lookup resolve UiAtlas itself.
        return overlay is null
            ? await ActivateAndCaptureAsync().ConfigureAwait(false)
            : await overlay.RunHiddenAsync(ActivateAndCaptureAsync, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AutoPassRefreshOutcome> CaptureFirstVisitToLegacyNavigationAsync(
        ManualRecordingSession session,
        AdaptiveCaptureCoordinator adaptive,
        AutomationObservation control,
        string markerPrefix,
        Action onInvoked,
        CancellationToken cancellationToken,
        RecordingHighlightOverlay overlay)
    {
        session.AddMarker(markerPrefix + ":armed");
        session.AddMarker($"{markerPrefix}:target:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}");
        var clicked = await overlay.RunHiddenAsync(
            () => session.TryClickControlAsync(control, 1, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!clicked)
            return AutoPassRefreshOutcome.CreateInvocationFailed();

        session.AddMarker(markerPrefix + ":clicked");
        onInvoked();
        var frame = await adaptive.RefreshRootSurfaceAsync(
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return frame is null
            ? AutoPassRefreshOutcome.CreateCaptureUnavailableAfterActivation()
            : AutoPassRefreshOutcome.CreateStructuralChangePersisted(frame);
    }

    internal static bool ShouldInvokeAutoTabBeforeClick(AutomationObservation control, int attempt)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        // Revit exposes Ribbon tabs as WPF Buttons whose Invoke provider can block
        // for tens of seconds while resolving the virtualized application tree.
        // The tab is already visible and has trusted observed bounds, so activate
        // it with the same physical click the user would make. Confirmation still
        // comes from the selected PanelBarScrollViewer, never from click success.
        return false;
    }

    internal static bool IsRequestedTabSelected(
        AutomationObservation requestedTab,
        RectI sourceRootBounds,
        FrameObservation capturedFrame)
    {
        var targetFingerprint = AutoMappingTargetFingerprint.Create(requestedTab, sourceRootBounds);
        if (AutoTabDiscovery.Discover(capturedFrame).Any(candidate =>
            candidate.IsSelected &&
            string.Equals(
                AutoMappingTargetFingerprint.Create(candidate.Observation, capturedFrame.Window.Bounds),
                targetFingerprint,
                StringComparison.Ordinal)))
            return true;

        // Revit exposes Ribbon tabs as Invoke-only buttons, so SelectionItem and
        // IsSelected are always unavailable. The materialized panel carries the
        // selected tab's stable AutomationId (for example Home_Family ->
        // Home_Family_PanelBarScrollViewer), which is stronger evidence than a
        // generic pixel or tree change.
        if (!LooksLikeRevitWindow(capturedFrame.Window) ||
            string.IsNullOrWhiteSpace(requestedTab.AutomationId))
            return false;

        var requestedId = requestedTab.AutomationId.Trim();
        return capturedFrame.Automation.Any(control =>
            !control.IsOffscreen &&
            control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
            (control.AutomationId.Equals(requestedId + "_PanelBarScrollViewer", StringComparison.OrdinalIgnoreCase) ||
             control.AutomationId.StartsWith(requestedId + "_Panel", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<QuickSurfaceScanResult> CaptureAndRegisterInitialSurfaceAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecorderWorkspace workspace,
        SpeculativePlanningCoordinator speculative,
        string sessionId,
        AutoMappingCampaignState? campaign,
        string trigger,
        CancellationToken cancellationToken,
        bool enableHoverAndFocusDiscovery)
    {
        panel.SetStatus("Stage 1 of 5: capturing the current screen before scanning controls.");
        Console.WriteLine("Recording started. Capturing the screen, then scanning controls and tables. Complex applications can take several minutes.");
        var scan = await QuickSurfaceScanner.CaptureAsync(
            session, trigger, cancellationToken, panel.SetStatus,
            enableHoverAndFocusDiscovery).ConfigureAwait(false);
        panel.SetStatus(scan.HasUsableControls
            ? $"Initial screen saved: {scan.VisibleControlCount} visible controls. Preparing the next steps..."
            : "Initial screen saved without controls. Preparing safe manual capture...");
        if (scan.HasUsableControls)
        {
            await speculative.PrepareAsync(
                scan.Frame, campaign, sessionId, null, cancellationToken).ConfigureAwait(false);
        }

        // Stage the snapshot after speculative planning. Planning persists its own
        // state immediately, while this snapshot becomes immutable evidence only
        // when the surrounding recording bundle is successfully sealed.
        workspace.StageQuickMapSnapshot(new QuickMapSnapshotState(
            sessionId,
            scan.SurfaceFingerprint,
            scan.Status,
            scan.VisibleControlCount,
            scan.UnverifiedControlCount,
            scan.DiagnosticCodes,
            DateTimeOffset.UtcNow,
            scan.ConfirmedControlCount,
            scan.ObservedControlCount,
            scan.CoverageGapCount,
            scan.ExtractionStatus?.ToString() ?? ""));
        Console.WriteLine(
            $"Initial scan finished. Visible: {scan.VisibleControlCount}. Confirmed: {scan.ConfirmedControlCount}. Observed: {scan.ObservedControlCount}. Coverage gaps: {scan.CoverageGapCount}. Status: {scan.ExtractionStatus?.ToString() ?? scan.Status.ToString()}.");
        return scan;
    }

    private static void ShowObservedSurfaceHighlights(
        RecordingHighlightOverlay overlay,
        FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(frame);
        const string initialLayerKey = "__initial_surface__";
        var overlayRootBounds = ResolveOverlayRootBounds(overlay, frame);
        var native = AutomationObservationVisibility.FilterEffectivelyVisible(frame.Automation)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => !control.FrameworkId.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase));
        var visual = frame.Automation.Where(control => IsVisualSurfaceHighlight(control, overlayRootBounds));
        var visible = native.Concat(visual)
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(1_500)
            .ToArray();
        if (visible.Length == 0) return;

        var visibleLayerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(
            frame, [], initialLayerKey);
        var highlightsByLayer = visible.GroupBy(control =>
                TabHighlightLayerResolver.ResolveLayerKey(
                    frame, [control.Bounds], visibleLayerKey),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RectI>)group.Select(control => control.Bounds).ToArray(),
                StringComparer.Ordinal);
        overlay.ReplaceObservedHighlights(overlayRootBounds, highlightsByLayer, visibleLayerKey);
    }

    private static RectI ResolveOverlayRootBounds(
        RecordingHighlightOverlay overlay,
        FrameObservation frame)
    {
        var capturedTarget = frame.ScopedWindows?.FirstOrDefault(window => window.Hwnd == overlay.TargetHwnd);
        if (capturedTarget is not null && capturedTarget.Bounds.Width > 0 && capturedTarget.Bounds.Height > 0)
            return capturedTarget.Bounds;
        try
        {
            return WindowCatalog.Resolve(overlay.TargetHwnd).Bounds;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            return frame.Window.Bounds;
        }
    }

    private static bool IsVisualSurfaceHighlight(AutomationObservation control, RectI rootBounds)
    {
        if (control.FrameworkId is not ("UiAtlas.Visual.Ocr" or "UiAtlas.Visual.Geometry") ||
            control.Bounds.Width < 12 || control.Bounds.Height < 8)
            return false;

        var type = control.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
            ? control.ControlType[12..]
            : control.ControlType;
        if (type is "DataItem" or "ListItem")
            return false;
        if (type is not ("Button" or "Edit" or "Table" or "List"))
            return false;

        var area = (long)control.Bounds.Width * control.Bounds.Height;
        var rootArea = Math.Max(1L, (long)rootBounds.Width * rootBounds.Height);
        return type is "Table" or "List" ? area <= rootArea * 9 / 10 : area <= rootArea / 3;
    }

    private static void SynchronizeOverlayWithAdaptiveFrames(
        AdaptiveCaptureCoordinator adaptive,
        RecordingHighlightOverlay overlay)
    {
        adaptive.FullFrameRegistered += frame =>
        {
            ShowObservedSurfaceHighlights(overlay, frame);
        };
    }

    private static async Task<FrameObservation?> CaptureRibbonSurfaceAsync(
        ManualRecordingSession session,
        string trigger,
        long baseFrameSequence,
        CancellationToken cancellationToken,
        RibbonSurfaceCaptureProfile? profile = null,
        bool reportEmpty = true,
        bool includeWorksheet = false,
        bool preferScreenBoundsScreenshot = false,
        FrameObservation? requiredInlineChangeFrom = null,
        AutomationObservation? activatedControl = null)
    {
        var captureProfile = profile ?? RibbonSurfaceCapturePolicy.Fast;
        captureProfile = ResolveRibbonCaptureProfile(session, captureProfile);
        var visiblePopupTargets = AdaptiveCaptureCoordinator.SelectVisiblePopupTargets(
            session.TargetRootOwnerHwnd,
            WindowCatalog.ListScopedWindows(session.TargetRootOwnerHwnd));
        var popupItems = new List<AutomationObservation>();
        var popupTimedOut = false;
        foreach (var popup in visiblePopupTargets)
        {
            var popupAutomation = await session.CollectPopupAutomationAsync(
                popup.Hwnd,
                TimeSpan.FromMilliseconds(3_500),
                600,
                cancellationToken).ConfigureAwait(false);
            popupTimedOut |= popupAutomation.TimedOut;
            if (popupAutomation.TimedOut || popupAutomation.Status is not ("ok" or "node-limit"))
            {
                session.AddCaptureHealth("ribbon-popup", popupAutomation.Status,
                    $"Visible popup 0x{popup.Hwnd:X} could not be attached to the Ribbon frame.");
                continue;
            }

            var normalized = AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, popupAutomation.Items);
            if (normalized.Count > 0)
                popupItems.AddRange(normalized);
        }
        // Do not issue concurrent navigation and Ribbon tree walks against Office.
        // Excel serves both from one UIA provider and parallel workers can make
        // both deadlines expire with no partial result.
        var navigation = await session.CollectNavigationAutomationAsync(
            session.TargetRootOwnerHwnd,
            captureProfile.NavigationTimeout,
            captureProfile.NavigationMaxNodes,
            cancellationToken).ConfigureAwait(false);
        var ribbon = await session.CollectRibbonAutomationAsync(
            session.TargetRootOwnerHwnd,
            captureProfile.RibbonTimeout,
            captureProfile.RibbonMaxNodes,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AutomationObservation> bodyItems = [];
        var bodyTimedOut = false;
        var target = WindowCatalog.Resolve(session.TargetRootOwnerHwnd);
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) nativePeripheral =
            ([], false, "ok");
        if (QuickSurfaceScanner.IsRevitTarget(target))
        {
            nativePeripheral = await session.CollectNativePeripheralAutomationAsync(
                session.TargetRootOwnerHwnd,
                TimeSpan.FromMilliseconds(700),
                800,
                cancellationToken).ConfigureAwait(false);
        }
        var needsOutlookBody = RibbonSurfaceCapturePolicy.NeedsVisibleApplicationBody(target);
        if (includeWorksheet || needsOutlookBody)
        {
            if (needsOutlookBody)
            {
                // Outlook Classic keeps its folder tree, virtualized message list and
                // bottom navigation outside the Ribbon provider subtree. Preserve that
                // complete visible surface for every Ribbon state; otherwise a fresh
                // screenshot can be paired with controls cached from another module.
                var outlookBody = await session.CollectWindowAutomationAsync(
                    session.TargetRootOwnerHwnd,
                    TimeSpan.FromSeconds(20),
                    RecordingContractLimits.MaxControlsPerFrame,
                    cancellationToken).ConfigureAwait(false);
                bodyTimedOut = outlookBody.TimedOut;
                if (!outlookBody.TimedOut && outlookBody.Status is "ok" or "node-limit")
                    bodyItems = outlookBody.Items;
                else
                    session.AddCaptureHealth("outlook-body", outlookBody.Status,
                        "The visible Outlook folders and message controls were unavailable; Ribbon controls were retained.");
            }
            else
            {
                var worksheet = await session.CollectWorksheetAutomationAsync(
                    session.TargetRootOwnerHwnd,
                    TimeSpan.FromSeconds(3),
                    2_000,
                    cancellationToken).ConfigureAwait(false);
                bodyTimedOut = worksheet.TimedOut;
                if (!worksheet.TimedOut && worksheet.Status is "ok" or "node-limit")
                    bodyItems = worksheet.Items;
                else
                    session.AddCaptureHealth("worksheet", worksheet.Status,
                        "The visible worksheet control pass was unavailable; Ribbon controls were retained.");
            }
        }
        var controls = navigation.Items.Concat(ribbon.Items).Concat(nativePeripheral.Items)
            .Concat(bodyItems).Concat(popupItems)
            .GroupBy(control => string.IsNullOrWhiteSpace(control.RuntimeId)
                ? $"bounds:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}:{control.AutomationId}"
                : control.RuntimeId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        if (!RibbonSurfaceCapturePolicy.HasMaterializedRibbonContent(ribbon.Items))
        {
            if (reportEmpty)
                session.AddCaptureHealth("auto-tabs", "ribbon-empty", "The Ribbon surface returned no materialized content controls and was not marked as captured.");
            return null;
        }
        if (requiredInlineChangeFrom is not null && activatedControl is not null &&
            !HasMaterializedInlineFlyout(requiredInlineChangeFrom, controls, activatedControl))
        {
            if (reportEmpty)
                session.AddCaptureHealth("auto-tabs", "inline-flyout-not-open",
                    $"{activatedControl.Name} did not expose new flyout controls after activation.");
            return null;
        }

        return await session.CaptureAsync(trigger, cancellationToken,
            new FrameCaptureOptions(
                IncludeAutomation: false,
                CapturePhase: "materialized",
                ObservationScope: "full-root",
                BaseFrameSequence: baseFrameSequence,
                AutomationOverride: controls,
                AutomationTimedOutOverride: navigation.TimedOut || ribbon.TimedOut || nativePeripheral.TimedOut || bodyTimedOut || popupTimedOut,
                AutomationStatusOverride: navigation.TimedOut || ribbon.TimedOut || nativePeripheral.TimedOut || bodyTimedOut || popupTimedOut ? "partial" : "ok",
                PreferScreenBoundsScreenshot: preferScreenBoundsScreenshot)).ConfigureAwait(false);
    }

    private static RibbonSurfaceCaptureProfile ResolveRibbonCaptureProfile(
        ManualRecordingSession session,
        RibbonSurfaceCaptureProfile fallback)
    {
        try
        {
            return RibbonSurfaceCapturePolicy.ForTarget(
                WindowCatalog.Resolve(session.TargetRootOwnerHwnd), fallback);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return fallback;
        }
    }

    internal static bool LooksLikeRevitWindow(WindowObservation window) =>
        window.Title.Contains("Revit", StringComparison.OrdinalIgnoreCase) ||
        window.ClassName.Contains("Revit", StringComparison.OrdinalIgnoreCase);

    private static AutoTabCandidate? ResolveActiveAutoPassTab(
        IReadOnlyList<AutoTabCandidate> discoveredTabs,
        string? currentVisibleLayerKey,
        string? fallbackStableKey) =>
        AutoPassTraversalPolicy.ResolveActiveTab(discoveredTabs, currentVisibleLayerKey, fallbackStableKey);

    private static string ResolveAutoPassVisibleLayerKey(
        FrameObservation frame,
        string currentVisibleLayerKey,
        string? preferredVisibleLayerKey)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var fallbackVisibleLayerKey = string.IsNullOrWhiteSpace(preferredVisibleLayerKey)
            ? currentVisibleLayerKey
            : preferredVisibleLayerKey;
        return TabHighlightLayerResolver.ResolveVisibleLayerKey(frame.Window, frame.Automation, fallbackVisibleLayerKey);
    }

    private static bool TryResolveLaunchMode(string command, out SessionLaunchMode mode)
    {
        switch (command.Trim().ToUpperInvariant())
        {
            case "RESUME_QUICK":
                mode = SessionLaunchMode.RescanCurrentScreen;
                return true;

            case "START_MANUAL":
            case "RESUME_MANUAL":
                mode = SessionLaunchMode.Manual;
                return true;

            case "START_AUTO":
            case "RESUME_AUTO":
                mode = SessionLaunchMode.AutoTabs;
                return true;

            default:
                mode = default;
                return false;
        }
    }

    internal static bool IsMapReadyLaunchCommand(string command) =>
        command.Trim().ToUpperInvariant() is
            "RESUME_QUICK" or "RESUME_MANUAL" or "RESUME_AUTO" or
            "START_MANUAL" or "START_AUTO";

    internal static bool IsNewMapLaunchCommand(string command) =>
        command.Trim().ToUpperInvariant() is "START_MANUAL" or "START_AUTO";

    internal static string RescanLaunchCommand(bool resumeMode) =>
        resumeMode
            ? "RESUME_QUICK"
            : throw new InvalidOperationException("A current-screen rescan requires an existing map.");

    internal static string SessionModeLaunchCommand(bool resumeMode, bool autoTabs) =>
        resumeMode
            ? autoTabs ? "RESUME_AUTO" : "RESUME_MANUAL"
            : autoTabs ? "START_AUTO" : "START_MANUAL";

    private static async Task<string> WaitForMapReadyCommandAsync(RecordingControlPanel panel, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (panel.IsClosed)
                return PanelCloseCommand;
            if (panel.TryDequeueCommand(out var command))
            {
                command = command.Trim().ToUpperInvariant();
                if (IsMapReadyLaunchCommand(command) || command is "FOCUS_READY" or "C" or PanelCloseCommand)
                    return command;
            }
            if (TryReadConsoleKey(out var key))
            {
                switch (key.Key)
                {
                    case ConsoleKey.S:
                    case ConsoleKey.Enter:
                        panel.ShowSessionModeChooser(resumeMode: false);
                        break;

                    case ConsoleKey.D1:
                    case ConsoleKey.NumPad1:
                        if (panel.SessionModeChooserOpen)
                            return SessionModeLaunchCommand(panel.SessionModeChooserResumesMap, autoTabs: false);
                        break;

                    case ConsoleKey.D2:
                    case ConsoleKey.NumPad2:
                        if (panel.SessionModeChooserOpen)
                            return SessionModeLaunchCommand(panel.SessionModeChooserResumesMap, autoTabs: true);
                        break;

                    case ConsoleKey.Q:
                        if (panel.SessionModeChooserOpen && panel.SessionModeChooserResumesMap)
                            return RescanLaunchCommand(resumeMode: true);
                        break;

                    case ConsoleKey.Escape:
                        if (panel.SessionModeChooserOpen) panel.HideSessionModeChooser();
                        else return "C";
                        break;

                    case ConsoleKey.C:
                        return "C";

                    case ConsoleKey.A:
                        return "FOCUS_READY";
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> WaitForStartCommandAsync(RecordingControlPanel panel, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (panel.IsClosed)
                return PanelCloseCommand;
            if (panel.TryDequeueCommand(out var command))
            {
                command = command.Trim().ToUpperInvariant();
                if (command is "START_MANUAL" or "START_AUTO" or "C" or PanelCloseCommand)
                    return command;
            }
            if (TryReadConsoleKey(out var key))
            {
                if (IsHelpKey(key))
                {
                    PrintRecorderStartConsoleHelp(targetSelectionRequired: !panel.CanStartRecording);
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.S:
                    case ConsoleKey.Enter:
                        if (panel.CanStartRecording) panel.ShowSessionModeChooser(resumeMode: false);
                        else panel.PromptTargetSelection();
                        break;

                    case ConsoleKey.D1:
                    case ConsoleKey.NumPad1:
                        if (panel.SessionModeChooserOpen) return "START_MANUAL";
                        break;

                    case ConsoleKey.D2:
                    case ConsoleKey.NumPad2:
                        if (panel.SessionModeChooserOpen) return "START_AUTO";
                        break;

                    case ConsoleKey.Escape:
                        if (panel.SessionModeChooserOpen) panel.HideSessionModeChooser();
                        else return "C";
                        break;

                    case ConsoleKey.C:
                        return "C";
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> WaitForRecordingCommandAsync(RecordingControlPanel panel, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (panel.IsClosed)
                return PanelCloseCommand;
            if (panel.TryDequeueCommand(out var command))
            {
                command = command.Trim().ToUpperInvariant();
                if (command is "N" or "T" or "A" or "F" or "C" or "P" or "R" or "SKIP_AUTO" or "CONTINUE_AUTO" or PanelCloseCommand)
                    return command;
            }
            if (TryReadConsoleKey(out var key))
            {
                if (IsHelpKey(key))
                {
                    PrintRecorderActiveConsoleHelp();
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var keyboardCommand = key.Key switch
                {
                    ConsoleKey.N => "N",
                    ConsoleKey.T => "T",
                    ConsoleKey.A => "A",
                    ConsoleKey.P => "P",
                    ConsoleKey.R => "R",
                    ConsoleKey.F => "F",
                    ConsoleKey.C or ConsoleKey.Escape => "C",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(keyboardCommand)) return keyboardCommand;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> WaitForPausedRecordingCommandAsync(RecordingControlPanel panel, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (panel.IsClosed)
                return PanelCloseCommand;
            if (panel.TryDequeueCommand(out var command))
            {
                command = command.Trim().ToUpperInvariant();
                if (command is "R" or "F" or "C" or PanelCloseCommand)
                    return command;
            }

            if (TryReadConsoleKey(out var key))
            {
                if (IsHelpKey(key))
                {
                    PrintRecorderPausedConsoleHelp();
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var keyboardCommand = key.Key switch
                {
                    ConsoleKey.R => "R",
                    ConsoleKey.F => "F",
                    ConsoleKey.C or ConsoleKey.Escape => "C",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(keyboardCommand)) return keyboardCommand;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryReadActiveAutoPassCommand(RecordingControlPanel panel, out string command)
    {
        if (panel.IsClosed)
        {
            command = PanelCloseCommand;
            return true;
        }

        if (panel.TryDequeueCommand(out command))
        {
            command = command.Trim().ToUpperInvariant();
            return command is "SKIP_AUTO" or "F" or "C" or PanelCloseCommand;
        }

        if (!TryReadConsoleKey(out var key))
        {
            command = string.Empty;
            return false;
        }

        if (IsHelpKey(key))
        {
            PrintRecorderAutoTabsConsoleHelp();
            command = string.Empty;
            return false;
        }

        command = key.Key switch
        {
            ConsoleKey.S => "SKIP_AUTO",
            ConsoleKey.F => "F",
            ConsoleKey.C or ConsoleKey.Escape => "C",
            _ => string.Empty
        };
        return command.Length > 0;
    }

    private static bool TryReadConsoleKey(out ConsoleKeyInfo key)
    {
        key = default;
        try
        {
            if (!Console.KeyAvailable)
                return false;
            key = Console.ReadKey(intercept: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasBuiltMap(RecorderWorkspace workspace) =>
        File.Exists(workspace.MapPath);

    private static bool IsRecordingQuotaReached(Exception exception) =>
        exception is InvalidOperationException &&
        exception.Message.Contains("Recording quota reached", StringComparison.OrdinalIgnoreCase);

    private static AutoTabsOutcome StopAutoPassForQuota(RecordingControlPanel panel)
    {
        panel.SetStatus(AutoPassQuotaStopMessage);
        Console.WriteLine("Auto labels reached the capture limit. Saving a partial map and returning to Map Ready.");
        return AutoTabsOutcome.SavePartialMap;
    }

    private static bool IsRecorderWorkflowRecoverable(Exception exception) => exception is
        IOException or InvalidDataException or InvalidOperationException or ArgumentException or
        UnauthorizedAccessException or NotSupportedException or TimeoutException or
        System.ComponentModel.Win32Exception;

    private static bool IsTargetForeground(long rootOwnerHwnd, int processId)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
            return false;

        if (WindowCatalog.GetRootOwnerHandle(foreground).ToInt64() == rootOwnerHwnd)
            return true;

        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        return foregroundProcessId != 0 && foregroundProcessId == processId;
    }

    private static async Task<bool> ActivateTargetWithOutlineAsync(
        ManualRecordingSession session,
        RecordingHighlightOverlay overlay,
        CancellationToken cancellationToken)
    {
        var activated = await session.ActivateTargetAsync(cancellationToken).ConfigureAwait(false);
        if (activated)
            overlay.ShowTargetFocusOutline(TimeSpan.FromMilliseconds(800));
        return activated;
    }

    private static async Task<bool> ActivateReadyTargetWindowAsync(
        WindowTarget target,
        CancellationToken cancellationToken)
    {
        var focused = await WindowActivation.ActivateAsync((nint)target.Hwnd, cancellationToken).ConfigureAwait(false);
        if (!focused)
            return false;

        try
        {
            using var overlay = new RecordingHighlightOverlay(target);
            overlay.Start();
            overlay.ShowTargetFocusOutline(TimeSpan.FromMilliseconds(2200));
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Focusing the target matters more than the hint overlay; ignore transient UI-overlay failures.
        }

        return true;
    }

    private static async Task<bool> ReactivateTargetWindowAsync(
        ManualRecordingSession session,
        RecordingHighlightOverlay overlay,
        CancellationToken cancellationToken)
    {
        if (session.IsTargetForeground())
        {
            overlay.ShowTargetFocusOutline(TimeSpan.FromMilliseconds(800));
            return true;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(attempt == 0 ? 100 : 150, cancellationToken).ConfigureAwait(false);
            if (!await session.ActivateTargetAsync(cancellationToken).ConfigureAwait(false))
                continue;

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (!session.IsTargetForeground())
                continue;

            overlay.ShowTargetFocusOutline(TimeSpan.FromMilliseconds(800));
            return true;
        }

        overlay.HideTargetFocusOutline();
        return false;
    }

    private static async Task ShowInitialManualClickFeedbackAsync(
        ManualRecordingSession session,
        WindowTarget target,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        DateTimeOffset afterExclusive,
        Action surfaceInvalidated,
        CancellationToken cancellationToken)
    {
        var cursor = afterExclusive;
        while (true)
        {
            var clicks = await session.WaitForRecordedTargetClicksAsync(
                cursor, 1, progress: null, cancellationToken).ConfigureAwait(false);
            var click = clicks[0];
            cursor = click.TimestampUtc;
            surfaceInvalidated();
            var point = new RectI(click.X, click.Y, 1, 1);
            var currentTarget = ResolveCurrentOverlayTarget(target);
            overlay.ShowConfirmedClickPulse(
                currentTarget.Bounds,
                new RectI(click.X - 9, click.Y - 9, 18, 18),
                TimeSpan.FromSeconds(20));
            panel.SetStatus("Click detected. Background recognition is still finishing...");

            var bounds = await TryResolveImmediateVisualBoundsAsync(
                currentTarget, point, overlay, cancellationToken).ConfigureAwait(false);
            if (bounds is not null)
                overlay.ShowConfirmedClickPulse(currentTarget.Bounds, bounds, TimeSpan.FromSeconds(20));
        }
    }

    private static async Task<RectI?> TryResolveImmediateVisualBoundsAsync(
        WindowTarget target,
        RectI point,
        RecordingHighlightOverlay overlay,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
            var capture = await overlay.RunCaptureHiddenAsync(
                () => WindowSnapshotCapture.CapturePngAsync(
                    [target], timeout.Token, preferScreenBounds: true),
                timeout.Token).ConfigureAwait(false);
            var pixels = OpaqueSurfaceScanner.PixelFrame.Decode(capture.Png);
            var visual = VisualSurfaceScanner.Discover(target, pixels, [target.Bounds], []);
            return visual
                .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                                  point.X >= control.Bounds.X && point.Y >= control.Bounds.Y &&
                                  point.X < control.Bounds.X + control.Bounds.Width &&
                                  point.Y < control.Bounds.Y + control.Bounds.Height)
                .OrderBy(control => (long)control.Bounds.Width * control.Bounds.Height)
                .Select(control => (RectI?)control.Bounds)
                .FirstOrDefault();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async Task RefineManualClickFeedbackAsync(
        WindowTarget target,
        RectI point,
        RecordingHighlightOverlay overlay,
        CancellationToken cancellationToken)
    {
        var currentTarget = ResolveCurrentOverlayTarget(target);
        var bounds = await TryResolveImmediateVisualBoundsAsync(
            currentTarget, point, overlay, cancellationToken).ConfigureAwait(false);
        if (bounds is not null)
            overlay.ShowConfirmedClickPulse(currentTarget.Bounds, bounds, TimeSpan.FromSeconds(20));
    }

    private static WindowTarget ResolveCurrentOverlayTarget(WindowTarget target)
    {
        try
        {
            return WindowCatalog.Resolve(target.Hwnd);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            return target;
        }
    }

    private static async Task RestoreManualSingleClickCaptureAsync(
        ManualRecordingSession session,
        RecordingControlPanel panel,
        RecordingHighlightOverlay overlay,
        bool resumeInputCapture,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Auto tab pass finished. Manual single-click capture is active.");
        session.DismissTransientPopup();
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        if (!await ReactivateTargetWindowAsync(session, overlay, cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine("The selected application could not be activated automatically after auto tabs; focus it manually.");
            panel.SetStatus("The target app was not activated automatically. Focus it manually.");
            return;
        }

        if (resumeInputCapture)
        {
            session.SetInputCapturePaused(false);
            session.AddMarker("manual-mode:armed");
        }
        panel.SetStatus("Automatic single-click capture is active.");
        PrintRecorderActiveConsoleHelp();
    }

    private static int SyntheticRecord(string[] args)
    {
        var output = RequiredOption(args, "--out");
        var staging = Path.Combine(Path.GetTempPath(), "ui-atlas-synthetic-" + Guid.NewGuid().ToString("N"));
        using var writer = new RecordingBundleWriter(staging);
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        writer.WriteText("raw/input-events.jsonl", JsonSerializer.Serialize(new InputEvent(1, started.AddSeconds(1), InputEventKind.PointerDown, 120, 140, 0), JsonLineOptions) + "\n");
        writer.WriteText("raw/capture-health.jsonl", JsonSerializer.Serialize(new CaptureHealthEvent(started, "synthetic", "ok", "Synthetic fixture.", true), JsonLineOptions) + "\n");
        var window = new WindowObservation(100, 100, 4242, "SyntheticWindow", "Synthetic target", new(10, 20, 800, 600), true, true, false, false, 96,
            Style: 0x10CF0000);
        var resized = window with { Bounds = new RectI(10, 20, 1200, 900), Dpi = 144 };
        var popup = new WindowObservation(101, 100, 4242, "SyntheticPopup", "Synthetic popup", new(850, 140, 280, 320), true, true, false, false, 144,
            OwnerHwnd: 100, ZOrder: 1, Style: unchecked((int)0x90000000), ExStyle: 0x88, IsToolWindow: true, IsTopMost: true);
        const string firstFrameEntry = "raw/frames/frame-000001.png";
        const string secondFrameEntry = "raw/frames/frame-000002.png";
        writer.WriteBytes(firstFrameEntry, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        writer.WriteBytes(secondFrameEntry, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z8fQAAAAASUVORK5CYII="));
        var first = new FrameObservation(1, started, firstFrameEntry, window,
            [new("1", "", "root", "Document", "Pane", "RootPane", new(10, 20, 800, 600), true, false, "Synthetic", 100),
             new("1.1", "1", "primary", "Primary action", "Button", "Button", new(50, 60, 120, 30), true, false, "Synthetic", 100, ["Invoke"]),
             new("1.2", "1", "", "Secondary action", "Button", "Button", new(190, 60, 120, 30), false, true, "Synthetic", 100)],
            false, "ok", "initial", [window]);
        var second = new FrameObservation(2, started.AddSeconds(2), secondFrameEntry, resized,
            [new("2.1", "2", "choice", "Choice", "ListItem", "ListItem", new(880, 210, 180, 36), true, false, "Synthetic", 101, ["SelectionItem"], IsSelected: true),
             new("1.2", "1", "", "Secondary action", "Button", "Button", new(280, 90, 180, 45), true, false, "Synthetic", 100),
             new("1", "", "root", "Document changed", "Pane", "RootPane", new(10, 20, 1200, 900), true, false, "Synthetic", 100),
             new("2", "", "popup", "Synthetic popup", "Window", "Popup", new(850, 140, 280, 320), true, false, "Synthetic", 101),
             new("1.1", "1", "primary", "Primary action updated", "Button", "Button", new(70, 90, 180, 45), false, false, "Synthetic", 100, ["Invoke"])],
            false, "ok", "manual-next", [resized, popup]);
        writer.WriteJson("raw/observations/frame-000001.json", first);
        writer.WriteJson("raw/observations/frame-000002.json", second);
        writer.WriteJson("derived/statebook.json", new DerivedStatebook("statebook/1", [1, 2], [new("episode-000001", 1, 1, 2, "pointer", "input-correlated")]));
        var syntheticSessionId = Path.GetFileNameWithoutExtension(Path.GetFullPath(output));
        if (string.IsNullOrWhiteSpace(syntheticSessionId)) syntheticSessionId = "synthetic-golden-v1";
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, syntheticSessionId, started, started.AddSeconds(2),
            RecordingOutcome.Complete, new(100, 100, 4242, "SyntheticTarget", started.AddHours(-1), ProductVersion: "1.0.0",
                OriginalFilename: "SyntheticTarget.exe", CompanyName: "Example Corp", ProductName: "Synthetic Target"),
            new(ScreenshotsRetained: true), new(), true, 1, 2, Files: writer.DescribeEntries());
        writer.WriteJson("manifest.json", manifest);
        writer.Complete(output);
        Console.WriteLine(Path.GetFullPath(output));
        return 0;
    }

    private static int Build(string[] args)
    {
        if (args.Length == 0) return Fail("build requires a recording bundle.");
        var output = RequiredOption(args, "--out");
        var graph = new RecordingGraphBuilder().Build(args[0]);
        if (Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase)) GraphJsonStore.Save(graph, output);
        else SqliteGraphStore.Save(graph, output);
        Console.WriteLine($"Built {graph.Nodes.Count} nodes and {graph.Edges.Count} edges. Semantic hash: {graph.Metadata.SemanticHash}");
        return 0;
    }

    private static int Validate(string[] args)
    {
        if (args.Length != 1) return Fail("validate requires one path.");
        return Path.GetExtension(args[0]).Equals(".mlrec", StringComparison.OrdinalIgnoreCase)
            ? ValidateRecording(args[0])
            : ValidateGraph(args[0]);
    }

    private static int ValidateRecording(string path) =>
        PrintValidation(RecordingBundleValidator.Validate(path));

    private static int ValidateGraph(string path)
    {
        try { return PrintValidation(GraphValidator.Validate(new UiGraphReader().Load(path))); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"error: graph.invalid at graph: {BundleSecurity.SafeDiagnostic(ex.Message, 500)}");
            Console.WriteLine("invalid");
            return 4;
        }
    }

    private static int PrintValidation(ValidationReport report)
    {
        foreach (var issue in report.Issues) Console.WriteLine($"{issue.Severity}: {issue.Code} at {BundleSecurity.SafeDiagnostic(issue.Path)}: {BundleSecurity.SafeDiagnostic(issue.Message, 500)}");
        Console.WriteLine(report.IsValid ? "valid" : "invalid");
        return report.IsValid ? 0 : 4;
    }

    private static int Inspect(string[] args)
    {
        if (args.Length == 0) return Fail("inspect requires a graph path.");
        var reader = new UiGraphReader();
        var graph = reader.Load(args[0]);
        var query = OptionalOption(args, "--query");
        var nodes = query is null ? graph.Nodes : reader.Search(graph, query);
        var world = OptionalOption(args, "--world");
        if (world is not null)
        {
            var layer = world.ToLowerInvariant() switch
            {
                "streams" or "rds" or "raw-data-streams" => "raw-data-streams",
                "raw" => "raw-world",
                "semantic" => "semantic-world",
                "shared" => "shared",
                _ => throw new ArgumentException("--world must be streams, raw, semantic, or shared.")
            };
            nodes = nodes.Where(node => node.Properties.Any(property => property.Name == "layer" && property.Value == layer)).ToArray();
        }
        Console.WriteLine($"Graph {graph.Metadata.GraphId}: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, profile {graph.Metadata.PrivacyProfile}");
        foreach (var group in graph.Nodes.Select(node => node.Properties.FirstOrDefault(property => property.Name == "layer")?.Value ?? "unlayered")
                     .GroupBy(layer => layer).OrderBy(group => group.Key, StringComparer.Ordinal))
            Console.WriteLine($"{group.Key}: {group.Count()} nodes");
        foreach (var node in nodes.Take(200))
        {
            var layer = node.Properties.FirstOrDefault(property => property.Name == "layer")?.Value ?? "unlayered";
            Console.WriteLine($"{layer}\t{node.Kind}\t{node.Id}\t{SanitizeConsole(node.Label)}");
        }
        return 0;
    }

    private static int Export(string[] args)
    {
        if (args.Length == 0) return Fail("export requires a graph path.");
        var output = RequiredOption(args, "--out");
        var full = args.Contains("--include-sensitive-evidence", StringComparer.Ordinal);
        if (full && !args.Contains("--acknowledge-sensitive-evidence", StringComparer.Ordinal))
            return Fail("Full evidence export requires --acknowledge-sensitive-evidence.");
        var graph = new UiGraphReader().Load(args[0]);
        GraphJsonStore.Save(GraphExport.ApplyProfile(graph, full), output);
        Console.WriteLine($"Exported {Path.GetFullPath(output)} using {(full ? FormatVersions.FullEvidenceProfile : FormatVersions.SafeExportProfile)}.");
        return 0;
    }

    private static int ExportUiAtlas(string[] args)
    {
        if (args.Length == 0) return Fail("export-ui-atlas requires a graph path.");
        var output = RequiredOption(args, "--out");
        var projectId = RequiredOption(args, "--project-id");
        if (!args.Contains("--acknowledge-sensitive-identities", StringComparer.Ordinal))
            return Fail("UiAtlas compatibility export requires --acknowledge-sensitive-identities.");
        var graph = new UiGraphReader().Load(args[0]);
        var hash = UiAtlasVNextCompatibilityExporter.Publish(graph, output, projectId, acknowledgeSensitiveIdentities: true);
        Console.WriteLine($"Exported {Path.GetFullPath(output)} for project {SanitizeConsole(projectId)}.");
        Console.WriteLine($"SHA-256: {hash}");
        return 0;
    }

    private static int ValidateUiAtlasExport(string[] args)
    {
        if (args.Length != 1) return Fail("validate-ui-atlas-export requires one path.");
        var report = UiAtlasVNextCompatibilityValidator.ValidateFile(args[0]);
        foreach (var issue in report.Issues)
            Console.WriteLine($"{issue.Severity}: {issue.Code} at {BundleSecurity.SafeDiagnostic(issue.Path)}: {BundleSecurity.SafeDiagnostic(issue.Message, 500)}");
        Console.WriteLine(report.IsValid ? "valid" : "invalid");
        return report.IsValid ? 0 : 4;
    }

    private static int Diff(string[] args)
    {
        if (args.Length != 2) return Fail("diff requires two graph paths.");
        var reader = new UiGraphReader();
        var diff = UiGraphDiff.Compare(reader.Load(args[0]), reader.Load(args[1]));
        Console.WriteLine($"nodes +{diff.AddedNodes.Count} -{diff.RemovedNodes.Count}; edges +{diff.AddedEdges.Count} -{diff.RemovedEdges.Count}");
        return 0;
    }

    private static int Open(string[] args)
    {
        if (args.Length is < 1 or > 2) return Fail("open requires a graph path and optional evidence path.");
        OpenMapViewer(args[0], args.Length == 2 ? args[1] : null);
        return 0;
    }

    internal static void OpenMapViewer(string graphPath, string? evidencePath)
    {
        var basePath = AppContext.BaseDirectory;
        var executable = Path.Combine(basePath, "UiAtlas.Core.Desktop.exe");
        var assembly = Path.Combine(basePath, "UiAtlas.Core.Desktop.dll");
        if (!File.Exists(executable) && !File.Exists(assembly))
        {
            var outputDirectory = new DirectoryInfo(basePath.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = outputDirectory.Parent?.Name;
            var repository = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", ".."));
            if (configuration is not null)
                assembly = Path.Combine(repository, "src", "UiAtlas.Core.Desktop", "bin", configuration, "net10.0-windows10.0.19041.0", "UiAtlas.Core.Desktop.dll");
        }
        ProcessStartInfo start;
        if (File.Exists(executable)) start = new(executable);
        else if (File.Exists(assembly)) { start = new("dotnet"); start.ArgumentList.Add(assembly); }
        else throw new InvalidOperationException("Desktop explorer is not installed beside the CLI.");
        start.ArgumentList.Add(Path.GetFullPath(graphPath));
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            start.ArgumentList.Add("--evidence");
            start.ArgumentList.Add(Path.GetFullPath(evidencePath));
        }
        Process.Start(start);
    }

    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonDefaults.Options) { WriteIndented = false };
    private static void PrintHelpHeader()
    {
        Console.WriteLine($"UiAtlas Core version {FormatVersions.Tool}");
        Console.WriteLine();
    }
    private static bool IsCommand(string value, string command) =>
        string.Equals(value, command, StringComparison.OrdinalIgnoreCase);

    private static void ValidateOptions(string[] args, int positionalCount, IReadOnlyList<string> flags, IReadOnlyList<string> valued)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = positionalCount; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate option: {option}.");
            if (flags.Contains(option)) continue;
            if (!valued.Contains(option)) throw new ArgumentException($"Unknown option: {option}.");
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {option}.");
        }
    }

    private static string[] ParseInteractiveCommand(string line)
    {
        var pointer = CommandLineToArgvW("ui-atlas " + line, out var count);
        if (pointer == 0) throw new InvalidOperationException("Interactive command could not be parsed.");
        try
        {
            var values = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                values[index - 1] = Marshal.PtrToStringUni(item) ?? string.Empty;
            }
            return values;
        }
        finally { LocalFree(pointer); }
    }

    private static string? ReadInteractiveLine()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                return Console.ReadLine();
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Backspace:
                    if (buffer.Length == 0)
                        continue;
                    buffer.Length--;
                    Console.Write("\b \b");
                    continue;

                case ConsoleKey.Escape:
                    while (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
            }

            if (char.IsControl(key.KeyChar))
                continue;

            buffer.Append(key.KeyChar);
            Console.Write(key.KeyChar);
        }
    }

    private static string ExpandInteractiveShortcut(string line) =>
        string.Equals(line.Trim(), "R", StringComparison.Ordinal)
            ? "recording start"
            : line;

    private static string RequiredOption(string[] args, string name) => OptionalOption(args, name) ?? throw new ArgumentException($"Missing {name}.");
    private static string? OptionalOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (Array.LastIndexOf(args, name) != index) throw new ArgumentException($"Duplicate option: {name}.");
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }
    private static long ParseHwnd(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : long.Parse(value, CultureInfo.InvariantCulture);
    private static string SanitizeConsole(string value) => BundleSecurity.SafeDiagnostic(value, 4_096);
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nint CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

internal sealed class RecordingHighlightOverlay : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const int WmNcHitTest = 0x0084;
    private static readonly nint HtTransparent = new(-1);
    private static readonly TimeSpan VisibleLayerRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FocusOutlineDuration = TimeSpan.FromMilliseconds(2400);

    private readonly ManualResetEventSlim _ready = new();
    private readonly WindowTarget _target;
    private readonly Dictionary<string, List<HighlightAnchor>> _relativeHighlightsByLayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HighlightAnchor>> _observedRelativeHighlightsByLayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HighlightAnchor>> _historicalRelativeHighlightsByLayer = new(StringComparer.Ordinal);
    private Thread? _thread;
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private System.Windows.Window? _window;
    private System.Windows.Controls.Canvas? _canvas;
    private System.Windows.Interop.HwndSource? _windowSource;
    private System.Windows.Threading.DispatcherTimer? _positionTimer;
    private RectI _currentRootBounds;
    private string _visibleLayerKey = TabHighlightLayerResolver.GlobalLayerKey;
    private DateTimeOffset _lastVisibleLayerRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _focusOutlineUntilUtc = DateTimeOffset.MinValue;
    private bool _focusOutlinePersistent;
    private RectI? _clickPulseRelativeBounds;
    private DateTimeOffset _clickPulseUntilUtc = DateTimeOffset.MinValue;
    private bool _clickPulseConfirmed;
    private int _visibleLayerRefreshInFlight;
    private int _automationTransparencyHolds;
    private int _screenshotVisibilityHolds;
    private int _captureVisibleRequested = 1;

    public RecordingHighlightOverlay(WindowTarget target)
    {
        _target = target;
        _currentRootBounds = target.Bounds;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "UiAtlas recording highlight overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Recording highlight overlay did not become visible.");
    }

    public void AddHighlights(
        RectI capturedRootBounds,
        string layerKey,
        IReadOnlyList<RectI> absoluteBounds,
        string? visibleLayerKey = null)
        => AddHighlightsCore(
            _relativeHighlightsByLayer,
            capturedRootBounds,
            layerKey,
            absoluteBounds,
            visibleLayerKey,
            selectLayerWhenUnspecified: true,
            updateCurrentRootBounds: true,
            identity: null,
            promoteToConfirmed: false);

    public void AddControlHighlight(
        RectI capturedRootBounds,
        string layerKey,
        AutomationObservation control,
        string? visibleLayerKey = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        AddHighlightsCore(
            _relativeHighlightsByLayer,
            capturedRootBounds,
            layerKey,
            [control.Bounds],
            visibleLayerKey,
            selectLayerWhenUnspecified: true,
            updateCurrentRootBounds: true,
            identity: control,
            promoteToConfirmed: true);
    }

    public void AddObservedHighlights(
        RectI capturedRootBounds,
        string layerKey,
        IReadOnlyList<RectI> absoluteBounds,
        string? visibleLayerKey = null)
        => AddHighlightsCore(
            _observedRelativeHighlightsByLayer,
            capturedRootBounds,
            layerKey,
            absoluteBounds,
            visibleLayerKey,
            selectLayerWhenUnspecified: true,
            updateCurrentRootBounds: true,
            identity: null,
            promoteToConfirmed: false);

    public void ReplaceObservedHighlights(
        RectI capturedRootBounds,
        IReadOnlyDictionary<string, IReadOnlyList<RectI>> absoluteBoundsByLayer,
        string? visibleLayerKey = null)
    {
        ArgumentNullException.ThrowIfNull(absoluteBoundsByLayer);
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;

        var snapshot = absoluteBoundsByLayer
            .SelectMany(entry => entry.Value
                .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
                .Select(bounds => new
                {
                    LayerKey = string.IsNullOrWhiteSpace(entry.Key)
                        ? TabHighlightLayerResolver.GlobalLayerKey
                        : entry.Key,
                    RelativeBounds = new RectI(
                        bounds.X - capturedRootBounds.X,
                        bounds.Y - capturedRootBounds.Y,
                        bounds.Width,
                        bounds.Height)
                }))
            .GroupBy(item => item.LayerKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<HighlightAnchor>)group
                    .Select(item => item.RelativeBounds)
                    .Distinct()
                    .Select(bounds => new HighlightAnchor(bounds, identity: null))
                    .ToArray(),
                StringComparer.Ordinal);
        if (snapshot.Count == 0) return;

        var normalizedVisibleLayerKey = string.IsNullOrWhiteSpace(visibleLayerKey)
            ? null
            : visibleLayerKey;
        _dispatcher.BeginInvoke(() =>
        {
            _currentRootBounds = capturedRootBounds;
            RefreshObservedSurfaceLayers(
                _relativeHighlightsByLayer,
                _observedRelativeHighlightsByLayer,
                _historicalRelativeHighlightsByLayer,
                snapshot);
            if (normalizedVisibleLayerKey is not null)
                _visibleLayerKey = normalizedVisibleLayerKey;
            RenderHighlights();
        });
    }

    internal static void RefreshObservedSurfaceLayers<T>(
        IReadOnlyDictionary<string, List<T>> confirmedLayers,
        IDictionary<string, List<T>> observedLayers,
        IReadOnlyDictionary<string, List<T>> historicalLayers,
        IReadOnlyDictionary<string, IReadOnlyList<T>> replacement)
    {
        ArgumentNullException.ThrowIfNull(confirmedLayers);
        ArgumentNullException.ThrowIfNull(observedLayers);
        ArgumentNullException.ThrowIfNull(historicalLayers);
        ArgumentNullException.ThrowIfNull(replacement);

        observedLayers.Clear();
        foreach (var entry in replacement)
            observedLayers[entry.Key] = [.. entry.Value];
    }

    public void AddHistoricalHighlights(
        RectI capturedRootBounds,
        string layerKey,
        IReadOnlyList<RectI> absoluteBounds,
        string? visibleLayerKey = null)
        => AddHighlightsCore(
            _historicalRelativeHighlightsByLayer,
            capturedRootBounds,
            layerKey,
            absoluteBounds,
            visibleLayerKey,
            selectLayerWhenUnspecified: false,
            updateCurrentRootBounds: false,
            identity: null,
            promoteToConfirmed: false);

    private void AddHighlightsCore(
        Dictionary<string, List<HighlightAnchor>> highlightsByLayer,
        RectI capturedRootBounds,
        string layerKey,
        IReadOnlyList<RectI> absoluteBounds,
        string? visibleLayerKey,
        bool selectLayerWhenUnspecified,
        bool updateCurrentRootBounds,
        AutomationObservation? identity,
        bool promoteToConfirmed)
    {
        ArgumentNullException.ThrowIfNull(layerKey);
        ArgumentNullException.ThrowIfNull(absoluteBounds);
        if (absoluteBounds.Count == 0 || _dispatcher is null || _dispatcher.HasShutdownStarted) return;
        var snapshot = absoluteBounds
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
            .ToArray();
        if (snapshot.Length == 0) return;
        var normalizedLayerKey = string.IsNullOrWhiteSpace(layerKey)
            ? TabHighlightLayerResolver.GlobalLayerKey
            : layerKey;
        var normalizedVisibleLayerKey = string.IsNullOrWhiteSpace(visibleLayerKey)
            ? null
            : visibleLayerKey;

        _dispatcher.BeginInvoke(() =>
        {
            if (updateCurrentRootBounds)
                _currentRootBounds = capturedRootBounds;
            if (!highlightsByLayer.TryGetValue(normalizedLayerKey, out var layerHighlights))
            {
                layerHighlights = [];
                highlightsByLayer[normalizedLayerKey] = layerHighlights;
            }

            foreach (var bounds in snapshot)
            {
                var relative = new RectI(bounds.X - capturedRootBounds.X, bounds.Y - capturedRootBounds.Y, bounds.Width, bounds.Height);
                if (promoteToConfirmed)
                {
                    RemoveLowerPriorityHighlight(_observedRelativeHighlightsByLayer, normalizedLayerKey, relative);
                    RemoveLowerPriorityHighlight(_historicalRelativeHighlightsByLayer, normalizedLayerKey, relative);
                }
                if (identity is not null)
                {
                    var existing = layerHighlights.FirstOrDefault(anchor =>
                        anchor.Identity is not null && SameControlIdentity(anchor.Identity, identity));
                    if (existing is not null)
                    {
                        existing.RelativeBounds = relative;
                        continue;
                    }
                }

                if (!layerHighlights.Any(anchor => anchor.RelativeBounds == relative))
                    layerHighlights.Add(new HighlightAnchor(relative, identity));
            }

            if (normalizedVisibleLayerKey is not null)
                _visibleLayerKey = normalizedVisibleLayerKey;
            else if (selectLayerWhenUnspecified &&
                     !string.Equals(normalizedLayerKey, TabHighlightLayerResolver.GlobalLayerKey, StringComparison.Ordinal))
                _visibleLayerKey = normalizedLayerKey;
            RenderHighlights();
        });
    }

    private static void RemoveLowerPriorityHighlight(
        Dictionary<string, List<HighlightAnchor>> highlightsByLayer,
        string layerKey,
        RectI confirmedBounds)
    {
        if (highlightsByLayer.TryGetValue(layerKey, out var layer))
            layer.RemoveAll(anchor => AreEquivalentHighlightBounds(anchor.RelativeBounds, confirmedBounds));
        if (!string.Equals(layerKey, TabHighlightLayerResolver.GlobalLayerKey, StringComparison.Ordinal) &&
            highlightsByLayer.TryGetValue(TabHighlightLayerResolver.GlobalLayerKey, out var globalLayer))
            globalLayer.RemoveAll(anchor => AreEquivalentHighlightBounds(anchor.RelativeBounds, confirmedBounds));
    }

    internal static bool AreEquivalentHighlightBounds(RectI left, RectI right)
    {
        if (Math.Abs(left.X - right.X) <= 3 && Math.Abs(left.Y - right.Y) <= 3 &&
            Math.Abs(left.Width - right.Width) <= 3 && Math.Abs(left.Height - right.Height) <= 3)
            return true;
        var intersectionWidth = Math.Max(0,
            Math.Min((long)left.X + left.Width, (long)right.X + right.Width) - Math.Max(left.X, right.X));
        var intersectionHeight = Math.Max(0,
            Math.Min((long)left.Y + left.Height, (long)right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var smallerArea = Math.Min((long)left.Width * left.Height, (long)right.Width * right.Height);
        var largerArea = Math.Max((long)left.Width * left.Height, (long)right.Width * right.Height);
        return smallerArea > 0 && intersection >= smallerArea * 0.85 && largerArea <= smallerArea * 1.25;
    }

    public string CurrentVisibleLayerKey => _visibleLayerKey;
    public long TargetHwnd => _target.Hwnd;

    public void ClearSurfaceHighlights()
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            _relativeHighlightsByLayer.Clear();
            _observedRelativeHighlightsByLayer.Clear();
            _historicalRelativeHighlightsByLayer.Clear();
            _visibleLayerKey = TabHighlightLayerResolver.GlobalLayerKey;
            RenderHighlights();
        });
    }

    public void ClearObservedSurfaceHighlights()
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            _observedRelativeHighlightsByLayer.Clear();
            RenderHighlights();
        });
    }

    public void SetCaptureVisible(bool visible)
    {
        Volatile.Write(ref _captureVisibleRequested, visible ? 1 : 0);
        ApplyCaptureVisibility();
    }

    public async Task HideForScreenshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var holds = Interlocked.Increment(ref _screenshotVisibilityHolds);
        try
        {
            if (holds == 1)
                SetOverlayWindowVisible(false);

            // Hide() updates the WPF state immediately, while the desktop
            // compositor can still contain the previous transparent layer for a
            // frame. Flush it before any BitBlt/screen-bounds fallback starts.
            _ = DwmFlush();
            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            RestoreAfterScreenshot();
            throw;
        }
    }

    public void RestoreAfterScreenshot()
    {
        var holds = Interlocked.Decrement(ref _screenshotVisibilityHolds);
        if (holds < 0)
        {
            Interlocked.Exchange(ref _screenshotVisibilityHolds, 0);
            holds = 0;
        }

        if (holds == 0)
            ApplyCaptureVisibility();
    }

    public async Task<T> RunCaptureHiddenAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await HideForScreenshotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            RestoreAfterScreenshot();
        }
    }

    private void ApplyCaptureVisibility()
    {
        var visible = Volatile.Read(ref _captureVisibleRequested) != 0 &&
                      Volatile.Read(ref _screenshotVisibilityHolds) == 0;
        SetOverlayWindowVisible(visible);
    }

    private void SetOverlayWindowVisible(bool visible)
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_window is null) return;
                if (visible)
                {
                    RenderHighlights();
                    _window.Show();
                }
                else
                {
                    _window.Hide();
                }
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    public async Task<T> RunHiddenAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        SetAutomationTransparent(true);
        try
        {
            // A disabled top-level window remains visible, but WindowFromPoint and
            // UI Automation skip it. This keeps the accumulated map on screen while
            // the real application receives the click and is scanned underneath.
            await Task.Delay(35, cancellationToken).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }
        finally
        {
            SetAutomationTransparent(false);
        }
    }

    private void SetAutomationTransparent(bool transparent)
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        var holds = transparent
            ? Interlocked.Increment(ref _automationTransparencyHolds)
            : Math.Max(0, Interlocked.Decrement(ref _automationTransparencyHolds));
        if (transparent && holds != 1 || !transparent && holds != 0) return;
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_window is null) return;
                var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
                _ = EnableWindow(handle, !transparent);
                if (!transparent)
                    RenderHighlights();
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    public void ShowClickPulse(RectI capturedRootBounds, RectI absoluteBounds, TimeSpan? duration = null)
        => ShowClickPulseCore(capturedRootBounds, absoluteBounds, duration, confirmed: false);

    public void ShowConfirmedClickPulse(RectI capturedRootBounds, RectI absoluteBounds, TimeSpan? duration = null)
        => ShowClickPulseCore(capturedRootBounds, absoluteBounds, duration, confirmed: true);

    private void ShowClickPulseCore(
        RectI capturedRootBounds,
        RectI absoluteBounds,
        TimeSpan? duration,
        bool confirmed)
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted || absoluteBounds.Width <= 0 || absoluteBounds.Height <= 0)
            return;

        _dispatcher.BeginInvoke(() =>
        {
            _currentRootBounds = capturedRootBounds;
            _clickPulseRelativeBounds = new RectI(
                absoluteBounds.X - capturedRootBounds.X,
                absoluteBounds.Y - capturedRootBounds.Y,
                absoluteBounds.Width,
                absoluteBounds.Height);
            _clickPulseUntilUtc = DateTimeOffset.UtcNow.Add(duration ?? TimeSpan.FromMilliseconds(850));
            _clickPulseConfirmed = confirmed;
            RenderHighlights();
        });
    }

    public void HideClickPulse()
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted)
            return;

        _dispatcher.BeginInvoke(() =>
        {
            _clickPulseRelativeBounds = null;
            _clickPulseUntilUtc = DateTimeOffset.MinValue;
            _clickPulseConfirmed = false;
            RenderHighlights();
        });
    }

    public void ShowTargetFocusOutline(TimeSpan? duration = null)
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted)
            return;

        var outlineDuration = duration ?? FocusOutlineDuration;
        _dispatcher.BeginInvoke(() =>
        {
            _focusOutlinePersistent = !duration.HasValue;
            _focusOutlineUntilUtc = duration.HasValue
                ? DateTimeOffset.UtcNow.Add(outlineDuration)
                : DateTimeOffset.MinValue;
            RenderHighlights();
        });
    }

    public void HideTargetFocusOutline()
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted)
            return;

        _dispatcher.BeginInvoke(() =>
        {
            _focusOutlinePersistent = false;
            _focusOutlineUntilUtc = DateTimeOffset.MinValue;
            RenderHighlights();
        });
    }

    private void Run()
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _canvas = new System.Windows.Controls.Canvas
        {
            Width = System.Windows.SystemParameters.VirtualScreenWidth,
            Height = System.Windows.SystemParameters.VirtualScreenHeight,
            IsHitTestVisible = false
        };
        _window = new System.Windows.Window
        {
            Title = "UiAtlas mapped controls overlay",
            Left = System.Windows.SystemParameters.VirtualScreenLeft,
            Top = System.Windows.SystemParameters.VirtualScreenTop,
            Width = System.Windows.SystemParameters.VirtualScreenWidth,
            Height = System.Windows.SystemParameters.VirtualScreenHeight,
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Content = _canvas
        };
        _window.SourceInitialized += (_, _) => MakeClickThrough();
        _window.Closed += (_, _) =>
        {
            _positionTimer?.Stop();
            _dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
        };
        _positionTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(150),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RefreshRootBounds(),
            _dispatcher);
        _positionTimer.Start();
        _window.Show();
        _ready.Set();
        System.Windows.Threading.Dispatcher.Run();
    }

    private void RefreshRootBounds()
    {
        var focusOutlineWasActive = HasActiveFocusOutline();
        var clickPulseWasActive = HasActiveClickPulse();
        if (!HasAnyHighlights() && !focusOutlineWasActive && !clickPulseWasActive) return;
        try
        {
            var current = WindowCatalog.Resolve(_target.Hwnd);
            if (current.ProcessId != _target.ProcessId || current.ProcessStartedUtc != _target.ProcessStartedUtc) return;
            if (!Equals(current.Bounds, _currentRootBounds))
            {
                _currentRootBounds = current.Bounds;
                RenderHighlights();
            }

            RefreshVisibleLayer(current);
        }
        catch
        {
            // The overlay is best-effort; if the target is transiently unavailable, keep the last known projection.
        }

        if (focusOutlineWasActive && !HasActiveFocusOutline())
            RenderHighlights();
        if (clickPulseWasActive && !HasActiveClickPulse())
        {
            _clickPulseRelativeBounds = null;
            _clickPulseConfirmed = false;
            RenderHighlights();
        }
    }

    private void RenderHighlights()
    {
        if (_canvas is null || _window is null) return;
        _canvas.Children.Clear();

        if (HasActiveFocusOutline() && TryProjectToOverlayRect(_currentRootBounds, out var projectedRoot))
        {
            var focusFrame = new System.Windows.Shapes.Rectangle
            {
                Width = projectedRoot.Width,
                Height = projectedRoot.Height,
                RadiusX = 12,
                RadiusY = 12,
                Fill = System.Windows.Media.Brushes.Transparent,
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(236, 255, 215, 64)),
                StrokeThickness = 4,
                IsHitTestVisible = false
            };
            focusFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                Opacity = 0.75,
                ShadowDepth = 0,
                Color = System.Windows.Media.Color.FromArgb(255, 255, 215, 64)
            };
            System.Windows.Controls.Canvas.SetLeft(focusFrame, projectedRoot.X);
            System.Windows.Controls.Canvas.SetTop(focusFrame, projectedRoot.Y);
            _canvas.Children.Add(focusFrame);
        }

        foreach (var highlight in VisibleHighlights())
        {
            var relative = highlight.Bounds;
            var absolute = new RectI(
                _currentRootBounds.X + relative.X,
                _currentRootBounds.Y + relative.Y,
                relative.Width,
                relative.Height);
            if (!TryProjectToOverlayRect(absolute, out var projected)) continue;

            var (fill, stroke) = ResolveHighlightColors(highlight.Kind);
            var shape = new System.Windows.Shapes.Rectangle
            {
                Width = projected.Width,
                Height = projected.Height,
                Fill = new System.Windows.Media.SolidColorBrush(fill),
                Stroke = new System.Windows.Media.SolidColorBrush(stroke),
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            System.Windows.Controls.Canvas.SetLeft(shape, projected.X);
            System.Windows.Controls.Canvas.SetTop(shape, projected.Y);
            _canvas.Children.Add(shape);
        }

        if (HasActiveClickPulse() && _clickPulseRelativeBounds is { } pulse)
        {
            var absolute = new RectI(
                _currentRootBounds.X + pulse.X,
                _currentRootBounds.Y + pulse.Y,
                pulse.Width,
                pulse.Height);
            if (TryProjectToOverlayRect(absolute, out var projected))
            {
                var pulseFill = _clickPulseConfirmed
                    ? System.Windows.Media.Color.FromArgb(72, 33, 128, 255)
                    : System.Windows.Media.Color.FromArgb(70, 255, 196, 48);
                var pulseStroke = _clickPulseConfirmed
                    ? System.Windows.Media.Color.FromArgb(255, 33, 128, 255)
                    : System.Windows.Media.Color.FromArgb(255, 255, 94, 64);
                var pulseFrame = new System.Windows.Shapes.Rectangle
                {
                    Width = projected.Width,
                    Height = projected.Height,
                    RadiusX = 5,
                    RadiusY = 5,
                    Fill = new System.Windows.Media.SolidColorBrush(pulseFill),
                    Stroke = new System.Windows.Media.SolidColorBrush(pulseStroke),
                    StrokeThickness = 4,
                    IsHitTestVisible = false
                };
                pulseFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    Opacity = 0.9,
                    ShadowDepth = 0,
                    Color = _clickPulseConfirmed
                        ? System.Windows.Media.Color.FromRgb(33, 128, 255)
                        : System.Windows.Media.Color.FromRgb(255, 94, 64)
                };
                System.Windows.Controls.Canvas.SetLeft(pulseFrame, projected.X);
                System.Windows.Controls.Canvas.SetTop(pulseFrame, projected.Y);
                _canvas.Children.Add(pulseFrame);
            }
        }
    }

    private void RefreshVisibleLayer(WindowTarget current)
    {
        if (!HasAnchoredHighlights() && !HasTabSpecificLayers())
        {
            _visibleLayerKey = TabHighlightLayerResolver.GlobalLayerKey;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastVisibleLayerRefreshUtc < VisibleLayerRefreshInterval)
            return;

        _lastVisibleLayerRefreshUtc = now;
        if (Interlocked.CompareExchange(ref _visibleLayerRefreshInFlight, 1, 0) != 0)
            return;

        var visibleLayerAtStart = _visibleLayerKey;
        _ = Task.Run(() =>
        {
            WindowObservation window;
            IReadOnlyList<AutomationObservation> automation;
            try
            {
                // UI Automation can take many seconds for dense applications such as
                // Revit. Never run it on the overlay dispatcher: doing so freezes the
                // click pulse on the previous control while traversal keeps moving.
                window = WindowSnapshotCapture.Observe(current);
                automation = BoundedAutomationCollector.Collect(current.RootOwnerHwnd, 512, 18);
            }
            catch
            {
                Interlocked.Exchange(ref _visibleLayerRefreshInFlight, 0);
                return;
            }

            var dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                Interlocked.Exchange(ref _visibleLayerRefreshInFlight, 0);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var anchorsChanged = RefreshAnchoredHighlights(current.Bounds, automation);
                        var visibleLayerKey = HasTabSpecificLayers()
                            ? TabHighlightLayerResolver.ResolveVisibleLayerKey(window, automation, _visibleLayerKey)
                            : TabHighlightLayerResolver.GlobalLayerKey;
                        var layerMayBeApplied = string.Equals(
                            _visibleLayerKey,
                            visibleLayerAtStart,
                            StringComparison.Ordinal);
                        var layerChanged = layerMayBeApplied &&
                                           !string.Equals(visibleLayerKey, _visibleLayerKey, StringComparison.Ordinal);

                        if (layerMayBeApplied)
                            _visibleLayerKey = visibleLayerKey;
                        if (anchorsChanged || layerChanged)
                            RenderHighlights();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _visibleLayerRefreshInFlight, 0);
                    }
                });
            }
            catch
            {
                Interlocked.Exchange(ref _visibleLayerRefreshInFlight, 0);
            }
        });
    }

    private bool RefreshAnchoredHighlights(
        RectI currentRootBounds,
        IReadOnlyList<AutomationObservation> automation)
    {
        var changed = false;
        changed |= RefreshAnchoredHighlights(_relativeHighlightsByLayer, currentRootBounds, automation);
        changed |= RefreshAnchoredHighlights(_observedRelativeHighlightsByLayer, currentRootBounds, automation);
        changed |= RefreshAnchoredHighlights(_historicalRelativeHighlightsByLayer, currentRootBounds, automation);
        return changed;
    }

    private static bool RefreshAnchoredHighlights(
        IReadOnlyDictionary<string, List<HighlightAnchor>> highlightsByLayer,
        RectI currentRootBounds,
        IReadOnlyList<AutomationObservation> automation)
    {
        var changed = false;
        foreach (var anchor in highlightsByLayer.Values.SelectMany(layer => layer))
        {
            if (anchor.Identity is null)
                continue;

            var previousAbsolute = new RectI(
                currentRootBounds.X + anchor.RelativeBounds.X,
                currentRootBounds.Y + anchor.RelativeBounds.Y,
                anchor.RelativeBounds.Width,
                anchor.RelativeBounds.Height);
            var current = ResolveCurrentHighlightControl(anchor.Identity, automation, previousAbsolute);
            if (current is null)
                continue;

            var relative = new RectI(
                current.Bounds.X - currentRootBounds.X,
                current.Bounds.Y - currentRootBounds.Y,
                current.Bounds.Width,
                current.Bounds.Height);
            if (relative == anchor.RelativeBounds)
                continue;

            anchor.RelativeBounds = relative;
            changed = true;
        }

        return changed;
    }

    internal static AutomationObservation? ResolveCurrentHighlightControl(
        AutomationObservation identity,
        IReadOnlyList<AutomationObservation> currentAutomation,
        RectI previousAbsoluteBounds)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(currentAutomation);
        ArgumentNullException.ThrowIfNull(previousAbsoluteBounds);

        return currentAutomation
            .Where(candidate =>
                candidate.IsEnabled &&
                !candidate.IsOffscreen &&
                candidate.Bounds.Width > 0 &&
                candidate.Bounds.Height > 0 &&
                SameControlIdentity(identity, candidate))
            .OrderBy(candidate => BoundsDistanceSquared(previousAbsoluteBounds, candidate.Bounds))
            .FirstOrDefault();
    }

    private static bool SameControlIdentity(AutomationObservation left, AutomationObservation right)
    {
        var sameType = string.Equals(left.ControlType, right.ControlType, StringComparison.OrdinalIgnoreCase);
        var sameClass = string.IsNullOrWhiteSpace(left.ClassName) ||
                        string.IsNullOrWhiteSpace(right.ClassName) ||
                        string.Equals(left.ClassName, right.ClassName, StringComparison.OrdinalIgnoreCase);
        if (!sameType || !sameClass)
            return false;

        if (!string.IsNullOrWhiteSpace(left.AutomationId) &&
            !string.IsNullOrWhiteSpace(right.AutomationId))
        {
            return string.Equals(left.AutomationId, right.AutomationId, StringComparison.OrdinalIgnoreCase) &&
                   (string.IsNullOrWhiteSpace(left.Name) ||
                    string.IsNullOrWhiteSpace(right.Name) ||
                    string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        }

        return !string.IsNullOrWhiteSpace(left.Name) &&
               string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static long BoundsDistanceSquared(RectI left, RectI right)
    {
        var dx = (long)left.X + left.Width / 2L - ((long)right.X + right.Width / 2L);
        var dy = (long)left.Y + left.Height / 2L - ((long)right.Y + right.Height / 2L);
        return dx * dx + dy * dy;
    }

    private bool HasAnyHighlights() =>
        _relativeHighlightsByLayer.Values.Any(layer => layer.Count > 0) ||
        _observedRelativeHighlightsByLayer.Values.Any(layer => layer.Count > 0) ||
        _historicalRelativeHighlightsByLayer.Values.Any(layer => layer.Count > 0);

    private bool HasAnchoredHighlights() =>
        _relativeHighlightsByLayer.Values.Any(layer => layer.Any(anchor => anchor.Identity is not null)) ||
        _observedRelativeHighlightsByLayer.Values.Any(layer => layer.Any(anchor => anchor.Identity is not null)) ||
        _historicalRelativeHighlightsByLayer.Values.Any(layer => layer.Any(anchor => anchor.Identity is not null));

    private bool HasActiveFocusOutline() =>
        _focusOutlinePersistent || DateTimeOffset.UtcNow < _focusOutlineUntilUtc;

    private bool HasActiveClickPulse() =>
        _clickPulseRelativeBounds is not null && DateTimeOffset.UtcNow < _clickPulseUntilUtc;

    private bool HasTabSpecificLayers() =>
        HasTabSpecificLayers(_relativeHighlightsByLayer) ||
        HasTabSpecificLayers(_observedRelativeHighlightsByLayer) ||
        HasTabSpecificLayers(_historicalRelativeHighlightsByLayer);

    private static bool HasTabSpecificLayers(IReadOnlyDictionary<string, List<HighlightAnchor>> highlightsByLayer) =>
        highlightsByLayer.Any(entry =>
            !string.Equals(entry.Key, TabHighlightLayerResolver.GlobalLayerKey, StringComparison.Ordinal) &&
            entry.Value.Count > 0);

    private IEnumerable<HighlightVisual> VisibleHighlights()
    {
        var confirmed = VisibleHighlights(_relativeHighlightsByLayer).ToArray();
        var historical = VisibleHighlights(_historicalRelativeHighlightsByLayer)
            .Where(bounds => !confirmed.Any(current => AreEquivalentHighlightBounds(bounds, current)))
            .ToArray();
        var observed = VisibleHighlights(_observedRelativeHighlightsByLayer)
            .Where(bounds => !confirmed.Any(current => AreEquivalentHighlightBounds(bounds, current)) &&
                             !historical.Any(previous => AreEquivalentHighlightBounds(bounds, previous)))
            .ToArray();
        foreach (var highlight in observed)
            yield return new(highlight, HighlightKind.Observed);
        foreach (var highlight in historical)
            yield return new(highlight, HighlightKind.Historical);
        foreach (var highlight in confirmed)
            yield return new(highlight, HighlightKind.Confirmed);
    }

    private IEnumerable<RectI> VisibleHighlights(IReadOnlyDictionary<string, List<HighlightAnchor>> highlightsByLayer)
    {
        var yielded = new HashSet<RectI>();
        if (highlightsByLayer.TryGetValue(TabHighlightLayerResolver.GlobalLayerKey, out var globalHighlights))
        {
            foreach (var highlight in globalHighlights)
            {
                if (yielded.Add(highlight.RelativeBounds))
                    yield return highlight.RelativeBounds;
            }
        }

        foreach (var visibleLayerKey in VisibleLayerKeys())
        {
            if (!highlightsByLayer.TryGetValue(visibleLayerKey, out var layerHighlights))
                continue;
            foreach (var highlight in layerHighlights)
            {
                if (yielded.Add(highlight.RelativeBounds))
                    yield return highlight.RelativeBounds;
            }
        }
    }

    private IEnumerable<string> VisibleLayerKeys()
    {
        if (!string.Equals(_visibleLayerKey, TabHighlightLayerResolver.GlobalLayerKey, StringComparison.Ordinal))
            yield return _visibleLayerKey;
        if (OutlookNavigationDiscovery.TryGetModuleLayerKey(_visibleLayerKey, out var moduleLayerKey) &&
            !string.Equals(moduleLayerKey, _visibleLayerKey, StringComparison.Ordinal))
            yield return moduleLayerKey;
    }

    private sealed class HighlightAnchor(RectI relativeBounds, AutomationObservation? identity)
    {
        public RectI RelativeBounds { get; set; } = relativeBounds;
        public AutomationObservation? Identity { get; } = identity;
    }

    private enum HighlightKind { Observed, Confirmed, Historical }

    private sealed record HighlightVisual(RectI Bounds, HighlightKind Kind);

    private static (System.Windows.Media.Color Fill, System.Windows.Media.Color Stroke) ResolveHighlightColors(
        HighlightKind kind) => kind switch
        {
            HighlightKind.Observed =>
                (System.Windows.Media.Color.FromArgb(45, 45, 190, 145),
                 System.Windows.Media.Color.FromArgb(220, 26, 165, 122)),
            HighlightKind.Historical =>
                (System.Windows.Media.Color.FromArgb(82, 160, 112, 232),
                 System.Windows.Media.Color.FromArgb(232, 145, 92, 220)),
            _ =>
                (System.Windows.Media.Color.FromArgb(77, 93, 140, 255),
                 System.Windows.Media.Color.FromArgb(224, 93, 140, 255))
        };

    internal static (System.Windows.Media.Color Fill, System.Windows.Media.Color Stroke) ResolveHighlightColors(
        bool isHistorical) =>
        ResolveHighlightColors(isHistorical ? HighlightKind.Historical : HighlightKind.Confirmed);

    internal static (System.Windows.Media.Color Fill, System.Windows.Media.Color Stroke) ResolveObservedHighlightColors() =>
        ResolveHighlightColors(HighlightKind.Observed);

    private bool TryProjectToOverlayRect(RectI absolute, out System.Windows.Rect projected)
    {
        if (_window is null || !IsConnectedToPresentationSource(_window))
        {
            projected = default;
            return false;
        }

        System.Windows.Point topLeft;
        System.Windows.Point bottomRight;
        try
        {
            topLeft = _window.PointFromScreen(new System.Windows.Point(absolute.X, absolute.Y));
            bottomRight = _window.PointFromScreen(new System.Windows.Point(
                absolute.X + Math.Max(1, absolute.Width),
                absolute.Y + Math.Max(1, absolute.Height)));
        }
        catch (InvalidOperationException)
        {
            // A queued highlight render can race with overlay shutdown after map build.
            projected = default;
            return false;
        }

        var left = Math.Min(topLeft.X, bottomRight.X);
        var top = Math.Min(topLeft.Y, bottomRight.Y);
        var width = Math.Abs(bottomRight.X - topLeft.X);
        var height = Math.Abs(bottomRight.Y - topLeft.Y);
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height) ||
            width < 1 || height < 1)
        {
            projected = default;
            return false;
        }

        projected = new System.Windows.Rect(left, top, width, height);
        return true;
    }

    internal static bool IsConnectedToPresentationSource(System.Windows.Media.Visual visual) =>
        System.Windows.PresentationSource.FromVisual(visual) is not null;

    private void MakeClickThrough()
    {
        if (_window is null) return;
        MakeClickThrough(_window);
        _windowSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(_window).Handle);
        _windowSource?.AddHook(HitTestTransparentWindow);
    }

    private static nint HitTestTransparentWindow(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest) return 0;
        handled = true;
        return HtTransparent;
    }

    private static void MakeClickThrough(System.Windows.Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    public void Dispose()
    {
        if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
            _dispatcher.Invoke(() =>
            {
                _windowSource?.RemoveHook(HitTestTransparentWindow);
                _windowSource = null;
                _window?.Close();
            });
        _thread?.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

}

[SupportedOSPlatform("windows")]
internal static class WindowActivation
{
    private const uint GaRootOwner = 3;
    private const uint GwOwner = 4;
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint PmNoRemove = 0x0000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopMost = new(-1);
    private static readonly nint HwndNoTopMost = new(-2);

    public static async Task<bool> ActivateAsync(nint target, CancellationToken cancellationToken)
    {
        if (target == 0 || !IsWindow(target)) return false;
        var targetRoot = GetRootOwnerHandle(target);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryActivate(target);
            var foreground = GetForegroundWindow();
            if (foreground == target || targetRoot != 0 && GetRootOwnerHandle(foreground) == targetRoot)
                return true;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryActivate(nint target)
    {
        _ = PeekMessageW(out _, 0, 0, 0, PmNoRemove);
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try
        {
            RestoreWindowIfMinimized(target);
            SetWindowPos(target, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoOwnerZOrder);
            SetWindowPos(target, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoOwnerZOrder);
            BringWindowToTop(target);
            SetForegroundWindow(target);
            SetActiveWindow(target);
            SetFocus(target);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static void RestoreWindowIfMinimized(nint target)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(target, ref placement)) return;
        if (placement.showCmd == 2)
            ShowWindow(target, SwRestore);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    private static extern nint BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out MSG message, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private static nint GetRootOwnerHandle(nint hwnd)
    {
        if (hwnd == 0) return 0;
        var current = GetAncestor(hwnd, GaRootOwner);
        if (current == 0) current = hwnd;
        for (var depth = 0; depth < 32; depth++)
        {
            var owner = GetWindow(current, GwOwner);
            if (owner == 0 || owner == current) return current;
            current = GetAncestor(owner, GaRootOwner);
            if (current == 0) current = owner;
        }

        return current;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public POINT Pt;
    }
}

internal sealed class RecordingControlPanel : IDisposable
{
    private const double MapContextMenuItemWidth = 204;
    private const double MapContextMenuItemHeight = 40;
    internal event Action? PauseRequested;
    internal event Action? CancelRequested;
    internal event Action? AutoPassStopRequested;

    private sealed record CatalogImportResult(string MapId, string MapPath, int ImportedRecordingCount, int SkippedRecordingCount);
    private const string CancelRecordingCommand = "CANCEL_RECORDING";

    private const double ToolbarTargetWidth = 660;
    private const double RecorderFrameWidth = 350;
    private const double RecorderFrameHeight = 430;
    private const double RecorderCardWidth = 780;
    private const double RecorderCardHeight = 1080;
    private const double RecorderCardDisplayScale = 1.0;
    private const double RecorderCardCornerRadius = 68;
    private const double RecorderCardPadding = 36;
    private const double RecorderViewportInset = 16;
    private const double IdlePrimaryCircleSize = 188;
    private const double ActivePrimaryCircleSize = 188;
    private const double SecondaryCircleSize = 138;
    private const double CompactPrimaryCircleSize = 118;
    private const double CompactSecondaryCircleSize = 86;
    private const double BottomNavHeight = 139;
    private const double BottomNavButtonWidth = 218;
    private const double BottomNavButtonHeight = 139;
    private const double BottomNavButtonCornerRadius = 34;
    private const double BottomNavCornerRadius = 36;
    private const double BottomNavIconHeight = 50;
    private const double BottomNavSeparatorWidth = 3;
    private const double BottomNavSeparatorHeight = 96;
    private const double DecorativeArtHeight = 440;
    private const double TopActionSectionHeight = 238;
    private const double IdleStatusSectionHeight = 124;
    private const double ActiveStatusSectionHeight = 166;
    private const double TopActionGroupWidth = 684;
    private const double TopActionTopMargin = 0;
    private const double BottomNavLabelSize = 28;
    private const double SessionMenuWidth = 392;
    private const double SessionMenuCornerRadius = 24;
    private const double ToolbarViewportInset = 48;
    private const double ToolbarHeight = 80;
    private const double ToolbarPadding = 6;
    private const double ToolbarCornerRadius = 40;
    private const double ToolSlotHeight = 68;
    private const double ToolButtonWidth = 120;
    private const double ToolCornerRadius = 22;
    private const double DividerHeight = 32;
    private const double IconSize = 22;
    private const double LabelFontSize = 11;
    private const double LabelLineHeight = 12;
    private const double LabelGap = 3;
    private const double CenterBubbleSize = 38;
    private const double StatusTimerFontSize = 24;
    private const double StatusDetailFontSize = 28;
    private const double TimerWidth = 74;
    private const double InlineButtonHeight = 40;
    private const double StopOuterSize = 46;
    private const double StopInnerSize = 40;
    private const double ContentPanelCornerRadius = 34;
    private const double CompactFrameWidth = 268;
    private const double CompactFrameHeight = 96;
    private const double CompactShellHeight = 240;
    private const double SearchBoxHeight = 76;
    private const double CompactRowHeight = 72;
    private const double CompactMapRowHeight = CompactRowHeight;
    private const double CompactButtonMinWidth = 240;
    private const double CompactButtonHeight = CompactRowHeight;
    private static double CompactButtonFontSize => 13d / RecorderContentScale;
    private const double CompactButtonGroupGap = 10;
    private const double CompactButtonOuterGap = 18;
    private const double DoubleClickCircleIconSize = 44;
    private const double PauseCircleIconSize = 44;
    private const double ListItemTitleFontSize = 30;
    private const double ListItemSubtitleFontSize = 25;
    private const double PanelHeaderTitleFontSize = 35;
    private const double PanelHeaderSubtitleFontSize = 25;
    private const double PanelSurfacePadding = 25;
    private const double PanelSectionGap = 15;
    private const double PanelListIconGap = 20;
    private const double PanelListRowSpacing = 12;
    private const double PanelScrollRightInset = 24;
    private const double WindowListBadgeSize = 71;
    private const double WindowListBadgeColumnWidth = WindowListBadgeSize + PanelListIconGap;
    private const double MapListBadgeSize = 52;
    private const double MapListBadgeColumnWidth = MapListBadgeSize + PanelListIconGap;

    private static double RecorderFrameScale =>
        Math.Min(RecorderFrameWidth / RecorderCardWidth, RecorderFrameHeight / RecorderCardHeight);

    private static double RecorderFrameCornerRadius => RecorderCardCornerRadius * RecorderFrameScale;
    private static double CompactShellFrameHeight => CompactFrameHeight;
    private static double RecorderViewportWidth => RecorderFrameWidth - (RecorderViewportInset * 2);
    private static double ShellContentHeight(double designHeight) => designHeight - (RecorderCardPadding * 2);
    private static double ShellViewportHeight(double frameHeight) => frameHeight - (RecorderViewportInset * 2);
    private static double RecorderContentScale => ShellViewportHeight(RecorderFrameHeight) / ShellContentHeight(RecorderCardHeight);
    private static double RecorderSurfaceShadowBlur => 5d / RecorderContentScale;
    private static double RecorderSurfaceShadowDepth => 3d / RecorderContentScale;
    private static double ShellContentWidth(double frameWidth, double frameHeight, double designHeight) =>
        ShellContentHeight(designHeight) * ((frameWidth - (RecorderViewportInset * 2)) / ShellViewportHeight(frameHeight));
    private static double RecorderShellContentWidth => ShellContentWidth(RecorderFrameWidth, RecorderFrameHeight, RecorderCardHeight);
    private static double CompactShellContentWidth => ShellContentWidth(CompactFrameWidth, CompactShellFrameHeight, CompactShellHeight);
    private static double BottomNavWidth => RecorderShellContentWidth;
    private static double BottomNavGap => (BottomNavWidth - (BottomNavButtonWidth * 3) - (BottomNavSeparatorWidth * 2)) / 4d;
    private static double WindowUtilityButtonSize => 18d / RecorderContentScale;
    private static double WindowUtilityIconSize => 10d / RecorderContentScale;
    private static double WindowUtilityButtonGap => 8d / RecorderContentScale;
    private static double WindowUtilityEdgeInset => 8d / RecorderContentScale;
    private static double WindowUtilityShellInset => RecorderViewportInset / RecorderContentScale;
    private static double ContentPanelHeight => ShellContentHeight(RecorderCardHeight) - TopActionSectionHeight - IdleStatusSectionHeight - BottomNavHeight - (10d / RecorderContentScale);
    private static double IdlePopupUnderlaySideBleed => (RecorderViewportInset - 1d) / RecorderContentScale;
    private static double ListSelectionBorderThickness => 1d / RecorderContentScale;
    private static double ListSelectionCornerRadius => 8d / RecorderContentScale;
    private static double TopActionToStatusGap => 15d / RecorderContentScale;
    private static double TopActionGap => 28d / RecorderContentScale;
    private static double TopActionLabelFontSize => 10d / RecorderContentScale;
    private static double TopActionLabelGap => 10d / RecorderContentScale;
    private static double IdleStatusHeadlineLineHeight => 14d / RecorderContentScale;
    private static double SessionModeTopGap => 10d / RecorderContentScale;
    private static double SessionModeHintEdgeInset => 10d / RecorderContentScale;
    private static double SessionModePanelInset => 16d / RecorderContentScale;
    private static double SessionModePanelBottomInset => 5d / RecorderContentScale;
    private static double SessionModePanelGap => 10d / RecorderContentScale;
    private static double SessionModeDismissEdgeInset => 8d / RecorderContentScale;
    private static double SessionModeDismissReserve => 14d / RecorderContentScale;
    private static double SessionModeCardGap => 6d / RecorderContentScale;
    private static double SessionModeCardMinHeight => 117d / RecorderContentScale;
    private static double SessionModeCardPadding => 10d / RecorderContentScale;
    private static double SessionModeCardCornerRadius => 8d / RecorderContentScale;
    private static double SessionModeCardBorderThickness => 1d / RecorderContentScale;
    private static double SessionModeCardHighlightBorderThickness => 2d / RecorderContentScale;
    private static double SessionModeCardTitleFontSize => 12d / RecorderContentScale;
    private static double SessionModeCardSubtitleFontSize => 10d / RecorderContentScale;
    private static double SessionModeCardSubtitleLineHeight => 12d / RecorderContentScale;
    private static double SessionModeCardIconSize => 40d / RecorderContentScale;
    private static double SessionModeCardTitleTopMargin => 8d / RecorderContentScale;
    private static double SessionModeCardSubtitleTopMargin => 8d / RecorderContentScale;
    private static double SessionModeCardSubtitleWidth => 123d / RecorderContentScale;
    private static double ActiveProgressPanelInset => 16d / RecorderContentScale;
    private static double ActiveProgressPanelTopInset => 10d / RecorderContentScale;
    private static double ActiveProgressPanelBottomInset => 30d / RecorderContentScale;
    private static double ActiveProgressTextFontSize => 14d / RecorderContentScale;
    private static double ActiveProgressTextLineHeight => 18d / RecorderContentScale;
    private static double ActiveProgressTextMaxWidth => 250d / RecorderContentScale;
    private static double ActiveProgressTextBottomMargin => 6d / RecorderContentScale;
    private static double ActiveStatusTimerWidth => TimerWidth / RecorderContentScale;
    private static double ActiveStatusDotOuterSize => 14d / RecorderContentScale;
    private static double ActiveStatusDotInnerSize => 7d / RecorderContentScale;
    private static double ActiveStatusDotGap => 10d / RecorderContentScale;
    private static double ActiveProgressArtSideBleed => IdlePopupUnderlaySideBleed + ActiveProgressPanelInset;
    private static double ActiveProgressArtTopMargin => -6d / RecorderContentScale;
    private static double ActiveProgressArtBottomMargin => 8d / RecorderContentScale;
    private static double ActiveProgressActionButtonMinWidth => 123d / RecorderContentScale;
    private static double ActiveProgressActionButtonHeight => 28d / RecorderContentScale;
    private static double ActiveProgressActionButtonCornerRadius => 8d / RecorderContentScale;
    private static double ActiveProgressActionButtonFontSize => 16d / RecorderContentScale;
    private static double ActiveProgressActionButtonHorizontalPadding => 18d / RecorderContentScale;
    private static double PrimaryCircleSelectedShadowBlur => 20d / RecorderContentScale;
    private static double PrimaryCircleHoverShadowBlur => 16d / RecorderContentScale;

    private static double ToolbarOuterWidth =>
        Math.Min(ToolbarTargetWidth, Math.Max(360, System.Windows.SystemParameters.WorkArea.Width - ToolbarViewportInset));

    private static double IdleToolbarOuterWidth =>
        Math.Min(RecorderFrameWidth, Math.Max(280, System.Windows.SystemParameters.WorkArea.Width - 48));

    private static double ToolbarInnerWidth => ToolbarOuterWidth - (ToolbarPadding * 2);
    private static double ToolColumnWidth => ToolbarInnerWidth / 5d;
    private static double ActiveSideWidth => (ToolbarInnerWidth - ToolColumnWidth) / 2d;

    internal enum RecordingPanelMode
    {
        PreStart,
        Active,
        Paused,
        MapReady
    }

    private enum RecorderView
    {
        Windows,
        Maps,
        Library
    }

    private enum StatusTone
    {
        Neutral,
        Accent,
        Success,
        Danger
    }

    private static readonly System.Windows.Media.Brush ShellBackground = Brush("#F9F9FA");
    private static readonly System.Windows.Media.Brush ShellBorder =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 220, 225, 233));
    private static readonly System.Windows.Media.Brush CaptionBackground = System.Windows.Media.Brushes.Transparent;
    private static readonly System.Windows.Media.Brush CaptionAccentBackground = System.Windows.Media.Brushes.Transparent;
    private static readonly System.Windows.Media.Brush CaptionDangerBackground = System.Windows.Media.Brushes.Transparent;
    private static readonly System.Windows.Media.Brush CaptionForeground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 127, 136, 150));
    private static readonly System.Windows.Media.Brush Divider =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(184, 231, 234, 240));
    private static readonly System.Windows.Media.Brush SelectedTileBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(16, 10, 132, 255));
    private static readonly System.Windows.Media.Brush SelectedTileBorder =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 10, 132, 255));
    private static readonly System.Windows.Media.Brush SourceSwitchBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 245, 248, 252));
    private static readonly System.Windows.Media.Brush SourceSelectedBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(248, 255, 255, 255));
    private static readonly System.Windows.Media.Brush SourceHoverBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(138, 248, 250, 253));
    private static readonly System.Windows.Media.Brush SourceIdleForeground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(127, 136, 150));
    private static readonly System.Windows.Media.Brush BubbleBackground =
        new System.Windows.Media.LinearGradientBrush(
            new System.Windows.Media.GradientStopCollection
            {
                new(System.Windows.Media.Color.FromArgb(248, 255, 255, 255), 0),
                new(System.Windows.Media.Color.FromArgb(244, 251, 252, 255), 1)
            },
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
    private static readonly System.Windows.Media.Brush BubbleBorder =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(224, 228, 232, 239));
    private static readonly System.Windows.Media.Brush BottomNavBackground = Brush("#FFFFFF");
    private static readonly System.Windows.Media.Brush BottomNavBorder =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 226, 230, 237));
    private static readonly System.Windows.Media.Brush BottomNavHoverBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 255, 255));
    private static readonly System.Windows.Media.Brush BottomNavMuted =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(127, 136, 150));
    private static readonly System.Windows.Media.Brush BottomNavSelectedForeground = Brush("#107EFB");
    private static readonly System.Windows.Media.Brush BottomNavSelectedBorder =
        Brush("#107EFB");
    private static readonly System.Windows.Media.Brush BottomNavSelectedGlow =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 10, 132, 255));
    private static readonly System.Windows.Media.Brush MoreMenuBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(245, 255, 255, 255));
    private static readonly System.Windows.Media.Brush Ink = Brush("#17191D");
    private static readonly System.Windows.Media.Brush Muted = Brush("#7F8896");
    private static readonly System.Windows.Media.Brush PanelInk = Brush("#000000");
    private static readonly System.Windows.Media.Brush PanelMuted = Brush("#9199A5");
    private static readonly System.Windows.Media.Brush TopActionMuted = Brush("#9199A5");
    private static readonly System.Windows.Media.Brush Disabled = Brush("#B9C0CA");
    private static readonly System.Windows.Media.Brush Accent = Brush("#0A84FF");
    private static readonly System.Windows.Media.Brush AutoStage = Brush("#2DBE91");
    private static readonly System.Windows.Media.Brush AutoStageMuted = Brush("#347961");
    private static readonly System.Windows.Media.Brush BuildStage = Brush("#7B61FF");
    private static readonly System.Windows.Media.Brush BuildStageMuted = Brush("#6453C7");
    private static readonly System.Windows.Media.Brush SaveStage = Brush("#2DBE91");
    private static readonly System.Windows.Media.Brush SaveStageMuted = Brush("#347961");
    private static readonly System.Windows.Media.Brush Danger = Brush("#FF3B30");
    private static readonly System.Windows.Media.Brush CancelInk = Brush("#505966");
    private static readonly System.Windows.Media.Brush ActiveStagePanelBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(228, 255, 255, 255));
    private static readonly System.Windows.Media.Brush ActiveStagePanelBorder =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(224, 223, 228, 236));
    private static readonly System.Windows.Media.Brush ActiveStageMutedForeground = Brush("#505966");
    private static readonly System.Windows.Media.Brush ActiveStageMutedBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 255, 255, 255));
    private static readonly System.Windows.Media.Brush ActiveStageMutedBorder =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(224, 211, 217, 226));
    private static readonly Lazy<System.Windows.Media.ImageSource?> DecorativeWaveArt = new(LoadDecorativeWaveArt);
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderPopupUnderlayArt = new(() => LoadAssetImage("recorder-popup-underlay.png"));
    private static readonly Lazy<SvgAsset?> RecorderDoubleClickSvgArt = new(() => LoadSvgAsset("recorder-icon-double-click.svg"));
    private static readonly Lazy<SvgAsset?> RecorderExportSvgArt = new(() => LoadSvgAsset("recorder-icon-export.svg"));
    private static readonly Lazy<SvgAsset?> RecorderMapsSvgArt = new(() => LoadSvgAsset("recorder-icon-maps.svg"));
    private static readonly Lazy<SvgAsset?> RecorderPauseSvgArt = new(() => LoadSvgAsset("recorder-icon-pause.svg"));
    private static readonly Lazy<SvgAsset?> RecorderWindowsSvgArt = new(() => LoadSvgAsset("recorder-icon-windows.svg"));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderDoubleClickArt = new(() => LoadAssetImage("recorder-icon-double-click.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderExportArt = new(() => LoadAssetImage("recorder-icon-export.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderFolderArt = new(() => LoadAssetImage("recorder-icon-folder.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderMapsArt = new(() => LoadAssetImage("recorder-icon-maps.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderManualArt = new(() => LoadAssetImage("recorder-icon-manual.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderMoreArt = new(() => LoadAssetImage("recorder-icon-more.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderAutoLabelsArt = new(() => LoadAssetImage("recorder-icon-auto-labels.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderCloseArt = new(() => LoadAssetImage("recorder-icon-close.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderPauseArt = new(() => LoadAssetImage("recorder-icon-pause.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderPlayArt = new(() => LoadAssetImage("recorder-icon-play.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderSearchArt = new(() => LoadAssetImage("recorder-icon-search.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderStopArt = new(() => LoadAssetImage("recorder-icon-stop.png", trimTransparentPadding: true));
    private static readonly Lazy<System.Windows.Media.ImageSource?> RecorderWindowsArt = new(() => LoadAssetImage("recorder-icon-windows.png", trimTransparentPadding: true));
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> LiveAppIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> InstalledExecutablePathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TargetScope?> RecordedTargetCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LiveAppIconCacheGate = new();
    private static readonly SemaphoreSlim MapBadgeLoadLimiter = new(2, 2);

    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _closed = new();
    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
    private Exception? _startupException;
    private readonly bool _supportsTargetSelection;
    private bool _allowTargetSelection;
    private readonly object _targetGate = new();
    private string _recordingId;
    private string _processName;
    private RecorderView _currentView = RecorderView.Windows;
    private string _windowQuery = string.Empty;
    private string _mapQuery = string.Empty;
    private bool _recordingControlsLocked;
    private System.Windows.Media.ScaleTransform? _rootScaleTransform;
    private Thread? _thread;
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private System.Windows.Threading.DispatcherTimer? _elapsedTimer;
    private System.Windows.Window? _window;
    private System.Windows.Controls.Border? _captionShell;
    private System.Windows.Controls.TextBlock? _captionBlock;
    private System.Windows.Controls.Border? _sessionModeShell;
    private System.Windows.Controls.Button? _quickMapModeButton;
    private System.Windows.Controls.Primitives.ToggleButton? _hoverDiscoveryToggle;
    private System.Windows.Controls.Primitives.ToggleButton? _customerDataToggle;
    private System.Windows.Controls.Border? _idleShell;
    private System.Windows.Controls.Border? _activeShell;
    private System.Windows.Controls.Border? _compactShell;
    private System.Windows.Controls.RowDefinition? _idleStatusRowDefinition;
    private System.Windows.Controls.StackPanel? _idleStatusHost;
    private System.Windows.Controls.TextBlock? _idleStatusBlock;
    private System.Windows.Controls.TextBlock? _idleDetailBlock;
    private System.Windows.Shapes.Ellipse? _idleStatusDot;
    private System.Windows.Shapes.Ellipse? _activeStatusDotOuter;
    private System.Windows.Shapes.Ellipse? _activeStatusDotInner;
    private System.Windows.Controls.TextBlock? _activeStatusBlock;
    private System.Windows.Controls.TextBlock? _activeDetailBlock;
    private System.Windows.Controls.TextBlock? _elapsedBlock;
    private System.Windows.Controls.Border? _activeStageBadge;
    private System.Windows.Controls.TextBlock? _activeStageBadgeLabel;
    private System.Windows.Controls.Border? _windowsPanel;
    private System.Windows.Controls.Border? _mapsPanel;
    private System.Windows.Controls.Border? _libraryPanel;
    private System.Windows.Controls.TextBox? _mapSearchBox;
    private System.Windows.Controls.StackPanel? _windowRowsHost;
    private System.Windows.Controls.StackPanel? _mapRowsHost;
    private System.Windows.Controls.Border? _mapLoadingOverlay;
    private System.Windows.Controls.TextBlock? _mapLoadingTitleBlock;
    private System.Windows.Controls.TextBlock? _mapLoadingDetailBlock;
    private System.Windows.Controls.TextBlock? _libraryTitleBlock;
    private System.Windows.Controls.TextBlock? _libraryDetailBlock;
    private System.Windows.Controls.TextBlock? _libraryHintBlock;
    private System.Windows.Controls.Button? _libraryPrimaryButton;
    private System.Windows.Controls.Button? _librarySecondaryLinkButton;
    private System.Windows.Controls.Button? _libraryTertiaryLinkButton;
    private System.Windows.Controls.Button? _libraryCleanupRecordingsButton;
    private System.Windows.Controls.Button? _mapsButton;
    private System.Windows.Controls.Button? _startButton;
    private System.Windows.Controls.Button? _libraryButton;
    private System.Windows.Controls.Button? _moreButton;
    private System.Windows.Controls.Button? _stopButton;
    private System.Windows.Controls.Button? _resumePrimaryButton;
    private System.Windows.Controls.Button? _pauseButton;
    private System.Windows.Controls.Button? _resumeRecordingButton;
    private System.Windows.Controls.Button? _doubleButton;
    private System.Windows.Controls.Button? _compactStartButton;
    private System.Windows.Controls.Button? _compactStopButton;
    private System.Windows.Controls.Button? _compactResumePrimaryButton;
    private System.Windows.Controls.Button? _compactPauseButton;
    private System.Windows.Controls.Button? _compactResumeButton;
    private System.Windows.Controls.Button? _compactDoubleButton;
    private System.Windows.Controls.Button? _compactSkipAutoButton;
    private System.Windows.Controls.Button? _skipAutoButton;
    private System.Windows.Controls.Button? _cancelRecordingButton;
    private System.Windows.Controls.Button? _targetButton;
    private System.Windows.Controls.Primitives.Popup? _targetMenu;
    private System.Windows.Controls.StackPanel? _targetMenuStack;
    private System.Windows.Controls.Primitives.Popup? _mapsMenu;
    private System.Windows.Controls.StackPanel? _mapsMenuStack;
    private System.Windows.Controls.Primitives.Popup? _moreMenu;
    private IdlePopupRequest _pendingIdlePopupRequest;
    private RecordingPanelMode _currentMode;
    private DateTimeOffset _elapsedSegmentStartedUtc;
    private TimeSpan _elapsedAccumulated;
    private string? _mapPath;
    private string? _recordingPath;
    private string? _defaultExportPath;
    private WindowTarget? _selectedTarget;
    private long? _selectedTargetHwnd;
    private bool _autoPassActive;
    private bool _isCompactCollapsed;
    private volatile bool _sessionModeChooserOpen;
    private volatile bool _sessionModeChooserResumeMode;
    private volatile bool _enableHoverAndFocusDiscovery = true;
    private volatile bool _captureCustomerData;
    private int _mapRefreshGeneration;

    private enum IdlePopupRequest
    {
        None,
        Target,
        Maps
    }

    public RecordingControlPanel(string recordingId, string processName, WindowTarget? initialTarget = null, bool allowTargetSelection = false)
    {
        _recordingId = string.IsNullOrWhiteSpace(recordingId) ? "manual-recording" : recordingId;
        _processName = string.IsNullOrWhiteSpace(processName) ? "Choose a window" : processName;
        _supportsTargetSelection = allowTargetSelection;
        _allowTargetSelection = allowTargetSelection;
        _selectedTarget = initialTarget;
        _selectedTargetHwnd = initialTarget?.Hwnd;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "UiAtlas recording control panel" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Recording panel did not become visible.");
        if (_startupException is not null)
            throw new InvalidOperationException("Recording panel failed to start.", _startupException);
    }

    private void Run()
    {
        try
        {
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _elapsedTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _elapsedTimer.Tick += (_, _) => UpdateElapsed();

            _captionShell = BuildCaptionShell();
            _sessionModeShell = BuildSessionModeChooser();
            _idleShell = BuildIdleShell();
            _activeShell = BuildActiveShell();
            _compactShell = BuildCompactShell();
            _activeShell.Visibility = System.Windows.Visibility.Collapsed;
            _compactShell.Visibility = System.Windows.Visibility.Collapsed;
            _rootScaleTransform = new System.Windows.Media.ScaleTransform(1, 1);

            var shellHost = new System.Windows.Controls.Grid
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Top
            };
            shellHost.Children.Add(_idleShell);
            shellHost.Children.Add(_activeShell);
            shellHost.Children.Add(_compactShell);

            var contentHost = new System.Windows.Controls.Grid
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                LayoutTransform = _rootScaleTransform
            };
            contentHost.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            contentHost.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(_captionShell, 0);
            System.Windows.Controls.Grid.SetRow(shellHost, 1);
            contentHost.Children.Add(_captionShell);
            contentHost.Children.Add(shellHost);

            var root = new System.Windows.Controls.Grid
            {
                Background = System.Windows.Media.Brushes.Transparent,
                Children = { contentHost }
            };
            root.PreviewMouseLeftButtonDown += HandlePanelDragMove;
            _window = new System.Windows.Window
            {
                Title = $"UiAtlas recording - {_recordingId}",
                Topmost = true,
                ShowInTaskbar = true,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStyle = System.Windows.WindowStyle.None,
                SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Inter, Segoe UI"),
                Content = root
            };
            _window.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml")
            });
            _window.SourceInitialized += (_, _) =>
            {
                UpdateWindowScale();
                ExcludeWindowFromCapture();
            };
            _window.Loaded += (_, _) =>
            {
                UpdateWindowScale();
                PositionWindow();
            };
            _window.Deactivated += (_, _) => CloseIdleMenus();
            _window.Closed += (_, _) =>
            {
                _closed.Set();
                _commands.Writer.TryWrite("CLOSE");
                _dispatcher?.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
            };

            _window.Show();
            _ready.Set();
            _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ApplyMode(RecordingPanelMode.PreStart);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: recorder pre-start failed: {ex}");
                    UpdateIdleStatus("Recorder Error", BundleSecurity.SafeDiagnostic(ex.Message, 220), StatusTone.Danger);
                }
            }));
            System.Windows.Threading.Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _startupException = ex;
            _ready.Set();
            _closed.Set();
        }
    }

    public bool TryDequeueCommand(out string command) => _commands.Reader.TryRead(out command!);
    public bool SessionModeChooserOpen => _sessionModeChooserOpen;
    public bool SessionModeChooserResumesMap => _sessionModeChooserResumeMode;
    public bool CanStartRecording => GetSelectedTarget() is not null;
    public bool IsClosed => _closed.IsSet;
    public RecordingLaunchOptions SelectedLaunchOptions => new(
        _enableHoverAndFocusDiscovery,
        _captureCustomerData);

    public WindowTarget? GetSelectedTarget()
    {
        WindowTarget? selectedTarget;
        long? selectedTargetHwnd;
        lock (_targetGate)
        {
            selectedTarget = _selectedTarget;
            selectedTargetHwnd = _selectedTargetHwnd;
        }

        var hwnd = selectedTarget?.Hwnd ?? selectedTargetHwnd;
        if (hwnd is not long resolvedHwnd)
            return null;

        try
        {
            var resolved = WindowCatalog.Resolve(resolvedHwnd);
            lock (_targetGate)
            {
                _selectedTarget = resolved;
                _selectedTargetHwnd = resolved.Hwnd;
            }
            return resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_targetGate)
            {
                _selectedTarget = null;
                _selectedTargetHwnd = null;
            }
            return null;
        }
    }

    public void PromptTargetSelection()
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_currentMode != RecordingPanelMode.PreStart) return;
            if (_isCompactCollapsed)
                SetCompactMode(false);
            SetCurrentView(RecorderView.Windows);
            ApplyVisualStatus("Choose a window before you start recording.", StatusTone.Neutral);
        });
    }

    public void UpdateRecordingContext(string recordingId, string processName, WindowTarget? selectedTarget)
    {
        if (!string.IsNullOrWhiteSpace(recordingId))
            _recordingId = recordingId;
        if (!string.IsNullOrWhiteSpace(processName))
            _processName = processName;
        if (selectedTarget is not null)
            _allowTargetSelection = false;
        lock (_targetGate)
        {
            _selectedTarget = selectedTarget;
            _selectedTargetHwnd = selectedTarget?.Hwnd;
        }
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_window is not null)
                _window.Title = $"UiAtlas recording - {_recordingId}";
            RefreshWindowList();
            RefreshMapList();
            RefreshPreStartTargetState();
        });
    }

    public void ShowPreStartState()
    {
        _sessionModeChooserOpen = false;
        _sessionModeChooserResumeMode = false;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => ApplyMode(RecordingPanelMode.PreStart));
    }

    public void ShowActiveRecordingState()
    {
        _sessionModeChooserOpen = false;
        _sessionModeChooserResumeMode = false;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => ApplyMode(RecordingPanelMode.Active));
    }

    public void ShowPausedRecordingState()
    {
        _sessionModeChooserOpen = false;
        _sessionModeChooserResumeMode = false;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => ApplyMode(RecordingPanelMode.Paused));
    }

    public void ShowSessionModeChooser(bool resumeMode)
    {
        _sessionModeChooserResumeMode = resumeMode;
        _sessionModeChooserOpen = true;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => SetSessionModeChooserVisibility(true, resumeMode));
    }

    public void HideSessionModeChooser()
    {
        _sessionModeChooserOpen = false;
        _sessionModeChooserResumeMode = false;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => SetSessionModeChooserVisibility(false, resumeMode: false));
    }

    public void SetAutoPassActive(bool active)
    {
        _autoPassActive = active;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(ApplyAutoPassState);
    }

    public void SetStatus(string message)
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => ApplyVisualStatus(message, ResolveTone(message)));
    }

    public void MarkMapReady(string mapPath, string? recordingPath, string defaultExportPath)
    {
        _mapPath = mapPath;
        _recordingPath = recordingPath;
        _defaultExportPath = defaultExportPath;
        _sessionModeChooserOpen = false;
        _sessionModeChooserResumeMode = false;
        // Commands generated before Map Ready belong to the completed session.
        // Never let a buffered click reopen recording after a long graph build.
        while (_commands.Reader.TryRead(out _)) { }
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => ApplyMode(RecordingPanelMode.MapReady));
    }

    public void BeginMapBuild()
    {
        _recordingControlsLocked = true;
        _sessionModeChooserOpen = false;
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            SetSessionModeChooserVisibility(false, resumeMode: false);
            ApplyRecordingControlsLock(locked: true);
        });
    }

    public bool WaitForCloseOrTimeout(TimeSpan timeout) => _closed.Wait(timeout);

    private System.Windows.Controls.Border BuildCaptionShell()
    {
        _captionBlock = new System.Windows.Controls.TextBlock
        {
            Text = "READY TO RECORD",
            FontSize = 12,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = CaptionForeground,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
            Opacity = 0.92,
            Effect = Shadow(0.14, 2, 0)
        };

        return new System.Windows.Controls.Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            Background = CaptionBackground,
            Visibility = System.Windows.Visibility.Collapsed,
            Child = _captionBlock
        };
    }

    private System.Windows.Controls.Border BuildSessionModeChooser()
    {
        _hoverDiscoveryToggle = BuildRecordingOptionToggle(
            "Hover",
            "Actively reveal hover/focus states without clicking.",
            isChecked: true,
            value => _enableHoverAndFocusDiscovery = value);
        _customerDataToggle = BuildRecordingOptionToggle(
            "Clients",
            "Create a separate customer-data package for a supported application.",
            isChecked: false,
            value => _captureCustomerData = value);

        var header = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new System.Windows.Thickness(0, 0, SessionModeDismissReserve, SessionModePanelGap)
        };
        header.Children.Add(_hoverDiscoveryToggle);
        header.Children.Add(_customerDataToggle);

        _quickMapModeButton = BuildTextLinkButton(
            "Rescan current screen",
            QueueQuickMapCommand,
            outlined: true);
        _quickMapModeButton.Width = double.NaN;
        _quickMapModeButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        _quickMapModeButton.Margin = new System.Windows.Thickness(0, SessionModeCardGap, 0, 0);
        _quickMapModeButton.Visibility = System.Windows.Visibility.Collapsed;
        _quickMapModeButton.ToolTip = "Add the current screen to this existing map without clicking. Shortcut: Q.";

        var buttons = new System.Windows.Controls.Primitives.UniformGrid
        {
            Rows = 1,
            Columns = 2,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        buttons.Children.Add(SessionModeButton(
            "Manual",
            "Record clicks yourself",
            () => QueueSessionModeCommand(autoTabs: false),
            "Start manual recording. Shortcut: 1.",
            CreateManualModeIcon,
            new System.Windows.Thickness(0, 0, SessionModeCardGap / 2, 0)));
        buttons.Children.Add(SessionModeButton(
            "Auto labels",
            "Detect main buttons automatically",
            () => QueueSessionModeCommand(autoTabs: true),
            "Start auto labeling, then continue manually. Shortcut: 2.",
            CreateAutoLabelsModeIcon,
            new System.Windows.Thickness(SessionModeCardGap / 2, 0, 0, 0)));

        var content = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Children =
            {
                header,
                buttons,
                _quickMapModeButton
            }
        };

        var dismissButton = WindowUtilityButton(CreateCloseIcon, HideSessionModeChooser, "Cancel recording");
        dismissButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        dismissButton.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        dismissButton.Margin = new System.Windows.Thickness(
            0,
            SessionModeDismissEdgeInset - SessionModePanelInset,
            SessionModeDismissEdgeInset - SessionModePanelInset,
            0);

        var layout = new System.Windows.Controls.Grid();
        layout.Children.Add(content);
        System.Windows.Controls.Panel.SetZIndex(dismissButton, 1);
        layout.Children.Add(dismissButton);

        var panel = BuildPanelSurface(
            layout,
            new System.Windows.Thickness(
                SessionModePanelInset,
                SessionModePanelInset,
                SessionModePanelInset,
                SessionModePanelBottomInset));
        panel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        panel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        panel.Visibility = System.Windows.Visibility.Collapsed;
        return panel;
    }

    private static System.Windows.Controls.Primitives.ToggleButton BuildRecordingOptionToggle(
        string label,
        string toolTip,
        bool isChecked,
        Action<bool> changed)
    {
        var labelBlock = new System.Windows.Controls.TextBlock
        {
            FontWeight = System.Windows.FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center
        };
        var option = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = labelBlock,
            IsChecked = isChecked,
            MinWidth = 116d / RecorderContentScale,
            Height = 32d / RecorderContentScale,
            Padding = new System.Windows.Thickness(12d / RecorderContentScale, 0, 12d / RecorderContentScale, 0),
            Margin = new System.Windows.Thickness(0, 0, 8d / RecorderContentScale, 0),
            FontSize = 12d / RecorderContentScale,
            FontWeight = System.Windows.FontWeights.SemiBold,
            BorderThickness = new System.Windows.Thickness(1d / RecorderContentScale),
            ToolTip = toolTip,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        void Apply(bool enabled)
        {
            labelBlock.Text = $"{label}  {(enabled ? "ON" : "OFF")}";
            labelBlock.Foreground = enabled ? Accent : PanelMuted;
            option.Foreground = labelBlock.Foreground;
            option.Background = enabled ? Brush("#EAF4FF") : SourceSwitchBackground;
            option.BorderBrush = enabled ? Accent : Divider;
            changed(enabled);
        }

        option.Checked += (_, _) => Apply(true);
        option.Unchecked += (_, _) => Apply(false);
        Apply(isChecked);
        return option;
    }

    private System.Windows.Controls.Border BuildIdleShell()
    {
        var grid = new System.Windows.Controls.Grid
        {
            Width = RecorderShellContentWidth,
            Height = ShellContentHeight(RecorderCardHeight)
        };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(TopActionSectionHeight) });
        _idleStatusRowDefinition = new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(IdleStatusSectionHeight) };
        grid.RowDefinitions.Add(_idleStatusRowDefinition);
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(BottomNavHeight) });

        _targetMenu = BuildTargetMenu();
        _mapsMenu = BuildMapsMenu();
        _moreMenu = BuildMoreMenu();
        _moreButton = new System.Windows.Controls.Button { Visibility = System.Windows.Visibility.Collapsed };

        var disabledDouble = SecondaryCircleButton(
            "Double Click",
            CreateDoubleClickIcon,
            () => { },
            "Double-click capture becomes available while recording.",
            iconSize: DoubleClickCircleIconSize);
        disabledDouble.IsEnabled = false;
        var disabledPause = SecondaryCircleButton(
            "Pause",
            CreatePauseActionIcon,
            () => { },
            "Pause becomes available while recording.",
            iconSize: PauseCircleIconSize);
        disabledPause.IsEnabled = false;
        _startButton = PrimaryCircleButton(
            CreatePlayIcon,
            OpenSessionModeChooserForCurrentState,
            "Open the recording mode chooser. Shortcut: S or Enter.",
            IdlePrimaryCircleSize,
            Accent);
        var topRow = BuildTopActionRow(disabledDouble, _startButton, disabledPause);
        System.Windows.Controls.Grid.SetRow(topRow, 0);
        grid.Children.Add(topRow);

        var statusStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new System.Windows.Thickness(0, TopActionToStatusGap, 0, 0)
        };
        _idleStatusHost = statusStack;
        var statusRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _idleStatusDot = new System.Windows.Shapes.Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = Accent,
            Margin = new System.Windows.Thickness(0, 0, 14, 0)
        };
        _idleStatusBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Start Recording",
            FontSize = 34,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(690),
            Foreground = Ink,
            TextAlignment = System.Windows.TextAlignment.Center,
            LineHeight = IdleStatusHeadlineLineHeight,
            LineStackingStrategy = System.Windows.LineStackingStrategy.BlockLineHeight
        };
        statusRow.Children.Add(_idleStatusDot);
        statusRow.Children.Add(_idleStatusBlock);
        _idleDetailBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Select a window to get started",
            FontSize = StatusDetailFontSize,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = Muted,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };
        statusStack.Children.Add(statusRow);
        statusStack.Children.Add(_idleDetailBlock);
        System.Windows.Controls.Grid.SetRow(statusStack, 1);
        grid.Children.Add(statusStack);

        var contentHost = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        contentHost.Children.Add(BuildIdlePopupUnderlayPanel());

        var panelHost = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        _windowsPanel = BuildWindowsPanel();
        _mapsPanel = BuildMapsPanel();
        _libraryPanel = BuildLibraryPanel();
        panelHost.Children.Add(_windowsPanel);
        panelHost.Children.Add(_mapsPanel);
        panelHost.Children.Add(_libraryPanel);
        contentHost.Children.Add(panelHost);
        System.Windows.Controls.Grid.SetRow(contentHost, 2);
        grid.Children.Add(contentHost);

        if (_sessionModeShell is not null)
        {
            System.Windows.Controls.Grid.SetRow(_sessionModeShell, 1);
            System.Windows.Controls.Grid.SetRowSpan(_sessionModeShell, 2);
            System.Windows.Controls.Panel.SetZIndex(_sessionModeShell, 10);
            grid.Children.Add(_sessionModeShell);
        }

        _targetButton = BottomNavButton(
            CreateWindowTargetIcon,
            "Windows",
            () => SetCurrentView(RecorderView.Windows),
            "Choose which application window to record.");
        _mapsButton = BottomNavButton(
            CreateMapIcon,
            "Maps",
            () => SetCurrentView(RecorderView.Maps),
            "Browse saved maps and reopen a map.");
        _libraryButton = BottomNavButton(
            CreateFolderIcon,
            "Import",
            () => SetCurrentView(RecorderView.Library),
            "Import a map into the local library.");
        var bottomNav = BuildBottomNavigation(_targetButton, _mapsButton, _libraryButton);
        System.Windows.Controls.Grid.SetRow(bottomNav, 3);
        grid.Children.Add(bottomNav);

        UpdateIdleContentVisibility();
        return BuildRecorderShell(grid, RecorderFrameHeight, RecorderCardHeight, "Compact panel");
    }

    private System.Windows.Controls.Border BuildActiveShell()
    {
        var grid = new System.Windows.Controls.Grid
        {
            Width = RecorderShellContentWidth,
            Height = ShellContentHeight(RecorderCardHeight)
        };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(TopActionSectionHeight) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(BottomNavHeight) });

        _doubleButton = SecondaryCircleButton(
            "Double Click",
            CreateDoubleClickIcon,
            () => QueueCommand("T", "Double-click capture is armed.", StatusTone.Accent),
            "Record the next action as a double-click. Shortcut: T.",
            iconSize: DoubleClickCircleIconSize);
        _pauseButton = SecondaryCircleButton(
            "Pause",
            CreatePauseActionIcon,
            () => QueueCommand("P", "Recording paused.", StatusTone.Accent),
            "Pause this recording. Shortcut: P.",
            iconSize: PauseCircleIconSize);
        _resumeRecordingButton = SecondaryCircleButton(
            "Finish",
            CreateStopIcon,
            () => QueueCommand("F", "Finishing recording and building the map...", StatusTone.Accent),
            "Finish recording and build the map. Shortcut: F.");
        _resumeRecordingButton.Visibility = System.Windows.Visibility.Collapsed;
        _resumePrimaryButton = PrimaryCircleButton(
            CreatePlayIcon,
            () => QueueCommand("R", "Resuming recording...", StatusTone.Accent),
            "Resume the paused recording. Shortcut: R.",
            ActivePrimaryCircleSize,
            Accent);
        _resumePrimaryButton.Visibility = System.Windows.Visibility.Collapsed;
        _stopButton = PrimaryCircleButton(
            CreateStopActionIcon,
            () => QueueCommand("F", "Finishing recording and building the map...", StatusTone.Accent),
            "Finish recording and build the map. Shortcut: F.",
            ActivePrimaryCircleSize,
            Danger);
        var stopHost = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        stopHost.Children.Add(_stopButton);
        stopHost.Children.Add(_resumePrimaryButton);
        var rightHost = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        rightHost.Children.Add(_pauseButton);
        rightHost.Children.Add(_resumeRecordingButton);
        var actionRow = BuildTopActionRow(_doubleButton, stopHost, rightHost);
        grid.Children.Add(actionRow);

        var activeStatusStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _elapsedBlock = new System.Windows.Controls.TextBlock
        {
            Text = "00:00:00",
            FontSize = StatusTimerFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(520),
            Foreground = Muted,
            Width = ActiveStatusTimerWidth,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, 0, 2)
        };
        _elapsedBlock.SetValue(System.Windows.FrameworkElement.SnapsToDevicePixelsProperty, true);
        System.Windows.Media.TextOptions.SetTextFormattingMode(_elapsedBlock, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(_elapsedBlock, System.Windows.Media.TextRenderingMode.ClearType);
        System.Windows.Media.TextOptions.SetTextHintingMode(_elapsedBlock, System.Windows.Media.TextHintingMode.Fixed);
        System.Windows.Documents.Typography.SetNumeralAlignment(_elapsedBlock, System.Windows.FontNumeralAlignment.Tabular);
        System.Windows.Documents.Typography.SetNumeralStyle(_elapsedBlock, System.Windows.FontNumeralStyle.Lining);
        var activeStatusRow = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _activeStatusBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Recording in Progress...",
            FontSize = 34,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(690),
            Foreground = Ink,
            TextAlignment = System.Windows.TextAlignment.Center
        };
        var activeStatusDot = BuildActiveStatusDot();
        activeStatusDot.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        activeStatusDot.Margin = new System.Windows.Thickness(-(ActiveStatusDotOuterSize + ActiveStatusDotGap), 0, 0, 0);
        activeStatusStack.Children.Add(_elapsedBlock);
        activeStatusRow.Children.Add(_activeStatusBlock);
        activeStatusRow.Children.Add(activeStatusDot);
        activeStatusStack.Children.Add(activeStatusRow);
        System.Windows.Controls.Grid.SetRow(activeStatusStack, 1);
        grid.Children.Add(activeStatusStack);

        var activeArt = BuildActiveProgressPanel();
        System.Windows.Controls.Grid.SetRow(activeArt, 2);
        grid.Children.Add(activeArt);

        var disabledWindows = BottomNavButton(CreateWindowTargetIcon, "Windows", () => { }, "Navigation is unavailable while recording.");
        var disabledMaps = BottomNavButton(CreateMapIcon, "Maps", () => { }, "Navigation is unavailable while recording.");
        var disabledLibrary = BottomNavButton(CreateExportIcon, "Export", () => { }, "Navigation is unavailable while recording.");
        SetTileState(disabledWindows, enabled: false, selected: false);
        SetTileState(disabledMaps, enabled: false, selected: false);
        SetTileState(disabledLibrary, enabled: false, selected: false);
        disabledWindows.Opacity = 1.0;
        disabledMaps.Opacity = 1.0;
        disabledLibrary.Opacity = 1.0;
        var activeBottomNav = BuildBottomNavigation(disabledWindows, disabledMaps, disabledLibrary);
        activeBottomNav.Opacity = 0.30;
        System.Windows.Controls.Grid.SetRow(activeBottomNav, 3);
        grid.Children.Add(activeBottomNav);

        return BuildRecorderShell(grid, RecorderFrameHeight, RecorderCardHeight, "Compact panel");
    }

    private System.Windows.Controls.Border BuildCompactShell()
    {
        var grid = new System.Windows.Controls.Grid
        {
            Width = CompactShellContentWidth,
            Height = ShellContentHeight(CompactShellHeight)
        };

        var actionRow = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(24) });
        actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(196) });
        actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(24) });
        actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        _compactDoubleButton = SecondaryCircleButton(
            "Double Click",
            CreateDoubleClickIcon,
            () => QueueCommand("T", "Double-click capture is armed.", StatusTone.Accent),
            "Record the next action as a double-click. Shortcut: T.",
            CompactSecondaryCircleSize,
            DoubleClickCircleIconSize);
        HideCircleButtonLabel(_compactDoubleButton);

        _compactSkipAutoButton = SecondaryCircleButton(
            "Skip Auto",
            CreateSkipIcon,
            RequestAutoPassStop,
            "Stop the automatic tab pass and return to manual capture.",
            CompactSecondaryCircleSize);
        HideCircleButtonLabel(_compactSkipAutoButton);
        _compactSkipAutoButton.Visibility = System.Windows.Visibility.Collapsed;

        var leftHost = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        leftHost.Children.Add(_compactDoubleButton);
        leftHost.Children.Add(_compactSkipAutoButton);

        _compactPauseButton = SecondaryCircleButton(
            "Pause",
            CreatePauseActionIcon,
            () => QueueCommand("P", "Recording paused.", StatusTone.Accent),
            "Pause this recording. Shortcut: P.",
            CompactSecondaryCircleSize,
            PauseCircleIconSize);
        HideCircleButtonLabel(_compactPauseButton);

        _compactResumeButton = SecondaryCircleButton(
            "Finish",
            CreateStopIcon,
            () => QueueCommand("F", "Finishing recording and building the map...", StatusTone.Accent),
            "Finish recording and build the map. Shortcut: F.",
            CompactSecondaryCircleSize);
        HideCircleButtonLabel(_compactResumeButton);
        _compactResumeButton.Visibility = System.Windows.Visibility.Collapsed;

        var rightHost = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        rightHost.Children.Add(_compactPauseButton);
        rightHost.Children.Add(_compactResumeButton);

        _compactStartButton = PrimaryCircleButton(
            CreatePlayIcon,
            OpenSessionModeChooserForCurrentState,
            "Open the recording mode chooser. Shortcut: S or Enter.",
            CompactPrimaryCircleSize,
            Accent);
        _compactStopButton = PrimaryCircleButton(
            CreateStopActionIcon,
            () => QueueCommand("F", "Finishing recording and building the map...", StatusTone.Accent),
            "Finish recording and build the map. Shortcut: F.",
            CompactPrimaryCircleSize,
            Danger);
        _compactStopButton.Visibility = System.Windows.Visibility.Collapsed;
        _compactResumePrimaryButton = PrimaryCircleButton(
            CreatePlayIcon,
            () => QueueCommand("R", "Resuming recording...", StatusTone.Accent),
            "Resume the paused recording. Shortcut: R.",
            CompactPrimaryCircleSize,
            Accent);
        _compactResumePrimaryButton.Visibility = System.Windows.Visibility.Collapsed;

        var centerHost = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        centerHost.Children.Add(_compactStartButton);
        centerHost.Children.Add(_compactStopButton);
        centerHost.Children.Add(_compactResumePrimaryButton);

        Place(actionRow, leftHost, 0);
        Place(actionRow, BuildTallDivider(84), 1);
        Place(actionRow, centerHost, 2);
        Place(actionRow, BuildTallDivider(84), 3);
        Place(actionRow, rightHost, 4);
        grid.Children.Add(actionRow);

        RefreshCompactShellState();
        return BuildRecorderShell(
            grid,
            CompactShellFrameHeight,
            CompactShellHeight,
            "Expand panel",
            CompactFrameWidth,
            compactToggleExpands: true);
    }

    private void SetCurrentView(RecorderView view)
    {
        if (_currentMode is RecordingPanelMode.Active or RecordingPanelMode.Paused)
            return;

        _currentView = view;
        UpdateIdleContentVisibility();
        switch (view)
        {
            case RecorderView.Windows:
                RefreshWindowList();
                break;
            case RecorderView.Maps:
                RefreshMapList();
                break;
            case RecorderView.Library:
                RefreshLibraryPanel();
                break;
        }
    }

    private void UpdateIdleContentVisibility()
    {
        var showSessionChooser = _sessionModeChooserOpen;
        if (_idleStatusRowDefinition is not null)
            _idleStatusRowDefinition.Height = new System.Windows.GridLength(IdleStatusSectionHeight);
        if (_sessionModeShell is not null)
            _sessionModeShell.Margin = new System.Windows.Thickness(0);
        if (_idleStatusHost is not null)
            _idleStatusHost.Visibility = showSessionChooser ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        if (_idleDetailBlock is not null)
            _idleDetailBlock.Visibility = System.Windows.Visibility.Visible;

        if (_windowsPanel is not null)
            _windowsPanel.Visibility = !showSessionChooser && _currentView == RecorderView.Windows ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_mapsPanel is not null)
            _mapsPanel.Visibility = !showSessionChooser && _currentView == RecorderView.Maps ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_libraryPanel is not null)
            _libraryPanel.Visibility = !showSessionChooser && _currentView == RecorderView.Library ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        RefreshLibraryPanel();

        if (showSessionChooser)
        {
            SetTileState(_targetButton, enabled: false, selected: _currentView == RecorderView.Windows);
            SetTileState(_mapsButton, enabled: false, selected: _currentView == RecorderView.Maps);
            SetTileState(_libraryButton, enabled: false, selected: _currentView == RecorderView.Library);
        }
        else
        {
            SetTileState(_targetButton, enabled: true, selected: _currentView == RecorderView.Windows);
            SetTileState(_mapsButton, enabled: true, selected: _currentView == RecorderView.Maps);
            SetTileState(_libraryButton, enabled: true, selected: _currentView == RecorderView.Library);
        }
    }

    private System.Windows.Controls.Border BuildPanelSurface(System.Windows.UIElement child) =>
        BuildPanelSurface(child, new System.Windows.Thickness(PanelSurfacePadding));

    private System.Windows.Controls.Border BuildPanelSurface(System.Windows.UIElement child, System.Windows.Thickness padding) =>
        new()
        {
            Height = ContentPanelHeight,
            CornerRadius = new System.Windows.CornerRadius(ContentPanelCornerRadius),
            Background = Brush("#FFFFFF"),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = padding,
            Effect = CreateRecorderSurfaceShadow(),
            Child = child
        };

    private static System.Windows.Controls.StackPanel BuildPanelHeader(string title, string subtitle)
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };

        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = PanelHeaderTitleFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(700),
            Foreground = PanelInk
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = subtitle,
            FontSize = PanelHeaderSubtitleFontSize,
            Foreground = PanelMuted,
            Margin = new System.Windows.Thickness(0, 2, 0, 0),
            TextWrapping = System.Windows.TextWrapping.Wrap
        });

        return stack;
    }

    private System.Windows.Controls.Border BuildWindowsPanel()
    {
        var layout = new System.Windows.Controls.Grid();
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var header = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(0, 0, 0, PanelSectionGap)
        };
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = System.Windows.GridLength.Auto
        });

        var headerText = BuildPanelHeader("Recent Windows", "Select a window to get started");
        Place(header, headerText, 0);

        var refreshButton = BuildTextLinkButton("Refresh", RefreshWindowList, outlined: true);
        if (refreshButton.Content is System.Windows.Controls.TextBlock refreshLabel)
            refreshLabel.FontSize = PanelHeaderSubtitleFontSize;
        refreshButton.MinHeight = 48;
        refreshButton.ToolTip = "Reload the list to show apps opened after UiAtlas started.";
        refreshButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        refreshButton.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        refreshButton.Margin = new System.Windows.Thickness(8, -2, 0, 0);
        Place(header, refreshButton, 1);

        System.Windows.Controls.Grid.SetRow(header, 0);
        layout.Children.Add(header);

        _windowRowsHost = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = _windowRowsHost,
            Margin = new System.Windows.Thickness(0, 0, -PanelScrollRightInset, 0)
        };
        System.Windows.Controls.Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        return BuildPanelSurface(layout);
    }

    private System.Windows.Controls.Border BuildMapsPanel()
    {
        var layout = new System.Windows.Controls.Grid();
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var header = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Margin = new System.Windows.Thickness(0, 0, 0, PanelSectionGap)
        };

        var titleRow = new System.Windows.Controls.Grid();
        titleRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });
        titleRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = System.Windows.GridLength.Auto
        });
        titleRow.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Your Saved Maps",
            FontSize = PanelHeaderTitleFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(700),
            Foreground = PanelInk,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        var chevron = new System.Windows.Controls.Viewbox
        {
            Width = 22,
            Height = 22,
            Stretch = System.Windows.Media.Stretch.Uniform,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new System.Windows.Media.RotateTransform(0),
            Child = CreateStrokedPath("M3.5 5.5L8 10L12.5 5.5", PanelMuted, 1.8)
        };
        var searchToggle = new System.Windows.Controls.Button
        {
            Width = 44,
            Height = 44,
            Margin = new System.Windows.Thickness(8, -6, -6, -6),
            Padding = new System.Windows.Thickness(11),
            Content = chevron,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Show search",
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(22))
        };
        System.Windows.Automation.AutomationProperties.SetName(searchToggle, "Show map search");
        searchToggle.MouseEnter += (_, _) => searchToggle.Background = SourceSelectedBackground;
        searchToggle.MouseLeave += (_, _) => searchToggle.Background = System.Windows.Media.Brushes.Transparent;
        Place(titleRow, searchToggle, 1);
        header.Children.Add(titleRow);

        var searchPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Visibility = System.Windows.Visibility.Collapsed
        };
        searchPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Continue from where you left off or manage your recorded maps.",
            FontSize = PanelHeaderSubtitleFontSize,
            Foreground = PanelMuted,
            Margin = new System.Windows.Thickness(0, 2, 0, 0),
            TextWrapping = System.Windows.TextWrapping.Wrap
        });

        var search = BuildSearchField(
            "Search maps...",
            out _mapSearchBox,
            text =>
            {
                _mapQuery = text;
                RefreshMapList();
            });
        search.Margin = new System.Windows.Thickness(0, PanelSectionGap, 0, 0);
        search.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        search.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        searchPanel.Children.Add(search);
        header.Children.Add(searchPanel);

        var searchExpanded = false;
        searchToggle.Click += (_, _) =>
        {
            searchExpanded = !searchExpanded;
            searchPanel.Visibility = searchExpanded
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            chevron.RenderTransform = new System.Windows.Media.RotateTransform(searchExpanded ? 180 : 0);
            searchToggle.ToolTip = searchExpanded ? "Hide search" : "Show search";
            System.Windows.Automation.AutomationProperties.SetName(
                searchToggle,
                searchExpanded ? "Hide map search" : "Show map search");
        };

        System.Windows.Controls.Grid.SetRow(header, 0);
        layout.Children.Add(header);

        _mapRowsHost = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = _mapRowsHost,
            Margin = new System.Windows.Thickness(0, 0, -PanelScrollRightInset, 0)
        };
        var contentHost = new System.Windows.Controls.Grid();
        contentHost.Children.Add(scroll);
        _mapLoadingOverlay = BuildMapLoadingOverlay();
        contentHost.Children.Add(_mapLoadingOverlay);
        System.Windows.Controls.Grid.SetRow(contentHost, 1);
        layout.Children.Add(contentHost);

        return BuildPanelSurface(layout);
    }

    private System.Windows.Controls.Border BuildLibraryPanel()
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        _libraryTitleBlock = new System.Windows.Controls.TextBlock
        {
            FontSize = 27,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(700),
            Foreground = Ink
        };
        stack.Children.Add(_libraryTitleBlock);
        _libraryDetailBlock = new System.Windows.Controls.TextBlock
        {
            FontSize = 18,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 6, 0, 0),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        stack.Children.Add(_libraryDetailBlock);
        _libraryHintBlock = new System.Windows.Controls.TextBlock
        {
            FontSize = 18,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 28, 0, 0),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        stack.Children.Add(_libraryHintBlock);

        _libraryPrimaryButton = BuildPrimaryPanelButton("Import Map", HandleLibraryPrimaryAction);
        _libraryPrimaryButton.Margin = new System.Windows.Thickness(0, 26, 0, 0);
        stack.Children.Add(_libraryPrimaryButton);

        _librarySecondaryLinkButton = BuildTextLinkButton("Open maps folder", HandleLibrarySecondaryAction);
        _librarySecondaryLinkButton.Margin = new System.Windows.Thickness(0, 16, 0, 0);
        stack.Children.Add(_librarySecondaryLinkButton);

        _libraryTertiaryLinkButton = BuildTextLinkButton("Open recordings folder", HandleLibraryTertiaryAction);
        _libraryTertiaryLinkButton.Margin = new System.Windows.Thickness(0, 8, 0, 0);
        stack.Children.Add(_libraryTertiaryLinkButton);

        _libraryCleanupRecordingsButton = BuildTextLinkButton("Delete unused recordings", DeleteUnusedRecordingsFromLibrary);
        _libraryCleanupRecordingsButton.Margin = new System.Windows.Thickness(0, 8, 0, 0);
        _libraryCleanupRecordingsButton.Foreground = Danger;
        stack.Children.Add(_libraryCleanupRecordingsButton);

        RefreshLibraryPanel();
        return BuildPanelSurface(stack);
    }

    private System.Windows.Controls.Border BuildSearchField(
        string placeholder,
        out System.Windows.Controls.TextBox textBox,
        Action<string> onChanged)
    {
        var host = new System.Windows.Controls.Grid();
        host.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        host.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var icon = new System.Windows.Controls.Viewbox
        {
            Width = 32,
            Height = 32,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Margin = new System.Windows.Thickness(0, 0, 12, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = CreateSearchIcon(Muted)
        };
        Place(host, icon, 0);

        var textHost = new System.Windows.Controls.Grid();
        var localTextBox = new System.Windows.Controls.TextBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            FontSize = 28,
            Foreground = Ink,
            CaretBrush = Accent,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Padding = new System.Windows.Thickness(0),
            Margin = new System.Windows.Thickness(0)
        };
        var watermark = new System.Windows.Controls.TextBlock
        {
            Text = placeholder,
            FontSize = 24,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 154, 162, 175)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        localTextBox.TextChanged += (_, _) =>
        {
            watermark.Visibility = string.IsNullOrWhiteSpace(localTextBox.Text)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            onChanged(localTextBox.Text);
        };
        textBox = localTextBox;
        textHost.Children.Add(localTextBox);
        textHost.Children.Add(watermark);
        Place(host, textHost, 1);

        return new System.Windows.Controls.Border
        {
            Height = SearchBoxHeight,
            CornerRadius = new System.Windows.CornerRadius(20),
            BorderBrush = BubbleBorder,
            BorderThickness = new System.Windows.Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(246, 255, 255, 255)),
            Padding = new System.Windows.Thickness(16, 0, 16, 0),
            Child = host
        };
    }

    private System.Windows.Controls.Border BuildMapLoadingOverlay()
    {
        var progress = new System.Windows.Controls.ProgressBar
        {
            Width = 320,
            Height = 10,
            IsIndeterminate = true,
            Foreground = Accent,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 231, 234, 240)),
            BorderThickness = new System.Windows.Thickness(0),
            Margin = new System.Windows.Thickness(0, 0, 0, 22)
        };
        _mapLoadingTitleBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Loading maps",
            FontSize = 42,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(620),
            Foreground = Ink,
            TextAlignment = System.Windows.TextAlignment.Center,
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        _mapLoadingDetailBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Please wait while saved maps are loaded.",
            FontSize = 30,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 12, 0, 0),
            TextAlignment = System.Windows.TextAlignment.Center,
            TextWrapping = System.Windows.TextWrapping.Wrap
        };

        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        stack.Children.Add(progress);
        stack.Children.Add(_mapLoadingTitleBlock);
        stack.Children.Add(_mapLoadingDetailBlock);

        return new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(188, 247, 248, 252)),
            CornerRadius = new System.Windows.CornerRadius(18),
            Visibility = System.Windows.Visibility.Collapsed,
            Child = new System.Windows.Controls.Border
            {
                MinWidth = 480,
                MaxWidth = 560,
                MinHeight = 200,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(250, 255, 255, 255)),
                BorderBrush = BubbleBorder,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(22),
                Padding = new System.Windows.Thickness(34, 32, 34, 30),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Child = stack
            }
        };
    }

    private void SetMapLoadingState(bool isLoading, string title = "Loading maps", string detail = "Please wait while saved maps are loaded.")
    {
        if (_mapLoadingOverlay is not null)
            _mapLoadingOverlay.Visibility = isLoading ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_mapRowsHost is not null)
        {
            _mapRowsHost.Opacity = isLoading ? 0.28 : 1;
            _mapRowsHost.IsHitTestVisible = !isLoading;
        }
        if (_mapLoadingTitleBlock is not null)
            _mapLoadingTitleBlock.Text = title;
        if (_mapLoadingDetailBlock is not null)
            _mapLoadingDetailBlock.Text = detail;
    }

    private void RefreshWindowList()
    {
        if (_windowRowsHost is null)
            return;

        _windowRowsHost.Children.Clear();
        var selectedTarget = GetSelectedTarget();
        var selectionLocked = !_allowTargetSelection;

        IReadOnlyList<WindowTarget> windows;
        try
        {
            windows = WindowCatalog.ListTopLevelWindows()
                .Where(window => window.ProcessId != Environment.ProcessId)
                .ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _windowRowsHost.Children.Add(BuildPanelInfoRow("Could not load windows", BundleSecurity.SafeDiagnostic(ex.Message, 140)));
            return;
        }

        var filtered = windows
            .Where(window =>
            {
                if (string.IsNullOrWhiteSpace(_windowQuery))
                    return true;

                return window.Title.Contains(_windowQuery, StringComparison.OrdinalIgnoreCase) ||
                    window.ProcessName.Contains(_windowQuery, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(window.ProductName) && window.ProductName.Contains(_windowQuery, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(window => selectedTarget is not null && window.Hwnd == selectedTarget.Hwnd)
            .ThenBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selectedTarget is not null && filtered.All(window => window.Hwnd != selectedTarget.Hwnd))
            filtered = [selectedTarget, .. filtered];

        if (filtered.Length == 0)
        {
            _windowRowsHost.Children.Add(BuildPanelInfoRow("No windows found", "Open the app you want to record and try again."));
            return;
        }

        for (var index = 0; index < filtered.Length; index++)
        {
            var window = filtered[index];
            _windowRowsHost.Children.Add(BuildWindowRow(
                window,
                isSelected: selectedTarget is not null && selectedTarget.Hwnd == window.Hwnd,
                isLocked: selectionLocked));
        }
    }

    private void RefreshMapList(
        string loadingTitle = "Loading maps",
        string loadingDetail = "Please wait while saved maps are loaded.",
        bool showLoading = true)
    {
        if (_mapRowsHost is null)
            return;

        var refreshGeneration = Interlocked.Increment(ref _mapRefreshGeneration);
        var mapQuery = _mapQuery;
        var currentMapPath = string.IsNullOrWhiteSpace(_mapPath) ? null : Path.GetFullPath(_mapPath);

        if (showLoading)
            SetMapLoadingState(true, loadingTitle, loadingDetail);

        _ = Task.Run(() =>
        {
            try
            {
                var catalog = new LocalArtifactCatalog();
                catalog.EnsureSafe();
                CatalogMapRecovery.RecoverCompletedMaps(catalog);
                var filtered = catalog.ListMaps()
                    .Where(map =>
                    {
                        if (string.IsNullOrWhiteSpace(mapQuery))
                            return true;

                        return map.Id.Contains(mapQuery, StringComparison.OrdinalIgnoreCase) ||
                            FormatMapDisplayName(map.Id).Contains(mapQuery, StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(map =>
                    {
                        var mapPath = catalog.MapPath(map.Id);
                        var isCurrent = currentMapPath is not null &&
                            string.Equals(Path.GetFullPath(mapPath), currentMapPath, StringComparison.OrdinalIgnoreCase);
                        return (Map: map, MapPath: mapPath, IsCurrent: isCurrent);
                    })
                    .OrderByDescending(item => item.IsCurrent)
                    .ThenByDescending(item => item.Map.BuiltUtc)
                    .ToArray();

                return (Items: filtered, ErrorTitle: (string?)null, ErrorDetail: (string?)null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return (
                    Items: Array.Empty<(LocalMapInfo Map, string MapPath, bool IsCurrent)>(),
                    ErrorTitle: (string?)"Catalog unavailable",
                    ErrorDetail: BundleSecurity.SafeDiagnostic(ex.Message, 140));
            }
        }).ContinueWith(
            task =>
            {
                var dispatcher = _dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted)
                    return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_mapRowsHost is null || refreshGeneration != _mapRefreshGeneration)
                        return;

                    SetMapLoadingState(false);
                    _mapRowsHost.Children.Clear();
                    if (task.IsFaulted)
                    {
                        _mapRowsHost.Children.Add(BuildPanelInfoRow("Could not load maps", "Try opening Maps again in a moment."));
                        return;
                    }

                    var result = task.Result;
                    if (!string.IsNullOrWhiteSpace(result.ErrorTitle))
                    {
                        _mapRowsHost.Children.Add(BuildPanelInfoRow(result.ErrorTitle, result.ErrorDetail ?? string.Empty));
                        return;
                    }

                    if (result.Items.Length == 0)
                    {
                        _mapRowsHost.Children.Add(BuildPanelInfoRow("No saved maps yet", "Finish at least one map to build your library."));
                        return;
                    }

                    foreach (var item in result.Items)
                        _mapRowsHost.Children.Add(BuildMapRow(item.Map, item.MapPath, item.IsCurrent));
                }));
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void RefreshLibraryPanel()
    {
        RefreshLibraryButton();
        if (_libraryTitleBlock is null ||
            _libraryDetailBlock is null ||
            _libraryHintBlock is null ||
            _libraryPrimaryButton is null ||
            _librarySecondaryLinkButton is null ||
            _libraryTertiaryLinkButton is null ||
            _libraryCleanupRecordingsButton is null)
        {
            return;
        }

        var hasMap = HasCurrentMapAvailable();
        _libraryPrimaryButton.IsEnabled = true;
        _libraryPrimaryButton.Opacity = 1.0;

        if (hasMap)
        {
            _libraryTitleBlock.Text = "Export";
            _libraryDetailBlock.Text = "Create a human-readable JSON export of the current map.";
            _libraryHintBlock.Text = $"Current map: {Path.GetFileName(_mapPath)}";
            _libraryHintBlock.Foreground = Ink;
            SetButtonText(_libraryPrimaryButton, "Export Current Map");
            _libraryPrimaryButton.ToolTip = "Create a JSON export of the current map.";
            SetButtonText(_librarySecondaryLinkButton, "Import another map");
            _librarySecondaryLinkButton.ToolTip = "Import another map into the local library.";
            SetButtonText(_libraryTertiaryLinkButton, "Open exports folder");
            _libraryTertiaryLinkButton.ToolTip = "Open the managed exports folder.";
        }
        else
        {
            _libraryTitleBlock.Text = "Import";
            _libraryDetailBlock.Text = "Bring an existing map into your saved maps library.";
            _libraryHintBlock.Text = "Import a map now, or finish a recording to unlock export here.";
            _libraryHintBlock.Foreground = Muted;
            SetButtonText(_libraryPrimaryButton, "Import Map");
            _libraryPrimaryButton.ToolTip = "Import a map into the local library.";
            SetButtonText(_librarySecondaryLinkButton, "Open maps folder");
            _librarySecondaryLinkButton.ToolTip = "Open the managed maps folder.";
            SetButtonText(_libraryTertiaryLinkButton, "Open recordings folder");
            _libraryTertiaryLinkButton.ToolTip = "Open the managed recordings folder.";
        }

        _libraryCleanupRecordingsButton.ToolTip =
            "Permanently delete recording files that are not used by any saved map.";
    }

    private bool HasCurrentMapAvailable() =>
        !string.IsNullOrWhiteSpace(_mapPath) &&
        File.Exists(_mapPath) &&
        !string.IsNullOrWhiteSpace(_defaultExportPath);

    private void HandleLibraryPrimaryAction()
    {
        if (HasCurrentMapAvailable())
            ExportMap();
        else
            ImportMapIntoCatalog();
    }

    private void HandleLibrarySecondaryAction()
    {
        if (HasCurrentMapAvailable())
        {
            ImportMapIntoCatalog();
            return;
        }

        OpenPathInShell(new LocalArtifactCatalog().MapsDirectory);
    }

    private void HandleLibraryTertiaryAction()
    {
        var catalog = new LocalArtifactCatalog();
        OpenPathInShell(HasCurrentMapAvailable() ? catalog.ExportsDirectory : catalog.RecordingsDirectory);
    }

    private async void DeleteUnusedRecordingsFromLibrary()
    {
        if (_currentMode is RecordingPanelMode.Active or RecordingPanelMode.Paused)
        {
            SetStatus("Finish the current recording before deleting stored recordings.");
            return;
        }

        try
        {
            SetStatus("Checking which recordings are unused...");
            var catalog = new LocalArtifactCatalog();
            var unused = await Task.Run(() => CatalogMapRecovery.ListUnusedRecordingIds(catalog));
            if (unused.Count == 0)
            {
                SetStatus("No unused recordings were found.");
                return;
            }

            if (!ConfirmUnusedRecordingDeletion(unused.Count))
                return;

            SetStatus("Deleting unused recordings and captured screenshots...");
            var result = await Task.Run(() => CatalogMapRecovery.DeleteUnusedRecordingsPermanently(catalog));
            if (!result.ReferenceScanComplete)
            {
                SetStatus("Recordings were kept because one or more saved maps could not be checked safely.");
                return;
            }

            var detail = result.RecordingDeleteFailures == 0
                ? $"Deleted {result.RecordingsDeleted} unused recording(s) permanently."
                : $"Deleted {result.RecordingsDeleted} recording(s); {result.RecordingDeleteFailures} could not be removed because they are in use.";
            UpdateCaption("RECORDINGS CLEANED", StatusTone.Accent, detail);
            UpdateIdleStatus("Recordings cleaned", detail, StatusTone.Accent);
            SetStatus(detail);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            var detail = "Delete recordings failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160);
            SetStatus(detail);
            UpdateCaption("DELETE FAILED", StatusTone.Danger, detail);
            UpdateIdleStatus("Recordings were not deleted", detail, StatusTone.Danger);
        }
    }

    private bool ConfirmUnusedRecordingDeletion(int count)
    {
        var message = $"Delete {count} recording file(s) that are not used by any saved map?\n\n" +
                      "This also deletes their captured screenshots and cannot be undone.";
        var result = _window is null
            ? System.Windows.MessageBox.Show(message, "Delete unused recordings permanently?",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No)
            : System.Windows.MessageBox.Show(_window, message, "Delete unused recordings permanently?",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    private void RefreshLibraryButton()
    {
        if (_libraryButton is null)
            return;

        if (HasCurrentMapAvailable())
        {
            ConfigureBottomNavButton(
                _libraryButton,
                CreateExportIcon,
                "Export",
                "Open export options for the current map.");
            return;
        }

        ConfigureBottomNavButton(
            _libraryButton,
            CreateFolderIcon,
            "Import",
            "Import a map into the local library.");
    }

    private void SelectMapFromLibrary(LocalMapInfo map, string mapPath)
    {
        try
        {
            var catalog = new LocalArtifactCatalog();
            catalog.EnsureSafe();

            _mapPath = Path.GetFullPath(mapPath);
            _recordingPath = catalog.MatchingRecordingPath(map.Id);
            _defaultExportPath = catalog.DefaultExportPath(map.Id);

            RefreshMapList(showLoading: false);
            RefreshLibraryPanel();

            var mapName = FormatMapDisplayName(map.Id);
            var mapSummary = $"{mapName} - Built {FormatRelativeTime(map.BuiltUtc)}";
            if (_currentMode == RecordingPanelMode.MapReady)
            {
                UpdateCaption("MAP READY", StatusTone.Success, $"{mapName} is selected. Open it here, resume recording, or export it.");
            }
            else if (_currentView == RecorderView.Maps)
            {
                UpdateCaption("MAP LIBRARY", StatusTone.Accent, $"{mapName} is selected. Open it here or switch to Export.");
                UpdateIdleStatus("Current Map", mapSummary, StatusTone.Accent);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetStatus("Could not select map: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    private static void SetButtonText(System.Windows.Controls.Button? button, string text)
    {
        if (button?.Content is System.Windows.Controls.TextBlock block)
            block.Text = text;
    }

    private void ImportMapIntoCatalog()
    {
        if (_currentMode is RecordingPanelMode.Active or RecordingPanelMode.Paused)
        {
            SetStatus("Pause or finish the current recording before importing another map.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import map into library",
            Filter = "UI KG map|*.db;*.sqlite;*.json|All files|*.*"
        };
        var accepted = _window is null ? dialog.ShowDialog() : dialog.ShowDialog(_window);
        if (accepted != true)
            return;

        try
        {
            var imported = ImportMapToCatalog(dialog.FileName);
            if (imported is null)
                return;

            var catalog = new LocalArtifactCatalog();
            var recordingPath = catalog.MatchingRecordingPath(imported.MapId);
            MarkMapReady(imported.MapPath, recordingPath, catalog.DefaultExportPath(imported.MapId));

            var mapName = FormatMapDisplayName(imported.MapId);
            var detail = imported.SkippedRecordingCount > 0
                ? $"Imported {mapName}. {imported.ImportedRecordingCount} recording bundle(s) linked, {imported.SkippedRecordingCount} skipped."
                : imported.ImportedRecordingCount > 0
                    ? $"Imported {mapName} with {imported.ImportedRecordingCount} linked recording bundle(s)."
                    : $"Imported {mapName} into your saved maps library.";
            SetStatus(detail);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            var owner = _window;
            if (owner is null)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "Import failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    owner,
                    ex.Message,
                    "Import failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    private CatalogImportResult? ImportMapToCatalog(string sourceMapPath)
    {
        var sourceFullPath = Path.GetFullPath(sourceMapPath);
        var graph = new UiGraphReader().Load(sourceFullPath);
        var catalog = new LocalArtifactCatalog();
        catalog.EnsureSafe();

        var mapId = ResolveCatalogMapId(catalog, graph, sourceFullPath);
        var targetMapPath = catalog.MapPath(mapId);
        var sameMapPath = string.Equals(sourceFullPath, targetMapPath, StringComparison.OrdinalIgnoreCase);
        if (!sameMapPath && File.Exists(targetMapPath))
        {
            var owner = _window;
            var replace = owner is null
                ? System.Windows.MessageBox.Show(
                    $"A library map named '{mapId}' already exists.{Environment.NewLine}{Environment.NewLine}Replace the existing copy?",
                    "Import map",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question)
                : System.Windows.MessageBox.Show(
                    owner,
                    $"A library map named '{mapId}' already exists.{Environment.NewLine}{Environment.NewLine}Replace the existing copy?",
                    "Import map",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
            if (replace != System.Windows.MessageBoxResult.Yes)
                return null;
        }

        SqliteGraphStore.Save(graph, targetMapPath);
        catalog.ClearMapDeletionMarker(mapId);

        var importedRecordingPaths = new List<string>();
        var skippedRecordingCount = 0;
        var expectedSessionIds = graph.Metadata.EffectiveSourceBundleIds
            .Where(LocalArtifactCatalog.IsValidId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in MatchingEvidenceCandidates(sourceFullPath, graph.Metadata.EffectiveSourceBundleIds)
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var bundle = RecordingBundle.Open(candidate);
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                if (!expectedSessionIds.Contains(manifest.SessionId) || !LocalArtifactCatalog.IsValidId(manifest.SessionId))
                    continue;

                var targetRecordingPath = catalog.RecordingPath(manifest.SessionId);
                var candidateFullPath = Path.GetFullPath(candidate);
                if (!string.Equals(candidateFullPath, targetRecordingPath, StringComparison.OrdinalIgnoreCase))
                    File.Copy(candidateFullPath, targetRecordingPath, overwrite: true);
                importedRecordingPaths.Add(targetRecordingPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                skippedRecordingCount++;
            }
        }

        var manifestPath = catalog.MapSessionPath(mapId);
        var orderedRecordingPaths = importedRecordingPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedRecordingPaths.Length > 0)
        {
            var sessionManifest = LogicalMapSessionStore.Create(
                mapId,
                ResolveCatalogProcessName(graph, sourceFullPath),
                graph.Metadata.BuiltUtc);
            foreach (var recordingPath in orderedRecordingPaths)
            {
                var sessionId = Path.GetFileNameWithoutExtension(recordingPath);
                sessionManifest = LogicalMapSessionStore.AddRecording(sessionManifest, sessionId, recordingPath, DateTimeOffset.UtcNow);
            }
            LogicalMapSessionStore.Save(manifestPath, sessionManifest);
        }
        else if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        return new CatalogImportResult(mapId, targetMapPath, orderedRecordingPaths.Length, skippedRecordingCount);
    }

    private static IReadOnlyList<string> MatchingEvidenceCandidates(string graphPath, IReadOnlyList<string> sourceBundleIds)
    {
        var values = new List<string>();
        foreach (var sourceBundleId in sourceBundleIds)
        {
            if (!LocalArtifactCatalog.IsValidId(sourceBundleId))
                continue;

            try
            {
                var catalog = new LocalArtifactCatalog();
                values.Add(catalog.RecordingPath(sourceBundleId));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or UnauthorizedAccessException)
            {
                // Best-effort discovery only.
            }
        }

        var graphDirectory = Path.GetDirectoryName(Path.GetFullPath(graphPath));
        if (!string.IsNullOrWhiteSpace(graphDirectory))
        {
            var sessionManifestPath = Path.Combine(graphDirectory, Path.GetFileNameWithoutExtension(graphPath) + ".session.json");
            if (File.Exists(sessionManifestPath))
            {
                try
                {
                    var manifest = LogicalMapSessionStore.Load(sessionManifestPath);
                    values.AddRange(LogicalMapSessionStore.RecordingPaths(manifest));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // Best-effort discovery only.
                }
            }

            foreach (var sourceBundleId in sourceBundleIds)
            {
                values.Add(Path.Combine(graphDirectory, sourceBundleId + ".mlrec"));
                values.Add(Path.Combine(Directory.GetParent(graphDirectory)?.FullName ?? graphDirectory, "recordings", sourceBundleId + ".mlrec"));
            }

            values.Add(Path.Combine(graphDirectory, Path.GetFileNameWithoutExtension(graphPath) + ".mlrec"));
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveCatalogMapId(LocalArtifactCatalog catalog, UiKnowledgeGraph graph, string sourceMapPath)
    {
        foreach (var candidate in new[]
                 {
                     graph.Metadata.EffectiveLogicalMapId,
                     Path.GetFileNameWithoutExtension(sourceMapPath),
                     ResolveCatalogProcessName(graph, sourceMapPath)
                 })
        {
            var normalized = NormalizeCatalogId(candidate);
            if (normalized is not null)
                return normalized;
        }

        return catalog.CreateId(ResolveCatalogProcessName(graph, sourceMapPath), graph.Metadata.BuiltUtc);
    }

    private static string ResolveCatalogProcessName(UiKnowledgeGraph graph, string sourceMapPath)
    {
        var application = graph.Nodes.FirstOrDefault(node => node.Kind == GraphNodeKind.Application);
        var processName = application?.Properties.FirstOrDefault(property => property.Name == "processName")?.Value;
        return string.IsNullOrWhiteSpace(processName)
            ? Path.GetFileNameWithoutExtension(sourceMapPath)
            : processName;
    }

    private static string? NormalizeCatalogId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length == 0 || builder[^1] == '-')
                continue;

            builder.Append('-');
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length == 0)
            return null;
        if (normalized.Length > 128)
            normalized = normalized[..128].Trim('-');
        return LocalArtifactCatalog.IsValidId(normalized) ? normalized : null;
    }

    private System.Windows.UIElement BuildWindowRow(WindowTarget window, bool isSelected, bool isLocked)
    {
        var processLabel = !string.IsNullOrWhiteSpace(window.OriginalFilename)
            ? window.OriginalFilename.ToUpperInvariant()
            : (window.ProcessName + ".exe").ToUpperInvariant();
        var selectionCornerRadius = new System.Windows.CornerRadius(ListSelectionCornerRadius);
        var rowButton = new System.Windows.Controls.Button
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(selectionCornerRadius)
        };

        var content = new System.Windows.Controls.Grid
        {
            Height = CompactRowHeight
        };
        content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(WindowListBadgeColumnWidth) });
        content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var badge = CreateAppBadge(window, WindowListBadgeSize, 14);
        badge.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        badge.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        Place(content, badge, 0);

        var textStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var titleBlock = new System.Windows.Controls.TextBlock
        {
            Text = string.IsNullOrWhiteSpace(window.Title) ? "(Untitled window)" : window.Title,
            FontSize = ListItemTitleFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(400),
            Foreground = PanelInk,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(titleBlock);
        textStack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = processLabel,
            FontSize = ListItemSubtitleFontSize,
            Foreground = PanelMuted,
            Margin = new System.Windows.Thickness(0, 2, 0, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        Place(content, textStack, 1);

        rowButton.Content = content;
        var rowBorder = new System.Windows.Controls.Border
        {
            CornerRadius = selectionCornerRadius,
            SnapsToDevicePixels = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(ListSelectionBorderThickness),
            Margin = new System.Windows.Thickness(0, 0, 0, PanelListRowSpacing),
            Child = rowButton
        };

        void ApplyWindowRowChrome(bool highlight)
        {
            rowBorder.Background = highlight ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent;
            rowBorder.BorderBrush = highlight ? BottomNavSelectedBorder : System.Windows.Media.Brushes.Transparent;
            titleBlock.Foreground = highlight ? BottomNavSelectedForeground : PanelInk;
            rowButton.Background = System.Windows.Media.Brushes.Transparent;
        }

        ApplyWindowRowChrome(isSelected);
        rowButton.MouseEnter += (_, _) => ApplyWindowRowChrome(true);
        rowButton.MouseLeave += (_, _) => ApplyWindowRowChrome(isSelected);
        rowButton.Click += (_, _) =>
        {
            if (isLocked)
            {
                ApplyVisualStatus("This recording stays connected to its current target window.", StatusTone.Neutral);
                return;
            }

            lock (_targetGate)
            {
                _selectedTarget = window;
                _selectedTargetHwnd = window.Hwnd;
            }
            _processName = window.ProcessName;
            RefreshPreStartTargetState();
            ApplyVisualStatus("Selected " + FormatWindowSummary(window) + ".", StatusTone.Accent);
        };

        return rowBorder;
    }

    private System.Windows.UIElement BuildMapRow(LocalMapInfo map, string mapPath, bool isCurrent)
    {
        var selectionCornerRadius = new System.Windows.CornerRadius(ListSelectionCornerRadius);
        var rowGrid = new System.Windows.Controls.Grid
        {
            Height = CompactMapRowHeight
        };
        rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(MapListBadgeColumnWidth) });
        rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

        var selectionButton = new System.Windows.Controls.Button
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Template = RoundedButtonTemplate(selectionCornerRadius),
            ToolTip = $"Select {FormatMapDisplayName(map.Id)} as the current map."
        };
        selectionButton.Click += (_, _) => SelectMapFromLibrary(map, mapPath);
        System.Windows.Controls.Grid.SetColumnSpan(selectionButton, 3);
        rowGrid.Children.Add(selectionButton);

        var badge = CreateDeferredMapAppBadge(map, MapListBadgeSize, 14);
        badge.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        badge.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        badge.IsHitTestVisible = false;
        Place(rowGrid, badge, 0);

        var titleStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, CompactButtonOuterGap, 0)
        };
        titleStack.IsHitTestVisible = false;
        var titleBlock = new System.Windows.Controls.TextBlock
        {
            Text = FormatMapDisplayName(map.Id),
            FontSize = ListItemTitleFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(400),
            Foreground = PanelInk,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        };
        titleStack.Children.Add(titleBlock);
        titleStack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = map.Status == "incomplete"
                ? $"Incomplete - 0 controls - Built {FormatRelativeTime(map.BuiltUtc)}"
                : $"{map.NodeCount} nodes - Built {FormatRelativeTime(map.BuiltUtc)}",
            FontSize = ListItemSubtitleFontSize,
            Foreground = PanelMuted,
            Margin = new System.Windows.Thickness(0, 2, 0, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        Place(rowGrid, titleStack, 1);

        var openButton = BuildCompactPrimaryButton("Open Map", () =>
        {
            SelectMapFromLibrary(map, mapPath);
            var catalog = new LocalArtifactCatalog();
            var evidencePath = catalog.MatchingRecordingPath(map.Id);
            OpenMapViewerSafely(mapPath, evidencePath);
        });

        var moreButton = BuildMenuButton();
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = moreButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HasDropShadow = false,
            OverridesDefaultStyle = true,
            Background = MoreMenuBackground,
            BorderBrush = BubbleBorder,
            BorderThickness = new System.Windows.Thickness(1),
            Width = MapContextMenuItemWidth + 10,
            Padding = new System.Windows.Thickness(4),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            ItemContainerStyle = FlatContextMenuItemContainerStyle(),
            Template = FlatContextMenuTemplate(new System.Windows.CornerRadius(14))
        };
        menu.Items.Add(BuildContextMenuItem("Open Folder", () =>
        {
            menu.IsOpen = false;
            OpenPathInShell(Path.GetDirectoryName(Path.GetFullPath(mapPath)) ?? new LocalArtifactCatalog().MapsDirectory);
        }, CreateFolderIcon));
        menu.Items.Add(BuildContextMenuItem("Duplicate Map", () =>
        {
            menu.IsOpen = false;
            _ = DuplicateMapFromLibraryAsync(map);
        }, CreateDuplicateIcon));
        menu.Items.Add(BuildContextMenuItem("Delete Map", () =>
        {
            menu.IsOpen = false;
            DeleteMapFromLibrary(map, mapPath, deleteSourceRecordings: false);
        }, CreateDeleteIcon, Danger));
        menu.Items.Add(BuildContextMenuItem("Delete Map + Recordings", () =>
        {
            menu.IsOpen = false;
            DeleteMapFromLibrary(map, mapPath, deleteSourceRecordings: true);
        }, CreateDeleteIcon, Danger));
        moreButton.Click += (_, _) => menu.IsOpen = true;
        moreButton.ContextMenu = menu;

        var actionGroup = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        actionGroup.Children.Add(openButton);
        moreButton.Margin = new System.Windows.Thickness(CompactButtonGroupGap, 0, 0, 0);
        actionGroup.Children.Add(moreButton);
        Place(rowGrid, actionGroup, 2);

        var rowBorder = new System.Windows.Controls.Border
        {
            CornerRadius = selectionCornerRadius,
            SnapsToDevicePixels = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(ListSelectionBorderThickness),
            Margin = new System.Windows.Thickness(0, 0, 0, PanelListRowSpacing),
            Child = rowGrid
        };

        void ApplyMapRowChrome(bool highlight)
        {
            rowBorder.Background = highlight ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent;
            rowBorder.BorderBrush = highlight ? BottomNavSelectedBorder : System.Windows.Media.Brushes.Transparent;
            titleBlock.Foreground = highlight ? BottomNavSelectedForeground : PanelInk;
        }

        ApplyMapRowChrome(isCurrent);
        rowBorder.MouseEnter += (_, _) => ApplyMapRowChrome(true);
        rowBorder.MouseLeave += (_, _) => ApplyMapRowChrome(isCurrent);

        return rowBorder;
    }

    private async Task DuplicateMapFromLibraryAsync(LocalMapInfo map)
    {
        if (_currentMode is RecordingPanelMode.Active or RecordingPanelMode.Paused)
        {
            SetStatus("Pause or finish the current recording before duplicating a map.");
            return;
        }

        SetMapLoadingState(true, "Duplicating map", "Creating an independent copy for safe editing...");
        try
        {
            var duplicate = await Task.Run(() => new LocalArtifactCatalog().DuplicateMap(map.Id));
            var catalog = new LocalArtifactCatalog();
            var duplicateInfo = catalog.ListMaps().Single(item => item.Id == duplicate.Id);

            SelectMapFromLibrary(duplicateInfo, duplicate.MapPath);
            RefreshMapList(
                loadingTitle: "Loading duplicated map",
                loadingDetail: "Preparing the new independent copy.");
            var name = FormatMapDisplayName(duplicate.Id);
            UpdateCaption("MAP DUPLICATED", StatusTone.Success,
                $"{name} is selected. Resume will update this copy; the original map stays unchanged.");
            UpdateIdleStatus("Map duplicated",
                $"{name} is ready. The original map stays unchanged.", StatusTone.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   InvalidOperationException or ArgumentException)
        {
            SetMapLoadingState(false);
            SetStatus("Duplicate map failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    private void DeleteMapFromLibrary(LocalMapInfo map, string mapPath, bool deleteSourceRecordings)
    {
        var mapName = FormatMapDisplayName(map.Id);
        if (!ConfirmPermanentMapDeletion(mapName, deleteSourceRecordings))
            return;

        try
        {
            var catalog = new LocalArtifactCatalog();
            catalog.EnsureSafe();
            var isCurrentMap = !string.IsNullOrWhiteSpace(_mapPath) &&
                string.Equals(Path.GetFullPath(_mapPath), Path.GetFullPath(mapPath), StringComparison.OrdinalIgnoreCase);

            LocalMapDeletionResult? deletion = null;
            var deleted = deleteSourceRecordings
                ? (deletion = CatalogMapRecovery.DeleteMapAndUnusedRecordingsPermanently(catalog, map.Id)).MapDeleted
                : CatalogMapRecovery.DeleteUiAtlasmanently(catalog, map.Id);
            if (!deleted || File.Exists(catalog.MapPath(map.Id)))
                throw new IOException("The map file is still in use or could not be removed.");

            if (isCurrentMap)
            {
                _mapPath = null;
                _recordingPath = null;
                _defaultExportPath = null;
                ApplyMode(RecordingPanelMode.PreStart);
            }

            RefreshMapList(
                loadingTitle: "Removing map",
                loadingDetail: "Please wait while the library is updated.");
            RefreshLibraryPanel();
            UpdateIdleContentVisibility();

            ShowMapDeletionFeedback(mapName, deletion);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetMapLoadingState(false);
            RefreshMapList(showLoading: false);
            RefreshLibraryPanel();
            UpdateIdleContentVisibility();
            var detail = "Delete map failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160);
            SetStatus(detail);
            UpdateCaption("DELETE FAILED", StatusTone.Danger, detail);
            UpdateIdleStatus("Map was not deleted", detail, StatusTone.Danger);
        }
    }

    private bool ConfirmPermanentMapDeletion(string mapName, bool deleteSourceRecordings)
    {
        var scope = deleteSourceRecordings
            ? "This map and its source recordings, including captured screenshots, will be deleted permanently. Recordings shared with another saved map will be kept."
            : "This map will be deleted permanently. Its source recordings will be kept.";
        var owner = _window;
        var result = owner is null
            ? System.Windows.MessageBox.Show(
                $"{mapName}\n\n{scope}",
                "Delete map permanently?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No)
            : System.Windows.MessageBox.Show(
                owner,
                $"{mapName}\n\n{scope}",
                "Delete map permanently?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    private void ShowMapDeletionFeedback(string mapName, LocalMapDeletionResult? deletion)
    {
        var detail = deletion is null
            ? $"{mapName} was deleted permanently. Its source recordings were kept."
            : FormatMapAndRecordingDeletionFeedback(mapName, deletion);
        UpdateCaption("MAP DELETED", StatusTone.Accent, detail);
        UpdateIdleStatus("Map deleted", detail, StatusTone.Accent);
    }

    private static string FormatMapAndRecordingDeletionFeedback(string mapName, LocalMapDeletionResult deletion)
    {
        var detail = $"{mapName} and {deletion.RecordingsDeleted} source recording(s) were deleted permanently.";
        if (deletion.SharedRecordingsKept > 0)
            detail += $" {deletion.SharedRecordingsKept} shared recording(s) were kept.";
        if (!deletion.ReferenceScanComplete)
            detail += " Source recordings were kept because another saved map could not be checked safely.";
        if (deletion.RecordingDeleteFailures > 0)
            detail += $" {deletion.RecordingDeleteFailures} recording(s) are still in use and were kept.";
        return detail;
    }

    private static System.Windows.Controls.Button BuildContextMenuItem(
        string header,
        Action onClick,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement>? iconFactory = null,
        System.Windows.Media.Brush? foreground = null)
    {
        var tone = foreground ?? Ink;
        var content = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        if (iconFactory is not null)
        {
            content.Children.Add(new System.Windows.Controls.Viewbox
            {
                Width = 16,
                Height = 16,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Child = iconFactory(tone)
            });
        }

        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = header,
            Foreground = tone,
            FontSize = 15,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(560),
            Margin = iconFactory is null
                ? new System.Windows.Thickness(0)
                : new System.Windows.Thickness(8, 0, 0, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(1),
            Width = MapContextMenuItemWidth,
            Height = MapContextMenuItemHeight,
            Padding = new System.Windows.Thickness(8, 0, 8, 0),
            Margin = new System.Windows.Thickness(0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(10))
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = BottomNavSelectedBorder;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Border CreateAppBadge(WindowTarget window, double size, double radius)
    {
        var icon = GetLiveAppIcon(window);
        if (icon is null)
            return CreateAppBadge(window.ProcessName, window.ProductName, size, radius);

        return CreateImageBadge(icon, size, radius);
    }

    private static System.Windows.Controls.Border CreateMapAppBadge(LocalArtifactCatalog catalog, LocalMapInfo map, double size, double radius)
    {
        var target = GetRecordedTarget(catalog, map.Id);
        if (target is null)
        {
            var processKey = ResolveMapProcessKey(map.Id);
            return CreateAppBadge(processKey, processKey, size, radius);
        }

        var icon = GetRecordedAppIcon(target);
        if (icon is not null)
            return CreateImageBadge(icon, size, radius);

        var fallbackSeed = !string.IsNullOrWhiteSpace(target.ProductName)
            ? target.ProductName
            : !string.IsNullOrWhiteSpace(target.OriginalFilename)
                ? target.OriginalFilename
                : target.ProcessName;
        return CreateAppBadge(target.ProcessName, fallbackSeed, size, radius);
    }

    private System.Windows.Controls.ContentControl CreateDeferredMapAppBadge(LocalMapInfo map, double size, double radius)
    {
        var processKey = ResolveMapProcessKey(map.Id);
        var host = new System.Windows.Controls.ContentControl
        {
            Content = CreateAppBadge(processKey, processKey, size, radius),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Tag = map.Id
        };

        _ = PopulateMapAppBadgeAsync(map.Id, host, size, radius);
        return host;
    }

    private async Task PopulateMapAppBadgeAsync(
        string mapId,
        System.Windows.Controls.ContentControl host,
        double size,
        double radius)
    {
        try
        {
            await MapBadgeLoadLimiter.WaitAsync().ConfigureAwait(false);
            System.Windows.Media.ImageSource? icon;
            try
            {
                icon = await Task.Run(() =>
                {
                    var catalog = new LocalArtifactCatalog();
                    var target = GetRecordedTarget(catalog, mapId);
                    return target is null ? null : GetRecordedAppIcon(target);
                }).ConfigureAwait(false);
            }
            finally
            {
                MapBadgeLoadLimiter.Release();
            }

            if (icon is null)
                return;

            var dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.Equals(host.Tag as string, mapId, StringComparison.Ordinal))
                    return;

                host.Content = CreateImageBadge(icon, size, radius);
            }));
        }
        catch
        {
        }
    }

    private static System.Windows.Controls.Border CreateImageBadge(System.Windows.Media.ImageSource icon, double size, double radius)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = icon,
            Width = size * 0.78,
            Height = size * 0.78,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);

        return new System.Windows.Controls.Border
        {
            Width = size,
            Height = size,
            CornerRadius = new System.Windows.CornerRadius(radius),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 248, 250, 253)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 17, 24, 39)),
            BorderThickness = new System.Windows.Thickness(1),
            Child = image
        };
    }

    private static System.Windows.Controls.Border CreateAppBadge(string processKey, string textSeed, double size, double radius)
    {
        var (background, foreground) = ResolveBadgePalette(processKey);
        return new System.Windows.Controls.Border
        {
            Width = size,
            Height = size,
            CornerRadius = new System.Windows.CornerRadius(radius),
            Background = background,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 17, 24, 39)),
            BorderThickness = new System.Windows.Thickness(1),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = ResolveBadgeText(textSeed),
                FontSize = size >= 50 ? 18 : 16,
                FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(700),
                Foreground = foreground,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center
            }
        };
    }

    private static System.Windows.Media.ImageSource? GetLiveAppIcon(WindowTarget window)
    {
        var cacheKey = FormattableString.Invariant(
            $"{window.RootOwnerHwnd}:{window.ProcessId}:{window.ProcessStartedUtc.ToUnixTimeMilliseconds()}");
        lock (LiveAppIconCacheGate)
        {
            if (LiveAppIconCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var resolved = TryLoadWindowAppIcon(window);
        lock (LiveAppIconCacheGate)
            LiveAppIconCache[cacheKey] = resolved;
        return resolved;
    }

    private static TargetScope? GetRecordedTarget(LocalArtifactCatalog catalog, string mapId)
    {
        lock (LiveAppIconCacheGate)
        {
            if (RecordedTargetCache.TryGetValue(mapId, out var cached))
                return cached;
        }

        var resolved = TryReadRecordedTarget(catalog, mapId);
        lock (LiveAppIconCacheGate)
            RecordedTargetCache[mapId] = resolved;
        return resolved;
    }

    private static TargetScope? TryReadRecordedTarget(LocalArtifactCatalog catalog, string mapId)
    {
        foreach (var recordingPath in catalog.MatchingRecordingPaths(mapId))
        {
            try
            {
                using var bundle = RecordingBundle.Open(recordingPath);
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                if (!string.IsNullOrWhiteSpace(manifest.Target.ProcessName))
                    return manifest.Target;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return null;
    }

    private static System.Windows.Media.ImageSource? GetRecordedAppIcon(TargetScope target)
    {
        var cacheKey = FormattableString.Invariant($"recorded:{target.ProcessName}|{target.OriginalFilename}|{target.ProductName}");
        lock (LiveAppIconCacheGate)
        {
            if (LiveAppIconCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var resolved = TryLoadRecordedAppIcon(target);
        lock (LiveAppIconCacheGate)
            LiveAppIconCache[cacheKey] = resolved;
        return resolved;
    }

    private static System.Windows.Media.ImageSource? TryLoadRecordedAppIcon(TargetScope target)
    {
        var executablePath = TryResolveInstalledExecutablePath(target);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        return TryLoadExecutableShellIcon(executablePath);
    }

    private static System.Windows.Media.ImageSource? TryLoadWindowAppIcon(WindowTarget window)
    {
        foreach (var hwnd in EnumerateIconCandidateWindows(window))
        {
            var handle = TryGetWindowIconHandle(hwnd);
            if (handle != 0 && CreateBitmapSourceFromBorrowedIcon(handle) is { } source)
                return source;
        }

        var executablePath = TryGetExecutablePath(window);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        return TryLoadExecutableShellIcon(executablePath);
    }

    private static IEnumerable<nint> EnumerateIconCandidateWindows(WindowTarget window)
    {
        var primary = (nint)window.Hwnd;
        if (primary != 0)
            yield return primary;

        var root = (nint)window.RootOwnerHwnd;
        if (root != 0 && root != primary)
            yield return root;
    }

    private static nint TryGetWindowIconHandle(nint hwnd)
    {
        if (hwnd == 0)
            return 0;

        foreach (var iconType in new[] { IconSmall2, IconSmall, IconBig })
        {
            var handle = TrySendWindowIconMessage(hwnd, iconType);
            if (handle != 0)
                return handle;
        }

        var classSmall = GetClassLongPtr(hwnd, GclHIconSm);
        if (classSmall != 0)
            return classSmall;

        return GetClassLongPtr(hwnd, GclHIcon);
    }

    private static nint TrySendWindowIconMessage(nint hwnd, int iconType)
    {
        _ = SendMessageTimeoutW(
            hwnd,
            WmGetIcon,
            (nuint)iconType,
            0,
            SmtoAbortIfHung,
            AppIconMessageTimeoutMs,
            out var result);
        return (nint)result;
    }

    private static string? TryGetExecutablePath(WindowTarget window)
    {
        try
        {
            using var process = Process.GetProcessById(window.ProcessId);
            if (process.StartTime.ToUniversalTime() != window.ProcessStartedUtc.UtcDateTime)
                return null;
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryResolveInstalledExecutablePath(TargetScope target)
    {
        var cacheKey = FormattableString.Invariant($"{target.ProcessName}|{target.OriginalFilename}|{target.ProductName}");
        lock (LiveAppIconCacheGate)
        {
            if (InstalledExecutablePathCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var resolved = TryLocateInstalledExecutablePath(target);
        lock (LiveAppIconCacheGate)
            InstalledExecutablePathCache[cacheKey] = resolved;
        return resolved;
    }

    private static string? TryLocateInstalledExecutablePath(TargetScope target)
    {
        foreach (var fileName in EnumerateExecutableCandidates(target))
        {
            var processPath = TryResolveRunningProcessPath(fileName);
            if (!string.IsNullOrWhiteSpace(processPath))
                return processPath;

            var registryPath = TryResolveExecutableFromAppPaths(fileName);
            if (!string.IsNullOrWhiteSpace(registryPath))
                return registryPath;

            var productPath = TryResolveExecutableFromKnownProductFolders(target, fileName);
            if (!string.IsNullOrWhiteSpace(productPath))
                return productPath;

            var searchPathResult = TryResolveExecutableFromSearchPath(fileName);
            if (!string.IsNullOrWhiteSpace(searchPathResult))
                return searchPathResult;
        }

        return null;
    }

    private static string? TryResolveExecutableFromKnownProductFolders(TargetScope target, string fileName)
    {
        if (!IsAutodeskRevitExecutableCandidate(target, fileName))
            return null;

        var programRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var programRoot in programRoots)
        {
            var autodeskRoot = Path.Combine(programRoot!, "Autodesk");
            try
            {
                if (!Directory.Exists(autodeskRoot)) continue;
                foreach (var installDirectory in Directory.EnumerateDirectories(
                             autodeskRoot, "Revit *", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(installDirectory, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                             System.Security.SecurityException)
            {
            }
        }

        return null;
    }

    internal static bool IsAutodeskRevitExecutableCandidate(TargetScope target, string fileName)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.OriginalFilename} {target.ProductName} {target.CompanyName}";
        return Path.GetFileName(fileName).Equals("Revit.exe", StringComparison.OrdinalIgnoreCase) &&
               identity.Contains("Revit", StringComparison.OrdinalIgnoreCase) &&
               identity.Contains("Autodesk", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(TargetScope target)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExecutableCandidate(values, target.OriginalFilename);
        AddExecutableCandidate(values, target.ProcessName);
        return values;
    }

    private static void AddExecutableCandidate(ISet<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var fileName = Path.GetFileName(value.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        values.Add(string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".exe");
    }

    private static string? TryResolveRunningProcessPath(string fileName)
    {
        var processName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string? TryResolveExecutableFromAppPaths(string fileName)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPathKey = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                    var value = appPathKey?.GetValue(string.Empty) as string;
                    var executablePath = NormalizeRegisteredExecutablePath(value);
                    if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                        return executablePath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }

        return null;
    }

    internal static string? NormalizeRegisteredExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.Length > 1 && expanded[0] == '"')
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote > 1)
                expanded = expanded[1..closingQuote];
        }
        return expanded.Trim();
    }

    private static string? TryResolveExecutableFromSearchPath(string fileName)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = SearchPath(null, fileName, null, (uint)buffer.Capacity, buffer, nint.Zero);
            if (length == 0)
                return null;

            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                return File.Exists(path) ? path : null;
            }

            capacity = checked((int)length + 1);
        }

        return null;
    }

    private static System.Windows.Media.ImageSource? TryLoadExecutableShellIcon(string executablePath)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfoW(
            executablePath,
            0,
            ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShgfiIcon);
        if (result == 0 || info.IconHandle == 0)
            return null;

        return CreateBitmapSourceFromOwnedIcon(info.IconHandle);
    }

    private static System.Windows.Media.ImageSource? CreateBitmapSourceFromBorrowedIcon(nint iconHandle)
    {
        if (iconHandle == 0)
            return null;

        var ownedCopy = CopyIcon(iconHandle);
        if (ownedCopy == 0)
            return null;

        return CreateBitmapSourceFromOwnedIcon(ownedCopy);
    }

    private static System.Windows.Media.ImageSource? CreateBitmapSourceFromOwnedIcon(nint iconHandle)
    {
        if (iconHandle == 0)
            return null;

        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static (System.Windows.Media.Brush Background, System.Windows.Media.Brush Foreground) ResolveBadgePalette(string processKey)
    {
        var lower = processKey.ToLowerInvariant();
        if (lower.Contains("word", StringComparison.Ordinal))
            return (Brush("#1769D2"), BubbleBackground);
        if (lower.Contains("excel", StringComparison.Ordinal))
            return (Brush("#16834B"), BubbleBackground);
        if (lower.Contains("outlook", StringComparison.Ordinal))
            return (Brush("#1684DE"), BubbleBackground);
        if (lower.Contains("settings", StringComparison.Ordinal))
            return (Brush("#D9DDE4"), Brush("#596273"));
        if (lower.Contains("cmd", StringComparison.Ordinal) || lower.Contains("powershell", StringComparison.Ordinal) || lower.Contains("terminal", StringComparison.Ordinal))
            return (Brush("#24272D"), BubbleBackground);
        if (lower.Contains("explorer", StringComparison.Ordinal) || lower.Contains("folder", StringComparison.Ordinal))
            return (Brush("#F7B733"), Brush("#4C3A05"));

        return (new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 10, 132, 255)), Accent);
    }

    private static string ResolveBadgeText(string textSeed)
    {
        var value = new string((textSeed ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character))
            .Take(3)
            .ToArray())
            .ToUpperInvariant();
        return string.IsNullOrWhiteSpace(value) ? "M" : value.Length > 2 ? value[..2] : value;
    }

    private static string ResolveMapProcessKey(string mapId)
    {
        var parts = mapId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : mapId;
    }

    private static string FormatRelativeTime(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value.ToUniversalTime();
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta < TimeSpan.FromMinutes(1))
            return "just now";
        if (delta < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)delta.TotalMinutes)} min ago";
        if (delta < TimeSpan.FromDays(1))
            return $"{Math.Max(1, (int)delta.TotalHours)} hr ago";
        if (delta < TimeSpan.FromDays(7))
            return $"{Math.Max(1, (int)delta.TotalDays)} day{(delta.TotalDays >= 2 ? "s" : string.Empty)} ago";

        return value.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }

    private static System.Windows.Controls.Button BuildPrimaryPanelButton(string label, Action onClick)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 18,
                FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(650),
                Foreground = BubbleBackground,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            },
            Height = 60,
            MinWidth = 210,
            Background = Accent,
            BorderBrush = Accent,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(18, 0, 18, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(18))
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Button BuildCompactPrimaryButton(string label, Action onClick)
    {
        var labelBlock = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = CompactButtonFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(650),
            Foreground = BubbleBackground,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        System.Windows.Media.TextOptions.SetTextFormattingMode(labelBlock, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(labelBlock, System.Windows.Media.TextRenderingMode.ClearType);
        System.Windows.Media.TextOptions.SetTextHintingMode(labelBlock, System.Windows.Media.TextHintingMode.Fixed);

        var button = new System.Windows.Controls.Button
        {
            Content = labelBlock,
            Width = CompactButtonMinWidth,
            Height = CompactButtonHeight,
            Background = Accent,
            BorderBrush = Accent,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(22, 0, 22, 0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(20))
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Button BuildActiveActionButton(
        string label,
        Action onClick,
        System.Windows.Media.Brush background,
        System.Windows.Media.Brush hoverBackground,
        string toolTip)
    {
        var labelBlock = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = ActiveProgressActionButtonFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(650),
            Foreground = System.Windows.Media.Brushes.White,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        labelBlock.SetValue(System.Windows.FrameworkElement.SnapsToDevicePixelsProperty, true);
        System.Windows.Media.TextOptions.SetTextFormattingMode(labelBlock, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(labelBlock, System.Windows.Media.TextRenderingMode.ClearType);
        System.Windows.Media.TextOptions.SetTextHintingMode(labelBlock, System.Windows.Media.TextHintingMode.Fixed);

        var button = new System.Windows.Controls.Button
        {
            Content = labelBlock,
            Width = ActiveProgressActionButtonMinWidth,
            Height = ActiveProgressActionButtonHeight,
            Background = background,
            BorderBrush = background,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(ActiveProgressActionButtonHorizontalPadding, 0, ActiveProgressActionButtonHorizontalPadding, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = toolTip,
            Focusable = false,
            Tag = "ui-atlas-active-action",
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(ActiveProgressActionButtonCornerRadius))
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverBackground;
            button.BorderBrush = hoverBackground;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = background;
            button.BorderBrush = background;
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Button BuildActiveCancelButton(string label, Action onClick) =>
        BuildActiveActionButton(
            label,
            onClick,
            Danger,
            Brush("#E53228"),
            "Cancel this recording.");

    private static System.Windows.Controls.Button BuildMenuButton()
    {
        var button = new System.Windows.Controls.Button
        {
            Content = new System.Windows.Controls.Viewbox
            {
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Child = CreateMoreIcon(PanelMuted)
            },
            Width = CompactButtonHeight,
            Height = CompactButtonHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(20))
        };
        ApplyOutlineHover(button);
        return button;
    }

    private static System.Windows.Controls.Button BuildTextLinkButton(string label, Action onClick, bool outlined = false)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 17,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(620),
            Foreground = Accent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            MinHeight = 34,
            Background = outlined ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent,
            BorderBrush = outlined ? Accent : System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(outlined ? 1.5 : 0),
            Padding = new System.Windows.Thickness(8, 5, 8, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(12))
        };
        button.MouseEnter += (_, _) =>
        {
            text.Foreground = Brush("#0077ED");
            button.BorderBrush = Accent;
            button.Background = Brush("#F1F7FF");
        };
        button.MouseLeave += (_, _) =>
        {
            text.Foreground = Accent;
            button.BorderBrush = outlined ? Accent : System.Windows.Media.Brushes.Transparent;
            button.Background = outlined ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent;
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.FrameworkElement BuildPanelInfoRow(string title, string detail)
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Margin = new System.Windows.Thickness(0, 8, 0, 8)
        };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(620),
            Foreground = Ink,
            TextWrapping = System.Windows.TextWrapping.Wrap
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detail,
            FontSize = 17,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 6, 0, 0),
            TextWrapping = System.Windows.TextWrapping.Wrap
        });
        return stack;
    }

    private void ApplyMode(RecordingPanelMode mode)
    {
        _currentMode = mode;
        _allowTargetSelection = AllowsTargetSelection(_supportsTargetSelection, mode);
        SetSessionModeChooserVisibility(false, resumeMode: false);
        SetRecordingControlsLocked(false);
        if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
        if (_moreMenu is not null) _moreMenu.IsOpen = false;
        switch (mode)
        {
            case RecordingPanelMode.PreStart:
                if (_startButton is not null) _startButton.Visibility = System.Windows.Visibility.Visible;
                if (_stopButton is not null) _stopButton.Visibility = System.Windows.Visibility.Visible;
                if (_resumePrimaryButton is not null) _resumePrimaryButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_activeStatusBlock is not null) _activeStatusBlock.Text = "Recording in Progress...";
                if (_activeDetailBlock is not null) _activeDetailBlock.Text = "Capturing actions and building your map";
                if (_doubleButton is not null) _doubleButton.Visibility = System.Windows.Visibility.Visible;
                if (_pauseButton is not null) _pauseButton.Visibility = System.Windows.Visibility.Visible;
                if (_resumeRecordingButton is not null) _resumeRecordingButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_skipAutoButton is not null) _skipAutoButton.Visibility = System.Windows.Visibility.Collapsed;
                ResetElapsedTimer();
                if (_currentView == RecorderView.Windows)
                    RefreshWindowList();
                else if (_currentView == RecorderView.Maps)
                    RefreshMapList();
                else
                    RefreshLibraryPanel();
                UpdateIdleContentVisibility();
                RefreshPreStartTargetState();
                break;

            case RecordingPanelMode.Active:
                if (_activeStatusBlock is not null) _activeStatusBlock.Text = "Ready for next click";
                if (_activeDetailBlock is not null) _activeDetailBlock.Text = "Single-click capture is armed and waiting for your next action.";
                if (_stopButton is not null) _stopButton.Visibility = System.Windows.Visibility.Visible;
                if (_resumePrimaryButton is not null) _resumePrimaryButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_pauseButton is not null) _pauseButton.Visibility = System.Windows.Visibility.Visible;
                if (_resumeRecordingButton is not null) _resumeRecordingButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_doubleButton is not null) _doubleButton.Visibility = System.Windows.Visibility.Visible;
                SetInlineState(_doubleButton, selected: false);
                SetInlineState(_skipAutoButton, selected: false);
                StartElapsedTimer();
                ApplyAutoPassState();
                ApplyActiveStatusVisuals("Recording is active. The next single click is armed automatically.", StatusTone.Accent);
                UpdateCaption("RECORDING MAP", StatusTone.Accent, "Single clicks are captured automatically. Use Pause to hold this session or Stop to finish the map.");
                break;

            case RecordingPanelMode.Paused:
                if (_activeStatusBlock is not null) _activeStatusBlock.Text = "Recording Paused";
                if (_activeDetailBlock is not null) _activeDetailBlock.Text = "Recording is temporarily paused";
                if (_stopButton is not null) _stopButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_resumePrimaryButton is not null) _resumePrimaryButton.Visibility = System.Windows.Visibility.Visible;
                if (_pauseButton is not null) _pauseButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_resumeRecordingButton is not null) _resumeRecordingButton.Visibility = System.Windows.Visibility.Visible;
                if (_doubleButton is not null) _doubleButton.Visibility = System.Windows.Visibility.Collapsed;
                if (_skipAutoButton is not null) _skipAutoButton.Visibility = System.Windows.Visibility.Collapsed;
                SetInlineState(_doubleButton, selected: false);
                SetInlineState(_skipAutoButton, selected: false);
                PauseElapsedTimer();
                ApplyActiveStatusVisuals("Recording paused. Resume to continue or Finish to build the map.", StatusTone.Accent);
                UpdateCaption("RECORDING PAUSED", StatusTone.Accent, "Press Play to continue this session, or Finish to build the map.");
                break;

            case RecordingPanelMode.MapReady:
                if (_startButton is not null) _startButton.Visibility = System.Windows.Visibility.Visible;
                if (_stopButton is not null) _stopButton.Visibility = System.Windows.Visibility.Visible;
                if (_resumePrimaryButton is not null) _resumePrimaryButton.Visibility = System.Windows.Visibility.Collapsed;
                ResetElapsedTimer();
                if (_doubleButton is not null) _doubleButton.Visibility = System.Windows.Visibility.Visible;
                if (_pauseButton is not null) _pauseButton.Visibility = System.Windows.Visibility.Visible;
                if (_resumeRecordingButton is not null) _resumeRecordingButton.Visibility = System.Windows.Visibility.Collapsed;
                _currentView = RecorderView.Maps;
                RefreshMapList();
                RefreshLibraryPanel();
                UpdateIdleContentVisibility();
                RefreshPreStartTargetState();
                UpdateCaption("MAP READY", StatusTone.Success, "The map is ready. Use Maps to open it or Export to publish it.");
                break;
        }

        RefreshCompactShellState();
        UpdateShellVisibility();
    }

    internal static bool AllowsTargetSelection(bool supported, RecordingPanelMode mode) =>
        supported && mode is RecordingPanelMode.PreStart or RecordingPanelMode.MapReady;

    private void ApplyVisualStatus(string message, StatusTone tone)
    {
        switch (_currentMode)
        {
            case RecordingPanelMode.PreStart:
                UpdateCaption(PreStartCaption(message), tone, message);
                if (GetSelectedTarget() is { } selectedTarget)
                    UpdateIdleStatus("Start Recording", FormatWindowSummary(selectedTarget), tone == StatusTone.Danger ? StatusTone.Danger : StatusTone.Accent);
                else
                    UpdateIdleStatus("Start Recording", "Select a window to get started", tone);
                break;

            case RecordingPanelMode.Active:
            case RecordingPanelMode.Paused:
                var isFinalizingMap = IsFinalizingMapStatus(message);
                SetRecordingControlsLocked(isFinalizingMap);
                if (isFinalizingMap)
                    PauseElapsedTimer();
                UpdateCaption(ActiveCaption(message), tone, message);
                if (_activeStatusBlock is not null) _activeStatusBlock.Text = ActiveBarText(message);
                if (_activeDetailBlock is not null) _activeDetailBlock.Text = ActiveDetailText(message);
                ApplyActiveStatusVisuals(message, tone);
                var lower = message.ToLowerInvariant();
                SetInlineState(_doubleButton, lower.Contains("double-click", StringComparison.Ordinal) || lower.Contains("double click", StringComparison.Ordinal));
                SetInlineState(_skipAutoButton,
                    lower.Contains("skip auto", StringComparison.Ordinal) ||
                    lower.Contains("skipping auto", StringComparison.Ordinal) ||
                    lower.Contains("continue auto", StringComparison.Ordinal) ||
                    lower.Contains("continuing automatic", StringComparison.Ordinal));
                SetInlineState(_compactDoubleButton, lower.Contains("double-click", StringComparison.Ordinal) || lower.Contains("double click", StringComparison.Ordinal));
                SetInlineState(_compactSkipAutoButton, lower.Contains("skip auto", StringComparison.Ordinal) || lower.Contains("skipping auto", StringComparison.Ordinal));
                ApplyAutoPassState();
                break;

            case RecordingPanelMode.MapReady:
                UpdateCaption(MapReadyCaption(message), tone, message);
                if (message.StartsWith("Map ready:", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateIdleStatus("Map Ready", message, tone);
                }
                else if (message.Contains("partial map", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("resume to continue", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("capture limit", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("opened", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("export", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("fail", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateIdleStatus("Start Recording", message, tone);
                }
                else
                {
                    RefreshPreStartTargetState();
                }
                break;
        }
    }

    private void QueueCommand(string command, string statusMessage, StatusTone tone)
    {
        if (_recordingControlsLocked)
            return;

        if (command == CancelRecordingCommand)
            CancelRequested?.Invoke();

        switch (command)
        {
            case "P":
                PauseRequested?.Invoke();
                if (_autoPassActive)
                    AutoPassStopRequested?.Invoke();
                ApplyMode(RecordingPanelMode.Paused);
                statusMessage = "Recording paused. Resume to continue or Finish to build the map.";
                break;

            case "R":
                ApplyMode(RecordingPanelMode.Active);
                break;
        }

        _commands.Writer.TryWrite(command);
        ApplyVisualStatus(statusMessage, tone);
    }

    private void QueueAutoPassToggleCommand()
    {
        if (_autoPassActive)
            RequestAutoPassStop();
        else
            QueueCommand("CONTINUE_AUTO", "Continuing automatic Ribbon tab capture...", StatusTone.Accent);
    }

    private void RequestAutoPassStop()
    {
        if (_recordingControlsLocked)
            return;

        AutoPassStopRequested?.Invoke();
        ApplyVisualStatus("Stopping auto now. Manual capture will start when the active click has ended.", StatusTone.Accent);
    }

    private void QueueSessionModeCommand(bool autoTabs)
    {
        if (_recordingControlsLocked)
            return;

        if (_currentMode == RecordingPanelMode.PreStart && !HasSelectedTarget())
        {
            PromptTargetSelection();
            return;
        }

        var resumeMode = _sessionModeChooserResumeMode;
        SetSessionModeChooserVisibility(false, resumeMode: false);
        var command = Program.SessionModeLaunchCommand(resumeMode, autoTabs);
        var status = resumeMode
            ? autoTabs ? "Scanning the current screen, then resuming Auto labels..." : "Scanning the current screen, then resuming Manual..."
            : autoTabs ? "Scanning the current screen, then starting Auto labels..." : "Scanning the current screen, then starting Manual...";
        _commands.Writer.TryWrite(command);
        ApplyVisualStatus(status, StatusTone.Accent);
    }

    private void QueueQuickMapCommand()
    {
        if (_recordingControlsLocked)
            return;

        if (_currentMode == RecordingPanelMode.PreStart && !HasSelectedTarget())
        {
            PromptTargetSelection();
            return;
        }

        var resumeMode = _sessionModeChooserResumeMode;
        if (!resumeMode || _currentMode != RecordingPanelMode.MapReady)
            return;
        SetSessionModeChooserVisibility(false, resumeMode: false);
        _commands.Writer.TryWrite(Program.RescanLaunchCommand(resumeMode));
        ApplyVisualStatus("Rescanning the current screen...", StatusTone.Accent);
    }

    private void OpenSessionModeChooserForCurrentState()
    {
        if (_recordingControlsLocked)
            return;
        ShowSessionModeChooser(resumeMode: false);
    }

    private static bool UseManualStateBadges(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("capture limit", StringComparison.Ordinal) ||
            lower.Contains("auto tabs", StringComparison.Ordinal) ||
            lower.Contains("auto-mapping", StringComparison.Ordinal) ||
            lower.Contains("auto pass", StringComparison.Ordinal) ||
            lower.Contains("top-level tabs", StringComparison.Ordinal) ||
            lower.Contains("capturing the final frame", StringComparison.Ordinal) ||
            lower.Contains("finalizing the recording bundle", StringComparison.Ordinal) ||
            lower.Contains("finalizing the new recording bundle", StringComparison.Ordinal) ||
            lower.Contains("finalizing the partial recording bundle", StringComparison.Ordinal) ||
            lower.Contains("merging this recording into the current map", StringComparison.Ordinal) ||
            lower.Contains("saving the updated map", StringComparison.Ordinal) ||
            lower.Contains("finishing", StringComparison.Ordinal) ||
            lower.Contains("building", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool IsFinalizingMapStatus(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("finishing recording and building the map", StringComparison.Ordinal) ||
               lower.Contains("capturing the final frame", StringComparison.Ordinal) ||
               lower.Contains("finalizing the recording bundle", StringComparison.Ordinal) ||
               lower.Contains("finalizing the new recording bundle", StringComparison.Ordinal) ||
               lower.Contains("finalizing the partial recording bundle", StringComparison.Ordinal) ||
               lower.Contains("merging this recording into the current map", StringComparison.Ordinal) ||
               lower.Contains("saving the updated map", StringComparison.Ordinal);
    }

    private void ConfigureActiveStageBadge(string stageKey)
    {
        if (_activeStageBadgeLabel is null)
            return;

        _activeStageBadgeLabel.Text = stageKey switch
        {
            "auto" => "Finding controls",
            "verify" => "Verifying controls",
            "build" => "Building map",
            "save" => "Saving map",
            _ => "Saving screen"
        };
    }

    private static string ResolveActiveStageKey(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("stage 3 of 5", StringComparison.Ordinal) ||
            lower.Contains("verifying", StringComparison.Ordinal) ||
            lower.Contains("waiting for the screen to update", StringComparison.Ordinal) ||
            lower.Contains("waiting for the interface", StringComparison.Ordinal))
            return "verify";
        if (lower.Contains("stage 2 of 5", StringComparison.Ordinal) ||
            lower.Contains("mapping controls", StringComparison.Ordinal) ||
            lower.Contains("scanning visible controls", StringComparison.Ordinal) ||
            lower.Contains("scanning the app", StringComparison.Ordinal))
            return "auto";
        if (lower.Contains("stage 1 of 5", StringComparison.Ordinal))
            return "capture";
        if (UseManualStateBadges(message))
        {
            if (lower.Contains("paused", StringComparison.Ordinal))
                return "save";
            if (lower.Contains("double-click", StringComparison.Ordinal) || lower.Contains("double click", StringComparison.Ordinal))
                return "build";
            if (lower.Contains("saving the action", StringComparison.Ordinal) ||
                lower.Contains("saving the updated screen", StringComparison.Ordinal) ||
                lower.Contains("capturing actions", StringComparison.Ordinal))
                return "auto";
            if (lower.Contains("capturing", StringComparison.Ordinal) || lower.Contains("settle", StringComparison.Ordinal))
                return "auto";
            return "capture";
        }

        if (lower.Contains("capture limit", StringComparison.Ordinal))
            return "save";
        if (lower.Contains("saving the updated map", StringComparison.Ordinal) || lower.Contains("merging this recording into the current map", StringComparison.Ordinal))
            return "save";
        if (lower.Contains("capturing the final frame", StringComparison.Ordinal) ||
            lower.Contains("finalizing the recording bundle", StringComparison.Ordinal) ||
            lower.Contains("finalizing the new recording bundle", StringComparison.Ordinal) ||
            lower.Contains("finalizing the partial recording bundle", StringComparison.Ordinal) ||
            lower.Contains("finishing", StringComparison.Ordinal) ||
            lower.Contains("building", StringComparison.Ordinal))
            return "build";
        if (lower.Contains("auto tabs", StringComparison.Ordinal) ||
            lower.Contains("auto-mapping", StringComparison.Ordinal) ||
            lower.Contains("auto pass", StringComparison.Ordinal) ||
            lower.Contains("top-level tabs", StringComparison.Ordinal) ||
            lower.Contains("skip auto", StringComparison.Ordinal))
            return "auto";
        if (lower.Contains("waiting for the interface", StringComparison.Ordinal) || lower.Contains("capturing actions", StringComparison.Ordinal))
            return "auto";
        return "capture";
    }

    private static System.Windows.Media.Brush ResolveActiveStageBrush(string stageKey) =>
        stageKey switch
        {
            "auto" => AutoStage,
            "verify" => Accent,
            "build" => BuildStage,
            "save" => SaveStage,
            _ => Accent
        };

    private static System.Windows.Media.Brush ResolveActiveStageSecondaryBrush(string stageKey) =>
        stageKey switch
        {
            "auto" => AutoStageMuted,
            "verify" => Muted,
            "build" => BuildStageMuted,
            "save" => SaveStageMuted,
            _ => Muted
        };

    private static System.Windows.Media.Color ResolveActiveStageOuterColor(string stageKey) =>
        stageKey switch
        {
            "auto" => System.Windows.Media.Color.FromArgb(28, 45, 190, 145),
            "verify" => System.Windows.Media.Color.FromArgb(28, 10, 132, 255),
            "build" => System.Windows.Media.Color.FromArgb(28, 123, 97, 255),
            "save" => System.Windows.Media.Color.FromArgb(28, 45, 190, 145),
            _ => System.Windows.Media.Color.FromArgb(28, 10, 132, 255)
        };

    private static System.Windows.Media.Color ResolveActiveStageBackgroundColor(string stageKey) =>
        stageKey switch
        {
            "auto" => System.Windows.Media.Color.FromArgb(232, 45, 190, 145),
            "verify" => System.Windows.Media.Color.FromArgb(232, 10, 132, 255),
            "build" => System.Windows.Media.Color.FromArgb(228, 123, 97, 255),
            "save" => System.Windows.Media.Color.FromArgb(232, 45, 190, 145),
            _ => System.Windows.Media.Color.FromArgb(232, 10, 132, 255)
        };

    private static System.Windows.Media.Color ResolveActiveStageBorderColor(string stageKey) =>
        stageKey switch
        {
            "auto" => System.Windows.Media.Color.FromArgb(255, 26, 165, 122),
            "verify" => System.Windows.Media.Color.FromArgb(255, 0, 110, 230),
            "build" => System.Windows.Media.Color.FromArgb(255, 95, 73, 234),
            "save" => System.Windows.Media.Color.FromArgb(255, 26, 165, 122),
            _ => System.Windows.Media.Color.FromArgb(255, 0, 110, 230)
        };

    private void ApplyActiveStageBadge(string activeStageKey, bool error = false)
    {
        if (_activeStageBadge is null || _activeStageBadgeLabel is null)
            return;

        ConfigureActiveStageBadge(activeStageKey);
        _activeStageBadgeLabel.Foreground = System.Windows.Media.Brushes.White;
        _activeStageBadge.Background = new System.Windows.Media.SolidColorBrush(
            error
                ? System.Windows.Media.Color.FromArgb(236, 255, 59, 48)
                : ResolveActiveStageBackgroundColor(activeStageKey));
        _activeStageBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
            error
                ? System.Windows.Media.Color.FromArgb(255, 220, 46, 38)
                : ResolveActiveStageBorderColor(activeStageKey));
    }

    private void ApplyActiveStatusVisuals(string message, StatusTone tone)
    {
        if (_activeStatusBlock is null || _activeDetailBlock is null || _activeStatusDotOuter is null || _activeStatusDotInner is null)
            return;

        var lower = message.ToLowerInvariant();
        var stageKey = ResolveActiveStageKey(message);
        var activeBarText = ActiveBarText(message);
        System.Windows.Media.Brush primary;
        System.Windows.Media.Color outerColor;
        System.Windows.Media.Brush dotColor;
        if (tone == StatusTone.Danger)
        {
            primary = Danger;
            outerColor = System.Windows.Media.Color.FromArgb(26, 255, 59, 48);
            dotColor = Danger;
        }
        else if (string.Equals(activeBarText, "Ready for next click", StringComparison.Ordinal))
        {
            primary = Accent;
            outerColor = ResolveActiveStageOuterColor("capture");
            dotColor = Accent;
        }
        else if (string.Equals(activeBarText, "Recording in Progress...", StringComparison.Ordinal))
        {
            primary = AutoStage;
            outerColor = ResolveActiveStageOuterColor("auto");
            dotColor = AutoStage;
        }
        else if (lower.Contains("paused", StringComparison.Ordinal))
        {
            primary = Muted;
            outerColor = System.Windows.Media.Color.FromArgb(22, 127, 136, 150);
            dotColor = Muted;
        }
        else
        {
            primary = ResolveActiveStageBrush(stageKey);
            outerColor = ResolveActiveStageOuterColor(stageKey);
            dotColor = ResolveActiveStageBrush(stageKey);
        }

        _activeStatusBlock.Foreground = primary;
        _activeDetailBlock.Foreground = primary;
        _activeStatusDotOuter.Fill = new System.Windows.Media.SolidColorBrush(outerColor);
        _activeStatusDotInner.Fill = dotColor;
        ApplyActiveStageBadge(stageKey, tone == StatusTone.Danger);
    }

    private void SetSessionModeChooserVisibility(bool visible, bool resumeMode)
    {
        if (visible && _isCompactCollapsed)
            SetCompactMode(false);

        if (visible && _currentMode == RecordingPanelMode.PreStart && !HasSelectedTarget())
        {
            SetCurrentView(RecorderView.Windows);
            ApplyVisualStatus("Choose a window before you start recording.", StatusTone.Neutral);
            return;
        }

        var effectiveResumeMode = visible && resumeMode;
        _sessionModeChooserOpen = visible;
        _sessionModeChooserResumeMode = effectiveResumeMode;
        if (_targetMenu is not null) _targetMenu.IsOpen = false;
        if (_moreMenu is not null) _moreMenu.IsOpen = false;
        if (_sessionModeShell is not null)
            _sessionModeShell.Visibility = visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        SetPrimaryCircleState(_startButton, visible);
        SetPrimaryCircleState(_compactStartButton, false);
        if (_quickMapModeButton?.Content is System.Windows.Controls.TextBlock quickLabel)
            quickLabel.Text = "Rescan current screen";
        if (_quickMapModeButton is not null)
            _quickMapModeButton.Visibility = ShouldShowRescanAction(effectiveResumeMode)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        UpdateIdleContentVisibility();

        if (!visible)
        {
            if (_currentMode == RecordingPanelMode.PreStart)
                RefreshPreStartTargetState();
            else if (_currentMode == RecordingPanelMode.MapReady)
                UpdateCaption("MAP READY", StatusTone.Success, "The map is ready. Use Maps to open it or Export to publish it.");
            return;
        }

        UpdateCaption(
            effectiveResumeMode ? "CHOOSE RESUME MODE" : "CHOOSE RECORD MODE",
            StatusTone.Accent,
            effectiveResumeMode
                ? "Continue manually or automatically, or rescan only the current screen."
                : "Both modes begin with one quick scan of the current screen.");
    }

    internal static bool ShouldShowRescanAction(bool resumeMode) => resumeMode;

    private void ApplyAutoPassState()
    {
        if (_currentMode == RecordingPanelMode.Paused)
        {
            if (_stopButton is not null)
                _stopButton.Visibility = System.Windows.Visibility.Collapsed;
            if (_resumePrimaryButton is not null)
                _resumePrimaryButton.Visibility = System.Windows.Visibility.Visible;
            if (_pauseButton is not null)
                _pauseButton.Visibility = System.Windows.Visibility.Collapsed;
            if (_resumeRecordingButton is not null)
                _resumeRecordingButton.Visibility = System.Windows.Visibility.Visible;
            if (_doubleButton is not null)
                _doubleButton.Visibility = System.Windows.Visibility.Collapsed;
            if (_skipAutoButton is not null)
                _skipAutoButton.Visibility = System.Windows.Visibility.Collapsed;
            RefreshCompactShellState();
            return;
        }

        if (_stopButton is not null)
            _stopButton.Visibility = System.Windows.Visibility.Visible;
        if (_resumePrimaryButton is not null)
            _resumePrimaryButton.Visibility = System.Windows.Visibility.Collapsed;
        if (_pauseButton is not null)
            _pauseButton.Visibility = System.Windows.Visibility.Visible;
        if (_resumeRecordingButton is not null)
            _resumeRecordingButton.Visibility = System.Windows.Visibility.Collapsed;
        if (_doubleButton is not null)
            _doubleButton.Visibility = _autoPassActive ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        if (_skipAutoButton is not null)
        {
            _skipAutoButton.Visibility = System.Windows.Visibility.Visible;
            SetSecondaryTextButtonLabel(_skipAutoButton, _autoPassActive ? "Skip Auto" : "Continue Auto");
            _skipAutoButton.ToolTip = _autoPassActive
                ? "Stop the automatic tab pass and return to manual capture."
                : "Continue automatic capture of Ribbon tabs that have not been recorded yet.";
        }
        RefreshCompactShellState();
    }

    private void RefreshPreStartTargetState()
    {
        var selectedTarget = GetSelectedTarget();
        var hasTarget = selectedTarget is not null;
        if (_currentView == RecorderView.Windows)
            RefreshWindowList();

        if (_targetButton is not null)
        {
            _targetButton.ToolTip = selectedTarget is not null
                ? $"Selected window: {FormatWindowSummary(selectedTarget)}"
                : hasTarget
                    ? "A window is selected. Start recording when you are ready."
                : "Choose which application window to record.";
        }

        if (_startButton is not null)
        {
            _startButton.ToolTip = hasTarget
                ? "Open the recording mode chooser. Shortcut: S or Enter."
                : "Choose a window first to enable Start.";
        }
        if (_compactStartButton is not null)
        {
            _compactStartButton.ToolTip = hasTarget
                ? "Expand the panel and choose how to start recording."
                : "Choose a window first to enable Start.";
        }
        SetStartAvailability(_startButton, enabled: hasTarget);
        SetStartAvailability(_compactStartButton, enabled: hasTarget);
        UpdateIdleContentVisibility();

        if (selectedTarget is not null)
            UpdateIdleStatus("Start Recording", FormatWindowSummary(selectedTarget!), StatusTone.Accent);
        else
            UpdateIdleStatus("Start Recording", "Select a window to get started", StatusTone.Neutral);
    }

    private bool HasSelectedTarget()
    {
        return GetSelectedTarget() is not null;
    }

    private static string FormatWindowSummary(WindowTarget target)
    {
        var title = string.IsNullOrWhiteSpace(target.Title) ? "(Untitled window)" : target.Title;
        return $"{target.ProcessName} — {title}";
    }

    private void UpdateCaption(string text, StatusTone tone, string details)
    {
        if (_captionBlock is not null) _captionBlock.Text = text;
        if (_captionShell is not null)
        {
            _captionShell.Background = tone switch
            {
                StatusTone.Danger => CaptionDangerBackground,
                StatusTone.Accent => CaptionAccentBackground,
                StatusTone.Success => CaptionAccentBackground,
                _ => CaptionBackground
            };
            if (_captionBlock is not null)
            {
                _captionBlock.Foreground = tone switch
                {
                    StatusTone.Danger => Danger,
                    _ => CaptionForeground
                };
            }
            _captionShell.ToolTip = $"{details}\nTarget: {_processName}\nRecording: {_recordingId}";
        }
    }

    private void UpdateIdleStatus(string title, string details, StatusTone tone)
    {
        if (_idleStatusBlock is not null)
            _idleStatusBlock.Text = title;
        if (_idleDetailBlock is not null)
        {
            _idleDetailBlock.Text = details;
            _idleDetailBlock.ToolTip = details;
            _idleDetailBlock.SetValue(System.Windows.Controls.ToolTipService.InitialShowDelayProperty, 150);
            _idleDetailBlock.SetValue(System.Windows.Controls.ToolTipService.ShowDurationProperty, 30000);
        }
        if (_idleStatusHost is not null)
            _idleStatusHost.ToolTip = details;
        if (_idleStatusDot is not null)
        {
            _idleStatusDot.Fill = tone switch
            {
                StatusTone.Success => Brush("#2DBE91"),
                StatusTone.Danger => Danger,
                _ => Accent
            };
        }
    }

    private void UpdateElapsed()
    {
        if (_elapsedBlock is null) return;
        var elapsed = _elapsedAccumulated;
        if (_currentMode == RecordingPanelMode.Active)
            elapsed += DateTimeOffset.UtcNow - _elapsedSegmentStartedUtc;
        _elapsedBlock.Text = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private void StartElapsedTimer()
    {
        _elapsedSegmentStartedUtc = DateTimeOffset.UtcNow;
        UpdateElapsed();
        _elapsedTimer?.Start();
    }

    private void PauseElapsedTimer()
    {
        if (_elapsedSegmentStartedUtc != default)
            _elapsedAccumulated += DateTimeOffset.UtcNow - _elapsedSegmentStartedUtc;
        _elapsedSegmentStartedUtc = default;
        _elapsedTimer?.Stop();
        UpdateElapsed();
    }

    private void ResetElapsedTimer()
    {
        _elapsedAccumulated = TimeSpan.Zero;
        _elapsedSegmentStartedUtc = default;
        _elapsedTimer?.Stop();
        if (_elapsedBlock is not null) _elapsedBlock.Text = "00:00:00";
    }

    private static void SetStartAvailability(System.Windows.Controls.Button? button, bool enabled)
    {
        if (button is null) return;
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1.0 : 0.38;
    }

    private void RefreshCompactShellState()
    {
        var isRecording = _currentMode is RecordingPanelMode.Active or RecordingPanelMode.Paused;
        var isPaused = _currentMode == RecordingPanelMode.Paused;
        var showSkipAuto = _currentMode == RecordingPanelMode.Active && _autoPassActive;

        if (_compactStartButton is not null)
            _compactStartButton.Visibility = isRecording ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        if (_compactStopButton is not null)
            _compactStopButton.Visibility = isRecording && !isPaused ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_compactResumePrimaryButton is not null)
            _compactResumePrimaryButton.Visibility = isPaused ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (_compactDoubleButton is not null)
        {
            _compactDoubleButton.Visibility = showSkipAuto || isPaused ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            _compactDoubleButton.IsEnabled = isRecording && !showSkipAuto;
            if (!isRecording || showSkipAuto || isPaused)
                SetInlineState(_compactDoubleButton, selected: false);
        }

        if (_compactSkipAutoButton is not null)
        {
            _compactSkipAutoButton.Visibility = showSkipAuto ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            _compactSkipAutoButton.IsEnabled = showSkipAuto;
            if (!showSkipAuto)
                SetInlineState(_compactSkipAutoButton, selected: false);
        }

        if (_compactPauseButton is not null)
        {
            _compactPauseButton.Visibility = isPaused ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            _compactPauseButton.IsEnabled = isRecording && !isPaused;
        }

        if (_compactResumeButton is not null)
        {
            _compactResumeButton.Visibility = isPaused ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            _compactResumeButton.IsEnabled = isPaused;
        }
    }

    private void ToggleCompactMode() => SetCompactMode(!_isCompactCollapsed);

    private void SetCompactMode(bool collapsed)
    {
        if (_isCompactCollapsed == collapsed)
            return;

        _isCompactCollapsed = collapsed;
        RefreshCompactShellState();
        UpdateShellVisibility();
        _window?.UpdateLayout();
    }

    private void UpdateShellVisibility()
    {
        var showActiveShell = !_isCompactCollapsed &&
            (_currentMode == RecordingPanelMode.Active || _currentMode == RecordingPanelMode.Paused);
        var showIdleShell = !_isCompactCollapsed && !showActiveShell;

        if (_idleShell is not null)
            _idleShell.Visibility = showIdleShell ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_activeShell is not null)
            _activeShell.Visibility = showActiveShell ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (_compactShell is not null)
            _compactShell.Visibility = _isCompactCollapsed ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private static void SetTileState(System.Windows.Controls.Button? button, bool enabled, bool selected)
    {
        if (button is null) return;
        button.IsEnabled = enabled;
        button.Tag = selected;
        ApplyTileChrome(button, selected, isHovered: false);
        button.Opacity = enabled || (button.Uid == "bottom-nav" && selected)
            ? 1.0
            : button.Uid == "bottom-nav" ? 0.62 : 0.34;
        if (button.Content is System.Windows.FrameworkElement { Tag: TileVisualState tileVisual })
            tileVisual.Apply(enabled, selected);
        else if (button.Content is System.Windows.FrameworkElement { Tag: BottomNavVisualState bottomNavVisual })
            bottomNavVisual.Apply(enabled, selected);
    }

    private static void SetInlineState(System.Windows.Controls.Button? button, bool selected)
    {
        if (button is null) return;
        if (string.Equals(button.Tag as string, "ui-atlas-active-action", StringComparison.Ordinal))
        {
            button.Background = selected ? Brush("#0077ED") : Accent;
            button.BorderBrush = button.Background;
            if (button.Content is System.Windows.Controls.TextBlock activeText)
                activeText.Foreground = System.Windows.Media.Brushes.White;
            return;
        }
        if (string.Equals(button.Tag as string, "ui-atlas-secondary-outline", StringComparison.Ordinal))
        {
            button.Background = selected ? Brush("#EAF4FF") : System.Windows.Media.Brushes.White;
            button.BorderBrush = Accent;
            if (button.Content is System.Windows.Controls.TextBlock text)
                text.Foreground = selected ? Brush("#0077ED") : Accent;
            return;
        }
        if (button.Content is System.Windows.FrameworkElement { Tag: CircleActionVisualState circleVisual })
        {
            circleVisual.Apply(button.IsEnabled, selected);
            return;
        }

        button.Background = selected ? SelectedTileBackground : System.Windows.Media.Brushes.Transparent;
        button.BorderBrush = selected ? SelectedTileBorder : System.Windows.Media.Brushes.Transparent;
    }

    private static void SetPrimaryCircleState(System.Windows.Controls.Button? button, bool selected)
    {
        if (button?.Content is not System.Windows.Controls.Border { Tag: PrimaryCircleVisualState visual })
            return;

        visual.Apply(button.IsEnabled, selected, hovered: false);
    }

    private void SetRecordingControlsLocked(bool locked)
    {
        if (_recordingControlsLocked == locked)
            return;

        _recordingControlsLocked = locked;
        ApplyRecordingControlsLock(locked);
    }

    private void ApplyRecordingControlsLock(bool locked)
    {

        if (_activeShell is not null)
            _activeShell.IsHitTestVisible = !locked;
        if (_compactShell is not null)
            _compactShell.IsHitTestVisible = !locked;

        foreach (var button in new[]
                 {
                     _stopButton,
                     _resumePrimaryButton,
                     _pauseButton,
                     _resumeRecordingButton,
                     _doubleButton,
                     _skipAutoButton,
                     _cancelRecordingButton,
                     _compactResumePrimaryButton,
                     _compactPauseButton,
                     _compactResumeButton,
                     _compactDoubleButton,
                     _compactSkipAutoButton
                 })
        {
            if (button is not null)
                button.IsHitTestVisible = !locked;
        }
    }

    private void SetMapIndicator(bool visible) { }

    private void ClosePanel() => _window?.Close();

    private void MinimizeWindow()
    {
        if (_window is null)
            return;

        CloseIdleMenus();
        _window.WindowState = System.Windows.WindowState.Minimized;
    }

    private void CloseIdleMenus()
    {
        _pendingIdlePopupRequest = IdlePopupRequest.None;
        if (_targetMenu is not null) _targetMenu.IsOpen = false;
        if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
        if (_moreMenu is not null) _moreMenu.IsOpen = false;
    }

    private void HandlePanelDragMove(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_recordingControlsLocked)
        {
            e.Handled = true;
            return;
        }

        if (_window is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (HasInteractiveAncestor(e.OriginalSource as System.Windows.DependencyObject)) return;
        if ((_targetMenu?.IsOpen == true) || (_mapsMenu?.IsOpen == true) || (_moreMenu?.IsOpen == true))
        {
            CloseIdleMenus();
            e.Handled = true;
            return;
        }
        try
        {
            _window.DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool HasInteractiveAncestor(System.Windows.DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.Selector or
                System.Windows.Controls.MenuItem or
                System.Windows.Controls.Primitives.ScrollBar)
                return true;

            source = GetDependencyParent(source);
        }

        return false;
    }

    private static System.Windows.DependencyObject? GetDependencyParent(System.Windows.DependencyObject source) =>
        source switch
        {
            System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D => System.Windows.Media.VisualTreeHelper.GetParent(source),
            System.Windows.FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            System.Windows.ContentElement contentElement => System.Windows.ContentOperations.GetParent(contentElement),
            _ => System.Windows.LogicalTreeHelper.GetParent(source)
        };

    private void PositionWindow()
    {
        if (_window is null) return;
        _window.UpdateLayout();
        _window.Left = System.Windows.SystemParameters.WorkArea.Left +
            Math.Max(24, (System.Windows.SystemParameters.WorkArea.Width - _window.ActualWidth) / 2);
        _window.Top = System.Windows.SystemParameters.WorkArea.Top + 72;
    }

    private void UpdateWindowScale()
    {
        if (_window is null || _rootScaleTransform is null)
            return;

        var fitScaleX = (System.Windows.SystemParameters.WorkArea.Width - 48d) / RecorderFrameWidth;
        var fitScaleY = (System.Windows.SystemParameters.WorkArea.Height - 120d) / RecorderFrameHeight;
        var displayScale = Math.Max(0.5, Math.Min(RecorderCardDisplayScale, Math.Min(fitScaleX, fitScaleY)));
        _rootScaleTransform.ScaleX = displayScale;
        _rootScaleTransform.ScaleY = displayScale;
        _window.UpdateLayout();
    }

    private void ExcludeWindowFromCapture()
    {
        if (_window is null)
            return;

        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (handle != 0)
            _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    private static string PreStartCaption(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("choose", StringComparison.Ordinal) && lower.Contains("record", StringComparison.Ordinal)) return "CHOOSE RECORD MODE";
        if (lower.Contains("focus", StringComparison.Ordinal)) return "TARGET FOCUSED";
        if (lower.Contains("cancel", StringComparison.Ordinal)) return "CANCELLED";
        if (lower.Contains("start", StringComparison.Ordinal)) return "STARTING RECORDING";
        return "READY TO RECORD";
    }

    internal static string ActiveCaption(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("stage 1 of 5", StringComparison.Ordinal)) return "CAPTURING SCREEN";
        if (lower.Contains("stage 2 of 5", StringComparison.Ordinal) || lower.Contains("mapping controls", StringComparison.Ordinal) || lower.Contains("scanning the app", StringComparison.Ordinal)) return "SCANNING CONTROLS";
        if (lower.Contains("stage 3 of 5", StringComparison.Ordinal) || lower.Contains("verifying", StringComparison.Ordinal)) return "VERIFYING CONTROLS";
        if (lower.Contains("paused", StringComparison.Ordinal)) return "RECORDING PAUSED";
        if (lower.Contains("capture limit", StringComparison.Ordinal)) return "AUTO-LABELS LIMIT";
        if (lower.Contains("auto-mapping complete", StringComparison.Ordinal)) return "AUTO LABELS COMPLETE";
        if (lower.Contains("auto-mapping tabs", StringComparison.Ordinal) || lower.Contains("auto tabs is preparing", StringComparison.Ordinal)) return "AUTO LABELS";
        if (lower.Contains("auto-mapping ", StringComparison.Ordinal) && lower.Contains("controls", StringComparison.Ordinal)) return "AUTO LABELS";
        if (lower.Contains("auto pass skipped", StringComparison.Ordinal)) return "AUTO PASS SKIPPED";
        if (lower.Contains("focus left the target app", StringComparison.Ordinal) || lower.Contains("no eligible top-level tabs", StringComparison.Ordinal))
            return "AUTO PASS PAUSED";
        if (lower.Contains("capturing the final frame", StringComparison.Ordinal)) return "CAPTURING FINAL FRAME";
        if (lower.Contains("finalizing the recording bundle", StringComparison.Ordinal) || lower.Contains("finalizing the new recording bundle", StringComparison.Ordinal) || lower.Contains("finalizing the partial recording bundle", StringComparison.Ordinal))
            return "BUILDING MAP";
        if (lower.Contains("merging this recording into the current map", StringComparison.Ordinal)) return "MERGING MAP";
        if (lower.Contains("saving the updated map", StringComparison.Ordinal)) return "SAVING MAP";
        if (lower.Contains("finishing", StringComparison.Ordinal) || lower.Contains("building", StringComparison.Ordinal)) return "BUILDING MAP";
        if (lower.Contains("saving the action", StringComparison.Ordinal)) return "SAVING ACTION";
        if (lower.Contains("waiting for the screen to update", StringComparison.Ordinal)) return "WAITING FOR SCREEN";
        if (lower.Contains("saving the updated screen", StringComparison.Ordinal)) return "SAVING SCREEN";
        if (lower.Contains("double-click", StringComparison.Ordinal) || lower.Contains("double click", StringComparison.Ordinal)) return "DOUBLE CLICK ARMED";
        if (lower.Contains("resum", StringComparison.Ordinal)) return "RECORDING RESUMED";
        if (lower.Contains("waiting for the interface", StringComparison.Ordinal)) return "WAITING FOR THE INTERFACE";
        if (lower.Contains("capture complete", StringComparison.Ordinal) || lower.Contains("single-click", StringComparison.Ordinal)) return "READY FOR NEXT CLICK";
        if (lower.Contains("capturing actions", StringComparison.Ordinal)) return "CAPTURING ACTION";
        if (lower.Contains("capturing", StringComparison.Ordinal) || lower.Contains("settle", StringComparison.Ordinal)) return "CAPTURING ACTION";
        if (lower.Contains("focus it manually", StringComparison.Ordinal)) return "FOCUS TARGET APP";
        if (lower.Contains("activating", StringComparison.Ordinal)) return "FOCUSING TARGET";
        if (lower.Contains("cancel", StringComparison.Ordinal)) return "CANCELLING";
        return "RECORDING MAP";
    }

    internal static string ActiveBarText(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("stage 1 of 5", StringComparison.Ordinal))
            return "Capturing screen...";
        if (lower.Contains("stage 2 of 5", StringComparison.Ordinal) || lower.Contains("mapping controls", StringComparison.Ordinal) || lower.Contains("scanning the app", StringComparison.Ordinal))
            return "Scanning controls & tables...";
        if (lower.Contains("stage 3 of 5", StringComparison.Ordinal) || lower.Contains("verifying", StringComparison.Ordinal))
            return "Verifying discovered controls...";
        if (lower.Contains("paused", StringComparison.Ordinal))
            return "Recording Paused";
        if (lower.Contains("capture limit", StringComparison.Ordinal))
            return "Saving partial map...";
        if (lower.Contains("auto tabs is preparing", StringComparison.Ordinal))
            return "Scanning app tabs...";
        if (lower.Contains("auto-mapping tabs", StringComparison.Ordinal))
            return "Scanning app tabs...";
        if (lower.Contains("auto-mapping ", StringComparison.Ordinal) && lower.Contains("controls", StringComparison.Ordinal))
            return "Mapping controls...";
        if (lower.Contains("capturing the final frame", StringComparison.Ordinal))
            return "Capturing final frame...";
        if (lower.Contains("finalizing the recording bundle", StringComparison.Ordinal) || lower.Contains("finalizing the new recording bundle", StringComparison.Ordinal) || lower.Contains("finalizing the partial recording bundle", StringComparison.Ordinal))
            return "Building map...";
        if (lower.Contains("merging this recording into the current map", StringComparison.Ordinal))
            return "Merging map...";
        if (lower.Contains("saving the updated map", StringComparison.Ordinal))
            return "Saving map...";
        if (lower.Contains("saving the action", StringComparison.Ordinal) || lower.Contains("capturing actions", StringComparison.Ordinal))
            return "Saving action...";
        if (lower.Contains("waiting for the screen to update", StringComparison.Ordinal) || lower.Contains("waiting for the interface", StringComparison.Ordinal))
            return "Waiting for screen...";
        if (lower.Contains("saving the updated screen", StringComparison.Ordinal))
            return "Saving updated screen...";
        if (lower.Contains("auto-mapping complete", StringComparison.Ordinal))
            return "Auto labels complete";
        if (lower.Contains("auto pass skipped", StringComparison.Ordinal))
            return "Auto labels skipped";
        if (lower.Contains("focus left the target app", StringComparison.Ordinal))
            return "Auto labels paused";
        if (lower.Contains("no eligible top-level tabs", StringComparison.Ordinal))
            return "No tabs found";
        if (lower.Contains("skipped ", StringComparison.Ordinal) && lower.Contains("continuing auto tabs", StringComparison.Ordinal))
            return "Skipping one tab...";
        if (lower.Contains("double-click", StringComparison.Ordinal) || lower.Contains("double click", StringComparison.Ordinal))
            return "Double-click armed";
        if (lower.Contains("resum", StringComparison.Ordinal))
            return "Ready for next click";
        if (lower.Contains("ready for the next single click", StringComparison.Ordinal) ||
            lower.Contains("click recorded", StringComparison.Ordinal))
            return "Ready for next click";
        if (lower.Contains("capture complete", StringComparison.Ordinal) ||
            lower.Contains("single-click", StringComparison.Ordinal) ||
            lower.Contains("recording is active. the next single click is armed automatically.", StringComparison.Ordinal) ||
            lower.Contains("automatic single-click capture is active.", StringComparison.Ordinal))
            return "Ready for next click";
        if (lower.Contains("capturing", StringComparison.Ordinal) || lower.Contains("settle", StringComparison.Ordinal))
            return "Capturing action...";
        if (lower.Contains("focus it manually", StringComparison.Ordinal))
            return "Focus the target app.";
        if (lower.Contains("the target app is active. arm the next capture when ready.", StringComparison.Ordinal))
            return "Target focused";
        if (lower.Contains("activating", StringComparison.Ordinal))
            return "Focusing target...";
        if (lower.Contains("finishing", StringComparison.Ordinal) || lower.Contains("building", StringComparison.Ordinal))
            return "Building map...";
        if (lower.Contains("cancel", StringComparison.Ordinal))
            return "Cancelling...";
        return "Recording in Progress...";
    }

    internal static string ActiveDetailText(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("stage 1 of 5", StringComparison.Ordinal))
            return "Stage 1 of 5 · Saving the current screen before control discovery starts.";
        if (lower.Contains("stage 2 of 5", StringComparison.Ordinal) || lower.Contains("mapping controls", StringComparison.Ordinal) || lower.Contains("scanning the app", StringComparison.Ordinal))
            return "Stage 2 of 5 · Finding visible buttons, fields, and table cells. Complex applications can take several minutes.";
        if (lower.Contains("stage 3 of 5", StringComparison.Ordinal) || lower.Contains("verifying", StringComparison.Ordinal))
            return message.Replace("Stage 3 of 5:", "Stage 3 of 5 ·", StringComparison.OrdinalIgnoreCase);
        if (lower.Contains("capture limit", StringComparison.Ordinal))
            return "Auto labels reached the capture budget. Saving what was captured so far.";
        if (lower.Contains("auto tabs is preparing", StringComparison.Ordinal))
            return "Looking through the app tabs before labels are added.";
        if (lower.Contains("auto-mapping tabs", StringComparison.Ordinal))
            return "The recorder is moving through the app tabs automatically.";
        if (lower.Contains("auto-mapping ", StringComparison.Ordinal) && lower.Contains("controls", StringComparison.Ordinal))
            return "Controls inside the current tab are being added to the map.";
        if (lower.Contains("capturing the final frame", StringComparison.Ordinal))
            return "Saving the last screenshot before the map is built.";
        if (lower.Contains("finalizing the recording bundle", StringComparison.Ordinal) || lower.Contains("finalizing the new recording bundle", StringComparison.Ordinal) || lower.Contains("finalizing the partial recording bundle", StringComparison.Ordinal))
            return "Putting the recording together. Large maps can take a minute—keep UiAtlas open.";
        if (lower.Contains("merging this recording into the current map", StringComparison.Ordinal))
            return "Combining the recording with the map. Large maps can take a minute—keep UiAtlas open.";
        if (lower.Contains("saving the updated map", StringComparison.Ordinal))
            return "Writing the updated map to disk.";
        if (lower.Contains("saving the action", StringComparison.Ordinal) || lower.Contains("capturing actions", StringComparison.Ordinal))
            return "Saving the action and collecting the first snapshots.";
        if (lower.Contains("waiting for the screen to update", StringComparison.Ordinal) || lower.Contains("waiting for the interface", StringComparison.Ordinal))
            return "Action saved. Waiting for the screen to update.";
        if (lower.Contains("saving the updated screen", StringComparison.Ordinal))
            return "The screen looks ready. Saving the updated state now.";
        if (lower.Contains("auto-mapping complete", StringComparison.Ordinal))
            return "Automatic labels are done. Manual clicks are active again.";
        if (lower.Contains("auto pass skipped", StringComparison.Ordinal))
            return "Automatic labels were skipped. Manual clicks are active again.";
        if (lower.Contains("focus left the target app", StringComparison.Ordinal))
            return "The app moved out of focus, so manual capture is active again.";
        if (lower.Contains("no eligible top-level tabs", StringComparison.Ordinal))
            return "No usable top-level tabs were found, so manual capture is active again.";
        if (lower.Contains("capturing", StringComparison.Ordinal) || lower.Contains("settle", StringComparison.Ordinal))
            return "Saving the action and waiting for the screen to settle.";
        if (lower.Contains("capture complete", StringComparison.Ordinal) ||
            lower.Contains("single-click", StringComparison.Ordinal) ||
            lower.Contains("click recorded", StringComparison.Ordinal))
            return "Single-click capture is armed and waiting for your next action.";
        if (lower.Contains("double-click", StringComparison.Ordinal) || lower.Contains("double click", StringComparison.Ordinal))
            return "The next action will be captured as a double click.";
        if (lower.Contains("resum", StringComparison.Ordinal))
            return "Manual capture is active again.";
        if (lower.Contains("paused", StringComparison.Ordinal))
            return "Recording is temporarily paused";
        if (lower.Contains("recording is active. the next single click is armed automatically.", StringComparison.Ordinal) ||
            lower.Contains("automatic single-click capture is active.", StringComparison.Ordinal))
            return "Single-click capture is armed and waiting for your next action.";
        if (lower.Contains("the target app is active. arm the next capture when ready.", StringComparison.Ordinal))
            return "The app is focused. Continue when you are ready.";
        if (lower.Contains("focus it manually", StringComparison.Ordinal))
            return "Bring the target window forward, then continue.";
        if (lower.Contains("activating", StringComparison.Ordinal))
            return "Re-focusing the target window.";
        if (lower.Contains("finishing", StringComparison.Ordinal) || lower.Contains("building", StringComparison.Ordinal))
            return "Please wait while the map is being built.";
        if (lower.Contains("cancel", StringComparison.Ordinal))
            return "Stopping the current recording session.";
        return string.IsNullOrWhiteSpace(message)
            ? "Recording continues. Complex scans can take several minutes."
            : message;
    }

    private static string MapReadyCaption(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("partial map saved", StringComparison.Ordinal)) return "PARTIAL MAP SAVED";
        if (lower.Contains("focus failed", StringComparison.Ordinal)) return "FOCUS FAILED";
        if (lower.Contains("focused", StringComparison.Ordinal)) return "TARGET FOCUSED";
        if (lower.Contains("export failed", StringComparison.Ordinal)) return "EXPORT FAILED";
        if (lower.Contains("open map failed", StringComparison.Ordinal)) return "OPEN FAILED";
        if (lower.Contains("exported", StringComparison.Ordinal)) return "MAP EXPORTED";
        if (lower.Contains("opened", StringComparison.Ordinal)) return "MAP OPENED";
        return "MAP READY";
    }

    private static StatusTone ResolveTone(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("fail", StringComparison.Ordinal) || lower.Contains("cancel", StringComparison.Ordinal) || lower.Contains("focus left the target app", StringComparison.Ordinal))
            return StatusTone.Danger;
        if (lower.Contains("partial map saved", StringComparison.Ordinal))
            return StatusTone.Success;
        if (lower.Contains("opened", StringComparison.Ordinal) || lower.Contains("ready", StringComparison.Ordinal) || lower.Contains("exported", StringComparison.Ordinal) || lower.Contains("complete", StringComparison.Ordinal))
            return StatusTone.Success;
        if (lower.Contains("start", StringComparison.Ordinal) || lower.Contains("record", StringComparison.Ordinal) || lower.Contains("focus", StringComparison.Ordinal) || lower.Contains("map", StringComparison.Ordinal) || lower.Contains("export", StringComparison.Ordinal) || lower.Contains("skip", StringComparison.Ordinal))
            return StatusTone.Accent;
        return StatusTone.Neutral;
    }

    private static System.Windows.Controls.Border GlassShell(
        System.Windows.UIElement child,
        System.Windows.Thickness padding,
        double shadowOpacity,
        double shadowBlur,
        double width,
        double height,
        double cornerRadius)
    {
        return new System.Windows.Controls.Border
        {
            Width = width,
            Height = height,
            MinHeight = height,
            MaxWidth = Math.Max(360, System.Windows.SystemParameters.WorkArea.Width - ToolbarViewportInset),
            Background = ShellBackground,
            BorderBrush = ShellBorder,
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(cornerRadius),
            Padding = padding,
            Effect = Shadow(shadowOpacity, shadowBlur, 0),
            Child = child
        };
    }

    private static System.Windows.Controls.Border BuildDivider() =>
        new()
        {
            Width = 1,
            Height = DividerHeight,
            Background = Divider,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(0)
        };

    private static System.Windows.Controls.Border BuildToolSlot(System.Windows.UIElement content, bool showDivider)
    {
        var grid = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        if (showDivider)
        {
            grid.Children.Add(new System.Windows.Controls.Border
            {
                Width = 1,
                Height = DividerHeight,
                Background = Divider,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
        }

        if (content is System.Windows.FrameworkElement element)
        {
            element.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        }

        grid.Children.Add(content);
        return new System.Windows.Controls.Border { Child = grid };
    }

    private static void Place(System.Windows.Controls.Grid grid, System.Windows.UIElement element, int column)
    {
        System.Windows.Controls.Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static System.Windows.Controls.Button ModeTileButton(
        System.Windows.UIElement content,
        Action onClick,
        string toolTip)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Width = ToolButtonWidth,
            Height = ToolSlotHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(ToolCornerRadius))
        };
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1.0 : 0.34;
        button.MouseEnter += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: true);
        button.MouseLeave += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: false);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Button SourceSwitchButton(
        System.Windows.UIElement content,
        Action onClick,
        string toolTip)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(22))
        };
        button.Uid = "source-switch";
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1.0 : 0.34;
        button.MouseEnter += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: true);
        button.MouseLeave += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: false);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Button CloseUtilityButton(
        System.Windows.UIElement content,
        Action onClick,
        string toolTip)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(22))
        };
        button.Uid = "close-utility";
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1.0 : 0.34;
        button.MouseEnter += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: true);
        button.MouseLeave += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: false);
        button.Click += (_, _) => onClick();
        return button;
    }

    private System.Windows.Controls.Border BuildRecorderShell(
        System.Windows.UIElement child,
        double frameHeight,
        double designHeight,
        string toggleToolTip,
        double frameWidth = RecorderFrameWidth,
        bool compactToggleExpands = false)
    {
        var shellGrid = new System.Windows.Controls.Grid();
        shellGrid.Children.Add(child);

        var utilityRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new System.Windows.Thickness(
                0,
                WindowUtilityEdgeInset - WindowUtilityShellInset,
                WindowUtilityEdgeInset - WindowUtilityShellInset,
                0)
        };

        var toggleButton = WindowUtilityButton(
            compactToggleExpands ? CreateExpandPanelIcon : CreateCompactPanelIcon,
            ToggleCompactMode,
            toggleToolTip);
        toggleButton.Margin = new System.Windows.Thickness(0, 0, WindowUtilityButtonGap, 0);
        utilityRow.Children.Add(toggleButton);
        var minimizeButton = WindowUtilityButton(CreateMinimizeIcon, MinimizeWindow, "Minimize recorder to taskbar");
        minimizeButton.Margin = new System.Windows.Thickness(0, 0, WindowUtilityButtonGap, 0);
        utilityRow.Children.Add(minimizeButton);
        utilityRow.Children.Add(WindowUtilityButton(CreateCloseIcon, ClosePanel, "Close recorder"));
        System.Windows.Controls.Panel.SetZIndex(utilityRow, 12);
        shellGrid.Children.Add(utilityRow);

        return ReferenceCardShell(shellGrid, frameWidth, frameHeight, designHeight);
    }

    private static System.Windows.Controls.Button WindowUtilityButton(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        Action onClick,
        string toolTip)
    {
        var idleIconBrush = Muted;
        var hoverIconBrush = Accent;
        var iconHost = new System.Windows.Controls.Viewbox
        {
            Width = WindowUtilityIconSize,
            Height = WindowUtilityIconSize,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = iconFactory(idleIconBrush)
        };

        var bubble = new System.Windows.Controls.Border
        {
            Width = WindowUtilityButtonSize,
            Height = WindowUtilityButtonSize,
            CornerRadius = new System.Windows.CornerRadius(WindowUtilityButtonSize / 2),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 255, 255, 255)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 17, 24, 39)),
            BorderThickness = new System.Windows.Thickness(1),
            Child = iconHost
        };

        var button = new System.Windows.Controls.Button
        {
            Content = bubble,
            Width = WindowUtilityButtonSize,
            Height = WindowUtilityButtonSize,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(WindowUtilityButtonSize / 2))
        };
        button.MouseEnter += (_, _) =>
        {
            bubble.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(246, 255, 255, 255));
            bubble.BorderBrush = Accent;
            bubble.Effect = Shadow(0.08, 8, 0);
            iconHost.Child = iconFactory(hoverIconBrush);
        };
        button.MouseLeave += (_, _) =>
        {
            bubble.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 255, 255, 255));
            bubble.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 17, 24, 39));
            bubble.Effect = null;
            iconHost.Child = iconFactory(idleIconBrush);
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Border ReferenceCardShell(
        System.Windows.UIElement child,
        double frameWidth,
        double frameHeight,
        double designHeight)
    {
        var shellContentWidth = ShellContentWidth(frameWidth, frameHeight, designHeight);
        var shellContentHeight = ShellContentHeight(designHeight);
        var contentRoot = new System.Windows.Controls.Grid
        {
            Width = shellContentWidth,
            Height = shellContentHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            ClipToBounds = false
        };
        if (child is System.Windows.FrameworkElement childElement)
        {
            childElement.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            childElement.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        }
        contentRoot.Children.Add(child);

        var contentHost = new System.Windows.Controls.Viewbox
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            StretchDirection = System.Windows.Controls.StretchDirection.Both,
            Child = new System.Windows.Controls.Border
            {
                Width = shellContentWidth,
                Height = shellContentHeight,
                Background = System.Windows.Media.Brushes.Transparent,
                Child = contentRoot
            }
        };

        return new System.Windows.Controls.Border
        {
            Width = frameWidth,
            Height = frameHeight,
            MinHeight = frameHeight,
            MaxWidth = frameWidth,
            Background = ShellBackground,
            BorderBrush = ShellBorder,
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(RecorderFrameCornerRadius),
            Padding = new System.Windows.Thickness(RecorderViewportInset),
            Effect = Shadow(0.18, Math.Max(14, 42 * RecorderFrameScale), 0),
            Child = contentHost
        };
    }

    private static System.Windows.Controls.Border BuildBottomNavigation(
        System.Windows.Controls.Button windowsButton,
        System.Windows.Controls.Button mapsButton,
        System.Windows.Controls.Button libraryButton)
    {
        var grid = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            ClipToBounds = false
        };
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavButtonWidth) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavGap) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavSeparatorWidth) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavGap) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavButtonWidth) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavGap) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavSeparatorWidth) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavGap) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(BottomNavButtonWidth) });

        Place(grid, windowsButton, 0);
        Place(grid, BuildTallDivider(BottomNavSeparatorHeight, BottomNavSeparatorWidth), 2);
        Place(grid, mapsButton, 4);
        Place(grid, BuildTallDivider(BottomNavSeparatorHeight, BottomNavSeparatorWidth), 6);
        Place(grid, libraryButton, 8);

        return new System.Windows.Controls.Border
        {
            Height = BottomNavHeight,
            CornerRadius = new System.Windows.CornerRadius(BottomNavCornerRadius),
            Background = BottomNavBackground,
            BorderBrush = BottomNavBorder,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Effect = CreateRecorderSurfaceShadow(),
            Child = grid
        };
    }

    private static System.Windows.Controls.Border BuildTallDivider(double height, double width = 1) =>
        new()
        {
            Width = width,
            Height = height,
            Background = Divider,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

    private static System.Windows.Controls.Button BottomNavButton(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        string label,
        Action onClick,
        string toolTip)
    {
        var content = BottomNavContent(iconFactory, label);
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            MinWidth = BottomNavButtonWidth,
            Height = BottomNavButtonHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(3),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(BottomNavButtonCornerRadius)),
            Uid = "bottom-nav",
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.IsEnabledChanged += (_, _) =>
        {
            button.Opacity = button.IsEnabled ? 1.0 : 0.62;
            if (button.Content is System.Windows.FrameworkElement { Tag: BottomNavVisualState visual })
                visual.Apply(button.IsEnabled, IsTileSelected(button));
        };
        button.MouseEnter += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: true);
        button.MouseLeave += (_, _) => ApplyTileChrome(button, IsTileSelected(button), isHovered: false);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.StackPanel BottomNavContent(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        string label)
    {
        var iconHost = new System.Windows.Controls.Viewbox
        {
            Width = BottomNavIconHeight,
            Height = BottomNavIconHeight,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = iconFactory(BottomNavMuted)
        };
        var labelBlock = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = BottomNavLabelSize,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = BottomNavMuted,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
            Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        stack.Children.Add(iconHost);
        stack.Children.Add(labelBlock);
        stack.Tag = new BottomNavVisualState(iconFactory, iconHost, labelBlock);
        return stack;
    }

    private static void ConfigureBottomNavButton(
        System.Windows.Controls.Button? button,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        string label,
        string toolTip)
    {
        if (button is null)
            return;

        button.ToolTip = toolTip;
        if (button.Content is System.Windows.FrameworkElement { Tag: BottomNavVisualState visual })
        {
            visual.Update(iconFactory, label);
            visual.Apply(button.IsEnabled, IsTileSelected(button));
        }
    }

    private sealed class BottomNavVisualState(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        System.Windows.Controls.Viewbox iconHost,
        System.Windows.Controls.TextBlock labelBlock)
    {
        private Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> _iconFactory = iconFactory;

        public void Update(
            Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
            string label)
        {
            _iconFactory = iconFactory;
            labelBlock.Text = label;
        }

        public void Apply(bool enabled, bool selected)
        {
            var brush = selected
                ? BottomNavSelectedForeground
                : enabled ? BottomNavMuted : Disabled;
            iconHost.Child = _iconFactory(brush);
            labelBlock.Foreground = brush;
        }
    }

    private static System.Windows.Controls.Button PrimaryCircleButton(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        Action onClick,
        string toolTip,
        double size,
        System.Windows.Media.Brush accentBrush)
    {
        var iconHost = new System.Windows.Controls.Viewbox
        {
            Width = size * 0.40,
            Height = size * 0.40,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = iconFactory(accentBrush)
        };
        var circle = new System.Windows.Controls.Border
        {
            Width = size,
            Height = size,
            CornerRadius = new System.Windows.CornerRadius(size / 2),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = accentBrush,
            BorderThickness = new System.Windows.Thickness(2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = iconHost
        };
        circle.Tag = new PrimaryCircleVisualState(circle, accentBrush);
        var button = new System.Windows.Controls.Button
        {
            Content = circle,
            Width = size + 14,
            Height = size + 14,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius((size + 14) / 2))
        };
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.IsEnabledChanged += (_, _) =>
        {
            if (circle.Tag is PrimaryCircleVisualState visual)
                visual.Apply(button.IsEnabled, visual.Selected, hovered: false);
        };
        button.MouseEnter += (_, _) =>
        {
            if (circle.Tag is PrimaryCircleVisualState visual)
                visual.Apply(button.IsEnabled, visual.Selected, hovered: true);
        };
        button.MouseLeave += (_, _) =>
        {
            if (circle.Tag is PrimaryCircleVisualState visual)
                visual.Apply(button.IsEnabled, visual.Selected, hovered: false);
        };
        button.Click += (_, _) => onClick();
        if (circle.Tag is PrimaryCircleVisualState initialVisual)
            initialVisual.Apply(enabled: true, selected: false, hovered: false);
        return button;
    }

    private static System.Windows.Controls.Button SecondaryCircleButton(
        string label,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        Action onClick,
        string toolTip,
        double size = SecondaryCircleSize,
        double? iconSize = null)
    {
        var resolvedIconSize = iconSize ?? (size * 0.46);
        var iconHost = new System.Windows.Controls.Viewbox
        {
            Width = resolvedIconSize,
            Height = resolvedIconSize,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var circle = new System.Windows.Controls.Border
        {
            Width = size,
            Height = size,
            CornerRadius = new System.Windows.CornerRadius(size / 2),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 17, 24, 39)),
            BorderThickness = new System.Windows.Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = iconHost
        };
        var labelBlock = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = TopActionLabelFontSize,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = TopActionMuted,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
            Margin = new System.Windows.Thickness(0, TopActionLabelGap, 0, 0)
        };
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        stack.Children.Add(circle);
        stack.Children.Add(labelBlock);
        stack.Tag = new CircleActionVisualState(iconFactory, iconHost, circle, labelBlock);

        var button = new System.Windows.Controls.Button
        {
            Content = stack,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(30)),
            Uid = "circle-action"
        };
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.IsEnabledChanged += (_, _) =>
        {
            if (stack.Tag is CircleActionVisualState state)
                state.Apply(button.IsEnabled, selected: false);
        };
        button.MouseEnter += (_, _) =>
        {
            if (button.IsEnabled)
                circle.Effect = Shadow(0.08, 16, 0);
        };
        button.MouseLeave += (_, _) => circle.Effect = null;
        button.Click += (_, _) => onClick();
        if (stack.Tag is CircleActionVisualState visual)
            visual.Apply(true, false);
        return button;
    }

    private static System.Windows.Controls.Grid BuildTopActionRow(
        System.Windows.UIElement left,
        System.Windows.UIElement center,
        System.Windows.UIElement right)
    {
        var row = new System.Windows.Controls.Grid
        {
            Width = TopActionGroupWidth,
            Margin = new System.Windows.Thickness(0, TopActionTopMargin, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom
        };
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(TopActionGap) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(TopActionGap) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        Place(row, left, 0);
        Place(row, center, 2);
        Place(row, right, 4);
        return row;
    }

    private static void HideCircleButtonLabel(System.Windows.Controls.Button? button)
    {
        if (button?.Content is not System.Windows.Controls.StackPanel stack)
            return;

        foreach (var child in stack.Children)
        {
            if (child is System.Windows.Controls.TextBlock label)
                label.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    private static System.Windows.Controls.Button SecondaryTextButton(
        string label,
        Action onClick,
        string toolTip)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 24,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = Accent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            MinWidth = 190,
            MinHeight = 54,
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = Accent,
            BorderThickness = new System.Windows.Thickness(1.5),
            Padding = new System.Windows.Thickness(22, 10, 22, 10),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Tag = "ui-atlas-secondary-outline",
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(14))
        };
        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = Brush("#0077ED");
            button.Background = Brush("#EAF4FF");
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = Accent;
            button.Background = System.Windows.Media.Brushes.White;
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void SetSecondaryTextButtonLabel(System.Windows.Controls.Button button, string label)
    {
        if (button.Content is System.Windows.Controls.TextBlock text)
            text.Text = label;
    }

    private sealed class CircleActionVisualState(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        System.Windows.Controls.Viewbox iconHost,
        System.Windows.Controls.Border circle,
        System.Windows.Controls.TextBlock labelBlock)
    {
        public void Apply(bool enabled, bool selected)
        {
            var brush = enabled ? Accent : TopActionMuted;
            iconHost.Child = iconFactory(brush);
            circle.BorderBrush = brush;
            circle.Background = enabled
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(236, 255, 255, 255));
            labelBlock.Foreground = brush;
        }
    }

    private sealed class PrimaryCircleVisualState(
        System.Windows.Controls.Border circle,
        System.Windows.Media.Brush accentBrush)
    {
        public bool Selected { get; private set; }

        public void Apply(bool enabled, bool selected, bool hovered)
        {
            Selected = selected;
            circle.Background = enabled
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255));
            circle.Effect = enabled && (selected || hovered)
                ? CreatePrimaryCircleShadow(
                    accentBrush,
                    selected ? 0.30 : 0.16,
                    selected ? PrimaryCircleSelectedShadowBlur : PrimaryCircleHoverShadowBlur)
                : null;
        }
    }

    private static System.Windows.Controls.Grid BuildDecorativeWavePanel()
    {
        var grid = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(
                -ActiveProgressArtSideBleed,
                ActiveProgressArtTopMargin,
                -ActiveProgressArtSideBleed,
                -ActiveProgressArtTopMargin),
            Background = System.Windows.Media.Brushes.Transparent,
            ClipToBounds = true,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        if (RecorderPopupUnderlayArt.Value is { } source)
        {
            grid.Children.Add(new System.Windows.Controls.Image
            {
                Source = source,
                Stretch = System.Windows.Media.Stretch.Fill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            });
        }
        else if (DecorativeWaveArt.Value is { } fallbackSource)
        {
            grid.Children.Add(new System.Windows.Controls.Image
            {
                Source = fallbackSource,
                Stretch = System.Windows.Media.Stretch.Fill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            });
        }
        else
        {
            grid.Children.Add(new System.Windows.Controls.Viewbox
            {
                Stretch = System.Windows.Media.Stretch.Fill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                Child = BuildFallbackWaveArt()
            });
        }

        return grid;
    }

    private static System.Windows.Controls.Grid BuildIdlePopupUnderlayPanel()
    {
        var grid = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(-IdlePopupUnderlaySideBleed, 8, -IdlePopupUnderlaySideBleed, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            ClipToBounds = true,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };

        if (RecorderPopupUnderlayArt.Value is { } source)
        {
            grid.Children.Add(new System.Windows.Controls.Image
            {
                Source = source,
                Stretch = System.Windows.Media.Stretch.Fill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch
            });
        }

        return grid;
    }

    private System.Windows.Controls.Border BuildActiveProgressPanel()
    {
        var layout = new System.Windows.Controls.Grid
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        var artPanel = BuildDecorativeWavePanel();
        System.Windows.Controls.Grid.SetRowSpan(artPanel, 5);
        layout.Children.Add(artPanel);

        _activeDetailBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Saving the action and collecting the first snapshots.",
            FontSize = ActiveProgressTextFontSize,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = PanelInk,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            LineHeight = ActiveProgressTextLineHeight,
            MaxWidth = ActiveProgressTextMaxWidth,
            Margin = new System.Windows.Thickness(0, 0, 0, ActiveProgressTextBottomMargin)
        };
        System.Windows.Controls.Grid.SetRow(_activeDetailBlock, 0);
        layout.Children.Add(_activeDetailBlock);

        var stageBadge = BuildActiveStageBadge("Saving screen");
        stageBadge.Margin = new System.Windows.Thickness(0, 8, 0, 4);
        System.Windows.Controls.Grid.SetRow(stageBadge, 1);
        layout.Children.Add(stageBadge);
        ApplyActiveStageBadge("capture");

        _skipAutoButton = BuildActiveActionButton(
            "Continue Auto",
            QueueAutoPassToggleCommand,
            Accent,
            Brush("#0077ED"),
            "Continue automatic capture of Ribbon tabs that have not been recorded yet.");
        _skipAutoButton.Visibility = System.Windows.Visibility.Collapsed;

        _cancelRecordingButton = BuildActiveCancelButton(
            "Cancel Recording",
            () => QueueCommand(CancelRecordingCommand, "Cancelling recording...", StatusTone.Accent));
        _skipAutoButton.Margin = new System.Windows.Thickness(0, 0, 12d / RecorderContentScale, 0);
        var actionButtons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom
        };
        actionButtons.Children.Add(_skipAutoButton);
        actionButtons.Children.Add(_cancelRecordingButton);
        System.Windows.Controls.Grid.SetRow(actionButtons, 4);
        layout.Children.Add(actionButtons);

        return new System.Windows.Controls.Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            Padding = new System.Windows.Thickness(
                ActiveProgressPanelInset,
                ActiveProgressPanelTopInset,
                ActiveProgressPanelInset,
                ActiveProgressPanelBottomInset),
            Child = layout
        };
    }

    private System.Windows.Controls.Border BuildActiveStageBadge(string text)
    {
        var label = new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = 22,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = ActiveStageMutedForeground,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextWrapping = System.Windows.TextWrapping.NoWrap
        };
        var badge = new System.Windows.Controls.Border
        {
            Height = 58,
            Padding = new System.Windows.Thickness(18, 10, 18, 10),
            Background = ActiveStageMutedBackground,
            BorderBrush = ActiveStageMutedBorder,
            BorderThickness = new System.Windows.Thickness(1.5),
            CornerRadius = new System.Windows.CornerRadius(16),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            MinWidth = 220,
            Child = label
        };
        _activeStageBadge = badge;
        _activeStageBadgeLabel = label;
        return badge;
    }

    private static System.Windows.Controls.Canvas BuildFallbackWaveArt()
    {
        var canvas = new System.Windows.Controls.Canvas
        {
            Width = RecorderCardWidth,
            Height = DecorativeArtHeight
        };
        var waveA = CreateStrokedPath("M0 180 C120 120 240 220 360 170 C470 124 560 210 660 158", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 90, 145, 255)), 2);
        var waveB = CreateStrokedPath("M-10 205 C130 158 236 248 360 218 C486 188 552 258 668 210", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 90, 145, 255)), 2);
        var fill = CreateFilledPath("M0 240 C120 160 260 282 390 238 C500 204 556 280 668 210 L668 320 L0 320 Z", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 107, 157, 255)));
        canvas.Children.Add(fill);
        canvas.Children.Add(waveA);
        canvas.Children.Add(waveB);
        return canvas;
    }

    private static bool IsTileSelected(System.Windows.Controls.Button button) =>
        button.Tag is bool selected && selected;

    private static void ApplyTileChrome(System.Windows.Controls.Button button, bool selected, bool isHovered)
    {
        if (button.Uid == "bottom-nav")
        {
            button.Background = selected
                ? System.Windows.Media.Brushes.Transparent
                : isHovered ? BottomNavHoverBackground : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = selected ? BottomNavSelectedBorder : System.Windows.Media.Brushes.Transparent;
            button.Effect = null;
            return;
        }

        if (button.Uid == "circle-action")
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
            button.Effect = null;
            return;
        }

        if (button.Uid == "source-switch")
        {
            button.Background = selected
                ? SourceSelectedBackground
                : isHovered ? SourceHoverBackground : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
            button.Effect = selected ? Shadow(0.10, 8, 0) : null;
            return;
        }

        if (button.Uid == "close-utility")
        {
            button.Background = isHovered ? SourceSelectedBackground : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
            button.Effect = isHovered ? Shadow(0.08, 7, 0) : null;
            return;
        }

        var showHighlight = selected || isHovered;
        button.Background = showHighlight ? SelectedTileBackground : System.Windows.Media.Brushes.Transparent;
        button.BorderBrush = showHighlight ? SelectedTileBorder : System.Windows.Media.Brushes.Transparent;
    }

    private static System.Windows.Controls.Button SessionModeButton(
        string title,
        string subtitle,
        Action onClick,
        string toolTip,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        System.Windows.Thickness margin)
    {
        var hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
        var iconHost = new System.Windows.Controls.Viewbox
        {
            Width = SessionModeCardIconSize,
            Height = SessionModeCardIconSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Child = iconFactory(Accent)
        };
        var titleBlock = new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = SessionModeCardTitleFontSize,
            FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(680),
            Foreground = Ink,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
            Margin = new System.Windows.Thickness(0, SessionModeCardTitleTopMargin, 0, 0)
        };
        var subtitleBlock = new System.Windows.Controls.TextBlock
        {
            Text = subtitle,
            FontSize = SessionModeCardSubtitleFontSize,
            FontWeight = System.Windows.FontWeights.Regular,
            Foreground = PanelMuted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            LineHeight = SessionModeCardSubtitleLineHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
            MaxWidth = SessionModeCardSubtitleWidth,
            Margin = new System.Windows.Thickness(0, SessionModeCardSubtitleTopMargin, 0, 0),
            Visibility = hasSubtitle ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed
        };

        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Children = { iconHost, titleBlock, subtitleBlock }
        };

        var button = new System.Windows.Controls.Button
        {
            Content = stack,
            Margin = margin,
            MinHeight = hasSubtitle ? SessionModeCardMinHeight : SessionModeCardMinHeight,
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = BubbleBorder,
            BorderThickness = new System.Windows.Thickness(SessionModeCardBorderThickness),
            Padding = new System.Windows.Thickness(SessionModeCardPadding),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = toolTip,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(SessionModeCardCornerRadius))
        };
        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = Accent;
            button.Effect = Shadow(0.08, 7, 0);
            titleBlock.Foreground = Accent;
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = BubbleBorder;
            button.Effect = null;
            titleBlock.Foreground = Ink;
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private System.Windows.Controls.Primitives.Popup BuildMoreMenu()
    {
        var menuStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        menuStack.Children.Add(PopupMenuButton("Export map", ExportMap));
        menuStack.Children.Add(PopupMenuButton("Close toolbar", ClosePanel));

        return new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            VerticalOffset = 10,
            Child = new System.Windows.Controls.Border
            {
                MinWidth = 170,
                Padding = new System.Windows.Thickness(8),
                Background = MoreMenuBackground,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(46, 255, 255, 255)),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(16),
                Effect = Shadow(0.18, 48, 0),
                Child = menuStack
            }
        };
    }

    private System.Windows.Controls.Primitives.Popup BuildMapsMenu()
    {
        _mapsMenuStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = _mapsMenuStack
        };

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            StaysOpen = true,
            VerticalOffset = -14,
            Child = new System.Windows.Controls.Border
            {
                MinWidth = 420,
                MaxWidth = 520,
                Padding = new System.Windows.Thickness(8),
                Background = MoreMenuBackground,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(46, 255, 255, 255)),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(16),
                Effect = Shadow(0.18, 48, 0),
                Child = scroll
            }
        };

        popup.Opened += (_, _) => ApplyMapsLibraryVisibility(true);
        popup.Closed += (_, _) => ApplyMapsLibraryVisibility(false);
        return popup;
    }

    private System.Windows.Controls.Primitives.Popup BuildTargetMenu()
    {
        _targetMenuStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = _targetMenuStack
        };

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            StaysOpen = true,
            VerticalOffset = -14,
            Child = new System.Windows.Controls.Border
            {
                MinWidth = 320,
                MaxWidth = 400,
                Padding = new System.Windows.Thickness(8),
                Background = MoreMenuBackground,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(46, 255, 255, 255)),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(16),
                Effect = Shadow(0.18, 48, 0),
                Child = scroll
            }
        };
        popup.Opened += (_, _) => ApplyTargetMenuVisibility(true);
        popup.Closed += (_, _) => ApplyTargetMenuVisibility(false);
        return popup;
    }

    private static System.Windows.Controls.Button CenterActionButton(
        string label,
        System.Windows.FrameworkElement icon,
        Action onClick,
        string toolTip)
    {
        var bubble = new System.Windows.Controls.Border
        {
            Width = CenterBubbleSize,
            Height = CenterBubbleSize,
            CornerRadius = new System.Windows.CornerRadius(CenterBubbleSize / 2),
            Background = BubbleBackground,
            BorderBrush = BubbleBorder,
            BorderThickness = new System.Windows.Thickness(1),
            Effect = Shadow(0.18, 18, 1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = new System.Windows.Controls.Grid
            {
                Children = { icon }
            }
        };

        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(0, -2, 0, 0)
        };
        stack.Children.Add(bubble);
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = LabelFontSize,
            LineHeight = LabelLineHeight,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = Ink,
            Margin = new System.Windows.Thickness(0, 4, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });

        var button = new System.Windows.Controls.Button
        {
            Content = stack,
            Width = ToolButtonWidth,
            Height = ToolSlotHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(ToolCornerRadius))
        };
        button.SetValue(System.Windows.Controls.ToolTipService.ShowOnDisabledProperty, true);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.Button PopupMenuButton(string label, Action onClick)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Ink,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Height = 40,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(12, 11, 12, 11),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(10))
        };
        ApplyOutlineHover(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.FrameworkElement PopupMenuInfo(string title, string? subtitle = null)
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Margin = new System.Windows.Thickness(12, 8, 12, subtitle is null ? 10 : 8)
        };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = Ink,
            TextWrapping = System.Windows.TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Muted,
                Margin = new System.Windows.Thickness(0, 3, 0, 0),
                TextWrapping = System.Windows.TextWrapping.Wrap
            });
        }

        return stack;
    }

    private static System.Windows.Controls.Button PopupMenuButton(string title, string subtitle, Action onClick)
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = Ink,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 3, 0, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });

        var button = new System.Windows.Controls.Button
        {
            Content = stack,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(12, 10, 12, 10),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(10))
        };
        ApplyOutlineHover(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void ApplyOutlineHover(System.Windows.Controls.Button button)
    {
        button.MouseEnter += (_, _) =>
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = BottomNavSelectedBorder;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
        };
    }

    private void ShowMoreMenu()
    {
        if (_moreMenu is null || _moreButton is null) return;
        if (_targetMenu is not null) _targetMenu.IsOpen = false;
        if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
        _moreMenu.PlacementTarget = _moreButton;
        _moreMenu.IsOpen = !_moreMenu.IsOpen;
    }

    private void ShowTargetMenu()
    {
        if (!_allowTargetSelection && HasSelectedTarget())
        {
            ApplyVisualStatus("This recorder session already has a fixed target window.", StatusTone.Neutral);
            return;
        }

        ShowTargetMenuInternal();
    }

    private void ShowTargetMenuInternal()
    {
        if (_targetMenu is null || _targetButton is null || _targetMenuStack is null) return;
        if (_targetMenu.IsOpen)
        {
            _pendingIdlePopupRequest = IdlePopupRequest.None;
            _targetMenu.IsOpen = false;
            return;
        }

        _pendingIdlePopupRequest = IdlePopupRequest.None;
        if (_moreMenu is not null) _moreMenu.IsOpen = false;
        if (_mapsMenu is not null && _mapsMenu.IsOpen)
        {
            _pendingIdlePopupRequest = IdlePopupRequest.Target;
            _mapsMenu.IsOpen = false;
            return;
        }
        QueueIdlePopupOpen(IdlePopupRequest.Target);
    }

    private void RebuildTargetMenu()
    {
        if (_targetMenuStack is null) return;
        _targetMenuStack.Children.Clear();
        _targetMenuStack.Children.Add(PopupMenuInfo("Choose window", "Select the application window you want to record. Start unlocks after selection."));

        IReadOnlyList<WindowTarget> windows;
        try
        {
            windows = WindowCatalog.ListTopLevelWindows()
                .Where(window => window.ProcessId != Environment.ProcessId)
                .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            windows = [];
            _targetMenuStack.Children.Add(PopupMenuInfo("Could not enumerate windows", BundleSecurity.SafeDiagnostic(ex.Message, 140)));
        }

        if (windows.Count == 0)
        {
            _targetMenuStack.Children.Add(PopupMenuInfo("No selectable windows", "Open the app you want to map and try again."));
            return;
        }

        var selectedTarget = GetSelectedTarget();
        foreach (var window in windows.OrderByDescending(window => selectedTarget is not null && window.Hwnd == selectedTarget.Hwnd))
        {
            var title = string.IsNullOrWhiteSpace(window.Title) ? "(Untitled window)" : window.Title;
            var subtitle = $"{window.ProcessName} - 0x{window.RootOwnerHwnd:X}";
            _targetMenuStack.Children.Add(PopupMenuButton(title, subtitle, () =>
            {
                lock (_targetGate)
                {
                    _selectedTarget = window;
                    _selectedTargetHwnd = window.Hwnd;
                }
                _processName = window.ProcessName;
                if (_targetMenu is not null) _targetMenu.IsOpen = false;
                RefreshPreStartTargetState();
                ApplyVisualStatus("Selected " + FormatWindowSummary(window) + ".", StatusTone.Accent);
            }));
        }
    }

    private void ShowMapsMenu()
    {
        if (_mapsMenu is null || _mapsButton is null || _mapsMenuStack is null) return;
        try
        {
            if (_mapsMenu.IsOpen)
            {
                _pendingIdlePopupRequest = IdlePopupRequest.None;
                _mapsMenu.IsOpen = false;
                return;
            }

            _pendingIdlePopupRequest = IdlePopupRequest.None;
            if (_moreMenu is not null) _moreMenu.IsOpen = false;
            if (_targetMenu is not null && _targetMenu.IsOpen)
            {
                _pendingIdlePopupRequest = IdlePopupRequest.Maps;
                _targetMenu.IsOpen = false;
                return;
            }
            QueueIdlePopupOpen(IdlePopupRequest.Maps);
        }
        catch (Exception ex)
        {
            SetStatus("Maps menu failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    private void ApplyTargetMenuVisibility(bool visible)
    {
        SetTileState(_targetButton, enabled: true, selected: visible);
        if (visible)
        {
            UpdateCaption("WINDOW LIST", StatusTone.Neutral, "Choose the application window you want to record.");
            return;
        }

        if (_pendingIdlePopupRequest == IdlePopupRequest.Maps)
        {
            _pendingIdlePopupRequest = IdlePopupRequest.None;
            QueueIdlePopupOpen(IdlePopupRequest.Maps);
            return;
        }

        if (IsCursorOverButton(_mapsButton))
        {
            QueueIdlePopupOpen(IdlePopupRequest.Maps);
            return;
        }

        if (_currentMode == RecordingPanelMode.PreStart)
            RefreshPreStartTargetState();
        else if (_currentMode == RecordingPanelMode.MapReady)
            UpdateCaption("MAP READY", StatusTone.Success, "The map is ready. Use Maps to open it, then resume recording or export it.");
    }

    private void ApplyMapsLibraryVisibility(bool visible)
    {
        SetTileState(_mapsButton, enabled: true, selected: visible);
        if (visible)
        {
            UpdateCaption("MAP LIBRARY", StatusTone.Neutral, "Browse all saved maps here. Choosing a window is only required when you press Start.");
            return;
        }

        if (_pendingIdlePopupRequest == IdlePopupRequest.Target)
        {
            _pendingIdlePopupRequest = IdlePopupRequest.None;
            QueueIdlePopupOpen(IdlePopupRequest.Target);
            return;
        }

        if (IsCursorOverButton(_targetButton))
        {
            QueueIdlePopupOpen(IdlePopupRequest.Target);
            return;
        }

        if (_currentMode == RecordingPanelMode.PreStart)
            RefreshPreStartTargetState();
        else if (_currentMode == RecordingPanelMode.MapReady)
            UpdateCaption("MAP READY", StatusTone.Success, "The map is ready. Use Maps to open it, then resume recording or export it.");
    }

    private void QueueIdlePopupOpen(IdlePopupRequest request)
    {
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(85)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            OpenIdlePopupNow(request);
        };
        timer.Start();
    }

    private void OpenIdlePopupNow(IdlePopupRequest request)
    {
        switch (request)
        {
            case IdlePopupRequest.Target:
                OpenTargetMenuNow();
                break;
            case IdlePopupRequest.Maps:
                OpenMapsMenuNow();
                break;
        }
    }

    private void OpenTargetMenuNow()
    {
        if (_targetMenu is null || _targetButton is null || _targetMenuStack is null) return;
        RebuildTargetMenu();
        _targetMenu.IsOpen = false;
        _targetMenu.PlacementTarget = _targetButton;
        _targetMenu.IsOpen = true;
    }

    private void OpenMapsMenuNow()
    {
        if (_mapsMenu is null || _mapsButton is null || _mapsMenuStack is null) return;
        RebuildMapsMenu();
        _mapsMenu.IsOpen = false;
        _mapsMenu.PlacementTarget = _mapsButton;
        _mapsMenu.IsOpen = true;
    }

    private static bool GetCursorScreenPosition(out System.Windows.Point point)
    {
        point = default;
        if (!GetCursorPos(out var cursorPoint))
            return false;

        point = new System.Windows.Point(cursorPoint.X, cursorPoint.Y);
        return true;
    }

    private bool IsCursorOverButton(System.Windows.Controls.Button? button)
    {
        if (button is null || !button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0)
            return false;
        if (!GetCursorScreenPosition(out var screenPoint))
            return false;

        var relative = button.PointFromScreen(screenPoint);
        return relative.X >= 0 &&
            relative.Y >= 0 &&
            relative.X <= button.ActualWidth &&
            relative.Y <= button.ActualHeight;
    }

    private void RebuildMapsMenu()
    {
        if (_mapsMenuStack is null) return;
        _mapsMenuStack.Children.Clear();
        _mapsMenuStack.Children.Add(PopupMenuInfo("Map library", "See every recorded map here. Start asks for a window only when you actually begin recording."));

        var catalog = new LocalArtifactCatalog();
        IReadOnlyList<LocalMapInfo> maps;
        try
        {
            catalog.EnsureSafe();
            CatalogMapRecovery.RecoverCompletedMaps(catalog);
            maps = catalog.ListMaps();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            maps = [];
            _mapsMenuStack.Children.Add(PopupMenuInfo("Catalog unavailable", BundleSecurity.SafeDiagnostic(ex.Message, 140)));
            _mapsMenuStack.Children.Add(new System.Windows.Controls.Separator { Margin = new System.Windows.Thickness(6, 6, 6, 6) });
        }

        if (maps.Count == 0)
        {
            _mapsMenuStack.Children.Add(PopupMenuInfo("No saved maps yet", "Finish at least one map or import an existing map to build your library."));
        }
        else
        {
            var currentMapPath = string.IsNullOrWhiteSpace(_mapPath) ? null : Path.GetFullPath(_mapPath);
            _mapsMenuStack.Children.Add(PopupMenuInfo(
                $"{maps.Count} saved map{(maps.Count == 1 ? string.Empty : "s")}",
                "You can review the library here without opening the graph viewer."));

            foreach (var map in maps.OrderByDescending(item =>
            {
                var candidatePath = Path.GetFullPath(catalog.MapPath(item.Id));
                return currentMapPath is not null && string.Equals(candidatePath, currentMapPath, StringComparison.OrdinalIgnoreCase);
            }))
            {
                var mapPath = catalog.MapPath(map.Id);
                var isCurrent = currentMapPath is not null &&
                    string.Equals(Path.GetFullPath(mapPath), currentMapPath, StringComparison.OrdinalIgnoreCase);
                _mapsMenuStack.Children.Add(BuildMapLibraryCard(map, mapPath, isCurrent));
            }
        }

        _mapsMenuStack.Children.Add(new System.Windows.Controls.Separator { Margin = new System.Windows.Thickness(6, 6, 6, 6) });
        _mapsMenuStack.Children.Add(PopupMenuButton("Open maps folder", () =>
        {
            if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
            OpenPathInShell(catalog.MapsDirectory);
        }));
        _mapsMenuStack.Children.Add(PopupMenuButton("Open recordings folder", () =>
        {
            if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
            OpenPathInShell(catalog.RecordingsDirectory);
        }));
    }

    private System.Windows.UIElement BuildMapLibraryCard(LocalMapInfo map, string mapPath, bool isCurrent)
    {
        var card = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };

        var header = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 8)
        };
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

        var titleStack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };
        titleStack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = FormatMapDisplayName(map.Id),
            FontSize = 12.5,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = Ink,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        titleStack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = map.Id,
            FontSize = 12,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 3, 0, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });
        Place(header, titleStack, 0);

        var badges = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        if (isCurrent)
            badges.Children.Add(LibraryBadge("Current", Accent, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(22, 88, 126, 255))));
        badges.Children.Add(LibraryBadge(
            map.Status.Equals("valid", StringComparison.OrdinalIgnoreCase) ? "Valid" : "Invalid",
            map.Status.Equals("valid", StringComparison.OrdinalIgnoreCase) ? Accent : Danger,
            map.Status.Equals("valid", StringComparison.OrdinalIgnoreCase)
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 88, 126, 255))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 255, 91, 87))));
        Place(header, badges, 1);

        card.Children.Add(header);
        card.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Built {map.BuiltUtc.ToLocalTime():yyyy-MM-dd HH:mm} - {map.NodeCount} nodes - {map.EdgeCount} edges",
            FontSize = 12,
            Foreground = Ink,
            TextWrapping = System.Windows.TextWrapping.Wrap
        });
        card.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = Path.GetFileName(mapPath),
            FontSize = 12,
            Foreground = Muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });

        var actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };
        actions.Children.Add(MapLibraryActionButton("Open graph", () =>
        {
            if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
            SelectMapFromLibrary(map, mapPath);
            OpenMapViewerSafely(mapPath, null);
        }, primary: true));
        actions.Children.Add(MapLibraryActionButton("Show file", () =>
        {
            if (_mapsMenu is not null) _mapsMenu.IsOpen = false;
            RevealFileInExplorer(mapPath);
        }));
        card.Children.Add(actions);

        return new System.Windows.Controls.Border
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
            Padding = new System.Windows.Thickness(12, 12, 12, 12),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(16, 255, 255, 255)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(14),
            Child = card
        };
    }

    private static string FormatMapDisplayName(string mapId)
    {
        var parts = mapId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return mapId;
        var slug = string.Join(" ", parts.Skip(2).SkipLast(1)).Replace('-', ' ');
        if (string.IsNullOrWhiteSpace(slug)) return mapId;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug);
    }

    private static System.Windows.Controls.Border LibraryBadge(string text, System.Windows.Media.Brush foreground, System.Windows.Media.Brush background) =>
        new()
        {
            Margin = new System.Windows.Thickness(6, 0, 0, 0),
            Padding = new System.Windows.Thickness(8, 4, 8, 4),
            Background = background,
            CornerRadius = new System.Windows.CornerRadius(10),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = foreground
            }
        };

    private static System.Windows.Controls.Button MapLibraryActionButton(string label, Action onClick, bool primary = false)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = primary ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Medium,
                Foreground = primary ? BubbleBackground : Ink
            },
            Height = 30,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Padding = new System.Windows.Thickness(12, 6, 12, 6),
            Background = primary ? Accent : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(16, 255, 255, 255)),
            BorderBrush = primary ? Accent : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(32, 255, 255, 255)),
            BorderThickness = new System.Windows.Thickness(1),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(15))
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private System.Windows.Controls.Button RecordingStopButton()
    {
        var wrap = new System.Windows.Controls.Grid
        {
            Width = ToolColumnWidth,
            Height = ToolSlotHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var ring = new System.Windows.Controls.Border
        {
            Width = StopOuterSize,
            Height = StopOuterSize,
            CornerRadius = new System.Windows.CornerRadius(StopOuterSize / 2),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = Danger,
            BorderThickness = new System.Windows.Thickness(1.5)
        };
        var bubble = new System.Windows.Controls.Border
        {
            Width = StopInnerSize,
            Height = StopInnerSize,
            CornerRadius = new System.Windows.CornerRadius(StopInnerSize / 2),
            Background = BubbleBackground,
            BorderBrush = BubbleBorder,
            BorderThickness = new System.Windows.Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Effect = Shadow(0.12, 10, 1),
            Child = new System.Windows.Controls.Grid
            {
                Children = { CreateStopIcon(Danger) }
            }
        };
        wrap.Children.Add(ring);
        wrap.Children.Add(bubble);

        var button = new System.Windows.Controls.Button
        {
            Content = wrap,
            Width = ToolColumnWidth,
            Height = ToolSlotHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(0),
            ToolTip = "Finish recording and build the map. Shortcut: F.",
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(ToolCornerRadius))
        };
        button.Click += (_, _) => QueueCommand("F", "Finishing recording and building the map...", StatusTone.Accent);
        return button;
    }

    private static System.Windows.Controls.Button InlineButton(
        string label,
        System.Windows.FrameworkElement icon,
        Action onClick,
        string toolTip,
        double minWidth,
        System.Windows.Media.Brush foreground)
    {
        icon.Margin = new System.Windows.Thickness(0, 0, 6, 0);
        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        row.Children.Add(icon);
        row.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = LabelFontSize,
            LineHeight = LabelLineHeight,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = foreground,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        var button = new System.Windows.Controls.Button
        {
            Content = row,
            MinWidth = minWidth,
            Height = InlineButtonHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(0),
            Padding = new System.Windows.Thickness(10, 8, 10, 8),
            ToolTip = toolTip,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = RoundedButtonTemplate(new System.Windows.CornerRadius(20))
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static System.Windows.Controls.StackPanel TileContent(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        string label,
        System.Windows.Media.Brush foreground,
        System.Windows.UIElement? footer = null,
        bool sourceSwitchStyle = false,
        bool compactStyle = false)
    {
        var iconHost = new System.Windows.Controls.Grid();
        iconHost.Children.Add(iconFactory(sourceSwitchStyle ? SourceIdleForeground : Ink));
        var labelBlock = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = compactStyle ? 9 : LabelFontSize,
            LineHeight = compactStyle ? 10 : LabelLineHeight,
            FontWeight = compactStyle ? System.Windows.FontWeight.FromOpenTypeWeight(600) : System.Windows.FontWeights.Medium,
            Foreground = foreground,
            Margin = new System.Windows.Thickness(0, compactStyle ? 1 : LabelGap, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center
        };
        var stack = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        stack.Children.Add(new System.Windows.Controls.Border
        {
            Width = compactStyle ? 14 : IconSize,
            Height = compactStyle ? 14 : IconSize,
            Background = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new System.Windows.CornerRadius((compactStyle ? 14 : IconSize) / 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = iconHost
        });
        stack.Children.Add(labelBlock);
        if (footer is not null) stack.Children.Add(footer);
        stack.Tag = new TileVisualState(iconFactory, iconHost, labelBlock, sourceSwitchStyle);
        return stack;
    }

    private sealed class TileVisualState(
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> iconFactory,
        System.Windows.Controls.Grid iconHost,
        System.Windows.Controls.TextBlock labelBlock,
        bool sourceSwitchStyle)
    {
        public void Apply(bool enabled, bool selected)
        {
            var iconBrush = !enabled
                ? Disabled
                : sourceSwitchStyle
                    ? selected ? Accent : SourceIdleForeground
                    : selected ? Accent : Ink;
            var labelBrush = !enabled
                ? Disabled
                : sourceSwitchStyle
                    ? selected ? Ink : SourceIdleForeground
                    : Ink;
            iconHost.Children.Clear();
            iconHost.Children.Add(iconFactory(iconBrush));
            labelBlock.Foreground = labelBrush;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private System.Windows.FrameworkElement BuildActiveStatusDot()
    {
        var grid = new System.Windows.Controls.Grid
        {
            Width = ActiveStatusDotOuterSize,
            Height = ActiveStatusDotOuterSize,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _activeStatusDotOuter = new System.Windows.Shapes.Ellipse
        {
            Width = ActiveStatusDotOuterSize,
            Height = ActiveStatusDotOuterSize,
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 255, 91, 87))
        };
        _activeStatusDotInner = new System.Windows.Shapes.Ellipse
        {
            Width = ActiveStatusDotInnerSize,
            Height = ActiveStatusDotInnerSize,
            Fill = Danger,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        grid.Children.Add(_activeStatusDotOuter);
        grid.Children.Add(_activeStatusDotInner);
        return grid;
    }

    private static System.Windows.Controls.ControlTemplate RoundedButtonTemplate(System.Windows.CornerRadius radius)
    {
        var border = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetBinding(System.Windows.Controls.Border.BackgroundProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.Background)));
        border.SetBinding(System.Windows.Controls.Border.BorderBrushProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.BorderBrush)));
        border.SetBinding(System.Windows.Controls.Border.BorderThicknessProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.BorderThickness)));
        border.SetBinding(System.Windows.Controls.Border.PaddingProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.Padding)));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, radius);
        border.SetValue(System.Windows.UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        presenter.SetBinding(System.Windows.Controls.ContentPresenter.ContentProperty, TemplateParentBinding(nameof(System.Windows.Controls.ContentControl.Content)));
        presenter.SetBinding(System.Windows.Controls.ContentPresenter.ContentTemplateProperty, TemplateParentBinding(nameof(System.Windows.Controls.ContentControl.ContentTemplate)));
        presenter.SetBinding(
            System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty,
            TemplateParentBinding(nameof(System.Windows.Controls.Control.HorizontalContentAlignment)));
        presenter.SetBinding(
            System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty,
            TemplateParentBinding(nameof(System.Windows.Controls.Control.VerticalContentAlignment)));
        presenter.SetValue(System.Windows.UIElement.SnapsToDevicePixelsProperty, true);
        border.AppendChild(presenter);

        return new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button))
        {
            VisualTree = border
        };
    }

    private static System.Windows.Controls.ControlTemplate FlatContextMenuTemplate(System.Windows.CornerRadius radius)
    {
        var border = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetBinding(System.Windows.Controls.Border.BackgroundProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.Background)));
        border.SetBinding(System.Windows.Controls.Border.BorderBrushProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.BorderBrush)));
        border.SetBinding(System.Windows.Controls.Border.BorderThicknessProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.BorderThickness)));
        border.SetBinding(System.Windows.Controls.Border.PaddingProperty, TemplateParentBinding(nameof(System.Windows.Controls.Control.Padding)));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, radius);
        border.SetValue(System.Windows.UIElement.SnapsToDevicePixelsProperty, true);

        var scrollViewer = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ScrollViewer));
        scrollViewer.SetValue(System.Windows.Controls.ScrollViewer.CanContentScrollProperty, false);
        scrollViewer.SetValue(System.Windows.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty, System.Windows.Controls.ScrollBarVisibility.Disabled);
        scrollViewer.SetValue(System.Windows.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty, System.Windows.Controls.ScrollBarVisibility.Disabled);

        var itemsPresenter = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ItemsPresenter));
        scrollViewer.AppendChild(itemsPresenter);
        border.AppendChild(scrollViewer);

        return new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.ContextMenu))
        {
            VisualTree = border
        };
    }

    private static System.Windows.Style FlatContextMenuItemContainerStyle()
    {
        var border = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, System.Windows.Media.Brushes.Transparent);
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new System.Windows.Thickness(0));
        border.SetValue(System.Windows.Controls.Border.PaddingProperty, new System.Windows.Thickness(0));
        border.SetValue(System.Windows.UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        presenter.SetBinding(
            System.Windows.Controls.ContentPresenter.ContentProperty,
            TemplateParentBinding(nameof(System.Windows.Controls.HeaderedItemsControl.Header)));
        presenter.SetValue(
            System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty,
            System.Windows.HorizontalAlignment.Stretch);
        presenter.SetValue(
            System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty,
            System.Windows.VerticalAlignment.Center);
        presenter.SetValue(System.Windows.UIElement.SnapsToDevicePixelsProperty, true);
        border.AppendChild(presenter);

        var style = new System.Windows.Style(typeof(System.Windows.Controls.MenuItem));
        style.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Control.TemplateProperty,
            new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.MenuItem))
            {
                VisualTree = border
            }));
        style.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Control.PaddingProperty,
            new System.Windows.Thickness(0)));
        style.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Control.BackgroundProperty,
            System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Control.BorderThicknessProperty,
            new System.Windows.Thickness(0)));
        style.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Control.HorizontalContentAlignmentProperty,
            System.Windows.HorizontalAlignment.Stretch));
        return style;
    }

    private static T OffsetElement<T>(T element, double offsetX, double offsetY)
        where T : System.Windows.FrameworkElement
    {
        if (offsetX != 0 || offsetY != 0)
            element.RenderTransform = new System.Windows.Media.TranslateTransform(offsetX, offsetY);

        return element;
    }

    private static System.Windows.Data.Binding TemplateParentBinding(string path) =>
        new(path) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) };

    private static System.Windows.Media.Effects.DropShadowEffect CreateRecorderSurfaceShadow() =>
        new()
        {
            Color = System.Windows.Media.Colors.Black,
            Opacity = 0.05,
            BlurRadius = RecorderSurfaceShadowBlur,
            ShadowDepth = RecorderSurfaceShadowDepth,
            Direction = 270,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
        };

    private static System.Windows.Media.Effects.DropShadowEffect CreatePrimaryCircleShadow(
        System.Windows.Media.Brush brush,
        double opacity,
        double blurRadius) =>
        new()
        {
            Color = brush is System.Windows.Media.SolidColorBrush solid ? solid.Color : System.Windows.Media.Colors.Transparent,
            Opacity = opacity,
            BlurRadius = blurRadius,
            ShadowDepth = 0,
            Direction = 270,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
        };

    private static System.Windows.Media.Effects.Effect? Shadow(double opacity, double blur, double depth) => null;

    private static System.Windows.Media.ImageSource? LoadDecorativeWaveArt() =>
        LoadAssetImage("recorder-wave-art.png");

    private static System.Windows.Media.ImageSource? LoadAssetImage(string fileName, bool trimTransparentPadding = false)
    {
        try
        {
            var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(assetPath))
                return null;

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(assetPath, UriKind.Absolute);
            bitmap.EndInit();

            System.Windows.Media.Imaging.BitmapSource source = bitmap;
            if (trimTransparentPadding)
                source = TrimTransparentPadding(source);

            if (source.CanFreeze)
                source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static SvgAsset? LoadSvgAsset(string fileName)
    {
        try
        {
            var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(assetPath))
                return null;

            var document = XDocument.Load(assetPath);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
                return null;

            var (viewBoxWidth, viewBoxHeight) = ParseSvgViewBox(root);
            if (viewBoxWidth <= 0 || viewBoxHeight <= 0)
                return null;

            var paths = root.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "path", StringComparison.OrdinalIgnoreCase))
                .Select(ParseSvgPathAsset)
                .Where(path => path is not null)
                .Cast<SvgPathAsset>()
                .ToArray();
            if (paths.Length == 0)
                return null;

            return new SvgAsset(viewBoxWidth, viewBoxHeight, paths);
        }
        catch
        {
            return null;
        }
    }

    private static (double Width, double Height) ParseSvgViewBox(XElement root)
    {
        var viewBox = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            var parts = viewBox
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var viewBoxWidth) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var viewBoxHeight))
            {
                return (viewBoxWidth, viewBoxHeight);
            }
        }

        return (
            ParseSvgDouble(root.Attribute("width")?.Value) ?? 0,
            ParseSvgDouble(root.Attribute("height")?.Value) ?? 0);
    }

    private static SvgPathAsset? ParseSvgPathAsset(XElement element)
    {
        var data = element.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(data))
            return null;

        var geometry = System.Windows.Media.Geometry.Parse(data);
        if (geometry.CanFreeze)
            geometry.Freeze();

        var fill = ParseSvgBrush(element.Attribute("fill")?.Value);
        var stroke = ParseSvgBrush(element.Attribute("stroke")?.Value);
        var strokeThickness = ParseSvgDouble(element.Attribute("stroke-width")?.Value) ?? 1d;
        return new SvgPathAsset(
            geometry,
            fill,
            stroke,
            strokeThickness,
            ParseSvgLineCap(element.Attribute("stroke-linecap")?.Value),
            ParseSvgLineJoin(element.Attribute("stroke-linejoin")?.Value));
    }

    private static double? ParseSvgDouble(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        if (normalized.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^2];

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static System.Windows.Media.Brush? ParseSvgBrush(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var brush = Brush(raw.Trim());
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.PenLineCap ParseSvgLineCap(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "round" => System.Windows.Media.PenLineCap.Round,
            "square" => System.Windows.Media.PenLineCap.Square,
            _ => System.Windows.Media.PenLineCap.Flat
        };

    private static System.Windows.Media.PenLineJoin ParseSvgLineJoin(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "bevel" => System.Windows.Media.PenLineJoin.Bevel,
            "round" => System.Windows.Media.PenLineJoin.Round,
            _ => System.Windows.Media.PenLineJoin.Miter
        };

    private static System.Windows.Media.Imaging.BitmapSource TrimTransparentPadding(System.Windows.Media.Imaging.BitmapSource source)
    {
        var formatted = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        if (formatted.PixelWidth <= 0 || formatted.PixelHeight <= 0)
            return source;

        var stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);

        var minX = formatted.PixelWidth;
        var minY = formatted.PixelHeight;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < formatted.PixelHeight; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < formatted.PixelWidth; x++)
            {
                if (pixels[rowOffset + (x * 4) + 3] <= 6)
                    continue;

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0 || maxY < 0)
            return source;

        if (minX == 0 && minY == 0 && maxX == formatted.PixelWidth - 1 && maxY == formatted.PixelHeight - 1)
            return source;

        return new System.Windows.Media.Imaging.CroppedBitmap(
            source,
            new System.Windows.Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
    }

    private static System.Windows.FrameworkElement CreateAssetMaskIcon(
        Lazy<System.Windows.Media.ImageSource?> asset,
        double width,
        double height,
        System.Windows.Media.Brush brush)
    {
        if (asset.Value is not { } source)
            return CreateMissingAssetIcon(width, height);

        return new System.Windows.Controls.Border
        {
            Width = width,
            Height = height,
            Background = brush,
            OpacityMask = new System.Windows.Media.ImageBrush(source)
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                AlignmentX = System.Windows.Media.AlignmentX.Center,
                AlignmentY = System.Windows.Media.AlignmentY.Center
            },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
    }

    private static System.Windows.FrameworkElement CreateAssetMaskIcon(
        Lazy<System.Windows.Media.ImageSource?> asset,
        double width,
        double height,
        System.Windows.Media.Brush brush,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> fallback)
    {
        if (asset.Value is not { } source)
            return fallback(brush);

        return new System.Windows.Controls.Border
        {
            Width = width,
            Height = height,
            Background = brush,
            OpacityMask = new System.Windows.Media.ImageBrush(source)
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                AlignmentX = System.Windows.Media.AlignmentX.Center,
                AlignmentY = System.Windows.Media.AlignmentY.Center
            },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
    }

    private static System.Windows.FrameworkElement CreateAssetImageIcon(
        Lazy<System.Windows.Media.ImageSource?> asset,
        double width,
        double height)
    {
        if (asset.Value is not { } source)
            return CreateMissingAssetIcon(width, height);

        return new System.Windows.Controls.Image
        {
            Source = source,
            Width = width,
            Height = height,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
    }

    private static System.Windows.FrameworkElement CreateAssetImageIcon(
        Lazy<System.Windows.Media.ImageSource?> asset,
        double width,
        double height,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> fallback,
        System.Windows.Media.Brush brush)
    {
        if (asset.Value is not { } source)
            return fallback(brush);

        return new System.Windows.Controls.Image
        {
            Source = source,
            Width = width,
            Height = height,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
    }

    private static System.Windows.FrameworkElement CreateAssetSvgIcon(
        Lazy<SvgAsset?> asset,
        double width,
        double height,
        Func<System.Windows.FrameworkElement> fallback)
    {
        if (asset.Value is not { } source)
            return fallback();

        var canvas = new System.Windows.Controls.Canvas
        {
            Width = source.ViewBoxWidth,
            Height = source.ViewBoxHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        foreach (var item in source.Paths)
        {
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = item.Geometry,
                Fill = item.Fill ?? System.Windows.Media.Brushes.Transparent,
                Stroke = item.Stroke,
                StrokeThickness = item.Stroke is null ? 0 : item.StrokeThickness,
                StrokeStartLineCap = item.StrokeLineCap,
                StrokeEndLineCap = item.StrokeLineCap,
                StrokeLineJoin = item.StrokeLineJoin,
                Stretch = System.Windows.Media.Stretch.None
            });
        }

        return new System.Windows.Controls.Viewbox
        {
            Width = width,
            Height = height,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = canvas
        };
    }

    private static System.Windows.FrameworkElement CreateAssetSvgIcon(
        Lazy<SvgAsset?> asset,
        double width,
        double height,
        Func<System.Windows.Media.Brush, System.Windows.FrameworkElement> fallback,
        System.Windows.Media.Brush brush)
    {
        if (asset.Value is not { } source)
            return fallback(brush);

        var canvas = new System.Windows.Controls.Canvas
        {
            Width = source.ViewBoxWidth,
            Height = source.ViewBoxHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        foreach (var item in source.Paths)
        {
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = item.Geometry,
                Fill = item.Fill is null ? System.Windows.Media.Brushes.Transparent : brush,
                Stroke = item.Stroke is null ? null : brush,
                StrokeThickness = item.Stroke is null ? 0 : item.StrokeThickness,
                StrokeStartLineCap = item.StrokeLineCap,
                StrokeEndLineCap = item.StrokeLineCap,
                StrokeLineJoin = item.StrokeLineJoin,
                Stretch = System.Windows.Media.Stretch.None
            });
        }

        return new System.Windows.Controls.Viewbox
        {
            Width = width,
            Height = height,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = canvas
        };
    }

    private static System.Windows.FrameworkElement CreateMissingAssetIcon(double width, double height) =>
        new System.Windows.Controls.Border
        {
            Width = width,
            Height = height,
            Background = System.Windows.Media.Brushes.Transparent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

    private static bool IsSameSolidColor(System.Windows.Media.Brush brush, System.Windows.Media.Brush reference) =>
        brush is System.Windows.Media.SolidColorBrush left &&
        reference is System.Windows.Media.SolidColorBrush right &&
        left.Color == right.Color;

    private static System.Windows.FrameworkElement CreatePackPngIcon(
        Lazy<System.Windows.Media.ImageSource?> asset,
        double width,
        double height,
        System.Windows.Media.Brush brush,
        System.Windows.Media.Brush nativeBrush) =>
        IsSameSolidColor(brush, nativeBrush)
            ? CreateAssetImageIcon(asset, width, height)
            : CreateAssetMaskIcon(asset, width, height, brush);

    private static System.Windows.FrameworkElement CreatePlayIcon(System.Windows.Media.Brush brush) =>
        OffsetElement(CreatePackPngIcon(RecorderPlayArt, 19, 19, brush, Accent), 2, 0);

    private static System.Windows.FrameworkElement CreatePlayVectorIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            19,
            19,
            CreateFilledPath(
                "M8.4 6.2C8.4 5.38 9.3 4.88 10 5.32L18.2 10.42C18.86 10.83 18.86 11.77 18.2 12.18L10 17.28C9.3 17.72 8.4 17.23 8.4 16.42V6.2Z",
                brush));

    private static System.Windows.FrameworkElement CreateCheckIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            16,
            16,
            CreateStrokedPath("M3 8L7 12L13 4", brush, 2));

    private static System.Windows.FrameworkElement CreateStopIcon(System.Windows.Media.Brush brush) =>
        CreatePackPngIcon(RecorderStopArt, 18, 18, brush, Danger);

    private static System.Windows.FrameworkElement CreateStopVectorIcon(System.Windows.Media.Brush brush)
    {
        var square = new System.Windows.Shapes.Rectangle
        {
            Width = 13,
            Height = 13,
            RadiusX = 2,
            RadiusY = 2,
            Fill = brush
        };
        System.Windows.Controls.Canvas.SetLeft(square, 5.5);
        System.Windows.Controls.Canvas.SetTop(square, 5.5);
        return CreateSvgIcon(18, 18, square);
    }

    private static System.Windows.FrameworkElement CreatePauseIcon(System.Windows.Media.Brush brush) =>
        CreateAssetSvgIcon(RecorderPauseSvgArt, 16, 16, fallbackBrush => CreatePackPngIcon(RecorderPauseArt, 16, 16, fallbackBrush, Accent), brush);

    private static System.Windows.FrameworkElement CreatePauseVectorIcon(System.Windows.Media.Brush brush)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 16, Height = 16 };
        var left = new System.Windows.Shapes.Rectangle
        {
            Width = 3,
            Height = 10,
            RadiusX = 1,
            RadiusY = 1,
            Fill = brush
        };
        var right = new System.Windows.Shapes.Rectangle
        {
            Width = 3,
            Height = 10,
            RadiusX = 1,
            RadiusY = 1,
            Fill = brush
        };
        System.Windows.Controls.Canvas.SetLeft(left, 4);
        System.Windows.Controls.Canvas.SetTop(left, 3);
        System.Windows.Controls.Canvas.SetLeft(right, 9);
        System.Windows.Controls.Canvas.SetTop(right, 3);
        canvas.Children.Add(left);
        canvas.Children.Add(right);
        return canvas;
    }

    private static System.Windows.FrameworkElement CreatePauseActionIcon(System.Windows.Media.Brush brush) =>
        CreateAssetSvgIcon(RecorderPauseSvgArt, 28, 28, fallbackBrush => CreatePackPngIcon(RecorderPauseArt, 28, 28, fallbackBrush, Accent), brush);

    private static System.Windows.FrameworkElement CreatePauseActionVectorIcon(System.Windows.Media.Brush brush)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 28, Height = 28 };
        var left = new System.Windows.Shapes.Rectangle
        {
            Width = 6,
            Height = 18,
            RadiusX = 3,
            RadiusY = 3,
            Fill = brush
        };
        var right = new System.Windows.Shapes.Rectangle
        {
            Width = 6,
            Height = 18,
            RadiusX = 3,
            RadiusY = 3,
            Fill = brush
        };
        System.Windows.Controls.Canvas.SetLeft(left, 6);
        System.Windows.Controls.Canvas.SetTop(left, 5);
        System.Windows.Controls.Canvas.SetLeft(right, 16);
        System.Windows.Controls.Canvas.SetTop(right, 5);
        canvas.Children.Add(left);
        canvas.Children.Add(right);
        return canvas;
    }

    private static System.Windows.FrameworkElement CreateStopActionIcon(System.Windows.Media.Brush brush) =>
        CreatePackPngIcon(RecorderStopArt, 36, 36, brush, Danger);

    private static System.Windows.FrameworkElement CreateStopActionVectorIcon(System.Windows.Media.Brush brush)
    {
        var square = new System.Windows.Shapes.Rectangle
        {
            Width = 22,
            Height = 22,
            RadiusX = 4,
            RadiusY = 4,
            Fill = brush
        };
        System.Windows.Controls.Canvas.SetLeft(square, 7);
        System.Windows.Controls.Canvas.SetTop(square, 7);
        return CreateSvgIcon(36, 36, square);
    }

    private static System.Windows.FrameworkElement CreateDoubleClickIcon(System.Windows.Media.Brush brush) =>
        CreateAssetSvgIcon(RecorderDoubleClickSvgArt, 30, 30, fallbackBrush => CreatePackPngIcon(RecorderDoubleClickArt, 30, 30, fallbackBrush, Accent), brush);

    private static System.Windows.FrameworkElement CreateDoubleClickVectorIcon(System.Windows.Media.Brush brush)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 30, Height = 30 };
        var outer = new System.Windows.Shapes.Ellipse
        {
            Width = 18,
            Height = 18,
            Stroke = brush,
            StrokeThickness = 2.8
        };
        var inner = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = brush,
            StrokeThickness = 2.8
        };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = brush
        };
        var pointer = new System.Windows.Shapes.Polygon
        {
            Fill = brush,
            Points = new System.Windows.Media.PointCollection
            {
                new(17, 14),
                new(28, 25),
                new(24.5, 26.7),
                new(21.3, 22.6),
                new(18.7, 27.2),
                new(15.8, 25.7),
                new(18.3, 21),
                new(13.9, 17.5)
            }
        };
        System.Windows.Controls.Canvas.SetLeft(outer, 2.5);
        System.Windows.Controls.Canvas.SetTop(outer, 2.5);
        System.Windows.Controls.Canvas.SetLeft(inner, 6.5);
        System.Windows.Controls.Canvas.SetTop(inner, 6.5);
        System.Windows.Controls.Canvas.SetLeft(dot, 9.5);
        System.Windows.Controls.Canvas.SetTop(dot, 9.5);
        canvas.Children.Add(outer);
        canvas.Children.Add(inner);
        canvas.Children.Add(dot);
        canvas.Children.Add(pointer);
        return canvas;
    }

    private static System.Windows.FrameworkElement CreateWindowTargetIcon(System.Windows.Media.Brush brush) =>
        CreateAssetSvgIcon(RecorderWindowsSvgArt, 24, 24, fallbackBrush => CreatePackPngIcon(RecorderWindowsArt, 24, 24, fallbackBrush, Accent), brush);

    private static System.Windows.FrameworkElement CreateWindowTargetVectorIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            24,
            24,
            CreateRoundedRect(3.5, 5, 17, 14, 1.8, brush, 1.6),
            CreateStrokedPath("M3.5 8.5H20.5", brush, 1.6),
            CreateStrokedPath("M7 12H14M7 15H11", brush, 1.6));

    private static System.Windows.FrameworkElement CreateMapIcon(System.Windows.Media.Brush brush) =>
        CreateAssetSvgIcon(RecorderMapsSvgArt, 24, 24, fallbackBrush => CreatePackPngIcon(RecorderMapsArt, 24, 24, fallbackBrush, Accent), brush);

    private static System.Windows.FrameworkElement CreateMapVectorIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            24,
            24,
            CreateStrokedPath(
                "M3.5 6.5L8.7 4L15.3 6.5L20.5 4V17.5L15.3 20L8.7 17.5L3.5 20V6.5Z",
                brush,
                1.6),
            CreateStrokedPath(
                "M8.7 4V17.5M15.3 6.5V20",
                brush,
                1.6));

    private static System.Windows.FrameworkElement CreateFolderIcon(System.Windows.Media.Brush brush) =>
        CreateAssetMaskIcon(RecorderFolderArt, 24, 24, brush, CreateFolderVectorIcon);

    private static System.Windows.FrameworkElement CreateFolderVectorIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            24,
            24,
            CreateStrokedPath("M3.5 7.5H9.2L11 9.5H20.5V18.5H3.5V7.5Z", brush, 1.6),
            CreateStrokedPath("M3.5 9.5H20.5", brush, 1.6));

    private static System.Windows.FrameworkElement CreateLibraryIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            22,
            22,
            CreateStrokedPath("M5 6.5H15C16.1 6.5 17 7.4 17 8.5V16.5H7C5.9 16.5 5 15.6 5 14.5V6.5Z", brush, 1.5),
            CreateStrokedPath("M8 3.5H18C19.1 3.5 20 4.4 20 5.5V13.5", brush, 1.5),
            CreateStrokedPath("M8 10.5H14M8 13.5H12.5", brush, 1.4));

    private static System.Windows.FrameworkElement CreateExportIcon(System.Windows.Media.Brush brush) =>
        CreateAssetSvgIcon(RecorderExportSvgArt, 24, 24, fallbackBrush => CreateAssetMaskIcon(RecorderExportArt, 24, 24, fallbackBrush, CreateExportVectorIcon), brush);

    private static System.Windows.FrameworkElement CreateExportVectorIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            24,
            24,
            CreateStrokedPath("M12 3.5V13.7", brush, 1.6),
            CreateStrokedPath("M8.7 10.7L12 14L15.3 10.7", brush, 1.6),
            CreateStrokedPath("M5.5 18.5H18.5", brush, 1.6));

    private static System.Windows.FrameworkElement CreateSearchIcon(System.Windows.Media.Brush brush) =>
        CreatePackPngIcon(RecorderSearchArt, 18, 18, brush, Accent);

    private static System.Windows.FrameworkElement CreateSearchVectorIcon(System.Windows.Media.Brush brush)
    {
        var circle = new System.Windows.Shapes.Ellipse
        {
            Width = 11,
            Height = 11,
            Stroke = brush,
            StrokeThickness = 1.8
        };
        var handle = CreateLine(13.5, 13.5, 18.5, 18.5, brush, 1.8);
        System.Windows.Controls.Canvas.SetLeft(circle, 4);
        System.Windows.Controls.Canvas.SetTop(circle, 4);
        return CreateSvgIcon(18, 18, circle, handle);
    }

    private static System.Windows.FrameworkElement CreateManualModeIcon(System.Windows.Media.Brush brush) =>
        CreateAssetImageIcon(RecorderManualArt, 104, 104);

    private static System.Windows.FrameworkElement CreateManualModeVectorIcon(System.Windows.Media.Brush brush)
    {
        var bubble = new System.Windows.Shapes.Ellipse
        {
            Width = 52,
            Height = 52,
            Fill = Brush("#EAF4FF")
        };
        System.Windows.Controls.Canvas.SetLeft(bubble, 7);
        System.Windows.Controls.Canvas.SetTop(bubble, 7);

        return CreateSvgIcon(
            66,
            66,
            bubble,
            CreateStrokedPath("M64 22v10", brush, 5),
            CreateStrokedPath("M45 30l7 7", brush, 5),
            CreateStrokedPath("M83 30l-7 7", brush, 5),
            CreateStrokedPath("M38 48h10", brush, 5),
            CreateStrokedPath("M80 48h10", brush, 5),
            CreateStrokedPath("M58 76V51c0-5 8-5 8 0v18", brush, 5),
            CreateStrokedPath("M66 67v-8c0-5 8-5 8 0v10", brush, 5),
            CreateStrokedPath("M74 68v-6c0-5 8-5 8 0v10", brush, 5),
            CreateStrokedPath("M82 71c0-5 8-5 8 0v10c0 15-10 25-25 25h-2c-10 0-17-6-21-15l-6-13c-2-5 5-9 8-4l10 11", brush, 5));
    }

    private static System.Windows.FrameworkElement CreateAutoLabelsModeIcon(System.Windows.Media.Brush brush) =>
        CreateAssetImageIcon(RecorderAutoLabelsArt, 104, 104);

    private static System.Windows.FrameworkElement CreateAutoLabelsModeVectorIcon(System.Windows.Media.Brush brush)
    {
        var bubble = new System.Windows.Shapes.Ellipse
        {
            Width = 52,
            Height = 52,
            Fill = Brush("#EAF4FF")
        };
        System.Windows.Controls.Canvas.SetLeft(bubble, 7);
        System.Windows.Controls.Canvas.SetTop(bubble, 7);

        return CreateSvgIcon(
            66,
            66,
            bubble,
            CreateStrokedPath("M34 52V40a6 6 0 0 1 6-6h12", brush, 5),
            CreateStrokedPath("M76 34h12a6 6 0 0 1 6 6v12", brush, 5),
            CreateStrokedPath("M94 76v12a6 6 0 0 1-6 6H76", brush, 5),
            CreateStrokedPath("M52 94H40a6 6 0 0 1-6-6V76", brush, 5),
            CreateRoundedRect(52, 52, 24, 24, 5, brush, 5));
    }

    private static System.Windows.FrameworkElement CreateDoubleIcon(System.Windows.Media.Brush brush)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 16, Height = 16 };
        var left = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Stroke = brush,
            StrokeThickness = 1.3
        };
        var right = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Stroke = brush,
            StrokeThickness = 1.3
        };
        System.Windows.Controls.Canvas.SetLeft(left, 2.5);
        System.Windows.Controls.Canvas.SetTop(left, 6);
        System.Windows.Controls.Canvas.SetLeft(right, 9.5);
        System.Windows.Controls.Canvas.SetTop(right, 6);
        canvas.Children.Add(left);
        canvas.Children.Add(right);
        return canvas;
    }

    private static System.Windows.FrameworkElement CreateSkipIcon(System.Windows.Media.Brush brush)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 16, Height = 16 };
        var first = new System.Windows.Shapes.Polygon
        {
            Fill = brush,
            Points = new System.Windows.Media.PointCollection { new(2, 4), new(7, 8), new(2, 12) }
        };
        var second = new System.Windows.Shapes.Polygon
        {
            Fill = brush,
            Points = new System.Windows.Media.PointCollection { new(8, 4), new(13, 8), new(8, 12) }
        };
        canvas.Children.Add(first);
        canvas.Children.Add(second);
        return canvas;
    }

    private static System.Windows.FrameworkElement CreateUtilityIcon(params System.Windows.UIElement[] elements)
    {
        var canvas = new System.Windows.Controls.Canvas
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        foreach (var element in elements)
            canvas.Children.Add(element);

        return canvas;
    }

    private static System.Windows.FrameworkElement CreateCompactPanelIcon(System.Windows.Media.Brush brush) =>
        CreateUtilityIcon(
            CreateStrokedPath("M3.5 5.5L6 8L3.5 10.5", brush, 1.7),
            CreateStrokedPath("M12.5 5.5L10 8L12.5 10.5", brush, 1.7));

    private static System.Windows.FrameworkElement CreateExpandPanelIcon(System.Windows.Media.Brush brush) =>
        CreateUtilityIcon(
            CreateStrokedPath("M6 5.5L3.5 8L6 10.5", brush, 1.7),
            CreateStrokedPath("M10 5.5L12.5 8L10 10.5", brush, 1.7));

    private static System.Windows.FrameworkElement CreateMinimizeIcon(System.Windows.Media.Brush brush) =>
        CreateUtilityIcon(
            CreateStrokedPath("M3.5 8H12.5", brush, 1.9));

    private static System.Windows.FrameworkElement CreateCloseIcon(System.Windows.Media.Brush brush) =>
        CreateUtilityIcon(
            CreateStrokedPath("M4.2 4.2L11.8 11.8", brush, 1.9),
            CreateStrokedPath("M11.8 4.2L4.2 11.8", brush, 1.9));

    private static System.Windows.FrameworkElement CreateMoreIcon(System.Windows.Media.Brush brush) =>
        CreateAssetMaskIcon(RecorderMoreArt, 20, 20, brush, CreateMoreVectorIcon);

    private static System.Windows.FrameworkElement CreateDuplicateIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            24,
            24,
            CreateStrokedPath("M8 8H20V20H8Z", brush, 1.7),
            CreateStrokedPath("M4 4H16V8M4 4V16H8", brush, 1.7));

    private static System.Windows.FrameworkElement CreateDeleteIcon(System.Windows.Media.Brush brush) =>
        CreateSvgIcon(
            24,
            24,
            CreateStrokedPath("M3.5 6.5H20.5", brush, 1.7),
            CreateStrokedPath("M8 6.5V3.5H16V6.5", brush, 1.7),
            CreateStrokedPath("M8.5 10V18", brush, 1.6),
            CreateStrokedPath("M12 10V18", brush, 1.6),
            CreateStrokedPath("M15.5 10V18", brush, 1.6),
            CreateStrokedPath("M5 6.5L6.2 21H17.8L19 6.5", brush, 1.7));

    private static System.Windows.FrameworkElement CreateMoreVectorIcon(System.Windows.Media.Brush brush)
    {
        var first = new System.Windows.Shapes.Ellipse { Width = 2.5, Height = 2.5, Fill = brush };
        var second = new System.Windows.Shapes.Ellipse { Width = 2.5, Height = 2.5, Fill = brush };
        var third = new System.Windows.Shapes.Ellipse { Width = 2.5, Height = 2.5, Fill = brush };
        System.Windows.Controls.Canvas.SetLeft(first, 4.75);
        System.Windows.Controls.Canvas.SetTop(first, 10.75);
        System.Windows.Controls.Canvas.SetLeft(second, 10.75);
        System.Windows.Controls.Canvas.SetTop(second, 10.75);
        System.Windows.Controls.Canvas.SetLeft(third, 16.75);
        System.Windows.Controls.Canvas.SetTop(third, 10.75);

        return CreateSvgIcon(20, 20, first, second, third);
    }

    private static System.Windows.FrameworkElement CreateSvgIcon(
        double width,
        double height,
        params System.Windows.UIElement[] elements)
    {
        var canvas = new System.Windows.Controls.Canvas
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        foreach (var element in elements)
            canvas.Children.Add(element);

        return new System.Windows.Controls.Viewbox
        {
            Width = width,
            Height = height,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Child = canvas
        };
    }

    private static System.Windows.Shapes.Shape CreateRoundedRect(
        double x,
        double y,
        double width,
        double height,
        double radius,
        System.Windows.Media.Brush stroke,
        double strokeThickness)
    {
        var rectangle = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = radius,
            RadiusY = radius,
            Stroke = stroke,
            StrokeThickness = strokeThickness,
            Fill = System.Windows.Media.Brushes.Transparent
        };
        System.Windows.Controls.Canvas.SetLeft(rectangle, x);
        System.Windows.Controls.Canvas.SetTop(rectangle, y);
        return rectangle;
    }

    private static System.Windows.Shapes.Path CreateStrokedPath(
        string data,
        System.Windows.Media.Brush brush,
        double strokeThickness) =>
        new()
        {
            Data = System.Windows.Media.Geometry.Parse(data),
            Stroke = brush,
            StrokeThickness = strokeThickness,
            Fill = System.Windows.Media.Brushes.Transparent,
            Stretch = System.Windows.Media.Stretch.None,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        };

    private static System.Windows.Shapes.Path CreateFilledPath(
        string data,
        System.Windows.Media.Brush brush) =>
        new()
        {
            Data = System.Windows.Media.Geometry.Parse(data),
            Fill = brush,
            Stretch = System.Windows.Media.Stretch.None
        };

    private static System.Windows.Shapes.Line CreateLine(
        double x1,
        double y1,
        double x2,
        double y2,
        System.Windows.Media.Brush brush,
        double strokeThickness) =>
        new()
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
        };

    private const uint WmGetIcon = 0x007F;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int GclHIcon = -14;
    private const int GclHIconSm = -34;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint AppIconMessageTimeoutMs = 80;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private static System.Windows.Media.SolidColorBrush Brush(string hex) =>
        (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeoutW(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nuint result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SearchPath(
        string? lpPath,
        string lpFileName,
        string? lpExtension,
        uint nBufferLength,
        [Out] StringBuilder lpBuffer,
        nint lpFilePart);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nuint SHGetFileInfoW(
        string path,
        uint fileAttributes,
        ref ShellFileInfo info,
        uint infoSize,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private void OpenMap()
    {
        if (string.IsNullOrWhiteSpace(_mapPath))
        {
            SetStatus("Open map failed: The map is not built yet.");
            return;
        }

        OpenMapViewerSafely(_mapPath, string.IsNullOrWhiteSpace(_recordingPath) ? null : _recordingPath);
    }

    private void OpenMapViewerSafely(string graphPath, string? evidencePath)
    {
        try
        {
            var resolvedGraphPath = Path.GetFullPath(graphPath);
            if (!File.Exists(resolvedGraphPath))
                throw new FileNotFoundException("The selected map file was not found.", resolvedGraphPath);

            var resolvedEvidencePath = string.IsNullOrWhiteSpace(evidencePath)
                ? null
                : Path.GetFullPath(evidencePath);
            if (!string.IsNullOrWhiteSpace(resolvedEvidencePath) && !File.Exists(resolvedEvidencePath))
                resolvedEvidencePath = null;

            Program.OpenMapViewer(resolvedGraphPath, resolvedEvidencePath);
            SetStatus("The map explorer has been opened.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Open map failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    private void RevealFileInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = true });
            SetStatus("Opened file location.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Open file location failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    private void OpenPathInShell(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus("Opened " + path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Open folder failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    private void ExportMap()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_mapPath) || string.IsNullOrWhiteSpace(_defaultExportPath))
                throw new InvalidOperationException("The map is not built yet.");
            var graph = SqliteGraphStore.Load(_mapPath);
            HumanReadableMapExporter.Publish(graph, _defaultExportPath, acknowledgeSensitiveIdentities: true);
            SetStatus("Map exported to " + _defaultExportPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus("Export failed: " + BundleSecurity.SafeDiagnostic(ex.Message, 160));
        }
    }

    public void Dispose()
    {
        if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
            _dispatcher.Invoke(() => _window?.Close());
        _thread?.Join(TimeSpan.FromSeconds(5));
        _elapsedTimer?.Stop();
        _closed.Dispose();
        _commands.Writer.TryComplete();
        _ready.Dispose();
    }
}

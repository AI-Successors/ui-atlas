using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>
/// Finds conservative rectangular control candidates directly in captured
/// pixels. Results are intentionally disabled/unverified until pointer, focus,
/// UIA or MSAA evidence confirms them.
/// </summary>
public static class VisualSurfaceScanner
{
    private static readonly string[] CommonFieldLabels =
    [
        "Name", "Surname", "Phone", "Mobile", "Street", "Street1", "ZIP", "City", "State", "Country",
        "Fax", "Email", "Job Title", "Company", "Note"
    ];

    public static async Task<IReadOnlyList<AutomationObservation>> DiscoverAsync(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> screenRegions,
        IReadOnlyList<AutomationObservation> knownControls,
        CancellationToken cancellationToken,
        IReadOnlyList<RectI>? excludedScreenRegions = null)
    {
        var words = await WindowsOcrTextRecognizer.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        return await DiscoverWithWordsAsync(target, frame, screenRegions, knownControls, words,
            cancellationToken, excludedScreenRegions).ConfigureAwait(false);
    }

    public static IReadOnlyList<AutomationObservation> Discover(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> screenRegions,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<RectI>? excludedScreenRegions = null)
        => DiscoverCore(target, frame, screenRegions, knownControls, [], excludedScreenRegions);

    public static IReadOnlyList<AutomationObservation> DiscoverGeometry(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> screenRegions,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<RectI>? excludedScreenRegions = null) =>
        DiscoverCore(target, frame, screenRegions, knownControls, [], excludedScreenRegions)
            .Select(control => control with
            {
                FrameworkId = "UiAtlas.Visual.Geometry",
                OcrText = null
            })
            .ToArray();

    public static IReadOnlyList<AutomationObservation> DiscoverDatabaseGridControls(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(knownControls);
        if (frame.Width < 24 || frame.Height < 16 || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            return [];

        var scaleX = frame.Width / (double)target.Bounds.Width;
        var scaleY = frame.Height / (double)target.Bounds.Height;
        var tables = FindKnownGridTables(target, frame, knownControls, scaleX, scaleY, []);
        var combos = FindWideDatabaseGridCombos(target, frame, knownControls, tables, scaleX, scaleY);
        var result = new List<AutomationObservation>();
        foreach (var table in tables)
            AppendTableObservations(result, target, frame, table, [], scaleX, scaleY);
        foreach (var combo in combos)
            AppendWideComboObservation(result, target, frame, combo, scaleX, scaleY);
        return DisambiguateVisualIdentities(result);
    }

    public static IReadOnlyList<AutomationObservation> DiscoverLegacySurfaceControls(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls)
        => DiscoverLegacySurfaceControlsCore(target, frame, knownControls, []);

    public static async Task<IReadOnlyList<AutomationObservation>> DiscoverLegacySurfaceControlsAsync(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        CancellationToken cancellationToken = default)
    {
        var words = await WindowsOcrTextRecognizer.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        return await DiscoverLegacySurfaceControlsWithWordsAsync(
            target, frame, knownControls, words, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<AutomationObservation>> DiscoverLegacySurfaceControlsWithWordsAsync(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<VisualTextObservation> words,
        CancellationToken cancellationToken = default)
    {
        var controls = DiscoverLegacySurfaceControlsCore(target, frame, knownControls, words);
        var fields = await RefineFieldLabelsAsync(target, frame, controls, cancellationToken).ConfigureAwait(false);
        var refined = await RefineButtonLabelsAsync(target, frame, fields, words, cancellationToken).ConfigureAwait(false);
        return LabelReportPreviewButton(refined, refined.Any(control => NormalizeControlType(control.ControlType) == "Tree"));
    }

    internal static async Task<IReadOnlyList<AutomationObservation>> DiscoverWithWordsAsync(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> screenRegions,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<VisualTextObservation> words,
        CancellationToken cancellationToken,
        IReadOnlyList<RectI>? excludedScreenRegions = null)
    {
        var controls = DiscoverCore(target, frame, screenRegions, knownControls, words, excludedScreenRegions);
        var fields = await RefineFieldLabelsAsync(target, frame, controls, cancellationToken).ConfigureAwait(false);
        var refined = await RefineButtonLabelsAsync(target, frame, fields, words, cancellationToken).ConfigureAwait(false);
        return LabelReportPreviewButton(refined, refined.Any(control => NormalizeControlType(control.ControlType) == "Tree"));
    }

    private static async Task<IReadOnlyList<AutomationObservation>> RefineFieldLabelsAsync(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> controls,
        CancellationToken cancellationToken)
    {
        var scaleX = frame.Width / (double)Math.Max(1, target.Bounds.Width);
        var scaleY = frame.Height / (double)Math.Max(1, target.Bounds.Height);
        var candidates = controls
            .Select((control, index) => new
            {
                Control = control,
                Index = index,
                PixelBounds = ToPixelRect(Intersect(control.Bounds, target.Bounds), target.Bounds,
                    scaleX, scaleY, frame.Width, frame.Height)
            })
            .Where(item => string.IsNullOrEmpty(item.Control.ParentRuntimeId) &&
                           item.PixelBounds.Width >= 60 &&
                           item.PixelBounds.Height is >= 16 and <= 260 &&
                           NormalizeControlType(item.Control.ControlType) is "Edit" or "ComboBox" or "Button" or "List" &&
                           !HasTintedButtonInterior(frame, item.PixelBounds))
            .ToArray();
        if (candidates.Length == 0) return controls;

        var probes = new List<FieldLabelProbe>(candidates.Length * 2);
        foreach (var candidate in candidates)
        {
            var bounds = candidate.PixelBounds;
            var labelRight = Math.Max(0, bounds.X - 6);
            var labelLeft = Math.Max(0, labelRight - 100);
            if (labelLeft == 0 && labelRight >= 30) labelLeft = Math.Min(6, labelRight - 24);
            var leftWidth = labelRight - labelLeft;
            if (leftWidth >= 24)
            {
                var bandHeight = Math.Min(bounds.Height, bounds.Height <= 56 ? 22 : 32);
                var top = Math.Max(0, bounds.Y + Math.Min(5, Math.Max(0, bounds.Height - bandHeight)));
                probes.Add(new(candidate.Index,
                    new RectI(labelLeft, top, leftWidth,
                        Math.Min(frame.Height - top, bandHeight)), 0));
                var broadTop = Math.Max(0, bounds.Y - 6);
                probes.Add(new(candidate.Index,
                    new RectI(labelLeft, broadTop, leftWidth,
                        Math.Min(frame.Height - broadTop, Math.Min(44, bounds.Height + 12))), 1));
            }
            var aboveHeight = Math.Min(40, bounds.Y);
            if (aboveHeight >= 16)
                probes.Add(new(candidate.Index,
                    new RectI(Math.Max(0, bounds.X - 4), bounds.Y - aboveHeight,
                        Math.Min(frame.Width - Math.Max(0, bounds.X - 4), Math.Min(bounds.Width + 8, 180)),
                        aboveHeight), 2));
        }
        if (probes.Count == 0) return controls;

        var localized = await WindowsOcrTextRecognizer.RecognizeRegionsAsync(
            frame, probes.Select(probe => probe.Bounds).ToArray(), cancellationToken).ConfigureAwait(false);
        if (localized.Count == 0) return controls;

        var result = controls.ToArray();
        foreach (var candidate in candidates)
        {
            var existingLabel = candidate.Control.VisualRole == "field" &&
                                !candidate.Control.Name.StartsWith("Unlabelled", StringComparison.OrdinalIgnoreCase)
                ? NormalizeFieldLabel(candidate.Control.Name)
                : string.Empty;
            var label = existingLabel.Length > 0
                ? existingLabel
                : probes.Select((probe, probeIndex) => (probe, probeIndex))
                    .Where(item => item.probe.ControlIndex == candidate.Index && localized.ContainsKey(item.probeIndex))
                    .OrderBy(item => item.probe.Priority)
                    .Select(item => NormalizeFieldLabel(localized[item.probeIndex]))
                    .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
            if (label.Length == 0) continue;

            var type = LooksLikeComboBox(frame, candidate.PixelBounds) ? "ComboBox" : "Edit";
            var identity = StableVisualIdentity(type, label, $"label:{NormalizeIdentityText(label)}",
                CoarseFingerprint(frame, candidate.PixelBounds));
            result[candidate.Index] = candidate.Control with
            {
                RuntimeId = "visual:v3:" + identity,
                AutomationId = "visual:v3:" + identity,
                Name = label,
                ControlType = "ControlType." + type,
                VisualRole = "field",
                SupportedPatterns = type == "ComboBox" ? ["ExpandCollapse", "Value"] : ["Value"],
                OcrText = IsCommonFieldLabel(candidate.Control.OcrText) ? null : candidate.Control.OcrText
            };
        }
        return DisambiguateVisualIdentities(result);
    }

    internal static string NormalizeFieldLabel(string value)
    {
        var normalized = string.Join(' ', value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim().TrimEnd(':', ';', '.', ',');
        if (normalized.Length is < 2 or > 64 || normalized.Count(char.IsLetter) < 2)
            return string.Empty;
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4)
            return string.Empty;

        var variants = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => NormalizeIdentityText(token))
            .Where(token => token.Length > 0)
            .Append(NormalizeIdentityText(normalized))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var variant in variants)
        {
            if (variant is "hbtlde" or "jobtlde" or "jobtide") return "Job Title";
            if (variant is "3treet") return "Street";
            if (variant is "streetl" or "streeu" or "31reeu") return "Street1";
            if (variant is "lote" or "lotei") return "Note";
        }
        var nearest = variants
            .SelectMany(variant => CommonFieldLabels.Select(label =>
                (Label: label, Compact: NormalizeIdentityText(label), Variant: variant)))
            .Where(item => item.Compact.Length >= 3 &&
                           Math.Abs(item.Compact.Length - item.Variant.Length) <= 1 &&
                           item.Compact[0] == item.Variant[0])
            .Select(item => (item.Label, VariantLength: item.Variant.Length,
                Distance: EditDistance(item.Variant, item.Compact)))
            .OrderBy(item => item.Distance)
            .ThenByDescending(item => item.VariantLength)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return nearest.Label is not null && nearest.Distance <= (nearest.VariantLength >= 6 ? 2 : 1)
            ? nearest.Label
            : normalized;
    }

    private static bool IsCommonFieldLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NormalizeFieldLabel(value);
        return CommonFieldLabels.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<AutomationObservation>> RefineButtonLabelsAsync(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> controls,
        IReadOnlyList<VisualTextObservation> pageWords,
        CancellationToken cancellationToken)
    {
        var scaleX = frame.Width / (double)Math.Max(1, target.Bounds.Width);
        var scaleY = frame.Height / (double)Math.Max(1, target.Bounds.Height);
        var candidates = controls
            .Select((control, index) => new
            {
                Control = control,
                Index = index,
                PixelBounds = ToPixelRect(Intersect(control.Bounds, target.Bounds), target.Bounds,
                    scaleX, scaleY, frame.Width, frame.Height)
            })
            .Where(item => item.Control.ControlType == "ControlType.Button" &&
                           string.IsNullOrEmpty(item.Control.ParentRuntimeId) &&
                           item.PixelBounds.Width is >= 60 and <= 420 &&
                           item.PixelBounds.Height is >= 22 and <= 70)
            .ToArray();
        if (candidates.Length == 0) return controls;

        var localized = await WindowsOcrTextRecognizer.RecognizeRegionsAsync(
            frame, candidates.Select(item => item.PixelBounds).ToArray(), cancellationToken).ConfigureAwait(false);
        if (localized.Count == 0) return controls;

        var vocabulary = pageWords
            .SelectMany(word => word.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(token => token.Length >= 4 && token.All(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = controls.ToArray();
        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            if (!localized.TryGetValue(candidateIndex, out var localizedText)) continue;
            var item = candidates[candidateIndex];
            var selected = SelectLocalizedLabel(item.Control.OcrText ?? item.Control.Name, localizedText, vocabulary);
            if (selected.Length == 0 || string.Equals(selected, item.Control.Name, StringComparison.Ordinal)) continue;
            var type = item.Control.ControlType["ControlType.".Length..];
            var identity = StableVisualIdentity(type, selected, $"label:{NormalizeIdentityText(selected)}", "");
            var id = "visual:v3:" + identity;
            result[item.Index] = item.Control with
            {
                RuntimeId = id,
                AutomationId = id,
                Name = selected,
                OcrText = selected
            };
        }
        return DisambiguateVisualIdentities(result);
    }

    internal static string SelectLocalizedLabel(
        string current,
        string localized,
        IReadOnlyList<string> pageVocabulary)
    {
        var corrected = string.Join(' ', localized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => CorrectOcrToken(token, pageVocabulary))).Trim();
        if (corrected.Length == 0) return current;
        return LabelQuality(corrected) >= LabelQuality(current) ? corrected : current;
    }

    private static string CorrectOcrToken(string token, IReadOnlyList<string> vocabulary)
    {
        var raw = token.Trim();
        var trimmed = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        if (trimmed.Length < 4) return trimmed;
        var nearest = vocabulary
            .Where(candidate => Math.Abs(candidate.Length - trimmed.Length) <= 1 &&
                                candidate.Length >= 2 &&
                                trimmed.StartsWith(candidate[..2], StringComparison.OrdinalIgnoreCase))
            .Select(candidate => (Value: candidate, Distance: EditDistance(trimmed, candidate)))
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var containsNoise = raw.Any(character => !char.IsLetter(character));
        return nearest.Value is not null &&
               (containsNoise && nearest.Distance <= 3 || !containsNoise && nearest.Distance == 2)
            ? nearest.Value
            : trimmed;
    }

    private static int LabelQuality(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return value.Count(char.IsLetterOrDigit) * 2 + tokens.Length * 3 -
               value.Count(character => !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character)) * 5 -
               tokens.Count(token => token.Length <= 2) * 3;
    }

    private static int EditDistance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var row = 1; row <= first.Length; row++)
        {
            var current = new int[second.Length + 1];
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] +
                    (char.ToUpperInvariant(first[row - 1]) == char.ToUpperInvariant(second[column - 1]) ? 0 : 1));
            previous = current;
        }
        return previous[second.Length];
    }

    private static IReadOnlyList<AutomationObservation> DiscoverLegacySurfaceControlsCore(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<VisualTextObservation> words)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(knownControls);
        if (frame.Width < 24 || frame.Height < 16 || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            return [];

        var scaleX = frame.Width / (double)target.Bounds.Width;
        var scaleY = frame.Height / (double)target.Bounds.Height;
        var structuralRectangles = ExpandGridRows(
            frame,
            FindRectangles(frame, new RectI(0, 0, frame.Width, frame.Height), allowWideField: true));
        var tables = FindKnownGridTables(target, frame, knownControls, scaleX, scaleY, [])
            .Concat(FindSparseTableGroups(structuralRectangles, words))
            .GroupBy(table => table.Bounds)
            .Select(group => group.First())
            .ToArray();
        var combos = FindWideDatabaseGridCombos(target, frame, knownControls, tables, scaleX, scaleY);
        var trees = FindClassicTrees(target, frame, knownControls, words, scaleX, scaleY);
        var tabStrips = FindClassicTabStrips(frame, words);
        var radioButtons = FindClassicRadioButtons(frame, words);
        var result = new List<AutomationObservation>();
        foreach (var table in tables)
            AppendTableObservations(result, target, frame, table, words, scaleX, scaleY);
        foreach (var combo in combos)
            AppendWideComboObservation(result, target, frame, combo, scaleX, scaleY);
        foreach (var tree in trees)
            AppendTreeObservations(result, target, frame, tree, scaleX, scaleY);
        foreach (var tabStrip in tabStrips)
            AppendTabStripObservations(result, target, frame, tabStrip, scaleX, scaleY);
        AppendRadioButtonObservations(result, target, frame, radioButtons, scaleX, scaleY);

        var occupied = tables.Select(table => table.Bounds)
            .Concat(combos)
            .Concat(trees.Select(tree => tree.Bounds))
            .Concat(tabStrips.Select(tabStrip => tabStrip.Bounds))
            .Concat(radioButtons.Select(item => item.Bounds))
            .ToArray();
        var retained = new List<RectI>();
        foreach (var bounds in structuralRectangles
                     .Where(bounds => bounds.Width is >= 60 and <= 420 && bounds.Height is >= 22 and <= 70)
                     .Where(bounds => !occupied.Any(region => ContainsCenter(region, bounds)))
                     .OrderByDescending(bounds => (long)bounds.Width * bounds.Height))
        {
            var screenBounds = ToScreenRect(bounds, target.Bounds, scaleX, scaleY);
            if (knownControls.Any(control => IsCoveredBySemanticControl(control, screenBounds))) continue;
            if (retained.Any(existing => OverlapRatio(existing, bounds) >= .78)) continue;
            retained.Add(bounds);
        }
        foreach (var bounds in retained)
        {
            var containedText = TextInside(bounds, words);
            // Text enclosed by an independently detected frame belongs to that
            // frame. Do not replace it with a nearby word on the left: calendar
            // toolbars commonly put a date immediately before the next button,
            // which used to turn "Next Month" into an Edit named "2026".
            var fieldLabel = NearestFieldLabel(bounds, words);
            var type = ClassifyRectangle(frame, bounds, containedText, fieldLabel, words);
            var role = type == "Button" ? "button" : "field";
            var name = (type is "Edit" or "ComboBox") && fieldLabel.Length > 0
                ? fieldLabel
                : containedText;
            if (name.Length == 0)
                name = type == "Button" ? "Unlabelled button" : "Unlabelled field";
            var identity = StableVisualIdentity(type, name,
                StructureToken(bounds, new RectI(0, 0, frame.Width, frame.Height), role),
                CoarseFingerprint(frame, bounds));
            var id = "visual:v3:" + identity;
            if (!HasIndependentControlGeometry(bounds, words, fieldLabel)) continue;
            result.Add(CreateObservation(target, bounds, scaleX, scaleY, id, "", name,
                type, role, "", null, null, type switch
                {
                    "Button" => ["Invoke"],
                    "ComboBox" => ["ExpandCollapse", "Value"],
                    _ => ["Value"]
                }, containedText));
        }
        return DisambiguateVisualIdentities(LabelReportPreviewButton(result, trees.Count > 0));
    }

    internal static IReadOnlyList<AutomationObservation> DiscoverCore(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> screenRegions,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<VisualTextObservation> words,
        IReadOnlyList<RectI>? excludedScreenRegions = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(screenRegions);
        ArgumentNullException.ThrowIfNull(knownControls);
        if (frame.Width < 24 || frame.Height < 16 || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            return [];

        var scaleX = frame.Width / (double)target.Bounds.Width;
        var scaleY = frame.Height / (double)target.Bounds.Height;
        var excludedPixelRegions = (excludedScreenRegions ?? [])
            .Select(region => ToPixelRect(Intersect(region, target.Bounds), target.Bounds, scaleX, scaleY, frame.Width, frame.Height))
            .Where(region => region.Width > 0 && region.Height > 0)
            .ToArray();
        if (excludedPixelRegions.Length > 0)
        {
            words = words.Where(word => !excludedPixelRegions.Any(region => ContainsPoint(region,
                word.Bounds.X + word.Bounds.Width / 2,
                word.Bounds.Y + word.Bounds.Height / 2))).ToArray();
        }
        var pixelRegions = screenRegions
            .Select(region => ToPixelRect(Intersect(region, target.Bounds), target.Bounds, scaleX, scaleY, frame.Width, frame.Height))
            .Where(region => region.Width >= 24 && region.Height >= 16)
            .Distinct()
            .ToArray();
        if (pixelRegions.Length == 0)
            pixelRegions = [new RectI(0, 0, frame.Width, frame.Height)];

        // Classic Win32/Delphi database grids often expose only one native
        // container. Their cell borders are still reliable in the captured
        // pixels, so recognize the complete grid before the generic rectangle
        // budget is spent on glyph-sized shapes inside its cells.
        var knownGridTables = FindKnownGridTables(
            target, frame, knownControls, scaleX, scaleY, excludedPixelRegions);
        var knownGridCombos = FindWideDatabaseGridCombos(
            target, frame, knownControls, knownGridTables, scaleX, scaleY);
        var classicTrees = FindClassicTrees(target, frame, knownControls, words, scaleX, scaleY);
        var classicTabStrips = FindClassicTabStrips(frame, words);
        var classicRadioButtons = FindClassicRadioButtons(frame, words);
        var opaqueGalleryButtons = DiscoverOpaqueGalleryButtons(
            target, frame, knownControls, words, scaleX, scaleY);
        var templateGalleryRegions = knownControls
            .Where(VisualFallbackPolicy.IsTemplateGalleryContainer)
            .Select(gallery => TemplateGallerySearchPixels(
                ToPixelRect(Intersect(gallery.Bounds, target.Bounds), target.Bounds,
                    scaleX, scaleY, frame.Width, frame.Height), frame.Width, frame.Height))
            .Where(bounds => bounds.IsValid)
            .ToArray();

        var rectangles = new List<RectI>();
        foreach (var region in pixelRegions)
            rectangles.AddRange(FindRectangles(frame, region, allowWideField: true));
        if (templateGalleryRegions.Length > 0)
            rectangles = rectangles
                .Where(bounds => !templateGalleryRegions.Any(region => ContainsCenter(region, bounds)))
                .ToList();
        if (knownGridTables.Count > 0)
            rectangles = rectangles
                .Where(bounds => !knownGridTables.Any(table => ContainsCenter(table.Bounds, bounds)))
                .ToList();
        if (knownGridCombos.Count > 0)
            rectangles = rectangles
                .Where(bounds => !knownGridCombos.Any(combo => ContainsCenter(combo, bounds)))
                .ToList();
        if (classicTrees.Count > 0)
            rectangles = rectangles
                .Where(bounds => !classicTrees.Any(tree => ContainsCenter(tree.Bounds, bounds)))
                .ToList();
        if (classicTabStrips.Count > 0)
            rectangles = rectangles
                .Where(bounds => !classicTabStrips.Any(strip => ContainsCenter(strip.Bounds, bounds)))
                .ToList();
        if (classicRadioButtons.Count > 0)
            rectangles = rectangles
                .Where(bounds => !classicRadioButtons.Any(item => ContainsCenter(item.Bounds, bounds)))
                .ToList();

        var retained = new List<RectI>();
        foreach (var rectangle in rectangles
                     .OrderByDescending(bounds => IsPriorityInteractiveRectangle(bounds))
                     .ThenByDescending(bounds => IsPriorityInteractiveRectangle(bounds)
                         ? (long)bounds.Width * bounds.Height
                         : -(long)bounds.Width * bounds.Height)
                     .ThenBy(bounds => bounds.Y)
                     .ThenBy(bounds => bounds.X))
        {
            if (retained.Any(existing => OverlapRatio(existing, rectangle) >= .72)) continue;
            retained.Add(rectangle);
        }

        retained = ExpandGridRows(frame, retained)
            .OrderBy(bounds => (long)bounds.Width * bounds.Height)
            .ThenBy(bounds => bounds.Y)
            .ThenBy(bounds => bounds.X)
            .ToList();

        retained = retained
            .Where(pixelBounds =>
            {
                if (excludedPixelRegions.Any(region => ContainsPoint(region,
                        pixelBounds.X + pixelBounds.Width / 2,
                        pixelBounds.Y + pixelBounds.Height / 2)))
                    return false;
                var bounds = ToScreenRect(pixelBounds, target.Bounds, scaleX, scaleY);
                return !knownControls.Any(control => control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                                                     (IntersectionOverUnion(control.Bounds, bounds) >= .72 ||
                                                      IsCoveredBySemanticControl(control, bounds)));
            })
            .ToList();

        var result = new List<AutomationObservation>();
        var consumed = new HashSet<RectI>();
        var tables = knownGridTables
            .Concat(FindTableGroups(retained))
            .Concat(FindSparseTableGroups(retained, words))
            .GroupBy(table => table.Bounds)
            .Select(group => group.First())
            .ToArray();
        foreach (var table in tables)
        {
            foreach (var cell in table.Cells) consumed.Add(cell.Bounds);
            AppendTableObservations(result, target, frame, table, words, scaleX, scaleY);
        }
        foreach (var combo in knownGridCombos)
            AppendWideComboObservation(result, target, frame, combo, scaleX, scaleY);
        foreach (var tree in classicTrees)
            AppendTreeObservations(result, target, frame, tree, scaleX, scaleY);
        foreach (var tabStrip in classicTabStrips)
            AppendTabStripObservations(result, target, frame, tabStrip, scaleX, scaleY);
        AppendRadioButtonObservations(result, target, frame, classicRadioButtons, scaleX, scaleY);

        var ungrouped = retained
            .Where(bounds => !consumed.Contains(bounds))
            .Where(bounds => !tables.Any(table => ContainsCenter(table.Bounds, bounds)))
            .ToArray();
        var lists = FindListGroups(ungrouped, words);
        foreach (var list in lists)
        {
            foreach (var item in list.Items) consumed.Add(item);
            var listText = TextInside(list.Bounds, words);
            var listIdentity = StableVisualIdentity("List", "",
                StructureToken(list.Bounds, new RectI(0, 0, frame.Width, frame.Height), "vertical-items"), "");
            var listId = "visual:v3:" + listIdentity;
            result.Add(CreateObservation(target, list.Bounds, scaleX, scaleY, listId, "", listText,
                "List", "list", listId, null, null, ["Selection"]));
            for (var index = 0; index < list.Items.Count; index++)
            {
                var itemBounds = list.Items[index];
                var itemText = TextInside(itemBounds, words);
                var itemIdentity = StableVisualIdentity("ListItem", $"{listIdentity}|{itemText}",
                    $"{listIdentity}|item:{index}", itemText.Length == 0 ? CoarseFingerprint(frame, itemBounds) : "");
                var itemId = "visual:v3:" + itemIdentity;
                result.Add(CreateObservation(target, itemBounds, scaleX, scaleY, itemId, listId, itemText,
                    "ListItem", "list-item", listId, null, null, ["SelectionItem"]));
            }
        }

        foreach (var pixelBounds in retained.Where(bounds => !consumed.Contains(bounds))
                     .OrderBy(bounds => bounds.Y).ThenBy(bounds => bounds.X))
        {
            if (tables.Any(table => ContainsCenter(table.Bounds, pixelBounds)) ||
                lists.Any(list => ContainsCenter(list.Bounds, pixelBounds)) ||
                knownGridCombos.Any(combo => ContainsCenter(combo, pixelBounds)) ||
                classicTrees.Any(tree => ContainsCenter(tree.Bounds, pixelBounds)) ||
                classicTabStrips.Any(strip => ContainsCenter(strip.Bounds, pixelBounds)))
                continue;
            var containedText = TextInside(pixelBounds, words);
            if (IsNarrowUnlabelledFragment(pixelBounds, containedText)) continue;
            var fieldLabel = NearestFieldLabel(pixelBounds, words);
            if (!HasIndependentControlGeometry(pixelBounds, words, fieldLabel)) continue;
            var type = ClassifyRectangle(frame, pixelBounds, containedText, fieldLabel, words);
            var label = (type is "Edit" or "ComboBox") && fieldLabel.Length > 0 ? fieldLabel : containedText;
            var role = type switch
            {
                "Edit" or "ComboBox" => "field",
                "List" => "list",
                _ => "button"
            };
            var structure = label.Length > 0
                ? $"label:{NormalizeIdentityText(label)}"
                : StructureToken(pixelBounds, new RectI(0, 0, frame.Width, frame.Height), role);
            var identity = StableVisualIdentity(type, label, structure, CoarseFingerprint(frame, pixelBounds));
            var id = "visual:v3:" + identity;
            result.Add(CreateObservation(target, pixelBounds, scaleX, scaleY, id, "", label,
                type, role, "", null, null, type switch
                {
                    "Edit" => ["Value"],
                    "ComboBox" => ["ExpandCollapse", "Value"],
                    "List" => ["Selection"],
                    _ => ["Invoke"]
                }, type is "Edit" or "ComboBox"
                     ? containedText.Length > 0 ? containedText : label
                     : null));
        }
        foreach (var galleryButton in opaqueGalleryButtons)
        {
            var duplicate = result.FindIndex(existing =>
                NormalizeControlType(existing.ControlType) == "Button" &&
                (existing.Name.Equals(galleryButton.Name, StringComparison.OrdinalIgnoreCase) ||
                 IntersectionOverUnion(existing.Bounds, galleryButton.Bounds) >= .72));
            if (duplicate < 0)
            {
                result.Add(galleryButton);
            }
            else if (result[duplicate].Name.StartsWith("Unlabelled", StringComparison.OrdinalIgnoreCase))
            {
                result[duplicate] = galleryButton;
            }
        }
        return DisambiguateVisualIdentities(LabelReportPreviewButton(result, classicTrees.Count > 0));
    }

    internal static IReadOnlyList<AutomationObservation> DiscoverOpaqueGalleryButtons(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<VisualTextObservation> words,
        double? horizontalScale = null,
        double? verticalScale = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(knownControls);
        ArgumentNullException.ThrowIfNull(words);
        var scaleX = horizontalScale ?? frame.Width / (double)Math.Max(1, target.Bounds.Width);
        var scaleY = verticalScale ?? frame.Height / (double)Math.Max(1, target.Bounds.Height);
        var result = new List<AutomationObservation>();

        foreach (var gallery in knownControls.Where(VisualFallbackPolicy.IsOpaqueGalleryContainer))
        {
            var galleryPixels = ToPixelRect(
                Intersect(gallery.Bounds, target.Bounds), target.Bounds,
                scaleX, scaleY, frame.Width, frame.Height);
            if (galleryPixels.Width < 80 || galleryPixels.Height < 24) continue;

            var isTemplateGallery = VisualFallbackPolicy.IsTemplateGalleryContainer(gallery);
            var gallerySearchPixels = isTemplateGallery
                ? TemplateGallerySearchPixels(galleryPixels, frame.Width, frame.Height)
                : galleryPixels;

            var galleryWords = words.Where(word =>
                    CenterInside(gallerySearchPixels, word.Bounds) &&
                    word.Text.Count(char.IsLetterOrDigit) >= 2)
                .ToArray();
            IReadOnlyList<TextSegment> segments = TextLineSegments(galleryWords)
                .Where(segment => segment.Bounds.Height is >= 6 and <= 32 &&
                                  segment.Text.Count(char.IsLetterOrDigit) >= 2)
                .OrderBy(segment => segment.Bounds.Y)
                .ThenBy(segment => segment.Bounds.X)
                .ToArray();
            if (isTemplateGallery)
                segments = SelectTemplateGalleryLabels(segments, galleryPixels);
            if (segments.Count < 2) continue;

            var templateCards = isTemplateGallery
                ? FindRectangles(frame, gallerySearchPixels, allowWideField: true)
                    .Where(bounds => bounds.Width is >= 56 and <= 300 &&
                                     bounds.Height is >= 42 and <= 190)
                    .ToArray()
                : [];

            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                RectI itemPixels;
                if (isTemplateGallery)
                {
                    itemPixels = ResolveTemplateGalleryItemBounds(
                        segment, index, segments, templateCards, galleryPixels, gallerySearchPixels);
                }
                else
                {
                    var horizontalPadding = Math.Clamp(segment.Bounds.Height * 2, 18, 34);
                    var verticalPadding = Math.Clamp(segment.Bounds.Height / 2, 4, 10);
                    itemPixels = Intersect(
                        new RectI(
                            segment.Bounds.X - horizontalPadding,
                            segment.Bounds.Y - verticalPadding,
                            segment.Bounds.Width + horizontalPadding + 8,
                            segment.Bounds.Height + verticalPadding * 2),
                        galleryPixels);
                }
                if (itemPixels.Width < 30 || itemPixels.Height < 16) continue;

                var label = string.Join(' ', segment.Text
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                var identity = StableVisualIdentity(
                    "Button", label,
                    $"gallery:{gallery.AutomationId}:{index}",
                    CoarseFingerprint(frame, itemPixels));
                var id = "visual:v3:" + identity;
                result.Add(CreateObservation(
                    target, itemPixels, scaleX, scaleY, id, gallery.RuntimeId, label,
                    "Button", "button", gallery.RuntimeId, null, index, ["Invoke"], label));
            }
        }

        return result
            .GroupBy(control => control.ParentRuntimeId + "\u001f" + control.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static RectI TemplateGallerySearchPixels(RectI galleryPixels, int frameWidth, int frameHeight) =>
        Intersect(new RectI(
                galleryPixels.X,
                galleryPixels.Y,
                galleryPixels.Width,
                galleryPixels.Height + Math.Clamp(galleryPixels.Height / 2, 64, 112)),
            new RectI(0, 0, frameWidth, frameHeight));

    internal static IReadOnlyList<AutomationObservation> RealignOfficeBackstageControls(
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight,
        IReadOnlyList<AutomationObservation> controls,
        IReadOnlyList<VisualTextObservation> words)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(words);
        if (!windowBounds.IsValid || pixelWidth <= 0 || pixelHeight <= 0 || words.Count == 0)
            return controls;

        var byRuntimeId = controls
            .Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var scaleX = pixelWidth / (double)Math.Max(1, windowBounds.Width);
        var scaleY = pixelHeight / (double)Math.Max(1, windowBounds.Height);
        var segments = TextLineSegments(words)
            .Where(segment => segment.Text.Count(char.IsLetterOrDigit) >= 2)
            .ToArray();
        var result = new AutomationObservation[controls.Count];
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            result[index] = control;
            if (control.IsOffscreen || !control.Bounds.IsValid || string.IsNullOrWhiteSpace(control.Name) ||
                control.Name.Equals("[redacted]", StringComparison.OrdinalIgnoreCase) ||
                !byRuntimeId.TryGetValue(control.ParentRuntimeId, out var parent) ||
                !parent.ClassName.Contains("NetUISlabContainer", StringComparison.OrdinalIgnoreCase))
                continue;
            var type = NormalizeControlType(control.ControlType);
            if (type is not ("Button" or "Hyperlink") || control.Bounds.Height > 64 || control.Bounds.Width > 640)
                continue;

            var pixelBounds = ToPixelRect(
                Intersect(control.Bounds, windowBounds), windowBounds,
                scaleX, scaleY, pixelWidth, pixelHeight);
            if (!pixelBounds.IsValid) continue;
            var matching = segments
                .Where(segment => EquivalentLabel(control.Name, segment.Text))
                .ToArray();
            if (matching.Length == 0 || matching.Any(segment => CenterInside(pixelBounds, segment.Bounds)))
                continue;

            var centerX = pixelBounds.X + pixelBounds.Width / 2;
            var centerY = pixelBounds.Y + pixelBounds.Height / 2;
            var match = matching
                .Where(segment =>
                    Math.Abs(segment.Bounds.X + segment.Bounds.Width / 2 - centerX) <=
                    Math.Max(90, pixelBounds.Width) &&
                    Math.Abs(segment.Bounds.Y + segment.Bounds.Height / 2 - centerY) <= 140)
                .OrderBy(segment =>
                    Math.Abs(segment.Bounds.Y + segment.Bounds.Height / 2 - centerY) * 3 +
                    Math.Abs(segment.Bounds.X + segment.Bounds.Width / 2 - centerX))
                .FirstOrDefault();
            if (match is null) continue;

            var alignedPixels = new RectI(
                match.Bounds.X + match.Bounds.Width / 2 - pixelBounds.Width / 2,
                match.Bounds.Y + match.Bounds.Height / 2 - pixelBounds.Height / 2,
                pixelBounds.Width,
                pixelBounds.Height);
            var aligned = ToScreenRect(alignedPixels, windowBounds, scaleX, scaleY);
            result[index] = control with { Bounds = aligned };
        }
        return result;
    }

    private static bool EquivalentLabel(string expected, string observed)
    {
        var left = NormalizeIdentityText(expected);
        var right = NormalizeIdentityText(observed);
        return left.Length > 0 && right.Length > 0 &&
               (left.Equals(right, StringComparison.Ordinal) ||
                left.Length >= 5 && right.Length >= 5 &&
                (left.Contains(right, StringComparison.Ordinal) ||
                 right.Contains(left, StringComparison.Ordinal)));
    }

    private static IReadOnlyList<TextSegment> SelectTemplateGalleryLabels(
        IReadOnlyList<TextSegment> segments,
        RectI galleryPixels)
    {
        var candidates = segments
            .Where(segment => segment.Bounds.Y + segment.Bounds.Height / 2 >=
                              galleryPixels.Y + galleryPixels.Height / 2)
            .OrderBy(segment => segment.Bounds.Y + segment.Bounds.Height / 2)
            .ThenBy(segment => segment.Bounds.X)
            .ToArray();
        var rows = new List<List<TextSegment>>();
        foreach (var segment in candidates)
        {
            var centerY = segment.Bounds.Y + segment.Bounds.Height / 2;
            var row = rows.FirstOrDefault(existing =>
            {
                var rowCenter = existing.Sum(item => item.Bounds.Y + item.Bounds.Height / 2d) / existing.Count;
                return Math.Abs(rowCenter - centerY) <= Math.Max(8, segment.Bounds.Height);
            });
            if (row is null)
            {
                row = [];
                rows.Add(row);
            }
            row.Add(segment);
        }

        return rows
            .Where(row => row.Count >= 2)
            .OrderByDescending(row => row.Average(item => item.Bounds.Y + item.Bounds.Height / 2d))
            .ThenByDescending(row => row.Count)
            .Select(row => (IReadOnlyList<TextSegment>)row.OrderBy(item => item.Bounds.X).ToArray())
            .FirstOrDefault() ?? [];
    }

    private static RectI ResolveTemplateGalleryItemBounds(
        TextSegment label,
        int index,
        IReadOnlyList<TextSegment> labels,
        IReadOnlyList<RectI> cardCandidates,
        RectI galleryPixels,
        RectI gallerySearchPixels)
    {
        var labelCenterX = label.Bounds.X + label.Bounds.Width / 2;
        var previousCenter = index > 0
            ? labels[index - 1].Bounds.X + labels[index - 1].Bounds.Width / 2
            : labelCenterX;
        var nextCenter = index + 1 < labels.Count
            ? labels[index + 1].Bounds.X + labels[index + 1].Bounds.Width / 2
            : labelCenterX;
        var nearestPitch = index > 0 && index + 1 < labels.Count
            ? Math.Min(labelCenterX - previousCenter, nextCenter - labelCenterX)
            : index > 0 ? labelCenterX - previousCenter
            : index + 1 < labels.Count ? nextCenter - labelCenterX
            : 180;
        var width = Math.Clamp((int)Math.Round(nearestPitch * .78),
            Math.Max(96, label.Bounds.Width + 20), 220);
        var top = Math.Max(galleryPixels.Y,
            label.Bounds.Y - Math.Clamp(galleryPixels.Height * 3 / 4, 72, 160));
        var estimated = Intersect(new RectI(
            labelCenterX - width / 2,
            top,
            width,
            label.Bounds.Y + label.Bounds.Height + 10 - top), gallerySearchPixels);
        var card = cardCandidates
            .Where(bounds => bounds.Y < label.Bounds.Y &&
                             bounds.Y + bounds.Height <= label.Bounds.Y + 8 &&
                             label.Bounds.Y - (bounds.Y + bounds.Height) <= 72 &&
                             Math.Abs(bounds.X + bounds.Width / 2 - labelCenterX) <=
                             Math.Max(72, bounds.Width / 2 + 12))
            .OrderBy(bounds => Math.Abs(bounds.X + bounds.Width / 2 - labelCenterX))
            .ThenByDescending(bounds => (long)bounds.Width * bounds.Height)
            .FirstOrDefault();
        if (card is { Width: > 0, Height: > 0 })
        {
            var union = Union([card, label.Bounds, estimated]);
            return Intersect(new RectI(
                Math.Max(gallerySearchPixels.X, union.X - 4),
                Math.Max(gallerySearchPixels.Y, union.Y - 4),
                union.Width + 8,
                union.Height + 12), gallerySearchPixels);
        }

        return estimated;
    }

    private static void AppendTableObservations(
        List<AutomationObservation> result,
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        TableGroup table,
        IReadOnlyList<VisualTextObservation> words,
        double scaleX,
        double scaleY)
    {
        var headerTextByColumn = table.Cells
            .Where(cell => cell.Row == 0)
            .Select(cell => (cell.Column, Text: TextInside(cell.Bounds, words)))
            .Where(item => item.Text.Length > 0)
            .GroupBy(item => item.Column)
            .ToDictionary(group => group.Key, group => group.First().Text);
        var hasHeaderRow = table.HasHeaderRow ||
                           headerTextByColumn.Count >= Math.Max(2, table.ColumnCount / 3);
        var tableText = hasHeaderRow
            ? string.Join(' ', headerTextByColumn.OrderBy(item => item.Key).Select(item => item.Value))
            : string.Empty;
        var tableIdentity = StableVisualIdentity("Table", "",
            StructureToken(table.Bounds, new RectI(0, 0, frame.Width, frame.Height), $"columns:{table.ColumnCount}"),
            "");
        var tableId = "visual:v3:" + tableIdentity;
        result.Add(CreateObservation(target, table.Bounds, scaleX, scaleY, tableId, "", tableText,
            "Table", "table", tableId, null, null, ["Grid"]));
        foreach (var cell in table.Cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column))
        {
            var isHeader = hasHeaderRow && cell.Row == 0;
            // Header labels describe the table structure and are safe to keep.
            // Body values can contain customer data, so preserve only their
            // clickable geometry and row/column coordinates.
            var cellText = isHeader ? headerTextByColumn.GetValueOrDefault(cell.Column, "") : "";
            var cellType = isHeader ? "HeaderItem" : "DataItem";
            var cellRole = isHeader ? "column-header" : "table-cell";
            var cellIdentity = StableVisualIdentity(cellType, isHeader ? cellText : "",
                $"{tableIdentity}|row:{cell.Row}|column:{cell.Column}", "");
            var cellId = "visual:v3:" + cellIdentity;
            result.Add(CreateObservation(target, cell.Bounds, scaleX, scaleY, cellId, tableId, cellText,
                cellType, cellRole, tableId, cell.Row, cell.Column,
                isHeader ? ["Invoke"] : ["GridItem", "SelectionItem"]));
        }
    }

    private static void AppendWideComboObservation(
        List<AutomationObservation> result,
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI combo,
        double scaleX,
        double scaleY)
    {
        var identity = StableVisualIdentity("ComboBox", "View filter",
            StructureToken(combo, new RectI(0, 0, frame.Width, frame.Height), "database-grid-filter"), "");
        var id = "visual:v3:" + identity;
        result.Add(CreateObservation(target, combo, scaleX, scaleY, id, "", "View filter",
            "ComboBox", "combo-box", "", null, null, ["ExpandCollapse", "Selection"]));
    }

    private static void AppendTreeObservations(
        List<AutomationObservation> result,
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        ClassicTreeGroup tree,
        double scaleX,
        double scaleY)
    {
        var treeIdentity = StableVisualIdentity("Tree", "",
            StructureToken(tree.Bounds, new RectI(0, 0, frame.Width, frame.Height), "classic-tree"), "");
        var treeId = "visual:v3:" + treeIdentity;
        result.Add(CreateObservation(target, tree.Bounds, scaleX, scaleY, treeId, "", "Report tree",
            "Tree", "tree", treeId, null, null, ["Selection"]));
        for (var index = 0; index < tree.Rows.Count; index++)
        {
            var row = tree.Rows[index];
            var name = row.Name.Length > 0 ? row.Name : $"Tree item {index + 1}";
            var identity = StableVisualIdentity("TreeItem", name,
                $"{treeIdentity}|item:{index}|indent:{row.Indent}", "");
            var id = "visual:v3:" + identity;
            var patterns = row.CanExpand
                ? new[] { "SelectionItem", "ExpandCollapse", "Invoke" }
                : new[] { "SelectionItem", "Invoke" };
            result.Add(CreateObservation(target, row.Bounds, scaleX, scaleY, id, treeId, name,
                "TreeItem", "tree-item", treeId, index, row.Indent, patterns));
        }
    }

    private static void AppendTabStripObservations(
        List<AutomationObservation> result,
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        ClassicTabStrip strip,
        double scaleX,
        double scaleY)
    {
        var stripIdentity = StableVisualIdentity("Tab", "",
            StructureToken(strip.Bounds, new RectI(0, 0, frame.Width, frame.Height), "classic-tabs"), "");
        var stripId = "visual:v3:" + stripIdentity;
        result.Add(CreateObservation(target, strip.Bounds, scaleX, scaleY, stripId, "", "Tabs",
            "Tab", "tab-list", stripId, null, null, ["Selection"]));
        for (var index = 0; index < strip.Items.Count; index++)
        {
            var item = strip.Items[index];
            var identity = StableVisualIdentity("TabItem", item.Name,
                $"{stripIdentity}|item:{index}", "");
            var id = "visual:v3:" + identity;
            result.Add(CreateObservation(target, item.Bounds, scaleX, scaleY, id, stripId, item.Name,
                "TabItem", "tab", stripId, null, index, ["SelectionItem", "Invoke"]));
        }
    }

    private static IReadOnlyList<ClassicRadioButton> FindClassicRadioButtons(
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<VisualTextObservation> words)
    {
        var result = new List<ClassicRadioButton>();
        foreach (var line in words.GroupBy(word => word.LineIndex))
        {
            var ordered = line.OrderBy(word => word.Bounds.X).ToArray();
            if (ordered.Length == 0) continue;
            var textBounds = Union(ordered.Select(word => word.Bounds));
            if (textBounds.X < frame.Width * .50 || textBounds.Height is < 6 or > 28) continue;
            var label = string.Join(' ', ordered.Select(word => word.Text)).Trim();
            if (label.Length is < 2 or > 120) continue;
            var indicator = FindRadioIndicator(frame, textBounds);
            if (indicator is null) continue;
            var left = indicator.X;
            var top = Math.Min(indicator.Y, textBounds.Y - 2);
            var right = textBounds.X + textBounds.Width + 4;
            var bottom = Math.Max(indicator.Y + indicator.Height, textBounds.Y + textBounds.Height + 2);
            result.Add(new(new RectI(left, top, Math.Max(16, right - left), Math.Max(14, bottom - top)), label));
        }
        var orderedResult = result
            .GroupBy(item => (item.Bounds.Y, NormalizeIdentityText(item.Name)))
            .Select(group => group.First())
            .OrderBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.X)
            .ToArray();
        for (var index = 1; index < orderedResult.Length; index++)
            if (orderedResult[index - 1].Name.Equals("For Last", StringComparison.OrdinalIgnoreCase) &&
                orderedResult[index].Name.Length <= 6)
                orderedResult[index] = orderedResult[index] with { Name = "This" };
        return orderedResult;
    }

    private static RectI? FindRadioIndicator(OpaqueSurfaceScanner.PixelFrame frame, RectI textBounds)
    {
        var left = Math.Max(1, textBounds.X - 24);
        var right = Math.Min(frame.Width - 2, textBounds.X - 2);
        var top = Math.Max(1, textBounds.Y - 3);
        var bottom = Math.Min(frame.Height - 2, textBounds.Y + textBounds.Height + 3);
        if (right <= left || bottom <= top) return null;
        var points = new List<(int X, int Y)>();
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
            if (ColorDistance(frame, x, y, x - 1, y) >= 24 ||
                ColorDistance(frame, x, y, x, y - 1) >= 24)
                points.Add((x, y));
        if (points.Count < 6) return null;
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        if (width is < 5 or > 18 || height is < 5 or > 18 || Math.Abs(width - height) > 6)
            return null;
        return new RectI(Math.Max(0, minX - 1), Math.Max(0, minY - 1), width + 2, height + 2);
    }

    private static void AppendRadioButtonObservations(
        List<AutomationObservation> result,
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<ClassicRadioButton> radioButtons,
        double scaleX,
        double scaleY)
    {
        foreach (var radio in radioButtons)
        {
            var identity = StableVisualIdentity("RadioButton", radio.Name,
                StructureToken(radio.Bounds, new RectI(0, 0, frame.Width, frame.Height), "radio-button"), "");
            var id = "visual:v3:" + identity;
            result.Add(CreateObservation(target, radio.Bounds, scaleX, scaleY, id, "", radio.Name,
                "RadioButton", "radio-button", "", null, null, ["SelectionItem", "Invoke"], radio.Name));
        }
    }

    private static IReadOnlyList<AutomationObservation> LabelReportPreviewButton(
        IReadOnlyList<AutomationObservation> controls,
        bool hasReportTree)
    {
        if (!hasReportTree) return controls;
        var help = controls.FirstOrDefault(control =>
            NormalizeControlType(control.ControlType) == "Button" &&
            control.Name.Equals("Help", StringComparison.OrdinalIgnoreCase));
        if (help is null) return controls;
        var preview = controls.FirstOrDefault(control =>
            NormalizeControlType(control.ControlType) == "Button" &&
            control.Name.StartsWith("Unlabelled", StringComparison.OrdinalIgnoreCase) &&
            control.Bounds.X < help.Bounds.X &&
            Math.Abs(control.Bounds.Y - help.Bounds.Y) <= 4 &&
            Math.Abs(control.Bounds.Height - help.Bounds.Height) <= 4 &&
            help.Bounds.X - (control.Bounds.X + control.Bounds.Width) is >= 0 and <= 24);
        if (preview is null) return controls;
        var identity = StableVisualIdentity("Button", "Preview", "label:preview", "");
        return controls.Select(control => !ReferenceEquals(control, preview) ? control : control with
        {
            RuntimeId = "visual:v3:" + identity,
            AutomationId = "visual:v3:" + identity,
            Name = "Preview",
            OcrText = "Preview"
        }).ToArray();
    }

    private static bool ContainsPoint(RectI bounds, int x, int y) =>
        x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;

    private static AutomationObservation CreateObservation(
        WindowTarget target,
        RectI pixelBounds,
        double scaleX,
        double scaleY,
        string id,
        string parentId,
        string text,
        string type,
        string role,
        string groupId,
        int? row,
        int? column,
        IReadOnlyList<string> patterns,
        string? ocrText = null)
    {
        var name = string.IsNullOrWhiteSpace(text)
            ? type switch
            {
                "Edit" => "Unlabelled field",
                "Table" => "Visual table",
                "HeaderItem" => column is not null ? $"Column {column + 1}" : "Column header",
                "DataItem" => row is not null && column is not null ? $"Cell {row + 1},{column + 1}" : "Table cell",
                "List" => "Visual list",
                "ListItem" => "List item",
                _ => "Unlabelled button"
            }
            : text;
        return new(
            id,
            parentId,
            id,
            name,
            "ControlType." + type,
            "UiAtlas.VisualControlRegion",
            ToScreenRect(pixelBounds, target.Bounds, scaleX, scaleY),
            IsEnabled: false,
            IsOffscreen: true,
            FrameworkId: "UiAtlas.Visual.Ocr",
            WindowHwnd: target.Hwnd,
            SupportedPatterns: patterns,
            VisualRole: role,
            OcrText: ocrText ?? text,
            VisualGroupId: groupId,
            TableRow: row,
            TableColumn: column);
    }

    private static IReadOnlyList<TableGroup> FindTableGroups(IReadOnlyList<RectI> rectangles)
    {
        var groups = ConnectedGroups(rectangles, SharesGridEdge);
        var result = new List<TableGroup>();
        foreach (var group in groups.Where(value => value.Count >= 4))
        {
            var rowPositions = ClusterAxis(group.Select(item => item.Y), 5);
            var columnPositions = ClusterAxis(group.Select(item => item.X), 5);
            if (rowPositions.Count < 2 || columnPositions.Count < 2) continue;
            var cells = group
                .Select(bounds => new TableCell(
                    bounds,
                    NearestAxis(rowPositions, bounds.Y),
                    NearestAxis(columnPositions, bounds.X)))
                .GroupBy(cell => (cell.Row, cell.Column))
                .Select(values => values.OrderBy(cell => (long)cell.Bounds.Width * cell.Bounds.Height).First())
                .ToArray();
            var expected = rowPositions.Count * columnPositions.Count;
            if (cells.Length < Math.Max(4, (int)Math.Ceiling(expected * .55))) continue;
            result.Add(new(Union(group), cells, columnPositions.Count, HasHeaderRow: false));
        }
        return result
            .OrderBy(group => group.Bounds.Y)
            .ThenBy(group => group.Bounds.X)
            .ToArray();
    }

    private static IReadOnlyList<TableGroup> FindSparseTableGroups(
        IReadOnlyList<RectI> rectangles,
        IReadOnlyList<VisualTextObservation> words)
    {
        if (rectangles.Count < 3 || words.Count < 8) return [];

        var candidates = rectangles
            .Where(bounds => bounds.Width >= 24 && bounds.Height is >= 14 and <= 48)
            .ToArray();
        var rowAxes = ClusterAxis(candidates.Select(bounds => bounds.Y), 4);
        var result = new List<TableGroup>();
        foreach (var rowAxis in rowAxes)
        {
            var sameRow = candidates
                .Where(bounds => Math.Abs(bounds.Y - rowAxis) <= 4)
                .OrderBy(bounds => bounds.X)
                .ToArray();
            foreach (var run in ContiguousHorizontalRuns(sameRow))
            {
                if (run.Count < 3) continue;
                var headerTop = run.Min(bounds => bounds.Y);
                var headerBottom = run.Max(bounds => bounds.Y + bounds.Height);
                var left = run[0].X;
                var right = run[^1].X + run[^1].Width;
                if (right - left < 180) continue;

                var headerTextCount = run.Count(bounds => TextInside(bounds, words).Length > 0);
                if (headerTextCount < Math.Max(2, run.Count / 2)) continue;

                var bodyWords = words
                    .Where(word => word.Bounds.Y >= headerBottom - 1 &&
                                   word.Bounds.Y < headerBottom + 520 &&
                                   word.Bounds.X + word.Bounds.Width / 2 >= left &&
                                   word.Bounds.X + word.Bounds.Width / 2 < right + 4)
                    .ToArray();
                // Windows OCR commonly emits each visual table column as an
                // independent text line. Reconstruct rows from vertical
                // alignment instead of trusting its LineIndex metadata.
                var visualRowAxes = ClusterAxis(
                    bodyWords.Select(word => word.Bounds.Y + word.Bounds.Height / 2), 5);
                var bodyRows = bodyWords
                    .GroupBy(word => NearestAxis(
                        visualRowAxes, word.Bounds.Y + word.Bounds.Height / 2))
                    .Select(group => new
                    {
                        Top = group.Min(word => word.Bounds.Y),
                        Bottom = group.Max(word => word.Bounds.Y + word.Bounds.Height),
                        Columns = group.Select(word => ColumnAt(run, word.Bounds.X + word.Bounds.Width / 2))
                            .Where(column => column >= 0)
                            .Distinct()
                            .Count()
                    })
                    .Where(row => row.Columns >= 2)
                    .OrderBy(row => row.Top)
                    .ToArray();
                var contiguousRows = new List<(int Top, int Bottom)>();
                foreach (var row in bodyRows)
                {
                    if (contiguousRows.Count == 0)
                    {
                        if (row.Top - headerBottom <= 48)
                            contiguousRows.Add((row.Top, row.Bottom));
                        continue;
                    }

                    if (row.Top - contiguousRows[^1].Bottom > 40)
                        break;
                    contiguousRows.Add((row.Top, row.Bottom));
                }
                if (contiguousRows.Count < 3) continue;

                var cells = new List<TableCell>(run.Count * (contiguousRows.Count + 1));
                for (var column = 0; column < run.Count; column++)
                    cells.Add(new(run[column], 0, column));
                for (var row = 0; row < contiguousRows.Count; row++)
                {
                    var rowTop = row == 0
                        ? headerBottom
                        : (contiguousRows[row - 1].Bottom + contiguousRows[row].Top) / 2;
                    var rowBottom = row == contiguousRows.Count - 1
                        ? contiguousRows[row].Bottom + Math.Max(2, contiguousRows[row].Bottom - contiguousRows[row].Top) / 2
                        : (contiguousRows[row].Bottom + contiguousRows[row + 1].Top) / 2;
                    for (var column = 0; column < run.Count; column++)
                    {
                        var columnBounds = run[column];
                        cells.Add(new(
                            new RectI(columnBounds.X, rowTop, columnBounds.Width, Math.Max(1, rowBottom - rowTop)),
                            row + 1,
                            column));
                    }
                }

                var tableBottom = cells.Max(cell => cell.Bounds.Y + cell.Bounds.Height);
                var table = new TableGroup(
                    new RectI(left, headerTop, right - left, tableBottom - headerTop),
                    cells,
                    run.Count,
                    HasHeaderRow: true);
                if (!result.Any(existing => OverlapRatio(existing.Bounds, table.Bounds) >= .72))
                    result.Add(table);
            }
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<RectI>> ContiguousHorizontalRuns(IReadOnlyList<RectI> rectangles)
    {
        var result = new List<IReadOnlyList<RectI>>();
        var current = new List<RectI>();
        foreach (var bounds in rectangles)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                var gap = bounds.X - (previous.X + previous.Width);
                var overlap = Math.Max(0,
                    Math.Min(previous.Y + previous.Height, bounds.Y + bounds.Height) - Math.Max(previous.Y, bounds.Y));
                if (gap is < -3 or > 4 || overlap < Math.Min(previous.Height, bounds.Height) * .72)
                {
                    if (current.Count >= 3) result.Add(current.ToArray());
                    current.Clear();
                }
            }
            current.Add(bounds);
        }
        if (current.Count >= 3) result.Add(current.ToArray());
        return result;
    }

    private static int ColumnAt(IReadOnlyList<RectI> columns, int x)
    {
        for (var index = 0; index < columns.Count; index++)
            if (x >= columns[index].X && x < columns[index].X + columns[index].Width)
                return index;
        // OCR boxes may extend a few pixels over a visible cell border. Keep
        // those words in the nearest column instead of dropping the cell.
        var nearest = -1;
        var nearestDistance = int.MaxValue;
        for (var index = 0; index < columns.Count; index++)
        {
            var distance = x < columns[index].X
                ? columns[index].X - x
                : x - (columns[index].X + columns[index].Width);
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }
        if (nearestDistance <= 4) return nearest;
        return -1;
    }

    private static IReadOnlyList<ListGroup> FindListGroups(
        IReadOnlyList<RectI> rectangles,
        IReadOnlyList<VisualTextObservation> words)
    {
        var groups = ConnectedGroups(rectangles, (left, right) =>
        {
            var upper = left.Y <= right.Y ? left : right;
            var lower = left.Y <= right.Y ? right : left;
            var gap = lower.Y - (upper.Y + upper.Height);
            return gap is >= -2 and <= 3 &&
                   Math.Abs(left.X - right.X) <= 5 &&
                   Math.Abs(left.Width - right.Width) <= Math.Max(6, Math.Min(left.Width, right.Width) / 12);
        });
        return groups
            .Where(group => group.Count >= 3 &&
                            group.Count(item => TextInside(item, words).Length > 0) >= 2 &&
                            group.All(item => NearestFieldLabel(item, words).Length == 0))
            .Select(group => new ListGroup(Union(group), group.OrderBy(item => item.Y).ThenBy(item => item.X).ToArray()))
            .OrderBy(group => group.Bounds.Y)
            .ThenBy(group => group.Bounds.X)
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<RectI>> ConnectedGroups(
        IReadOnlyList<RectI> rectangles,
        Func<RectI, RectI, bool> connected)
    {
        var groups = new List<IReadOnlyList<RectI>>();
        var visited = new bool[rectangles.Count];
        for (var start = 0; start < rectangles.Count; start++)
        {
            if (visited[start]) continue;
            var queue = new Queue<int>();
            var group = new List<RectI>();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                group.Add(rectangles[current]);
                for (var candidate = 0; candidate < rectangles.Count; candidate++)
                {
                    if (visited[candidate] || !connected(rectangles[current], rectangles[candidate])) continue;
                    visited[candidate] = true;
                    queue.Enqueue(candidate);
                }
            }
            groups.Add(group);
        }
        return groups;
    }

    private static bool SharesGridEdge(RectI first, RectI second)
    {
        var horizontalGap = Math.Min(
            Math.Abs(first.X + first.Width - second.X),
            Math.Abs(second.X + second.Width - first.X));
        var verticalOverlap = Math.Max(0,
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var verticalGap = Math.Min(
            Math.Abs(first.Y + first.Height - second.Y),
            Math.Abs(second.Y + second.Height - first.Y));
        var horizontalOverlap = Math.Max(0,
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        return horizontalGap <= 3 && verticalOverlap >= Math.Min(first.Height, second.Height) * .72 ||
               verticalGap <= 3 && horizontalOverlap >= Math.Min(first.Width, second.Width) * .72;
    }

    private static IReadOnlyList<TableGroup> FindKnownGridTables(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        double scaleX,
        double scaleY,
        IReadOnlyList<RectI> excludedPixelRegions)
    {
        var candidates = knownControls
            .Where(control => control.ClassName.Contains("DBGrid", StringComparison.OrdinalIgnoreCase) &&
                              !control.FrameworkId.StartsWith("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase) &&
                              control.Bounds.Width >= 240 && control.Bounds.Height >= 80)
            .Select(control => ToPixelRect(
                Intersect(control.Bounds, target.Bounds), target.Bounds,
                scaleX, scaleY, frame.Width, frame.Height))
            .Where(bounds => bounds.Width >= 240 && bounds.Height >= 80 &&
                             !excludedPixelRegions.Any(excluded => IntersectionOverUnion(excluded, bounds) >= .72))
            .OrderByDescending(bounds => (long)bounds.Width * bounds.Height)
            .ToArray();
        var result = new List<TableGroup>();
        foreach (var candidate in candidates)
        {
            if (result.Any(table => OverlapRatio(table.Bounds, candidate) >= .72)) continue;
            var table = TryFindBorderedGrid(frame, candidate);
            if (table is not null) result.Add(table);
        }
        return result;
    }

    private static IReadOnlyList<ClassicTreeGroup> FindClassicTrees(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<VisualTextObservation> words,
        double scaleX,
        double scaleY)
    {
        var scanRight = Math.Min(frame.Width - 1, Math.Max(180, frame.Width * 2 / 5));
        var activeRows = new List<(int Y, int Count)>();
        for (var y = 1; y < frame.Height - 1; y++)
        {
            var count = 0;
            for (var x = 1; x < scanRight; x++)
                if (IsClassicTreeAccent(frame, x, y)) count++;
            if (count >= 3) activeRows.Add((y, count));
        }

        var bands = new List<TreeAccentBand>();
        for (var index = 0; index < activeRows.Count;)
        {
            var end = index + 1;
            var total = activeRows[index].Count;
            while (end < activeRows.Count && activeRows[end].Y <= activeRows[end - 1].Y + 1)
            {
                total += activeRows[end].Count;
                end++;
            }
            var top = activeRows[index].Y;
            var bottom = activeRows[end - 1].Y;
            if (bottom - top + 1 is >= 6 and <= 20 && total >= 24)
                bands.Add(new(top, bottom, total));
            index = end;
        }
        if (bands.Count < 6) return [];

        var centers = bands.Select(band => (band.Top + band.Bottom) / 2).ToArray();
        var sequence = LongestRegularSequence(centers, 16, 28);
        if (sequence.Count < 6) return [];
        var selected = sequence
            .Select(center => bands.OrderBy(band => Math.Abs((band.Top + band.Bottom) / 2 - center)).First())
            .Distinct()
            .OrderBy(band => band.Top)
            .ToArray();
        var pitch = selected.Length > 1
            ? (int)Math.Round(selected.Zip(selected.Skip(1), (left, right) => right.Top - left.Top).Average())
            : 20;

        var rowParts = new List<(TreeAccentBand Band, int AccentLeft, int AccentRight,
            IReadOnlyList<VisualTextObservation> Words, IReadOnlyList<AutomationObservation> Controls)>();
        foreach (var band in selected)
        {
            var accentXs = new List<int>();
            for (var y = band.Top; y <= band.Bottom; y++)
            for (var x = 1; x < scanRight; x++)
                if (IsClassicTreeAccent(frame, x, y)) accentXs.Add(x);
            if (accentXs.Count == 0) continue;
            var centerY = (band.Top + band.Bottom) / 2;
            var rowWords = words
                .Where(word => Math.Abs(word.Bounds.Y + word.Bounds.Height / 2 - centerY) <= Math.Max(8, pitch / 2) &&
                               word.Bounds.X >= accentXs.Min() - 4 && word.Bounds.X < scanRight)
                .OrderBy(word => word.Bounds.X)
                .ToArray();
            var rowControls = knownControls
                .Where(control => control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
                                  !control.Name.StartsWith("Unlabelled", StringComparison.OrdinalIgnoreCase) &&
                                  control.Bounds.Height is >= 12 and <= 30)
                .Select(control => (Control: control, Pixel: ToPixelRect(
                    Intersect(control.Bounds, target.Bounds), target.Bounds,
                    scaleX, scaleY, frame.Width, frame.Height)))
                .Where(item => Math.Abs(item.Pixel.Y + item.Pixel.Height / 2 - centerY) <= Math.Max(8, pitch / 2) &&
                               item.Pixel.X >= accentXs.Min() - 4 && item.Pixel.X < scanRight)
                .OrderBy(item => item.Pixel.X)
                .Select(item => item.Control)
                .ToArray();
            rowParts.Add((band, accentXs.Min(), accentXs.Max(), rowWords, rowControls));
        }
        if (rowParts.Count < 6) return [];

        var baseIndent = rowParts.Min(row => row.AccentLeft);
        if (rowParts.Max(row => row.AccentLeft) - baseIndent < 16 ||
            rowParts.Count(row => row.AccentLeft >= baseIndent + 16) < 4)
            return [];
        var rows = new List<ClassicTreeRow>();
        foreach (var row in rowParts)
        {
            var wordRight = row.Words.Count > 0
                ? row.Words.Max(word => word.Bounds.X + word.Bounds.Width)
                : 0;
            var controlPixels = row.Controls.Select(control => ToPixelRect(
                    Intersect(control.Bounds, target.Bounds), target.Bounds,
                    scaleX, scaleY, frame.Width, frame.Height))
                .ToArray();
            var controlRight = controlPixels.Length > 0
                ? controlPixels.Max(bounds => bounds.X + bounds.Width)
                : 0;
            var right = Math.Min(scanRight, Math.Max(row.AccentRight + 80, Math.Max(wordRight, controlRight) + 6));
            var left = Math.Max(0, row.AccentLeft - 4);
            var top = Math.Max(0, row.Band.Top - Math.Max(1, (pitch - (row.Band.Bottom - row.Band.Top + 1)) / 2));
            var nameParts = row.Words.Count > 0
                ? row.Words.Select(word => word.Text)
                : row.Controls.Select(control => control.Name);
            var name = string.Join(' ', nameParts
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)).Trim();
            var indent = Math.Max(0, (int)Math.Round((row.AccentLeft - baseIndent) / 20d));
            rows.Add(new(new RectI(left, top, Math.Max(1, right - left), Math.Max(16, pitch)),
                name, indent, row.AccentLeft <= baseIndent + 12));
        }
        var treeBounds = Union(rows.Select(row => row.Bounds));
        return treeBounds.Width > 0 && treeBounds.Height > 0
            ? [new ClassicTreeGroup(treeBounds, rows)]
            : [];
    }

    private static IReadOnlyList<ClassicTabStrip> FindClassicTabStrips(
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<VisualTextObservation> words)
    {
        var result = new List<ClassicTabStrip>();
        foreach (var line in words.GroupBy(word => word.LineIndex))
        {
            var ordered = line
                .Where(word => word.Text is not "I" and not "|" and not "l")
                .OrderBy(word => word.Bounds.X)
                .ToArray();
            if (ordered.Length < 4 || ordered.Length > 12) continue;
            var top = ordered.Min(word => word.Bounds.Y);
            var bottom = ordered.Max(word => word.Bounds.Y + word.Bounds.Height);
            var left = ordered[0].Bounds.X;
            var right = ordered[^1].Bounds.X + ordered[^1].Bounds.Width;
            if (top < frame.Height * .45 || bottom - top > 24 ||
                left > frame.Width * .18 || right - left > frame.Width * .45)
                continue;

            var labels = new List<(string Name, RectI TextBounds)>();
            foreach (var word in ordered)
            {
                if (labels.Count > 0 && word.Bounds.X -
                    (labels[^1].TextBounds.X + labels[^1].TextBounds.Width) <= 8)
                {
                    var previous = labels[^1];
                    var mergedRight = word.Bounds.X + word.Bounds.Width;
                    labels[^1] = ($"{previous.Name} {word.Text}", previous.TextBounds with
                    {
                        Width = mergedRight - previous.TextBounds.X,
                        Height = Math.Max(previous.TextBounds.Height, word.Bounds.Height)
                    });
                }
                else
                {
                    labels.Add((word.Text, word.Bounds));
                }
            }
            if (labels.Count < 4) continue;

            var itemTop = Math.Max(0, top - 7);
            var itemBottom = Math.Min(frame.Height, bottom + 7);
            var items = new List<ClassicTabItem>();
            for (var index = 0; index < labels.Count; index++)
            {
                var itemLeft = index == 0
                    ? Math.Max(0, labels[index].TextBounds.X - 8)
                    : (labels[index - 1].TextBounds.X + labels[index - 1].TextBounds.Width +
                       labels[index].TextBounds.X) / 2;
                var itemRight = index == labels.Count - 1
                    ? Math.Min(frame.Width, labels[index].TextBounds.X + labels[index].TextBounds.Width + 10)
                    : (labels[index].TextBounds.X + labels[index].TextBounds.Width +
                       labels[index + 1].TextBounds.X) / 2;
                items.Add(new(new RectI(itemLeft, itemTop,
                    Math.Max(16, itemRight - itemLeft), Math.Max(18, itemBottom - itemTop)), labels[index].Name));
            }
            var bounds = Union(items.Select(item => item.Bounds));
            if (!HasClassicTabStripGeometry(frame, bounds)) continue;
            result.Add(new(bounds, items));
        }
        return result
            .OrderByDescending(strip => strip.Items.Count)
            .ThenBy(strip => strip.Bounds.Y)
            .Take(2)
            .ToArray();
    }

    private static bool HasClassicTabStripGeometry(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI bounds)
    {
        var left = Math.Max(1, bounds.X - 8);
        var right = Math.Min(frame.Width - 1, bounds.X + bounds.Width + 8);
        var top = Math.Max(1, bounds.Y - 4);
        var bottom = Math.Min(frame.Height - 1, bounds.Y + bounds.Height + 8);
        var required = Math.Max(24, (int)Math.Round(bounds.Width * .55));
        for (var y = top; y <= bottom; y++)
        {
            var edgePixels = 0;
            for (var x = left; x <= right; x++)
                if (ColorDistance(frame, x, y, x, y - 1) >= 36) edgePixels++;
            if (edgePixels >= required) return true;
        }
        return false;
    }

    private static bool IsClassicTreeAccent(OpaqueSurfaceScanner.PixelFrame frame, int x, int y)
    {
        var offset = (y * frame.Width + x) * 4;
        var blue = frame.Pixels[offset];
        var green = frame.Pixels[offset + 1];
        var red = frame.Pixels[offset + 2];
        return red is >= 70 and <= 180 &&
               green is >= 70 and <= 180 &&
               blue is >= 140 and <= 230 &&
               blue >= red + 24 && blue >= green + 24;
    }

    private static bool HasTintedButtonInterior(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        var tinted = 0;
        var samples = 0;
        for (var row = 1; row <= 3; row++)
        for (var column = 1; column <= 5; column++)
        {
            var x = Math.Clamp(bounds.X + column * bounds.Width / 6, 0, frame.Width - 1);
            var y = Math.Clamp(bounds.Y + row * bounds.Height / 4, 0, frame.Height - 1);
            var offset = (y * frame.Width + x) * 4;
            samples++;
            if (frame.Pixels[offset] >= frame.Pixels[offset + 2] + 6) tinted++;
        }
        return tinted >= Math.Max(3, samples / 3);
    }

    private static bool IsPriorityInteractiveRectangle(RectI bounds) =>
        bounds.Width is >= 60 and <= 420 && bounds.Height is >= 22 and <= 70;

    private static bool HasInteriorHorizontalSeparator(
        RectI bounds,
        IReadOnlyDictionary<int, IReadOnlyList<HorizontalRun>> horizontalRunsByRow)
    {
        var minimumWidth = bounds.Width * .78;
        for (var y = bounds.Y + 8; y <= bounds.Y + bounds.Height - 9; y++)
        {
            if (!horizontalRunsByRow.TryGetValue(y, out var runs)) continue;
            if (runs.Any(run =>
                    Math.Min(run.Right, bounds.X + bounds.Width - 1) - Math.Max(run.Left, bounds.X) + 1 >= minimumWidth))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<RectI> FindWideDatabaseGridCombos(
        WindowTarget target,
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<AutomationObservation> knownControls,
        IReadOnlyList<TableGroup> tables,
        double scaleX,
        double scaleY)
    {
        if (tables.Count == 0) return [];
        var nativePeers = knownControls
            .Where(control => control.ControlType.EndsWith(".ComboBox", StringComparison.OrdinalIgnoreCase) &&
                              control.Bounds.Width >= 80 && control.Bounds.Height is >= 18 and <= 44)
            .Select(control => ToPixelRect(
                Intersect(control.Bounds, target.Bounds), target.Bounds,
                scaleX, scaleY, frame.Width, frame.Height))
            .Where(bounds => bounds.Width >= 80 && bounds.Height >= 18)
            .ToArray();
        var result = new List<RectI>();
        foreach (var table in tables)
        {
            var peer = nativePeers
                .Where(bounds => bounds.Y + bounds.Height <= table.Bounds.Y + 2 &&
                                 table.Bounds.Y - (bounds.Y + bounds.Height) <= 72)
                .OrderBy(bounds => Math.Abs(table.Bounds.Y - (bounds.Y + bounds.Height)))
                .ThenByDescending(bounds => bounds.X)
                .FirstOrDefault();
            var hasPeer = peer is { Height: > 0 };
            var bandTop = hasPeer
                ? Math.Max(0, peer!.Y - 4)
                : Math.Max(0, table.Bounds.Y - 72);
            var bandBottom = Math.Max(bandTop + 16, table.Bounds.Y - 3);
            var bandRight = hasPeer
                ? Math.Max(table.Bounds.X + 240, peer!.X - 24)
                : table.Bounds.X + Math.Max(240, table.Bounds.Width * 3 / 4);
            var band = new RectI(
                table.Bounds.X,
                bandTop,
                Math.Min(frame.Width, bandRight) - table.Bounds.X,
                Math.Min(frame.Height, bandBottom) - bandTop);
            if (band.Width < 240 || band.Height < 16) continue;

            var combo = FindRectangles(frame, band, allowWideField: true)
                .Where(bounds => bounds.Width >= 240 && bounds.Height is >= 18 and <= 44 &&
                                 bounds.Width >= bounds.Height * 8)
                .OrderByDescending(bounds => bounds.Width)
                .ThenBy(bounds => bounds.Y)
                .FirstOrDefault();
            if (combo is not { Width: > 0, Height: > 0 })
            {
                // Some owner-drawn Delphi combos expose only a long top edge;
                // their arrow area has a different background and therefore is
                // not a closed rectangle. Use the adjacent native combo as the
                // row-height authority and extend the long edge by one arrow box.
                var underline = FindHorizontalRuns(frame, band)
                    .Where(run => run.Right - run.Left + 1 >= 240 &&
                                  (!hasPeer || Math.Abs(run.Y - peer!.Y) <= 5))
                    .OrderByDescending(run => run.Right - run.Left)
                    .ThenBy(run => run.Y)
                    .FirstOrDefault();
                if (underline.Right > underline.Left)
                {
                    var height = hasPeer
                        ? peer!.Height
                        : Math.Clamp(table.Bounds.Y - underline.Y - 3, 18, 36);
                    var top = hasPeer ? peer!.Y : underline.Y;
                    var right = Math.Min(band.X + band.Width, underline.Right + height);
                    combo = new RectI(underline.Left, top, right - underline.Left + 1, height);
                }
            }
            if (combo is { Width: > 0, Height: > 0 }) result.Add(combo);
        }
        return result;
    }

    private static TableGroup? TryFindBorderedGrid(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI candidate)
    {
        var horizontal = FindStrongAxisLines(frame, candidate, horizontal: true);
        var rowEdges = LongestRegularSequence(horizontal, 14, 64);
        if (rowEdges.Count < 4) return null;

        var rowSpan = new RectI(candidate.X, rowEdges[0], candidate.Width, rowEdges[^1] - rowEdges[0] + 1);
        var columnEdges = KeepSeparatedLines(
            FindStrongAxisLines(frame, rowSpan, horizontal: false), 12);
        if (columnEdges.Count < 4) return null;

        // A DBGrid commonly has a narrow row-selector strip on the left and a
        // vertical scrollbar on the right. They are grid chrome, not data
        // columns, and must not become HeaderItem/DataItem controls.
        var columnSpans = Enumerable.Range(0, columnEdges.Count - 1)
            .Select(index => new
            {
                Left = columnEdges[index],
                Right = columnEdges[index + 1],
                Width = columnEdges[index + 1] - columnEdges[index] + 1
            })
            .Where(span => span.Width >= 24)
            .ToArray();
        if (columnSpans.Length < 3) return null;

        var cells = new List<TableCell>((rowEdges.Count - 1) * columnSpans.Length);
        for (var row = 0; row < rowEdges.Count - 1; row++)
        for (var column = 0; column < columnSpans.Length; column++)
        {
            var span = columnSpans[column];
            var height = rowEdges[row + 1] - rowEdges[row] + 1;
            if (height < 12) continue;
            cells.Add(new(
                new RectI(span.Left, rowEdges[row], span.Width, height),
                row,
                column));
        }
        if (cells.Count < 6) return null;
        var bounds = new RectI(
            columnSpans[0].Left, rowEdges[0],
            columnSpans[^1].Right - columnSpans[0].Left + 1,
            rowEdges[^1] - rowEdges[0] + 1);
        return new(bounds, cells, columnSpans.Length, HasHeaderRow: true);
    }

    private static IReadOnlyList<int> FindStrongAxisLines(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI bounds,
        bool horizontal)
    {
        var active = new List<int>();
        var start = horizontal ? Math.Max(1, bounds.Y) : Math.Max(1, bounds.X);
        var end = horizontal
            ? Math.Min(frame.Height, bounds.Y + bounds.Height)
            : Math.Min(frame.Width, bounds.X + bounds.Width);
        for (var axis = start; axis < end; axis++)
        {
            var matches = 0;
            var samples = 0;
            if (horizontal)
            {
                for (var x = Math.Max(1, bounds.X); x < Math.Min(frame.Width, bounds.X + bounds.Width); x++)
                {
                    samples++;
                    if (ColorDistance(frame, x, axis, x, axis - 1) >= 20) matches++;
                }
            }
            else
            {
                for (var y = Math.Max(1, bounds.Y); y < Math.Min(frame.Height, bounds.Y + bounds.Height); y++)
                {
                    samples++;
                    if (ColorDistance(frame, axis, y, axis - 1, y) >= 20) matches++;
                }
            }
            if (samples > 0 && matches / (double)samples >= .62) active.Add(axis);
        }

        var lines = new List<int>();
        for (var index = 0; index < active.Count;)
        {
            var endIndex = index + 1;
            while (endIndex < active.Count && active[endIndex] <= active[endIndex - 1] + 3) endIndex++;
            lines.Add(active[index + (endIndex - index) / 2]);
            index = endIndex;
        }
        return lines;
    }

    private static IReadOnlyList<int> LongestRegularSequence(
        IReadOnlyList<int> values,
        int minimumPitch,
        int maximumPitch)
    {
        IReadOnlyList<int> best = [];
        for (var start = 0; start < values.Count - 1; start++)
        {
            var initialPitch = values[start + 1] - values[start];
            if (initialPitch < minimumPitch || initialPitch > maximumPitch) continue;
            var sequence = new List<int> { values[start], values[start + 1] };
            var pitch = (double)initialPitch;
            for (var index = start + 2; index < values.Count; index++)
            {
                var gap = values[index] - sequence[^1];
                if (gap < pitch * .75) continue;
                if (gap > pitch * 1.25) break;
                sequence.Add(values[index]);
                pitch = (pitch * (sequence.Count - 2) + gap) / (sequence.Count - 1);
            }
            if (sequence.Count > best.Count) best = sequence;
        }
        return best;
    }

    private static IReadOnlyList<int> KeepSeparatedLines(
        IReadOnlyList<int> values,
        int minimumDistance)
    {
        var result = new List<int>();
        foreach (var value in values)
            if (result.Count == 0 || value - result[^1] >= minimumDistance)
                result.Add(value);
        return result;
    }

    private static bool ContainsCenter(RectI outer, RectI inner) => ContainsPoint(
        outer,
        inner.X + inner.Width / 2,
        inner.Y + inner.Height / 2);

    private static IReadOnlyList<int> ClusterAxis(IEnumerable<int> values, int tolerance)
    {
        var result = new List<int>();
        foreach (var value in values.Order())
        {
            var index = result.FindIndex(existing => Math.Abs(existing - value) <= tolerance);
            if (index < 0) result.Add(value);
            else result[index] = (result[index] + value) / 2;
        }
        return result;
    }

    private static int NearestAxis(IReadOnlyList<int> axes, int value)
    {
        var nearest = 0;
        for (var index = 1; index < axes.Count; index++)
            if (Math.Abs(axes[index] - value) < Math.Abs(axes[nearest] - value)) nearest = index;
        return nearest;
    }

    private static string ClassifyRectangle(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI bounds,
        string containedText,
        string fieldLabel,
        IReadOnlyList<VisualTextObservation> words)
    {
        var aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
        var labelledField = fieldLabel.Length > 0 && !HasTintedButtonInterior(frame, bounds) &&
                            (bounds.Height >= 60 ||
                             aspect >= 1.6 &&
                             (containedText.Length == 0 || HasLeftAlignedText(bounds, words)));
        if (labelledField && LooksLikeComboBox(frame, bounds)) return "ComboBox";
        if (labelledField) return "Edit";
        if (bounds.Height >= 72 && bounds.Height >= bounds.Width * .55) return "List";
        if (aspect >= 2.35 && containedText.Length == 0)
            return "Edit";
        return "Button";
    }

    private static bool HasLeftAlignedText(
        RectI bounds,
        IReadOnlyList<VisualTextObservation> words)
    {
        var text = words
            .Where(word => CenterInside(bounds, word.Bounds) || IntersectionOverUnion(bounds, word.Bounds) >= .25)
            .Select(word => word.Bounds)
            .ToArray();
        if (text.Length == 0) return false;
        return text.Min(item => item.X) - bounds.X <= Math.Max(12, bounds.Width / 12);
    }

    private static bool LooksLikeComboBox(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        if (bounds.Height is < 16 or > 48 || bounds.Width < 64) return false;
        var minimumX = bounds.X + bounds.Width * 3 / 4;
        return Enumerable.Range(minimumX, Math.Max(0, bounds.X + bounds.Width - 8 - minimumX))
            .Any(x => VerticalEdgeScore(frame, bounds, x) >= .55);
    }

    private static IReadOnlyList<RectI> ExpandGridRows(
        OpaqueSurfaceScanner.PixelFrame frame,
        IReadOnlyList<RectI> rectangles)
    {
        var result = new List<RectI>();
        var verticalEdges = new VerticalEdgeIndex(frame);
        foreach (var bounds in rectangles)
        {
            var separators = FindVerticalSeparators(frame, bounds, verticalEdges);
            if (separators.Count < 2)
            {
                result.Add(bounds);
                continue;
            }

            var edges = new[] { bounds.X }.Concat(separators).Append(bounds.X + bounds.Width - 1)
                .Distinct().Order().ToArray();
            var cells = new List<RectI>();
            for (var index = 0; index < edges.Length - 1; index++)
            {
                var width = edges[index + 1] - edges[index] + 1;
                if (width >= 16) cells.Add(new(edges[index], bounds.Y, width, bounds.Height));
            }
            if (cells.Count >= 3) result.AddRange(cells);
            else result.Add(bounds);
        }
        return result;
    }

    private static IReadOnlyList<int> FindVerticalSeparators(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI bounds,
        VerticalEdgeIndex? verticalEdges = null)
    {
        var active = new List<int>();
        for (var x = bounds.X + 8; x < bounds.X + bounds.Width - 8; x++)
            if ((verticalEdges?.Score(bounds, x) ?? VerticalEdgeScore(frame, bounds, x)) >= .72) active.Add(x);
        var separators = new List<int>();
        for (var index = 0; index < active.Count;)
        {
            var end = index + 1;
            while (end < active.Count && active[end] <= active[end - 1] + 1) end++;
            separators.Add(active[index + (end - index) / 2]);
            index = end;
        }
        return separators;
    }

    private static string TextInside(RectI bounds, IReadOnlyList<VisualTextObservation> words)
    {
        var selected = words
            .Where(word => CenterInside(bounds, word.Bounds) || IntersectionOverUnion(bounds, word.Bounds) >= .25)
            .Where(word => !IsOcrFrameArtifact(bounds, word.Bounds))
            .OrderBy(word => word.LineIndex)
            .ThenBy(word => word.Bounds.X)
            .Select(word => word.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12);
        return string.Join(' ', selected).Trim();
    }

    private static bool HasIndependentControlGeometry(
        RectI bounds,
        IReadOnlyList<VisualTextObservation> words,
        string fieldLabel)
    {
        var textBounds = words
            .Where(word => CenterInside(bounds, word.Bounds) || IntersectionOverUnion(bounds, word.Bounds) >= .25)
            .Where(word => !IsOcrFrameArtifact(bounds, word.Bounds))
            .Select(word => word.Bounds)
            .ToArray();
        if (textBounds.Length == 0) return true;

        var text = Union(textBounds);
        var leftPadding = text.X - bounds.X;
        var rightPadding = bounds.X + bounds.Width - (text.X + text.Width);
        var topPadding = text.Y - bounds.Y;
        var bottomPadding = bounds.Y + bounds.Height - (text.Y + text.Height);
        var labelledField = fieldLabel.Length > 0 && bounds.Width >= bounds.Height * 1.6;
        if (leftPadding < 4 || rightPadding < 4 || topPadding < 3 || bottomPadding < 3)
            return labelledField && leftPadding >= 0 && rightPadding >= 3 &&
                   topPadding >= 1 && bottomPadding >= 1 &&
                   bounds.Width >= text.Width + 8 && bounds.Height >= text.Height + 2;

        // A glyph or a tight OCR word box can contain short edge runs that look
        // like a rectangle. A real control frame must enclose materially more
        // space than the text it labels.
        var controlArea = Math.Max(1L, (long)bounds.Width * bounds.Height);
        var textArea = Math.Max(1L, (long)text.Width * text.Height);
        return controlArea >= textArea * 3 / 2 &&
               bounds.Width >= text.Width + 12 &&
               bounds.Height >= text.Height + 8;
    }

    private static string NearestFieldLabel(RectI field, IReadOnlyList<VisualTextObservation> words)
    {
        var centerY = field.Y + field.Height / 2;
        var segments = TextLineSegments(words);
        var left = segments
            .Where(segment => segment.Bounds.X + segment.Bounds.Width <= field.X + 5 &&
                              field.X - (segment.Bounds.X + segment.Bounds.Width) <= 220 &&
                              Math.Abs(segment.Bounds.Y + segment.Bounds.Height / 2 - centerY) <=
                              Math.Max(10, Math.Min(18, field.Height / 2 + 2)))
            .OrderBy(segment => Math.Abs(segment.Bounds.Y + segment.Bounds.Height / 2 - centerY))
            .ThenBy(segment => field.X - (segment.Bounds.X + segment.Bounds.Width))
            .FirstOrDefault();
        if (left is not null) return NormalizeFieldLabel(left.Text);

        return segments
            .Where(segment => segment.Bounds.Y + segment.Bounds.Height <= field.Y + 5 &&
                              field.Y - (segment.Bounds.Y + segment.Bounds.Height) <= 36 &&
                              segment.Bounds.X <= field.X + Math.Max(12, field.Width / 4) &&
                              segment.Bounds.X + segment.Bounds.Width >= field.X - 8)
            .OrderBy(segment => field.Y - (segment.Bounds.Y + segment.Bounds.Height))
            .ThenBy(segment => Math.Abs(segment.Bounds.X - field.X))
            .Select(segment => NormalizeFieldLabel(segment.Text))
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool IsOcrFrameArtifact(RectI control, RectI word)
    {
        var controlRight = control.X + control.Width;
        var controlBottom = control.Y + control.Height;
        var wordRight = word.X + word.Width;
        var wordBottom = word.Y + word.Height;
        return word.Width >= control.Width * .82 && word.Height >= control.Height * .65 &&
               Math.Abs(word.X - control.X) <= 4 && Math.Abs(word.Y - control.Y) <= 4 &&
               Math.Abs(wordRight - controlRight) <= 6 && Math.Abs(wordBottom - controlBottom) <= 6;
    }

    private static IReadOnlyList<TextSegment> TextLineSegments(IReadOnlyList<VisualTextObservation> words)
    {
        var result = new List<TextSegment>();
        foreach (var line in words.GroupBy(word => word.LineIndex))
        {
            TextSegment? current = null;
            foreach (var word in line.OrderBy(word => word.Bounds.X))
            {
                if (current is not null &&
                    word.Bounds.X - (current.Bounds.X + current.Bounds.Width) <=
                    Math.Max(12, Math.Max(current.Bounds.Height, word.Bounds.Height)))
                {
                    var right = Math.Max(current.Bounds.X + current.Bounds.Width,
                        word.Bounds.X + word.Bounds.Width);
                    var bottom = Math.Max(current.Bounds.Y + current.Bounds.Height,
                        word.Bounds.Y + word.Bounds.Height);
                    current = new TextSegment($"{current.Text} {word.Text}", new RectI(
                        current.Bounds.X,
                        Math.Min(current.Bounds.Y, word.Bounds.Y),
                        right - current.Bounds.X,
                        bottom - Math.Min(current.Bounds.Y, word.Bounds.Y)));
                    continue;
                }

                if (current is not null) result.Add(current);
                current = new TextSegment(word.Text, word.Bounds);
            }
            if (current is not null) result.Add(current);
        }
        return result;
    }

    private static bool CenterInside(RectI outer, RectI inner)
    {
        var x = inner.X + inner.Width / 2;
        var y = inner.Y + inner.Height / 2;
        return x >= outer.X - 2 && x < outer.X + outer.Width + 2 &&
               y >= outer.Y - 2 && y < outer.Y + outer.Height + 2;
    }

    private static string StructureToken(RectI bounds, RectI root, string role)
    {
        var centerX = (bounds.X + bounds.Width / 2d) / Math.Max(1, root.Width);
        var centerY = (bounds.Y + bounds.Height / 2d) / Math.Max(1, root.Height);
        var aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
        return string.Create(CultureInfo.InvariantCulture,
            $"{role}|x:{Math.Clamp((int)(centerX * 24), 0, 23)}|y:{Math.Clamp((int)(centerY * 24), 0, 23)}|a:{Math.Clamp((int)Math.Round(aspect * 2), 0, 40)}");
    }

    private static string StableVisualIdentity(string type, string label, string structure, string fingerprint)
    {
        var normalizedLabel = NormalizeIdentityText(label);
        var material = normalizedLabel.Length > 0
            ? $"{type}|label:{normalizedLabel}"
            : fingerprint.Length > 0 ? $"{type}|shape:{fingerprint}" : $"{type}|structure:{structure}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..24];
    }

    private static string NormalizeControlType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;

    private static string CoarseFingerprint(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        long luminance = 0;
        long luminanceSquared = 0;
        var edgeCount = 0;
        var samples = 0;
        var stepX = Math.Max(1, bounds.Width / 12);
        var stepY = Math.Max(1, bounds.Height / 8);
        // Exclude a scale-relative rim. The same control can have a one-pixel
        // border at 100% DPI and a two-pixel border at 200%; sampling that rim
        // made its persistent identity change across otherwise identical frames.
        var inset = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 12, 2, 6);
        for (var y = Math.Max(1, bounds.Y + inset);
             y < Math.Min(frame.Height, bounds.Y + bounds.Height - inset); y += stepY)
        for (var x = Math.Max(1, bounds.X + inset);
             x < Math.Min(frame.Width, bounds.X + bounds.Width - inset); x += stepX)
        {
            var offset = (y * frame.Width + x) * 4;
            var value = (frame.Pixels[offset] * 29 + frame.Pixels[offset + 1] * 150 + frame.Pixels[offset + 2] * 77) >> 8;
            luminance += value;
            luminanceSquared += value * value;
            if (ColorDistance(frame, x, y, x - 1, y) >= 42 || ColorDistance(frame, x, y, x, y - 1) >= 42)
                edgeCount++;
            samples++;
        }
        if (samples == 0) return "empty";
        var mean = luminance / (double)samples;
        var variance = Math.Max(0, luminanceSquared / (double)samples - mean * mean);
        var aspectBucket = Math.Clamp((int)Math.Round(bounds.Width / (double)Math.Max(1, bounds.Height) * 2), 0, 63);
        var toneBucket = Math.Clamp((int)Math.Round(mean / 32), 0, 7);
        var contrastBucket = Math.Clamp((int)Math.Round(Math.Sqrt(variance) / 24), 0, 7);
        var edgeBucket = Math.Clamp((int)Math.Round(edgeCount / (double)samples * 8), 0, 7);
        return $"a{aspectBucket:x2}t{toneBucket:x1}c{contrastBucket:x1}e{edgeBucket:x1}";
    }

    private static IReadOnlyList<AutomationObservation> DisambiguateVisualIdentities(
        IReadOnlyList<AutomationObservation> controls)
    {
        var result = controls.ToArray();
        foreach (var group in result.Select((control, index) => new { control, index })
                     .GroupBy(item => item.control.RuntimeId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var ordered = group.OrderBy(item => item.control.Bounds.Y)
                .ThenBy(item => item.control.Bounds.X)
                .ThenBy(item => item.index)
                .ToArray();
            for (var ordinal = 0; ordinal < ordered.Length; ordinal++)
            {
                var id = "visual:v3:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{group.Key}|ordinal:{ordinal}"))).ToLowerInvariant()[..24];
                result[ordered[ordinal].index] = ordered[ordinal].control with
                {
                    RuntimeId = id,
                    AutomationId = id
                };
            }
        }
        return result;
    }

    private static string NormalizeIdentityText(string value)
    {
        var buffer = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(character)) buffer.Append(character);
        return buffer.ToString();
    }

    private static RectI Union(IEnumerable<RectI> values)
    {
        var items = values.ToArray();
        var left = items.Min(item => item.X);
        var top = items.Min(item => item.Y);
        var right = items.Max(item => item.X + item.Width);
        var bottom = items.Max(item => item.Y + item.Height);
        return new(left, top, right - left, bottom - top);
    }

    internal static IReadOnlyList<RectI> FindRectangles(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI region,
        bool allowWideField = false)
    {
        var horizontal = FindHorizontalRuns(frame, region);
        var horizontalByRow = horizontal
            .GroupBy(run => run.Y)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<HorizontalRun>)group.ToArray());
        var verticalEdges = new VerticalEdgeIndex(frame);
        var candidates = new List<RectI>();
        for (var topIndex = 0; topIndex < horizontal.Count; topIndex++)
        {
            var top = horizontal[topIndex];
            for (var bottomIndex = topIndex + 1; bottomIndex < horizontal.Count; bottomIndex++)
            {
                var bottom = horizontal[bottomIndex];
                var height = bottom.Y - top.Y + 1;
                if (height < 16) continue;
                if (height > 260) break;
                if (Math.Abs(top.Left - bottom.Left) > 8 || Math.Abs(top.Right - bottom.Right) > 8)
                    continue;
                var left = Math.Max(top.Left, bottom.Left);
                var right = Math.Min(top.Right, bottom.Right);
                if (right - left + 1 < 24) continue;
                var bounds = new RectI(left, top.Y, right - left + 1, height);
                var leftScore = verticalEdges.Score(bounds, left);
                var rightScore = verticalEdges.Score(bounds, right);
                if (leftScore < .28 || rightScore < .28 || leftScore + rightScore < .72)
                    continue;
                // Ordinary buttons are bounded, but database grids commonly span
                // an entire desktop window. Accept a wide rectangle only when
                // persistent inner column separators prove that it is a row.
                var wideFieldAspect = bounds.Height <= 44 ? 8 : 4;
                var allowsUnseparatedWideField = allowWideField && bounds.Height <= 260 &&
                                                 bounds.Width >= bounds.Height * wideFieldAspect;
                if (bounds.Width > 500 && !allowsUnseparatedWideField &&
                    FindVerticalSeparators(frame, bounds, verticalEdges).Count < 2)
                    continue;
                if (HasInteriorHorizontalSeparator(bounds, horizontalByRow) &&
                    !HasTintedButtonInterior(frame, bounds)) continue;
                candidates.Add(bounds);
            }
        }
        return candidates;
    }

    private static IReadOnlyList<HorizontalRun> FindHorizontalRuns(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI region)
    {
        var result = new List<HorizontalRun>();
        var left = Math.Max(1, region.X);
        var right = Math.Min(frame.Width - 1, region.X + region.Width);
        var top = Math.Max(1, region.Y);
        var bottom = Math.Min(frame.Height, region.Y + region.Height);
        for (var y = top; y < bottom; y++)
        {
            var runStart = -1;
            var lastEdge = -1;
            var edgeCount = 0;
            for (var x = left; x <= right; x++)
            {
                var edge = x < right && ColorDistance(frame, x, y, x, y - 1) >= 42;
                if (edge)
                {
                    if (runStart < 0) runStart = x;
                    lastEdge = x;
                    edgeCount++;
                    continue;
                }
                if (runStart < 0 || x - lastEdge <= 2) continue;
                AddRun(runStart, lastEdge, edgeCount, y);
                runStart = -1;
                lastEdge = -1;
                edgeCount = 0;
            }
            if (runStart >= 0) AddRun(runStart, lastEdge, edgeCount, y);
        }
        return result;

        void AddRun(int start, int end, int active, int y)
        {
            var width = end - start + 1;
            if (width < 24 || active < Math.Max(12, (int)Math.Round(width * .55))) return;
            result.Add(new(y, start, end));
        }
    }

    private static double VerticalEdgeScore(
        OpaqueSurfaceScanner.PixelFrame frame,
        RectI bounds,
        int edgeX)
    {
        var matches = 0;
        var samples = 0;
        for (var y = bounds.Y + 1; y < bounds.Y + bounds.Height - 1; y++)
        {
            samples++;
            var strongest = 0;
            for (var offset = -2; offset <= 2; offset++)
            {
                var x = Math.Clamp(edgeX + offset, 1, frame.Width - 1);
                strongest = Math.Max(strongest, ColorDistance(frame, x, y, x - 1, y));
            }
            if (strongest >= 36) matches++;
        }
        return samples == 0 ? 0 : matches / (double)samples;
    }

    private sealed class VerticalEdgeIndex
    {
        private readonly OpaqueSurfaceScanner.PixelFrame _frame;
        private readonly Dictionary<int, int[]> _prefixByColumn = [];

        public VerticalEdgeIndex(OpaqueSurfaceScanner.PixelFrame frame) => _frame = frame;

        public double Score(RectI bounds, int edgeX)
        {
            var top = Math.Clamp(bounds.Y + 1, 0, _frame.Height);
            var bottom = Math.Clamp(bounds.Y + bounds.Height - 1, 0, _frame.Height);
            if (bottom <= top) return 0;

            var column = Math.Clamp(edgeX, 1, _frame.Width - 1);
            if (!_prefixByColumn.TryGetValue(column, out var prefix))
            {
                prefix = BuildPrefix(column);
                _prefixByColumn[column] = prefix;
            }
            return (prefix[bottom] - prefix[top]) / (double)(bottom - top);
        }

        private int[] BuildPrefix(int edgeX)
        {
            var prefix = new int[_frame.Height + 1];
            for (var y = 0; y < _frame.Height; y++)
            {
                var strongest = 0;
                for (var offset = -2; offset <= 2; offset++)
                {
                    var x = Math.Clamp(edgeX + offset, 1, _frame.Width - 1);
                    strongest = Math.Max(strongest, ColorDistance(_frame, x, y, x - 1, y));
                }
                prefix[y + 1] = prefix[y] + (strongest >= 36 ? 1 : 0);
            }
            return prefix;
        }
    }

    private static int ColorDistance(
        OpaqueSurfaceScanner.PixelFrame frame,
        int firstX,
        int firstY,
        int secondX,
        int secondY)
    {
        var first = (firstY * frame.Width + firstX) * 4;
        var second = (secondY * frame.Width + secondX) * 4;
        return Math.Abs(frame.Pixels[first] - frame.Pixels[second]) +
               Math.Abs(frame.Pixels[first + 1] - frame.Pixels[second + 1]) +
               Math.Abs(frame.Pixels[first + 2] - frame.Pixels[second + 2]);
    }

    private static string Fingerprint(OpaqueSurfaceScanner.PixelFrame frame, RectI bounds)
    {
        const int columns = 9;
        const int rows = 8;
        Span<int> cells = stackalloc int[columns * rows];
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var left = bounds.X + column * bounds.Width / columns;
            var right = bounds.X + (column + 1) * bounds.Width / columns;
            var top = bounds.Y + row * bounds.Height / rows;
            var bottom = bounds.Y + (row + 1) * bounds.Height / rows;
            long total = 0;
            var samples = 0;
            for (var y = top; y < Math.Max(top + 1, bottom); y++)
            for (var x = left; x < Math.Max(left + 1, right); x++)
            {
                var offset = (y * frame.Width + x) * 4;
                total += (frame.Pixels[offset] * 29 + frame.Pixels[offset + 1] * 150 + frame.Pixels[offset + 2] * 77) >> 8;
                samples++;
            }
            cells[row * columns + column] = samples == 0 ? 0 : (int)(total / samples);
        }

        // Difference hashing compares neighboring normalized cells instead of
        // absolute colors. It is stable under uniform resize, DPI scaling and
        // theme brightness shifts while retaining the control's visual shape.
        Span<byte> bits = stackalloc byte[8];
        var bit = 0;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns - 1; column++)
        {
            if (cells[row * columns + column] >= cells[row * columns + column + 1])
                bits[bit / 8] |= (byte)(1 << (bit % 8));
            bit++;
        }
        return Convert.ToHexString(bits).ToLowerInvariant();
    }

    private static RectI ToPixelRect(
        RectI screen,
        RectI window,
        double scaleX,
        double scaleY,
        int pixelWidth,
        int pixelHeight)
    {
        var left = Math.Clamp((int)Math.Round((screen.X - window.X) * scaleX), 0, pixelWidth);
        var top = Math.Clamp((int)Math.Round((screen.Y - window.Y) * scaleY), 0, pixelHeight);
        var right = Math.Clamp((int)Math.Round((screen.X + screen.Width - window.X) * scaleX), left, pixelWidth);
        var bottom = Math.Clamp((int)Math.Round((screen.Y + screen.Height - window.Y) * scaleY), top, pixelHeight);
        return new(left, top, right - left, bottom - top);
    }

    private static RectI ToScreenRect(RectI pixel, RectI window, double scaleX, double scaleY) => new(
        window.X + (int)Math.Round(pixel.X / scaleX),
        window.Y + (int)Math.Round(pixel.Y / scaleY),
        Math.Max(1, (int)Math.Round(pixel.Width / scaleX)),
        Math.Max(1, (int)Math.Round(pixel.Height / scaleY)));

    private static RectI Intersect(RectI left, RectI right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var r = Math.Min(left.X + left.Width, right.X + right.Width);
        var b = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    private static double OverlapRatio(RectI first, RectI second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var smaller = Math.Max(1L, Math.Min((long)first.Width * first.Height, (long)second.Width * second.Height));
        return intersection / (double)smaller;
    }

    private static double IntersectionOverUnion(RectI first, RectI second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var union = Math.Max(1L,
            (long)first.Width * first.Height + (long)second.Width * second.Height - intersection);
        return intersection / (double)union;
    }

    private static bool IsCoveredBySemanticControl(AutomationObservation control, RectI candidate)
    {
        var semantic = control.ControlType.EndsWith(".MenuItem", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".TitleBar", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".Hyperlink", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".CheckBox", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".RadioButton", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".ComboBox", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".Edit", StringComparison.OrdinalIgnoreCase) ||
                       control.ControlType.EndsWith(".ListItem", StringComparison.OrdinalIgnoreCase) ||
                       control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);
        if (!semantic) return false;
        if (VisualFallbackPolicy.IsOpaqueGalleryContainer(control))
        {
            var controlArea = Math.Max(1L, (long)control.Bounds.Width * control.Bounds.Height);
            var candidateArea = Math.Max(1L, (long)candidate.Width * candidate.Height);
            // The Office gallery is one accessibility node wrapped around several
            // painted commands. Suppress only another outline of the whole gallery;
            // smaller visual children must remain available as separate buttons.
            return IntersectionOverUnion(control.Bounds, candidate) >= .72 ||
                   candidateArea >= controlArea * .60;
        }
        return OverlapRatio(control.Bounds, candidate) >= .20;
    }

    private static bool IsNarrowUnlabelledFragment(RectI bounds, string containedText) =>
        containedText.Length == 0 &&
        bounds.Width <= 30 &&
        bounds.Height <= 48 &&
        bounds.Height >= bounds.Width * 1.25;

    private sealed record TableGroup(
        RectI Bounds,
        IReadOnlyList<TableCell> Cells,
        int ColumnCount,
        bool HasHeaderRow);
    private sealed record TableCell(RectI Bounds, int Row, int Column);
    private sealed record TreeAccentBand(int Top, int Bottom, int PixelCount);
    private sealed record ClassicTreeGroup(RectI Bounds, IReadOnlyList<ClassicTreeRow> Rows);
    private sealed record ClassicTreeRow(RectI Bounds, string Name, int Indent, bool CanExpand);
    private sealed record ClassicTabStrip(RectI Bounds, IReadOnlyList<ClassicTabItem> Items);
    private sealed record ClassicTabItem(RectI Bounds, string Name);
    private sealed record ClassicRadioButton(RectI Bounds, string Name);
    private sealed record TextSegment(string Text, RectI Bounds);
    private sealed record FieldLabelProbe(int ControlIndex, RectI Bounds, int Priority);
    private sealed record ListGroup(RectI Bounds, IReadOnlyList<RectI> Items);
    private readonly record struct HorizontalRun(int Y, int Left, int Right);
}

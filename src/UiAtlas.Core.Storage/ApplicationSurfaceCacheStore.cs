using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public sealed record CachedSurfaceControl(
    string StableKey,
    string AutomationId,
    string Name,
    string ControlType,
    string ClassName,
    string FrameworkId,
    int NormalizedX,
    int NormalizedY,
    int NormalizedWidth,
    int NormalizedHeight,
    IReadOnlyList<string> SupportedPatterns,
    int Observations,
    DateTimeOffset LastObservedUtc);

public sealed record ApplicationSurfaceCache(
    string FormatVersion,
    ApplicationPlanningProfileKey Key,
    IReadOnlyList<CachedSurfaceControl> Controls,
    DateTimeOffset UpdatedUtc)
{
    public const string CurrentFormatVersion = "ui-atlas.application-surface-cache/1";
}

public sealed class ApplicationSurfaceCacheStore
{
    private const int GeometryScale = 10_000;
    private const int MaximumControls = 5_000;
    private readonly string _directory;

    public ApplicationSurfaceCacheStore(string root)
    {
        _directory = Path.Combine(Path.GetFullPath(root), "planning-profiles");
        Directory.CreateDirectory(_directory);
    }

    public ApplicationSurfaceCache Load(ApplicationPlanningProfileKey key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return new(ApplicationSurfaceCache.CurrentFormatVersion, key, [], DateTimeOffset.UtcNow);
        var bytes = File.ReadAllBytes(path);
        StrictJsonValidator.Validate(bytes);
        var cache = JsonSerializer.Deserialize<ApplicationSurfaceCache>(bytes, JsonDefaults.Options)
                    ?? throw new InvalidDataException("Application surface cache is invalid.");
        Validate(cache, key);
        return cache;
    }

    public IReadOnlyList<AutomationObservation> Project(ApplicationSurfaceCache cache, RectI bounds, long windowHwnd)
    {
        Validate(cache, cache.Key);
        if (bounds.Width <= 0 || bounds.Height <= 0) return [];
        return cache.Controls
            .OrderByDescending(control => control.Observations)
            .ThenBy(control => control.StableKey, StringComparer.Ordinal)
            .Select(control => new AutomationObservation(
                "cache:" + control.StableKey,
                "",
                control.AutomationId,
                control.Name,
                control.ControlType,
                control.ClassName,
                ProjectBounds(control, bounds),
                IsEnabled: false,
                IsOffscreen: true,
                FrameworkId: "UiAtlas.Cached",
                WindowHwnd: windowHwnd,
                SupportedPatterns: control.SupportedPatterns))
            .ToArray();
    }

    public ApplicationSurfaceCache Observe(
        ApplicationPlanningProfileKey key,
        RectI bounds,
        IEnumerable<AutomationObservation> controls,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var existing = Load(key);
        var values = existing.Controls.ToDictionary(control => control.StableKey, StringComparer.Ordinal);
        foreach (var control in controls.Where(control =>
                     (!control.IsOffscreen || control.FrameworkId.Equals("UiAtlas.Shadow.Hover", StringComparison.OrdinalIgnoreCase)) &&
                     control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                     !control.FrameworkId.Equals("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase)))
        {
            var cached = ToCached(control, bounds, now);
            if (cached is null) continue;
            if (values.TryGetValue(cached.StableKey, out var prior))
                cached = cached with { Observations = Math.Min(1_000_000, prior.Observations + 1) };
            values[cached.StableKey] = cached;
        }

        var updated = new ApplicationSurfaceCache(
            ApplicationSurfaceCache.CurrentFormatVersion,
            key,
            values.Values.OrderByDescending(control => control.LastObservedUtc)
                .ThenByDescending(control => control.Observations)
                .ThenBy(control => control.StableKey, StringComparer.Ordinal)
                .Take(MaximumControls)
                .ToArray(),
            now);
        Validate(updated, key);
        AtomicFile.Publish(PathFor(key), temporary =>
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(updated, JsonDefaults.Options)));
        return updated;
    }

    private static CachedSurfaceControl? ToCached(
        AutomationObservation control,
        RectI root,
        DateTimeOffset now)
    {
        if (root.Width <= 0 || root.Height <= 0) return null;
        var normalizedX = Normalize(control.Bounds.X - root.X, root.Width);
        var normalizedY = Normalize(control.Bounds.Y - root.Y, root.Height);
        var normalizedWidth = Normalize(control.Bounds.Width, root.Width);
        var normalizedHeight = Normalize(control.Bounds.Height, root.Height);
        var identity = string.Join('|',
            NormalizeType(control.ControlType),
            control.AutomationId.Trim().ToLowerInvariant(),
            control.ClassName.Trim().ToLowerInvariant(),
            control.Name.Trim().ToLowerInvariant(),
            normalizedX / 80,
            normalizedY / 80,
            normalizedWidth / 80,
            normalizedHeight / 80);
        var stableKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..32];
        return new(
            stableKey,
            Clamp(control.AutomationId, 512),
            Clamp(control.Name, 4_096),
            Clamp(control.ControlType, 256),
            Clamp(control.ClassName, 512),
            Clamp(control.FrameworkId, 128),
            normalizedX,
            normalizedY,
            normalizedWidth,
            normalizedHeight,
            (control.SupportedPatterns ?? []).Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            1,
            now);
    }

    private static RectI ProjectBounds(CachedSurfaceControl control, RectI root) => new(
        root.X + Project(control.NormalizedX, root.Width),
        root.Y + Project(control.NormalizedY, root.Height),
        Math.Max(1, Project(control.NormalizedWidth, root.Width)),
        Math.Max(1, Project(control.NormalizedHeight, root.Height)));

    private static int Normalize(int value, int extent) =>
        Math.Clamp((int)Math.Round(value * (double)GeometryScale / Math.Max(1, extent)), -GeometryScale, GeometryScale * 2);

    private static int Project(int value, int extent) =>
        (int)Math.Round(value * (double)Math.Max(1, extent) / GeometryScale);

    private string PathFor(ApplicationPlanningProfileKey key) =>
        Path.Combine(_directory, key.StableId() + ".surfaces.json");

    private static string NormalizeType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;

    private static string Clamp(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];

    private static void Validate(ApplicationSurfaceCache cache, ApplicationPlanningProfileKey expectedKey)
    {
        if (cache.FormatVersion != ApplicationSurfaceCache.CurrentFormatVersion || cache.Key != expectedKey ||
            cache.Controls is null || cache.Controls.Count > MaximumControls ||
            cache.Controls.GroupBy(control => control.StableKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Application surface cache is invalid.");
        if (cache.Controls.Any(control => string.IsNullOrWhiteSpace(control.StableKey) || control.StableKey.Length > 128 ||
                control.AutomationId.Length > 512 || control.Name.Length > 4_096 || control.ControlType.Length > 256 ||
                control.ClassName.Length > 512 || control.FrameworkId.Length > 128 || control.Observations < 1 ||
                control.SupportedPatterns is null || control.SupportedPatterns.Count > 32))
            throw new InvalidDataException("Application surface cache control is invalid.");
    }
}

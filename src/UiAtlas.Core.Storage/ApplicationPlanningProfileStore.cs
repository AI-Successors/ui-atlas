using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public sealed record ApplicationPlanningProfileKey(
    string ProcessName,
    string ProductName,
    string MajorVersion,
    string WindowClass)
{
    public string StableId()
    {
        var material = string.Join('\n', ProcessName, ProductName, MajorVersion, WindowClass).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }
}

public sealed record ApplicationTransitionRule(
    string ActionFingerprint,
    AutoMappingWorkKind Kind,
    string ExpectedOutcomeKind,
    int Confirmations,
    int Contradictions,
    int ConsecutiveContradictions,
    bool Disabled,
    DateTimeOffset LastCheckedUtc)
{
    public int Attempts => Confirmations + Contradictions;
    public double SuccessRate => Attempts == 0 ? 0 : Confirmations / (double)Attempts;
    public bool IsReusable => !Disabled && Confirmations >= 2 && SuccessRate >= 0.75;
}

public sealed record ApplicationPlanningProfile(
    string FormatVersion,
    ApplicationPlanningProfileKey Key,
    IReadOnlyList<ApplicationTransitionRule> Rules,
    DateTimeOffset UpdatedUtc)
{
    public const string CurrentFormatVersion = "ui-atlas.application-planning-profile/1";

    public static ApplicationPlanningProfile Empty(ApplicationPlanningProfileKey key, DateTimeOffset now) =>
        new(CurrentFormatVersion, key, [], now);
}

public sealed class ApplicationPlanningProfileStore
{
    private readonly string _directory;

    public ApplicationPlanningProfileStore(string root)
    {
        _directory = Path.Combine(Path.GetFullPath(root), "planning-profiles");
        Directory.CreateDirectory(_directory);
    }

    public ApplicationPlanningProfile Load(ApplicationPlanningProfileKey key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return ApplicationPlanningProfile.Empty(key, DateTimeOffset.UtcNow);
        var bytes = File.ReadAllBytes(path);
        StrictJsonValidator.Validate(bytes);
        var profile = JsonSerializer.Deserialize<ApplicationPlanningProfile>(bytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("Application planning profile is invalid.");
        Validate(profile, key);
        return profile;
    }

    public void Save(ApplicationPlanningProfile profile)
    {
        Validate(profile, profile.Key);
        AtomicFile.Publish(PathFor(profile.Key), temporary =>
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(profile, JsonDefaults.Options)));
    }

    public ApplicationPlanningProfile RecordOutcome(
        ApplicationPlanningProfile profile,
        string actionFingerprint,
        AutoMappingWorkKind kind,
        string expectedOutcomeKind,
        bool confirmed,
        DateTimeOffset now)
    {
        var rules = profile.Rules.ToDictionary(rule => rule.ActionFingerprint, StringComparer.Ordinal);
        var existing = rules.GetValueOrDefault(actionFingerprint) ?? new(
            actionFingerprint, kind, expectedOutcomeKind, 0, 0, 0, false, now);
        var consecutiveContradictions = confirmed ? 0 : existing.ConsecutiveContradictions + 1;
        rules[actionFingerprint] = existing with
        {
            Kind = kind,
            ExpectedOutcomeKind = expectedOutcomeKind,
            Confirmations = existing.Confirmations + (confirmed ? 1 : 0),
            Contradictions = existing.Contradictions + (confirmed ? 0 : 1),
            ConsecutiveContradictions = consecutiveContradictions,
            Disabled = consecutiveContradictions >= 2,
            LastCheckedUtc = now
        };
        var updated = profile with
        {
            Rules = rules.Values.OrderBy(rule => rule.ActionFingerprint, StringComparer.Ordinal).ToArray(),
            UpdatedUtc = now
        };
        Save(updated);
        return updated;
    }

    private string PathFor(ApplicationPlanningProfileKey key) => Path.Combine(_directory, key.StableId() + ".json");

    private static void Validate(ApplicationPlanningProfile profile, ApplicationPlanningProfileKey expectedKey)
    {
        if (profile.FormatVersion != ApplicationPlanningProfile.CurrentFormatVersion || profile.Key != expectedKey ||
            profile.Rules is null || profile.Rules.Count > 50_000 ||
            string.IsNullOrWhiteSpace(profile.Key.ProcessName) || profile.Key.ProcessName.Length > 256 ||
            profile.Key.ProductName is null || profile.Key.ProductName.Length > 512 ||
            profile.Key.MajorVersion is null || profile.Key.MajorVersion.Length > 64 ||
            profile.Key.WindowClass is null || profile.Key.WindowClass.Length > 256)
            throw new InvalidDataException("Application planning profile is invalid.");
        if (profile.Rules.GroupBy(rule => rule.ActionFingerprint, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
            profile.Rules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.ActionFingerprint) ||
                rule.ActionFingerprint.Length > 128 || string.IsNullOrWhiteSpace(rule.ExpectedOutcomeKind) ||
                rule.ExpectedOutcomeKind.Length > 128 || rule.Confirmations < 0 || rule.Contradictions < 0 ||
                rule.ConsecutiveContradictions < 0 || rule.ConsecutiveContradictions > rule.Contradictions))
            throw new InvalidDataException("Application planning profile rule is invalid.");
    }
}

using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

public static class EvidenceBundleResolver
{
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> sessions,
        IEnumerable<string> requested, IEnumerable<string> candidates)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string path, bool required)
        {
            try
            {
                using var bundle = RecordingBundle.Open(path);
                var session = bundle.ReadJson<RecordingManifest>("manifest.json").SessionId;
                if (!sessions.Contains(session, StringComparer.Ordinal))
                {
                    if (required) throw new InvalidDataException("The recording bundle does not match this map.");
                    return;
                }
                result.TryAdd(session, Path.GetFullPath(path));
            }
            catch (Exception ex) when (!required && ex is IOException or InvalidDataException or
                System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        foreach (var path in requested.Distinct(StringComparer.OrdinalIgnoreCase)) Add(path, true);
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) Add(path, false);
        return result.Values.ToArray();
    }
}

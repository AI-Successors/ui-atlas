using System.Security.Cryptography;
using System.Text;

namespace UiAtlas.Core.Build;

public static class StableIdentity
{
    public static string Create(string prefix, params string?[] parts)
    {
        var normalized = string.Join('\u001f', parts.Select(Normalize));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"{prefix}_{hash[..24]}";
    }

    public static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

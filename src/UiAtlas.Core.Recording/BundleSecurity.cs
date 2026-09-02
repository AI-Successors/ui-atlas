using System.IO.Compression;
using System.Text;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

public sealed record BundleLimits(
    int MaxEntries = RecordingContractLimits.MaxBundleEntries,
    long MaxEntryBytes = 64 * 1024 * 1024,
    long MaxTotalBytes = 512L * 1024 * 1024,
    double MaxCompressionRatio = 100);

public static class BundleSecurity
{
    public static IReadOnlyList<string> Inspect(ZipArchive archive, BundleLimits? limits = null)
    {
        limits ??= new BundleLimits();
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        if (archive.Entries.Count > limits.MaxEntries)
            errors.Add("entry-count-limit");

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(part => part is "" or "." or ".."))
                errors.Add($"unsafe-path:{Safe(name)}");
            var segments = name.Split('/');
            if (name.Length > 240 || segments.Length > 16 || segments.Any(UnsafeSegment))
                errors.Add($"unsafe-path-alias:{Safe(name)}");
            var collisionKey = name.Normalize(NormalizationForm.FormC);
            if (!names.Add(collisionKey))
                errors.Add($"duplicate-or-case-collision:{Safe(name)}");

            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            var windowsAttributes = entry.ExternalAttributes & 0xFFFF;
            if (unixType == 0xA000 || (windowsAttributes & 0x400) != 0)
                errors.Add($"link-or-reparse-entry:{Safe(name)}");

            if (entry.Length > limits.MaxEntryBytes)
                errors.Add($"entry-size-limit:{Safe(name)}");
            total = total > limits.MaxTotalBytes - Math.Min(entry.Length, limits.MaxTotalBytes) ? limits.MaxTotalBytes + 1 : total + entry.Length;
            if (entry.Length > 0 && entry.CompressedLength == 0)
                errors.Add($"invalid-compression-size:{Safe(name)}");
            else if (entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                errors.Add($"compression-ratio-limit:{Safe(name)}");
        }

        if (total > limits.MaxTotalBytes)
            errors.Add("total-size-limit");
        return errors;
    }

    public static string SafeDiagnostic(string value, int maxLength = 120)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string Safe(string value) => SafeDiagnostic(value);

    private static bool UnsafeSegment(string value)
    {
        if (value.Contains('\0') || value.EndsWith(' ') || value.EndsWith('.')) return true;
        var stem = value.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }
}

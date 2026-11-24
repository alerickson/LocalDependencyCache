using System.Diagnostics.CodeAnalysis;
using NuGet.Versioning;

namespace LocalDependencyCache.Query;

public static class VersionRangeParser
{
    /// <summary>
    /// Parses a NuGet version range as it appears in PowerShell Gallery metadata.
    /// Empty, <c>*</c>, and unbounded forms such as <c>(, )</c> mean any version.
    /// </summary>
    public static bool TryParse(string? rawVersionRange, [NotNullWhen(true)] out VersionRange? range)
    {
        var cleaned = rawVersionRange?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned == "*" || IsUnbounded(cleaned))
        {
            range = VersionRange.All;
            return true;
        }

        return VersionRange.TryParse(cleaned, out range);
    }

    private static bool IsUnbounded(string range)
        => range.Replace(" ", string.Empty) is "(,)" or "[,]" or "[,)" or "(,]";
}

using NuGet.Versioning;

namespace LocalDependencyCache.Query;

/// <summary>
/// Orders version strings by NuGet semantic-version precedence. Strings that are not valid
/// versions sort after valid ones, ordinally.
/// </summary>
public sealed class VersionStringComparer : IComparer<string>
{
    public static VersionStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        var xParsed = NuGetVersion.TryParse(x, out var xVersion);
        var yParsed = NuGetVersion.TryParse(y, out var yVersion);

        if (xParsed && yParsed)
        {
            var result = VersionComparer.Default.Compare(xVersion!, yVersion!);
            return result != 0 ? result : string.CompareOrdinal(x, y);
        }

        if (xParsed != yParsed)
        {
            return xParsed ? -1 : 1;
        }

        return string.CompareOrdinal(x, y);
    }
}

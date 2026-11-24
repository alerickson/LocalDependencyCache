using LocalDependencyCache.Query;
using NuGet.Versioning;

namespace LocalDependencyCache.Core;

public static class NuGetRangeParser
{
    public static VersionRange? Parse(string? rawVersionRange)
        => VersionRangeParser.TryParse(rawVersionRange, out var range) ? range : null;
}
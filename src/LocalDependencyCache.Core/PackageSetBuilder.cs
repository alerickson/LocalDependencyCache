using System.Security.Cryptography;
using System.Text;
using LocalDependencyCache.Query;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace LocalDependencyCache.Core;

/// <param name="Packages">Selected package versions, ordered by id then version.</param>
/// <param name="TopPackageCount">How many of <paramref name="Packages"/> came from the top-downloads list.</param>
/// <param name="UnresolvedDependencies">Dependencies with no satisfying version, as "Id range".</param>
public sealed record PackageSet(
    IReadOnlyList<PackageEntry> Packages,
    int TopPackageCount,
    IReadOnlyList<string> UnresolvedDependencies);

/// <summary>
/// Selects the cache contents: the latest stable version of the most-downloaded packages, plus the
/// versions needed to satisfy their dependencies, transitively.
/// </summary>
public sealed class PackageSetBuilder
{
    private readonly IGalleryClient _galleryClient;
    private readonly ILogger _logger;

    public PackageSetBuilder(IGalleryClient galleryClient, ILogger logger)
    {
        _galleryClient = galleryClient;
        _logger = logger;
    }

    public async Task<PackageSet> BuildAsync(int topPackageCount, bool includeDependencies, CancellationToken cancellationToken)
    {
        var top = await _galleryClient.GetTopPackagesAsync(topPackageCount, cancellationToken);
        var selected = new Dictionary<string, Dictionary<string, PackageEntry>>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<PackageEntry>();
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var latestCache = new Dictionary<string, PackageEntry?>(StringComparer.OrdinalIgnoreCase);
        var allVersionsCache = new Dictionary<string, IReadOnlyList<PackageEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in top)
        {
            if (TryAdd(selected, package))
            {
                pending.Enqueue(package);
            }
        }

        var topCount = Count(selected);

        while (includeDependencies && pending.Count > 0)
        {
            var package = pending.Dequeue();
            foreach (var dependency in package.Dependencies)
            {
                var match = await ResolveAsync(dependency, selected, latestCache, allVersionsCache, cancellationToken);
                if (match is null)
                {
                    var description = $"{dependency.Id} {(string.IsNullOrEmpty(dependency.RawVersionRange) ? "(any)" : dependency.RawVersionRange)}";
                    if (unresolved.Add(description))
                    {
                        _logger.LogWarning("No version of {Dependency} (required by {Package} {Version}) was found in the Gallery.", description, package.Id, package.Version);
                    }

                    continue;
                }

                if (TryAdd(selected, match))
                {
                    pending.Enqueue(match);
                }
            }
        }

        var packages = selected.Values
            .SelectMany(versions => versions.Values)
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, VersionStringComparer.Instance)
            .ToList();

        _logger.LogInformation(
            "Selected {Total} package versions: {Top} top packages and {Dependencies} dependency versions ({Unresolved} unresolved).",
            packages.Count, topCount, packages.Count - topCount, unresolved.Count);

        return new PackageSet(packages, topCount, unresolved.ToList());
    }

    /// <summary>
    /// Picks the version for a dependency, preferring, in order: a version already selected, the
    /// latest stable version, the highest satisfying stable version, then the highest satisfying prerelease.
    /// </summary>
    private async Task<PackageEntry?> ResolveAsync(
        DependencyEntry dependency,
        Dictionary<string, Dictionary<string, PackageEntry>> selected,
        Dictionary<string, PackageEntry?> latestCache,
        Dictionary<string, IReadOnlyList<PackageEntry>> allVersionsCache,
        CancellationToken cancellationToken)
    {
        if (!VersionRangeParser.TryParse(dependency.RawVersionRange, out var range))
        {
            _logger.LogWarning("Dependency {Dependency} has an invalid version range '{Range}'; using any version.", dependency.Id, dependency.RawVersionRange);
            range = VersionRange.All;
        }

        if (selected.TryGetValue(dependency.Id, out var alreadySelected))
        {
            var reuse = Highest(alreadySelected.Values, range, allowPrerelease: true);
            if (reuse is not null)
            {
                return reuse;
            }
        }

        if (!latestCache.TryGetValue(dependency.Id, out var latest))
        {
            latest = await _galleryClient.GetLatestVersionAsync(dependency.Id, cancellationToken);
            latestCache[dependency.Id] = latest;
        }

        if (latest is not null && Satisfies(latest, range))
        {
            return latest;
        }

        if (!allVersionsCache.TryGetValue(dependency.Id, out var allVersions))
        {
            allVersions = await _galleryClient.GetAllVersionsAsync(dependency.Id, cancellationToken);
            allVersionsCache[dependency.Id] = allVersions;
        }

        return Highest(allVersions, range, allowPrerelease: false) ?? Highest(allVersions, range, allowPrerelease: true);
    }

    private static PackageEntry? Highest(IEnumerable<PackageEntry> candidates, VersionRange range, bool allowPrerelease)
        => candidates
            .Select(candidate => (Package: candidate, Version: NuGetVersion.TryParse(candidate.Version, out var version) ? version : null))
            .Where(candidate => candidate.Version is not null
                && (allowPrerelease || !candidate.Version.IsPrerelease)
                && range.Satisfies(candidate.Version))
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Package)
            .FirstOrDefault();

    private static bool Satisfies(PackageEntry package, VersionRange range)
        => NuGetVersion.TryParse(package.Version, out var version) && range.Satisfies(version);

    private static bool TryAdd(Dictionary<string, Dictionary<string, PackageEntry>> selected, PackageEntry package)
    {
        if (!selected.TryGetValue(package.Id, out var versions))
        {
            selected[package.Id] = versions = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        }

        return versions.TryAdd(package.Version, package);
    }

    private static int Count(Dictionary<string, Dictionary<string, PackageEntry>> selected)
        => selected.Values.Sum(versions => versions.Count);

    /// <summary>
    /// Fingerprint of everything stored for the selected packages. Equal fingerprints mean the cache
    /// content would be identical, so the run can skip publishing.
    /// </summary>
    public static string ComputeContentHash(IEnumerable<PackageEntry> packages)
    {
        var builder = new StringBuilder();
        foreach (var package in packages
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(package.Id.ToLowerInvariant()).Append('|')
                .Append(package.Version).Append('|')
                .Append(package.NormalizedVersion).Append('|')
                .Append(package.PublishedUtc?.ToUnixTimeSeconds()).Append('|')
                .Append(package.UpdatedUtc?.ToUnixTimeSeconds()).Append('|')
                .Append(package.IsPrerelease ? '1' : '0').Append('|')
                .AppendJoin(',', package.Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)).Append('|')
                .AppendJoin(',', package.Dependencies
                    .Select(dependency => dependency.Id.ToLowerInvariant() + ":" + dependency.RawVersionRange)
                    .OrderBy(dependency => dependency, StringComparer.Ordinal))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

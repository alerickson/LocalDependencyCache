using LocalDependencyCache.Core;

namespace LocalDependencyCache.Tests;

/// <summary>
/// In-memory Gallery: a ranked top list plus every version of every package.
/// </summary>
internal sealed class FakeGalleryClient : IGalleryClient
{
    private readonly List<PackageEntry> _top = new();
    private readonly Dictionary<string, List<PackageEntry>> _versions = new(StringComparer.OrdinalIgnoreCase);

    public int TopRequests { get; private set; }

    public List<string> LatestRequests { get; } = new();

    public List<string> AllVersionsRequests { get; } = new();

    /// <summary>Runs before the top list is returned, e.g. to throw or to simulate a lost lease.</summary>
    public Action? OnTopRequested { get; set; }

    public static PackageEntry Package(string id, string version, string? dependencies = null, params string[] tags)
        => PackageEntry.Create(id, version, version, null, null, null, null,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            version.Contains('-'), tags, dependencies);

    /// <summary>Adds versions of a package that is not in the top list.</summary>
    public FakeGalleryClient With(params PackageEntry[] versions)
    {
        foreach (var version in versions)
        {
            if (!_versions.TryGetValue(version.Id, out var list))
            {
                _versions[version.Id] = list = new List<PackageEntry>();
            }

            list.Add(version);
        }

        return this;
    }

    /// <summary>Adds a package to the top list (in call order) and to the catalog.</summary>
    public FakeGalleryClient WithTop(PackageEntry latest, params PackageEntry[] olderVersions)
    {
        _top.Add(latest);
        return With(olderVersions.Append(latest).ToArray());
    }

    public Task<IReadOnlyList<PackageEntry>> GetTopPackagesAsync(int count, CancellationToken cancellationToken)
    {
        TopRequests++;
        OnTopRequested?.Invoke();
        return Task.FromResult<IReadOnlyList<PackageEntry>>(_top.Take(count).ToList());
    }

    public Task<PackageEntry?> GetLatestVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LatestRequests.Add(packageId);
        var latest = _versions.TryGetValue(packageId, out var versions)
            ? versions.Where(v => !v.IsPrerelease).OrderByDescending(v => NuGet.Versioning.NuGetVersion.Parse(v.Version)).FirstOrDefault()
            : null;
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<PackageEntry>> GetAllVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AllVersionsRequests.Add(packageId);
        return Task.FromResult<IReadOnlyList<PackageEntry>>(_versions.TryGetValue(packageId, out var versions) ? versions.ToList() : new List<PackageEntry>());
    }
}

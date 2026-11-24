using NuGet.Versioning;

namespace LocalDependencyCache.Core;

public sealed record SyncSettings
{
    public string GalleryEndpoint { get; init; } = "https://www.powershellgallery.com/api/v2/Search()";

    public int PageSize { get; init; } = 100;

    public int OverlapMinutes { get; init; } = 5;

    /// <summary>
    /// How far back to look on the very first run when no checkpoint exists.
    /// Set to a large value (e.g. 87600 = 10 years) to seed the full catalog.
    /// Defaults to 5 hours for fast incremental top-up.
    /// </summary>
    public int InitialLookBackHours { get; init; } = 5;

    /// <summary>
    /// Optional hard override: start from this UTC timestamp regardless of stored checkpoint.
    /// Use this to force a full re-sync from a known date (e.g. "2010-01-01T00:00:00Z").
    /// When set, the stored checkpoint is ignored.
    /// </summary>
    public string? FullSyncFromUtc { get; init; }

    /// <summary>
    /// Retries for transient gallery failures (network errors, timeouts, 408, 429, 5xx) per page.
    /// </summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>
    /// Base delay for exponential backoff between retries. A Retry-After header takes precedence.
    /// </summary>
    public int RetryBaseDelayMilliseconds { get; init; } = 1000;

    /// <summary>
    /// Stop fetching after this many minutes and publish the progress so far; the next run resumes
    /// from the saved checkpoint. Keep it below the Functions timeout. 0 means no limit.
    /// </summary>
    public int MaxSyncDurationMinutes { get; init; }

    public string StorageMode { get; init; } = "FileSystem";

    public string OutputDirectory { get; init; } = ".artifacts";

    public string DatabaseName { get; init; } = "psgallery-cache.sqlite";

    public string LatestBlobName { get; init; } = "latest/psgallery-cache.zip";

    public string ManifestBlobName { get; init; } = "latest/psgallery-cache.manifest.json";

    /// <summary>
    /// Blob (or file under OutputDirectory) used to stop overlapping sync runs.
    /// </summary>
    public string LockBlobName { get; init; } = "locks/psgallery-cache.lock";

    /// <summary>
    /// Number of versioned artifacts to keep under versions/; older ones are deleted after a publish.
    /// 0 keeps every version.
    /// </summary>
    public int RetainVersions { get; init; } = 10;

    public string? BlobConnectionString { get; init; }

    public string? BlobContainerName { get; init; }

    public string? BlobContainerUrl { get; init; }

    public string? BlobBaseUrl { get; init; }
}

public sealed record PackageEntry(
    string Id,
    string Version,
    string? NormalizedVersion,
    string? Title,
    string? Description,
    string? Authors,
    string? Owners,
    DateTimeOffset? PublishedUtc,
    DateTimeOffset? UpdatedUtc,
    bool IsPrerelease,
    IReadOnlyList<string> Tags,
    string? RawDependencies,
    IReadOnlyList<DependencyEntry> Dependencies)
{
    public static PackageEntry Create(
        string id,
        string version,
        string? normalizedVersion,
        string? title,
        string? description,
        string? authors,
        string? owners,
        DateTimeOffset? publishedUtc,
        DateTimeOffset? updatedUtc,
        bool isPrerelease,
        IEnumerable<string>? tags,
        string? rawDependencies)
    {
        var distinctTags = (tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PackageEntry(
            id,
            version,
            normalizedVersion,
            title,
            description,
            authors,
            owners,
            publishedUtc,
            updatedUtc,
            isPrerelease,
            distinctTags,
            rawDependencies,
            DependencyEntry.ParseMany(rawDependencies).ToArray());
    }
}

public sealed record DependencyEntry(
    string Id,
    string RawVersionRange,
    string? MinVersion,
    string? MaxVersion,
    bool IncludeMin,
    bool IncludeMax,
    bool IsExact,
    bool IsAny)
{
    public static IEnumerable<DependencyEntry> ParseMany(string? rawDependencies)
    {
        if (string.IsNullOrWhiteSpace(rawDependencies))
        {
            yield break;
        }

        var tokens = rawDependencies.Split(new[] { '|', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var cleaned = token.Trim().TrimEnd(':');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            // Gallery format is "Id:Range:TargetFramework"; the framework segment is usually empty.
            var parts = cleaned.Split(':', 3, StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            yield return From(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
        }
    }

    public static DependencyEntry From(string id, string? rawVersionRange)
    {
        var range = NuGetRangeParser.Parse(rawVersionRange);
        var minVersion = range?.MinVersion?.ToNormalizedString();
        var maxVersion = range?.MaxVersion?.ToNormalizedString();
        var isAny = range is null || range == VersionRange.All;
        var isExact = range is not null
            && range != VersionRange.All
            && range.MinVersion is not null
            && range.MaxVersion is not null
            && range.IsMinInclusive
            && range.IsMaxInclusive
            && string.Equals(range.MinVersion.ToNormalizedString(), range.MaxVersion.ToNormalizedString(), StringComparison.OrdinalIgnoreCase);

        return new DependencyEntry(
            id,
            rawVersionRange ?? string.Empty,
            minVersion,
            maxVersion,
            range?.IsMinInclusive ?? false,
            range?.IsMaxInclusive ?? false,
            isExact,
            isAny);
    }
}

public sealed record CacheManifest(
    string ArtifactUrl,
    string Version,
    string Sha256,
    string? ETag,
    DateTimeOffset LastModifiedUtc);

/// <param name="IsComplete">False when the run stopped early (time budget or gallery failure) after
/// publishing partial progress; the next run resumes from <paramref name="CheckpointUtc"/>.</param>
/// <param name="Manifest">Null when the run found no changes and skipped publishing.</param>
public sealed record SyncResult(
    int ProcessedPackages,
    string CheckpointUtc,
    string? ArtifactVersion,
    string? ArtifactPath,
    CacheManifest? Manifest,
    bool IsComplete = true)
{
    public bool Published => Manifest is not null;
}

public interface IGalleryClient
{
    IAsyncEnumerable<IReadOnlyList<PackageEntry>> GetPackagePagesAsync(DateTimeOffset checkpointUtc, int pageSize, CancellationToken cancellationToken);
}

public interface ICacheArtifactStore
{
    Task<bool> TryDownloadLatestAsync(string destinationPath, CancellationToken cancellationToken);

    Task<CacheManifest> PublishAsync(string sourcePath, string version, string sha256, DateTimeOffset lastModifiedUtc, CancellationToken cancellationToken);
}

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
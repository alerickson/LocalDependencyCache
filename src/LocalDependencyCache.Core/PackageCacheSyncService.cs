using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Core;

/// <summary>
/// Rebuilds the cache from scratch each run: the latest stable version of the most-downloaded
/// packages plus their dependencies. Publishes only when the content differs from the published cache.
/// </summary>
public sealed class PackageCacheSyncService
{
    public const string ContentHashStateKey = "content_hash";
    public const string GeneratedUtcStateKey = "generated_utc";
    public const string TopPackageCountStateKey = "top_package_count";

    private readonly IGalleryClient _galleryClient;
    private readonly ICacheArtifactStore _artifactStore;
    private readonly SyncSettings _settings;
    private readonly ISystemClock _clock;
    private readonly ILogger<PackageCacheSyncService> _logger;
    private readonly ISyncLock? _syncLock;

    public PackageCacheSyncService(
        IGalleryClient galleryClient,
        ICacheArtifactStore artifactStore,
        IOptions<SyncSettings> settings,
        ISystemClock clock,
        ILogger<PackageCacheSyncService> logger,
        ISyncLock? syncLock = null)
    {
        _galleryClient = galleryClient;
        _artifactStore = artifactStore;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;
        _syncLock = syncLock;
    }

    /// <summary>
    /// Runs one sync. Returns null without changing anything when another run holds the sync lock.
    /// </summary>
    public async Task<SyncResult?> RunAsync(CancellationToken cancellationToken)
    {
        if (_syncLock is null)
        {
            return await RunLockedAsync(cancellationToken);
        }

        await using var lease = await _syncLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            _logger.LogWarning("Another sync run holds the lock; skipping this run.");
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LeaseLost);
        return await RunLockedAsync(linked.Token);
    }

    private async Task<SyncResult> RunLockedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_settings.OutputDirectory);
        var previousHash = await GetPublishedContentHashAsync(cancellationToken);

        var builder = new PackageSetBuilder(_galleryClient, _logger);
        var packageSet = await builder.BuildAsync(_settings.TopPackageCount, _settings.IncludeDependencies, cancellationToken);
        var contentHash = PackageSetBuilder.ComputeContentHash(packageSet.Packages);

        if (previousHash == contentHash)
        {
            _logger.LogInformation("Cache content is unchanged ({PackageCount} package versions); skipping publish.", packageSet.Packages.Count);
            return Result(packageSet, contentHash, null, null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var databasePath = Path.Combine(_settings.OutputDirectory, _settings.DatabaseName);
        DeleteIfExists(databasePath);
        var cache = new SqlitePackageCache(databasePath);
        cache.Initialize();
        var generatedUtc = _clock.UtcNow;
        cache.UpsertPackages(packageSet.Packages, new Dictionary<string, string>
        {
            [ContentHashStateKey] = contentHash,
            [GeneratedUtcStateKey] = generatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            [TopPackageCountStateKey] = packageSet.TopPackageCount.ToString(CultureInfo.InvariantCulture)
        });
        cache.Compact();

        var artifactPath = Path.Combine(_settings.OutputDirectory, Path.GetFileNameWithoutExtension(_settings.DatabaseName) + ".zip");
        ArtifactZipUtilities.CreateSingleFileArchive(databasePath, artifactPath);
        var sha256 = ComputeSha256(artifactPath);

        // Content-addressed so a published version name is never reused for different bytes.
        var version = $"{generatedUtc.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{sha256[..12]}";
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = await _artifactStore.PublishAsync(artifactPath, version, sha256, generatedUtc, cancellationToken);

        _logger.LogInformation("Published cache version {Version} with {PackageCount} package versions.", version, packageSet.Packages.Count);
        return Result(packageSet, contentHash, version, artifactPath, manifest);
    }

    private async Task<string?> GetPublishedContentHashAsync(CancellationToken cancellationToken)
    {
        var previousPath = Path.Combine(_settings.OutputDirectory, "published-" + _settings.DatabaseName);
        DeleteIfExists(previousPath);

        try
        {
            return await _artifactStore.TryDownloadLatestAsync(previousPath, cancellationToken)
                ? new SqlitePackageCache(previousPath).GetState(ContentHashStateKey)
                : null;
        }
        finally
        {
            DeleteIfExists(previousPath);
        }
    }

    private static SyncResult Result(PackageSet packageSet, string contentHash, string? version, string? artifactPath, CacheManifest? manifest)
        => new(packageSet.Packages.Count, packageSet.TopPackageCount, packageSet.UnresolvedDependencies, contentHash, version, artifactPath, manifest);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Core;

public sealed class PackageCacheSyncService
{
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

    private const string FullSyncFromStateKey = "full_sync_from_utc";
    private const string FullSyncCursorStateKey = "full_sync_cursor_utc";
    private const string FullSyncCompletedStateKey = "full_sync_completed";

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
        var workingDatabasePath = Path.Combine(_settings.OutputDirectory, _settings.DatabaseName);
        var latestDownloaded = await _artifactStore.TryDownloadLatestAsync(workingDatabasePath, cancellationToken);

        if (!latestDownloaded && File.Exists(workingDatabasePath))
        {
            File.Delete(workingDatabasePath);
        }

        var cache = new SqlitePackageCache(workingDatabasePath);
        cache.Initialize();

        var overlap = TimeSpan.FromMinutes(Math.Abs(_settings.OverlapMinutes));
        var storedCheckpoint = cache.GetCheckpoint();
        var fullSync = GetActiveFullSync(cache, overlap);
        DateTimeOffset fetchFrom;

        if (fullSync is not null)
        {
            fetchFrom = fullSync.Value.FetchFrom;
            _logger.LogInformation("Full sync from {FullSyncFrom} active. Fetching from {FetchFrom}.", fullSync.Value.RequestedFrom, fetchFrom);
        }
        else
        {
            fetchFrom = storedCheckpoint?.Subtract(overlap)
                ?? _clock.UtcNow.AddHours(-Math.Abs(_settings.InitialLookBackHours));
            _logger.LogInformation("Starting cache sync from {FetchFrom}.", fetchFrom);
        }

        // The checkpoint only moves forward; the overlap window and full syncs re-read older packages.
        var latestPublished = storedCheckpoint ?? fetchFrom;
        var fullSyncCursor = fetchFrom;
        var processed = 0;
        var isComplete = true;
        Exception? failure = null;
        var deadline = _settings.MaxSyncDurationMinutes > 0
            ? _clock.UtcNow.AddMinutes(_settings.MaxSyncDurationMinutes)
            : (DateTimeOffset?)null;

        try
        {
            await foreach (var page in _galleryClient.GetPackagePagesAsync(fetchFrom, _settings.PageSize, cancellationToken))
            {
                var pageMax = page.Max(package => package.PublishedUtc) ?? fetchFrom;
                latestPublished = Max(latestPublished, pageMax);
                fullSyncCursor = Max(fullSyncCursor, pageMax);

                var state = new Dictionary<string, string> { [SqlitePackageCache.LastPublishedStateKey] = FormatUtc(latestPublished) };
                if (fullSync is not null)
                {
                    state[FullSyncCursorStateKey] = FormatUtc(fullSyncCursor);
                }

                cache.UpsertPackages(page, state);
                processed += page.Count;

                if (deadline is not null && _clock.UtcNow >= deadline)
                {
                    isComplete = false;
                    _logger.LogWarning(
                        "Sync time budget of {Minutes} minutes reached after {Processed} packages. Publishing progress; the next run resumes from {Checkpoint}.",
                        _settings.MaxSyncDurationMinutes, processed, fullSync is not null ? fullSyncCursor : latestPublished);
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && processed > 0)
        {
            // Committed pages are consistent with their checkpoint, so publish them before failing the run.
            isComplete = false;
            failure = exception;
            _logger.LogError(exception, "Gallery sync failed after {Processed} packages. Publishing progress before rethrowing.", processed);
        }

        if (isComplete)
        {
            if (fullSync is not null)
            {
                cache.SetState(FullSyncCompletedStateKey, "true");
                cache.DeleteState(FullSyncCursorStateKey);
            }

            cache.SetState("last_success_utc", FormatUtc(_clock.UtcNow));
        }

        cancellationToken.ThrowIfCancellationRequested();
        cache.Compact();

        var version = latestPublished.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var compressedArtifactPath = Path.Combine(_settings.OutputDirectory, Path.GetFileNameWithoutExtension(_settings.DatabaseName) + ".zip");
        ArtifactZipUtilities.CreateSingleFileArchive(workingDatabasePath, compressedArtifactPath);
        var sha256 = ComputeSha256(compressedArtifactPath);
        var manifest = await _artifactStore.PublishAsync(compressedArtifactPath, version, sha256, _clock.UtcNow, cancellationToken);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return new SyncResult(processed, FormatUtc(latestPublished), version, compressedArtifactPath, manifest, isComplete);
    }

    /// <summary>
    /// A FullSyncFromUtc value runs to completion once, resuming across runs if it is interrupted.
    /// Changing the value starts a new full sync.
    /// </summary>
    private (DateTimeOffset RequestedFrom, DateTimeOffset FetchFrom)? GetActiveFullSync(SqlitePackageCache cache, TimeSpan overlap)
    {
        if (string.IsNullOrWhiteSpace(_settings.FullSyncFromUtc)
            || !DateTimeOffset.TryParse(_settings.FullSyncFromUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestedFrom))
        {
            return null;
        }

        var requested = FormatUtc(requestedFrom);
        if (cache.GetState(FullSyncFromStateKey) != requested)
        {
            cache.SetState(FullSyncFromStateKey, requested);
            cache.DeleteState(FullSyncCursorStateKey);
            cache.DeleteState(FullSyncCompletedStateKey);
            return (requestedFrom, requestedFrom);
        }

        if (cache.GetState(FullSyncCompletedStateKey) == "true")
        {
            _logger.LogInformation("Full sync from {FullSyncFrom} already completed; running incremental sync.", requested);
            return null;
        }

        var cursorText = cache.GetState(FullSyncCursorStateKey);
        var fetchFrom = DateTimeOffset.TryParse(cursorText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var cursor)
            ? Max(requestedFrom, cursor.Subtract(overlap))
            : requestedFrom;
        return (requestedFrom, fetchFrom);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
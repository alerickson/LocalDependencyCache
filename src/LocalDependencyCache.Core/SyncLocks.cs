using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace LocalDependencyCache.Core;

/// <summary>
/// Prevents overlapping sync runs from downloading, modifying, and publishing the cache concurrently.
/// </summary>
public interface ISyncLock
{
    /// <summary>
    /// Returns a held lease, or null when another run already holds the lock.
    /// </summary>
    Task<ISyncLease?> TryAcquireAsync(CancellationToken cancellationToken);
}

public interface ISyncLease : IAsyncDisposable
{
    /// <summary>
    /// Cancelled if the lock is lost while held; the run must stop without publishing.
    /// </summary>
    CancellationToken LeaseLost { get; }
}

/// <summary>
/// Holds an exclusive handle on a lock file. The OS releases it if the process dies.
/// </summary>
public sealed class FileSystemSyncLock : ISyncLock
{
    private readonly string _lockFilePath;

    public FileSystemSyncLock(string lockFilePath)
    {
        _lockFilePath = lockFilePath;
    }

    public Task<ISyncLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_lockFilePath)) ?? ".");

        try
        {
            var stream = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult<ISyncLease?>(new FileSyncLease(stream));
        }
        catch (IOException)
        {
            return Task.FromResult<ISyncLease?>(null);
        }
    }

    private sealed class FileSyncLease : ISyncLease
    {
        private readonly FileStream _stream;

        public FileSyncLease(FileStream stream) => _stream = stream;

        public CancellationToken LeaseLost => CancellationToken.None;

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}

/// <summary>
/// Takes a renewable Azure Blob lease on a lock blob. An abandoned lease expires after
/// <see cref="LeaseDuration"/>, so a crashed run cannot block later runs.
/// </summary>
public sealed class BlobSyncLock : ISyncLock
{
    private readonly BlobContainerClient _containerClient;
    private readonly string _lockBlobName;
    private readonly ILogger _logger;

    public BlobSyncLock(BlobContainerClient containerClient, string lockBlobName, ILogger logger)
    {
        _containerClient = containerClient;
        _lockBlobName = lockBlobName;
        _logger = logger;
    }

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<ISyncLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var lockBlob = _containerClient.GetBlobClient(_lockBlobName);

        try
        {
            await lockBlob.UploadAsync(BinaryData.Empty, overwrite: false, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // The lock blob already exists (and may be leased).
        }

        var leaseClient = lockBlob.GetBlobLeaseClient();
        try
        {
            await leaseClient.AcquireAsync(LeaseDuration, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            return null;
        }

        return new BlobSyncLease(leaseClient, LeaseDuration, RenewInterval, _logger);
    }

    private sealed class BlobSyncLease : ISyncLease
    {
        private readonly BlobLeaseClient _leaseClient;
        private readonly TimeSpan _leaseDuration;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewal;

        public BlobSyncLease(BlobLeaseClient leaseClient, TimeSpan leaseDuration, TimeSpan renewInterval, ILogger logger)
        {
            _leaseClient = leaseClient;
            _leaseDuration = leaseDuration;
            _logger = logger;
            _renewal = RenewLoopAsync(renewInterval);
        }

        public CancellationToken LeaseLost => _lost.Token;

        private async Task RenewLoopAsync(TimeSpan renewInterval)
        {
            var lastRenewed = DateTimeOffset.UtcNow;
            using var timer = new PeriodicTimer(renewInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    try
                    {
                        await _leaseClient.RenewAsync(cancellationToken: _stop.Token);
                        lastRenewed = DateTimeOffset.UtcNow;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // A conflict means another holder took the lease; otherwise tolerate
                        // transient failures until the lease may have expired.
                        var lost = exception is RequestFailedException { Status: 409 or 412 }
                            || DateTimeOffset.UtcNow - lastRenewed >= _leaseDuration - renewInterval;

                        if (lost)
                        {
                            _logger.LogError(exception, "Lost the sync lease; stopping the run without publishing.");
                            _lost.Cancel();
                            return;
                        }

                        _logger.LogWarning(exception, "Failed to renew the sync lease; will retry.");
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _renewal;

            if (!_lost.IsCancellationRequested)
            {
                try
                {
                    await _leaseClient.ReleaseAsync();
                }
                catch (RequestFailedException exception)
                {
                    _logger.LogWarning(exception, "Failed to release the sync lease; it will expire on its own.");
                }
            }

            _stop.Dispose();
            _lost.Dispose();
        }
    }
}

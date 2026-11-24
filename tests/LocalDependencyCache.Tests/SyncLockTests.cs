using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using LocalDependencyCache.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Tests;

public sealed class SyncLockTests : IDisposable
{
    private const string BlobConnectionVariable = "LDC_TEST_BLOB_CONNECTION_STRING";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task File_lock_is_exclusive_until_released()
    {
        var syncLock = new FileSystemSyncLock(Path.Combine(_rootPath, "locks", "cache.lock"));

        var first = await syncLock.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await syncLock.TryAcquireAsync(CancellationToken.None));

        await first.DisposeAsync();
        var second = await syncLock.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Sync_is_skipped_without_changes_when_lock_is_held()
    {
        var syncLock = new FileSystemSyncLock(Path.Combine(_rootPath, "locks", "cache.lock"));
        var gallery = Gallery();
        await using var held = await syncLock.TryAcquireAsync(CancellationToken.None);

        var result = await CreateService(gallery, syncLock).RunAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, gallery.TopRequests);
        Assert.False(File.Exists(Path.Combine(_rootPath, "latest", "cache.zip")));
    }

    [Fact]
    public async Task Sync_runs_and_releases_the_lock_when_free()
    {
        var syncLock = new FileSystemSyncLock(Path.Combine(_rootPath, "locks", "cache.lock"));

        var result = await CreateService(Gallery(), syncLock).RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(_rootPath, "latest", "cache.zip")));
        await using var after = await syncLock.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Losing_the_lease_stops_the_run_without_publishing()
    {
        var syncLock = new LosableLock();
        var gallery = Gallery();
        gallery.OnTopRequested = syncLock.Lose;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(gallery, syncLock).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_rootPath, "latest", "cache.zip")));
        Assert.True(syncLock.Disposed);
    }

    [SkippableFact]
    public async Task Blob_lease_is_exclusive_until_released()
    {
        var container = await CreateContainerAsync();
        var syncLock = new BlobSyncLock(container, "locks/cache.lock", NullLogger.Instance);

        var first = await syncLock.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await syncLock.TryAcquireAsync(CancellationToken.None));

        await first.DisposeAsync();
        var second = await syncLock.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(second);
        await second.DisposeAsync();
        await container.DeleteAsync();
    }

    [SkippableFact]
    public async Task Blob_lease_is_renewed_past_its_duration()
    {
        var container = await CreateContainerAsync();
        var syncLock = new BlobSyncLock(container, "locks/cache.lock", NullLogger.Instance)
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(2)
        };

        await using (var lease = await syncLock.TryAcquireAsync(CancellationToken.None))
        {
            Assert.NotNull(lease);
            await Task.Delay(TimeSpan.FromSeconds(18));
            Assert.Null(await syncLock.TryAcquireAsync(CancellationToken.None));
            Assert.False(lease.LeaseLost.IsCancellationRequested);
        }

        await container.DeleteAsync();
    }

    [SkippableFact]
    public async Task Blob_lease_reports_loss_when_broken()
    {
        var container = await CreateContainerAsync();
        var syncLock = new BlobSyncLock(container, "locks/cache.lock", NullLogger.Instance)
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromMilliseconds(500)
        };

        await using (var lease = await syncLock.TryAcquireAsync(CancellationToken.None))
        {
            Assert.NotNull(lease);
            await container.GetBlobClient("locks/cache.lock").GetBlobLeaseClient().BreakAsync(TimeSpan.Zero);

            var lost = new TaskCompletionSource();
            using var registration = lease.LeaseLost.Register(() => lost.TrySetResult());
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await container.DeleteAsync();
    }

    private static async Task<BlobContainerClient> CreateContainerAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(BlobConnectionVariable);
        Skip.If(string.IsNullOrWhiteSpace(connectionString), $"Set {BlobConnectionVariable} (for example to UseDevelopmentStorage=true with Azurite running) to run Blob lease tests.");

        var container = new BlobContainerClient(connectionString, "ldc-test-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        return container;
    }

    private PackageCacheSyncService CreateService(IGalleryClient gallery, ISyncLock syncLock)
        => new(
            gallery,
            new FileSystemArtifactStore(_rootPath, "latest/cache.zip", "latest/cache.manifest.json", null),
            Options.Create(new SyncSettings
            {
                OutputDirectory = Path.Combine(_rootPath, "work"),
                DatabaseName = "cache.sqlite",
                LatestBlobName = "latest/cache.zip",
                ManifestBlobName = "latest/cache.manifest.json"
            }),
            new SystemClock(),
            NullLogger<PackageCacheSyncService>.Instance,
            syncLock);

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private static FakeGalleryClient Gallery()
        => new FakeGalleryClient()
            .WithTop(FakeGalleryClient.Package("First", "1.0.0", "Second:[1.0, )"))
            .With(FakeGalleryClient.Package("Second", "1.0.0"));

    private sealed class LosableLock : ISyncLock, ISyncLease
    {
        private readonly CancellationTokenSource _lost = new();

        public bool Disposed { get; private set; }

        public CancellationToken LeaseLost => _lost.Token;

        public void Lose() => _lost.Cancel();

        public Task<ISyncLease?> TryAcquireAsync(CancellationToken cancellationToken) => Task.FromResult<ISyncLease?>(this);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

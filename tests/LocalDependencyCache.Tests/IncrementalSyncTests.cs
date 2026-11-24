using System.Runtime.CompilerServices;
using LocalDependencyCache.Core;
using LocalDependencyCache.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Tests;

public sealed class IncrementalSyncTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));
    private readonly SettableClock _clock = new(Now);

    [Fact]
    public async Task Each_page_is_committed_with_its_checkpoint()
    {
        var gallery = new ScriptedGallery(
            Page(("A", "2026-06-04T10:00:00Z")),
            Page(("B", "2026-06-04T10:30:00Z")),
            Page(("C", "2026-06-04T11:00:00Z")));

        var result = await CreateService(gallery).RunAsync(CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.ProcessedPackages);
        var published = await DownloadPublishedAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-06-04T11:00:00Z"), published.GetCheckpoint());
        Assert.Equal(3, new[] { "A", "B", "C" }.Count(id => new SqlitePackageQueryClient(published.DatabasePath).FindByPackageId(id).Count == 1));
        Assert.Equal(gallery.PageCommittedCheckpoints, new DateTimeOffset?[]
        {
            DateTimeOffset.Parse("2026-06-04T10:00:00Z"),
            DateTimeOffset.Parse("2026-06-04T10:30:00Z")
        });
    }

    [Fact]
    public async Task Time_budget_publishes_partial_progress_and_next_run_resumes()
    {
        var first = new ScriptedGallery(
            Page(("A", "2026-06-04T10:00:00Z")),
            Page(("B", "2026-06-04T10:30:00Z")),
            Page(("C", "2026-06-04T11:00:00Z")))
        {
            // Advance past the budget while the second page is being fetched.
            BeforePage = index => { if (index == 1) _clock.UtcNow = Now.AddMinutes(30); }
        };

        var result = await CreateService(first, maxMinutes: 10).RunAsync(CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(2, result.ProcessedPackages);
        Assert.Equal(2, first.PagesServed);
        var published = await DownloadPublishedAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-06-04T10:30:00Z"), published.GetCheckpoint());
        Assert.Null(published.GetState("last_success_utc"));

        _clock.UtcNow = Now;
        var second = new ScriptedGallery(Page(("C", "2026-06-04T11:00:00Z")));
        result = await CreateService(second, maxMinutes: 10).RunAsync(CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(DateTimeOffset.Parse("2026-06-04T10:25:00Z"), Assert.Single(second.RequestedFrom));
        Assert.Equal(DateTimeOffset.Parse("2026-06-04T11:00:00Z"), (await DownloadPublishedAsync()).GetCheckpoint());
    }

    [Fact]
    public async Task Gallery_failure_after_progress_publishes_then_rethrows()
    {
        var gallery = new ScriptedGallery(
            Page(("A", "2026-06-04T10:00:00Z")),
            new HttpRequestException("Gallery down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(gallery).RunAsync(CancellationToken.None));

        var published = await DownloadPublishedAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-06-04T10:00:00Z"), published.GetCheckpoint());
        Assert.Single(new SqlitePackageQueryClient(published.DatabasePath).FindByPackageId("A"));
    }

    [Fact]
    public async Task Gallery_failure_before_any_progress_publishes_nothing()
    {
        var gallery = new ScriptedGallery(new HttpRequestException("Gallery down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(gallery).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_rootPath, "latest", "cache.zip")));
    }

    [Fact]
    public async Task Empty_runs_do_not_move_the_checkpoint_backwards()
    {
        await CreateService(new ScriptedGallery(Page(("A", "2026-06-04T10:00:00Z")))).RunAsync(CancellationToken.None);

        var empty = new ScriptedGallery();
        await CreateService(empty).RunAsync(CancellationToken.None);
        await CreateService(empty).RunAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-06-04T10:00:00Z"), (await DownloadPublishedAsync()).GetCheckpoint());
        Assert.All(empty.RequestedFrom, from => Assert.Equal(DateTimeOffset.Parse("2026-06-04T09:55:00Z"), from));
    }

    [Fact]
    public async Task Overlap_window_does_not_regress_checkpoint_when_older_packages_are_reread()
    {
        await CreateService(new ScriptedGallery(Page(("A", "2026-06-04T10:00:00Z")))).RunAsync(CancellationToken.None);
        await CreateService(new ScriptedGallery(Page(("Old", "2026-06-04T09:58:00Z")))).RunAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-06-04T10:00:00Z"), (await DownloadPublishedAsync()).GetCheckpoint());
    }

    [Fact]
    public async Task Full_sync_resumes_across_runs_and_runs_once()
    {
        const string fullSyncFrom = "2010-01-01T00:00:00Z";
        await CreateService(new ScriptedGallery(Page(("Recent", "2026-06-04T10:00:00Z")))).RunAsync(CancellationToken.None);

        var first = new ScriptedGallery(
            Page(("Old1", "2015-01-01T00:00:00Z")),
            Page(("Old2", "2018-01-01T00:00:00Z")),
            Page(("Old3", "2020-01-01T00:00:00Z")))
        {
            BeforePage = index => { if (index == 1) _clock.UtcNow = Now.AddMinutes(30); }
        };
        var result = await CreateService(first, maxMinutes: 10, fullSyncFrom: fullSyncFrom).RunAsync(CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(DateTimeOffset.Parse(fullSyncFrom), Assert.Single(first.RequestedFrom));
        Assert.Equal(DateTimeOffset.Parse("2026-06-04T10:00:00Z"), (await DownloadPublishedAsync()).GetCheckpoint());

        _clock.UtcNow = Now;
        var second = new ScriptedGallery(Page(("Old3", "2020-01-01T00:00:00Z")), Page(("Recent", "2026-06-04T10:00:00Z")));
        result = await CreateService(second, maxMinutes: 10, fullSyncFrom: fullSyncFrom).RunAsync(CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(DateTimeOffset.Parse("2017-12-31T23:55:00Z"), Assert.Single(second.RequestedFrom));

        var third = new ScriptedGallery();
        await CreateService(third, fullSyncFrom: fullSyncFrom).RunAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-06-04T09:55:00Z"), Assert.Single(third.RequestedFrom));
        var published = await DownloadPublishedAsync();
        Assert.Equal("true", published.GetState("full_sync_completed"));
        Assert.All(new[] { "Old1", "Old2", "Old3", "Recent" },
            id => Assert.Single(new SqlitePackageQueryClient(published.DatabasePath).FindByPackageId(id)));
    }

    private PackageCacheSyncService CreateService(ScriptedGallery gallery, int maxMinutes = 0, string? fullSyncFrom = null)
    {
        var store = new FileSystemArtifactStore(_rootPath, "latest/cache.zip", "latest/cache.manifest.json", null);
        gallery.Store = store;
        gallery.WorkingDatabasePath = Path.Combine(_rootPath, "work", "cache.sqlite");

        return new PackageCacheSyncService(
            gallery,
            store,
            Options.Create(new SyncSettings
            {
                OutputDirectory = Path.Combine(_rootPath, "work"),
                DatabaseName = "cache.sqlite",
                LatestBlobName = "latest/cache.zip",
                ManifestBlobName = "latest/cache.manifest.json",
                OverlapMinutes = 5,
                InitialLookBackHours = 24,
                MaxSyncDurationMinutes = maxMinutes,
                FullSyncFromUtc = fullSyncFrom
            }),
            _clock,
            NullLogger<PackageCacheSyncService>.Instance);
    }

    private async Task<SqlitePackageCache> DownloadPublishedAsync()
    {
        var path = Path.Combine(_rootPath, "downloaded", Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new FileSystemArtifactStore(_rootPath, "latest/cache.zip", "latest/cache.manifest.json", null);
        Assert.True(await store.TryDownloadLatestAsync(path, CancellationToken.None));
        return new SqlitePackageCache(path);
    }

    private static IReadOnlyList<PackageEntry> Page(params (string Id, string Published)[] packages)
        => packages.Select(p =>
        {
            var published = DateTimeOffset.Parse(p.Published);
            return PackageEntry.Create(p.Id, "1.0.0", "1.0.0", null, null, null, null, published, published, false, null, null);
        }).ToList();

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private sealed class SettableClock : ISystemClock
    {
        public SettableClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }
    }

    /// <summary>
    /// Serves a fixed sequence of pages or exceptions, recording the requested start and the
    /// checkpoint committed to the working database before each subsequent page is requested.
    /// </summary>
    private sealed class ScriptedGallery : IGalleryClient
    {
        private readonly object[] _steps;

        public ScriptedGallery(params object[] steps) => _steps = steps;

        public Action<int>? BeforePage { get; init; }

        public List<DateTimeOffset> RequestedFrom { get; } = new();

        public List<DateTimeOffset?> PageCommittedCheckpoints { get; } = new();

        public int PagesServed { get; private set; }

        public ICacheArtifactStore? Store { get; set; }

        public string? WorkingDatabasePath { get; set; }

        public async IAsyncEnumerable<IReadOnlyList<PackageEntry>> GetPackagePagesAsync(DateTimeOffset checkpointUtc, int pageSize, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedFrom.Add(checkpointUtc);
            for (var index = 0; index < _steps.Length; index++)
            {
                await Task.Yield();
                if (index > 0 && WorkingDatabasePath is not null)
                {
                    PageCommittedCheckpoints.Add(new SqlitePackageCache(WorkingDatabasePath).GetCheckpoint());
                }

                BeforePage?.Invoke(index);
                if (_steps[index] is Exception exception)
                {
                    throw exception;
                }

                PagesServed++;
                yield return (IReadOnlyList<PackageEntry>)_steps[index];
            }
        }
    }
}

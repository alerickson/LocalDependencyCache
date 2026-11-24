using LocalDependencyCache.Core;
using LocalDependencyCache.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LocalDependencyCache.Tests.FakeGalleryClient;

namespace LocalDependencyCache.Tests;

public sealed class TopPackageSyncTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Publishes_top_packages_and_dependencies_with_state()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("App", "2.0.0", "Lib:[1.0, 2.0)", "PSCommand_Get-App"), Package("App", "1.0.0"))
            .With(Package("Lib", "2.0.0"), Package("Lib", "1.4.0"));

        var result = await RunAsync(gallery, topCount: 10);

        Assert.True(result.Published);
        Assert.Equal(2, result.PackageCount);
        Assert.Equal(1, result.TopPackageCount);

        var published = await DownloadPublishedAsync();
        var client = new SqlitePackageQueryClient(published.DatabasePath);
        Assert.Equal("2.0.0", Assert.Single(client.FindByPackageId("App")).Version);
        Assert.Equal("App", Assert.Single(client.FindByCommandName("Get-App")).Id);
        var dependency = Assert.Single(client.GetDependencies("App", "2.0.0"));
        Assert.Equal("1.4.0", client.FindLatestVersionSatisfying(dependency.Id, dependency.VersionRange)?.Version);
        Assert.Equal(result.ContentHash, published.GetState(PackageCacheSyncService.ContentHashStateKey));
        Assert.Equal("1", published.GetState(PackageCacheSyncService.TopPackageCountStateKey));
        Assert.NotNull(published.GetState(PackageCacheSyncService.GeneratedUtcStateKey));
    }

    [Fact]
    public async Task Each_run_rebuilds_so_packages_leaving_the_top_list_are_dropped()
    {
        await RunAsync(new FakeGalleryClient().WithTop(Package("A", "1.0.0")).WithTop(Package("B", "1.0.0")));
        await RunAsync(new FakeGalleryClient().WithTop(Package("A", "1.1.0"), Package("A", "1.0.0")).WithTop(Package("C", "1.0.0")));

        var client = new SqlitePackageQueryClient((await DownloadPublishedAsync()).DatabasePath);
        Assert.Equal("1.1.0", Assert.Single(client.FindByPackageId("A")).Version);
        Assert.Empty(client.FindByPackageId("B"));
        Assert.Single(client.FindByPackageId("C"));
    }

    [Fact]
    public async Task Unchanged_content_is_not_republished()
    {
        var first = await RunAsync(new FakeGalleryClient().WithTop(Package("A", "1.0.0")));
        var second = await RunAsync(new FakeGalleryClient().WithTop(Package("A", "1.0.0")));

        Assert.True(first.Published);
        Assert.False(second.Published);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Single(Directory.GetDirectories(Path.Combine(_rootPath, "versions")));
    }

    [Fact]
    public async Task Gallery_failure_publishes_nothing()
    {
        var gallery = new FakeGalleryClient { OnTopRequested = () => throw new HttpRequestException("Gallery down") };

        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(gallery));

        Assert.False(File.Exists(Path.Combine(_rootPath, "latest", "cache.zip")));
    }

    [Fact]
    public async Task Failure_leaves_the_previously_published_cache_in_place()
    {
        await RunAsync(new FakeGalleryClient().WithTop(Package("A", "1.0.0")));
        var manifestBefore = await File.ReadAllTextAsync(Path.Combine(_rootPath, "latest", "cache.manifest.json"));

        var failing = new FakeGalleryClient { OnTopRequested = () => throw new HttpRequestException("Gallery down") };
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(failing));

        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(Path.Combine(_rootPath, "latest", "cache.manifest.json")));
    }

    private async Task<SyncResult> RunAsync(FakeGalleryClient gallery, int topCount = 100)
    {
        var service = new PackageCacheSyncService(
            gallery,
            new FileSystemArtifactStore(_rootPath, "latest/cache.zip", "latest/cache.manifest.json", null),
            Options.Create(new SyncSettings
            {
                OutputDirectory = Path.Combine(_rootPath, "work"),
                DatabaseName = "cache.sqlite",
                LatestBlobName = "latest/cache.zip",
                ManifestBlobName = "latest/cache.manifest.json",
                TopPackageCount = topCount
            }),
            new SystemClock(),
            NullLogger<PackageCacheSyncService>.Instance);

        var result = await service.RunAsync(CancellationToken.None);
        Assert.NotNull(result);
        return result;
    }

    private async Task<SqlitePackageCache> DownloadPublishedAsync()
    {
        var path = Path.Combine(_rootPath, "downloaded", Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new FileSystemArtifactStore(_rootPath, "latest/cache.zip", "latest/cache.manifest.json", null);
        Assert.True(await store.TryDownloadLatestAsync(path, CancellationToken.None));
        return new SqlitePackageCache(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

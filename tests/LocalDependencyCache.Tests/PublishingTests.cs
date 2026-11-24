using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using LocalDependencyCache.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Tests;

public sealed class PublishingTests : IDisposable
{
    private const string BlobConnectionVariable = "LDC_TEST_BLOB_CONNECTION_STRING";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));

    private string VersionsDirectory => Path.Combine(_rootPath, "versions");

    private string ManifestPath => Path.Combine(_rootPath, "latest", "cache.manifest.json");

    [Fact]
    public void Upsert_reports_only_new_or_changed_package_versions()
    {
        var cache = new SqlitePackageCache(Path.Combine(_rootPath, "cache.sqlite"));
        cache.Initialize();
        var published = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        Assert.Equal(2, cache.UpsertPackages(new[] { Package("A", published), Package("B", published) }));
        Assert.Equal(0, cache.UpsertPackages(new[] { Package("A", published), Package("B", published) }));
        Assert.Equal(1, cache.UpsertPackages(new[] { Package("A", published), Package("B", published.AddDays(1)) }));
    }

    [Fact]
    public async Task Runs_without_package_changes_do_not_publish()
    {
        var first = await RunAsync(Package("A", DateTimeOffset.Parse("2026-06-04T10:00:00Z")));
        var manifestBefore = await File.ReadAllTextAsync(ManifestPath);

        var second = await RunAsync(Package("A", DateTimeOffset.Parse("2026-06-04T10:00:00Z")));

        Assert.True(first.Published);
        Assert.False(second.Published);
        Assert.Null(second.ArtifactVersion);
        Assert.Equal(1, second.PackageCount);
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(ManifestPath));
        Assert.Single(Directory.GetDirectories(VersionsDirectory));
    }

    [Fact]
    public async Task First_run_publishes_even_when_the_gallery_returns_nothing()
    {
        var result = await RunAsync();

        Assert.True(result.Published);
        Assert.True(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task Version_names_are_content_addressed_and_never_rewritten()
    {
        var first = await RunAsync(Package("A", DateTimeOffset.Parse("2026-06-04T10:00:00Z")));
        var firstPath = Path.Combine(VersionsDirectory, first.ArtifactVersion!, "cache.zip");
        var firstBytes = await File.ReadAllBytesAsync(firstPath);

        var second = await RunAsync(Package("B", DateTimeOffset.Parse("2026-06-04T10:00:00Z")));

        Assert.Matches(new Regex(@"^\d{14}-[0-9a-f]{12}$"), first.ArtifactVersion);
        Assert.StartsWith(first.ArtifactVersion![15..], first.Manifest!.Sha256);
        Assert.NotEqual(first.ArtifactVersion, second.ArtifactVersion);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));

        var manifest = JsonSerializer.Deserialize<CacheManifest>(await File.ReadAllTextAsync(ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(second.ArtifactVersion, manifest.Version);
        Assert.True(File.Exists(manifest.ArtifactUrl));
    }

    [Fact]
    public async Task Old_versions_beyond_retention_are_deleted()
    {
        var versions = new List<string>();
        for (var hour = 1; hour <= 4; hour++)
        {
            var result = await RunAsync(retainVersions: 2, Package($"M{hour}", DateTimeOffset.Parse("2026-06-04T00:00:00Z").AddHours(hour)));
            versions.Add(result.ArtifactVersion!);
        }

        Assert.Equal(versions.TakeLast(2).Order(), Directory.GetDirectories(VersionsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Expired_version_selection_keeps_most_recently_created_and_current()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        // Same name timestamp; only creation time says which is newer.
        var versions = new[]
        {
            ("20260101000000", start),
            ("20260102000000-ffffffffffff", start.AddHours(1)),
            ("20260102000000-000000000000", start.AddHours(2)),
            ("20260103000000-cccccccccccc", start.AddHours(3))
        };

        Assert.Equal(new[] { "20260102000000-ffffffffffff", "20260101000000" },
            ArtifactVersions.SelectExpired(versions, "20260103000000-cccccccccccc", retain: 2));
        Assert.DoesNotContain("20260101000000", ArtifactVersions.SelectExpired(versions, "20260101000000", retain: 1));
        Assert.Empty(ArtifactVersions.SelectExpired(versions, "20260103000000-cccccccccccc", retain: 0));
    }

    [SkippableFact]
    public async Task Blob_publish_sets_cache_headers_keeps_versions_immutable_and_prunes()
    {
        var connectionString = Environment.GetEnvironmentVariable(BlobConnectionVariable);
        Skip.If(string.IsNullOrWhiteSpace(connectionString), $"Set {BlobConnectionVariable} to run Blob publishing tests.");

        var container = new BlobContainerClient(connectionString, "ldc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BlobArtifactStore(container, "latest/cache.zip", "latest/cache.manifest.json", "https://cdn.example.test/cache", retainVersions: 2);
            var source = Path.Combine(_rootPath, "source.zip");
            Directory.CreateDirectory(_rootPath);

            await File.WriteAllTextAsync(source, "v1");
            var manifest = await store.PublishAsync(source, "20260101000000-aaaaaaaaaaaa", "aaaa", DateTimeOffset.UtcNow, CancellationToken.None);

            var versioned = container.GetBlobClient("versions/20260101000000-aaaaaaaaaaaa/cache.zip");
            var versionedProperties = (await versioned.GetPropertiesAsync()).Value;
            Assert.Equal(ArtifactCacheControl.Immutable, versionedProperties.CacheControl);
            Assert.Equal("application/zip", versionedProperties.ContentType);
            Assert.Equal(versionedProperties.ETag.ToString(), manifest.ETag);
            Assert.Equal("https://cdn.example.test/cache/versions/20260101000000-aaaaaaaaaaaa/cache.zip", manifest.ArtifactUrl);
            Assert.Equal(ArtifactCacheControl.Mutable, (await container.GetBlobClient("latest/cache.zip").GetPropertiesAsync()).Value.CacheControl);
            var manifestProperties = (await container.GetBlobClient("latest/cache.manifest.json").GetPropertiesAsync()).Value;
            Assert.Equal(ArtifactCacheControl.Mutable, manifestProperties.CacheControl);
            Assert.Equal("application/json", manifestProperties.ContentType);

            // Republishing an existing version name must not replace its bytes.
            await File.WriteAllTextAsync(source, "different");
            await store.PublishAsync(source, "20260101000000-aaaaaaaaaaaa", "aaaa", DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal("v1", (await versioned.DownloadContentAsync()).Value.Content.ToString());
            Assert.Equal("different", (await container.GetBlobClient("latest/cache.zip").DownloadContentAsync()).Value.Content.ToString());

            await store.PublishAsync(source, "20260102000000-bbbbbbbbbbbb", "bbbb", DateTimeOffset.UtcNow, CancellationToken.None);
            await store.PublishAsync(source, "20260103000000-cccccccccccc", "cccc", DateTimeOffset.UtcNow, CancellationToken.None);

            var remaining = new List<string>();
            await foreach (var blob in container.GetBlobsAsync(prefix: "versions/"))
            {
                remaining.Add(blob.Name);
            }

            Assert.Equal(new[] { "versions/20260102000000-bbbbbbbbbbbb/cache.zip", "versions/20260103000000-cccccccccccc/cache.zip" }, remaining.Order());
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private async Task<SyncResult> RunAsync(params PackageEntry[] packages)
        => await RunAsync(0, packages);

    private async Task<SyncResult> RunAsync(int retainVersions, params PackageEntry[] packages)
    {
        var gallery = new FakeGalleryClient();
        foreach (var package in packages)
        {
            gallery.WithTop(package);
        }

        var service = new PackageCacheSyncService(
            gallery,
            new FileSystemArtifactStore(_rootPath, "latest/cache.zip", "latest/cache.manifest.json", null, retainVersions),
            Options.Create(new SyncSettings
            {
                OutputDirectory = Path.Combine(_rootPath, "work"),
                DatabaseName = "cache.sqlite",
                LatestBlobName = "latest/cache.zip",
                ManifestBlobName = "latest/cache.manifest.json"
            }),
            new SystemClock(),
            NullLogger<PackageCacheSyncService>.Instance);

        var result = await service.RunAsync(CancellationToken.None);
        Assert.NotNull(result);
        return result;
    }

    private static PackageEntry Package(string id, DateTimeOffset published)
        => PackageEntry.Create(id, "1.0.0", "1.0.0", null, null, null, null, published, published, false, null, null);

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

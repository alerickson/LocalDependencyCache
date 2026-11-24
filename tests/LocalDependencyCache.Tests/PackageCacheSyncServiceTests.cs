using LocalDependencyCache.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Tests;

public sealed class PackageCacheSyncServiceTests
{
    [Fact]
    public async Task RunAsync_publishes_filesystem_artifacts_and_stores_command_tags()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var settings = Options.Create(new SyncSettings
            {
                OutputDirectory = rootPath,
                StorageMode = "FileSystem",
                DatabaseName = "psgallery-cache.sqlite",
                LatestBlobName = "latest/psgallery-cache.zip",
                ManifestBlobName = "latest/psgallery-cache.manifest.json",
                BlobBaseUrl = "https://cdn.example.test/cache"
            });

            var publishedUtc = DateTimeOffset.Parse("2026-06-04T11:00:06Z");
            var service = new PackageCacheSyncService(
                new StubGalleryClient(new[]
                {
                    PackageEntry.Create(
                        "Sample.Module",
                        "1.2.3",
                        "1.2.3",
                        "Sample Module",
                        "Demo package.",
                        "Contoso",
                        "contoso",
                        publishedUtc,
                        publishedUtc.AddMinutes(5),
                        false,
                        new[] { "PSCommand_Get-Sample", "Demo" },
                        "Other.Module:[1.0.0,2.0.0)")
                }),
                new FileSystemArtifactStore(rootPath, "latest/psgallery-cache.zip", "latest/psgallery-cache.manifest.json", "https://cdn.example.test/cache"),
                settings,
                new FakeClock(DateTimeOffset.Parse("2026-06-04T12:00:00Z")),
                NullLogger<PackageCacheSyncService>.Instance);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.NotNull(result.Manifest);
            Assert.NotNull(result.ArtifactVersion);

            Assert.Equal(1, result.ProcessedPackages);
            Assert.NotEmpty(result.Manifest.ArtifactUrl);
            Assert.EndsWith(".zip", result.ArtifactPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".zip", result.Manifest.ArtifactUrl, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(rootPath, "latest", "psgallery-cache.zip")));
            Assert.True(File.Exists(Path.Combine(rootPath, "latest", "psgallery-cache.manifest.json")));
            Assert.True(File.Exists(Path.Combine(rootPath, "versions", result.ArtifactVersion, "psgallery-cache.zip")));

            var extractedPath = Path.Combine(rootPath, "extracted", "psgallery-cache.sqlite");
            var store = new FileSystemArtifactStore(rootPath, "latest/psgallery-cache.zip", "latest/psgallery-cache.manifest.json", "https://cdn.example.test/cache");
            var downloaded = await store.TryDownloadLatestAsync(extractedPath, CancellationToken.None);

            Assert.True(downloaded);

            var cache = new SqlitePackageCache(extractedPath);
            var matches = cache.FindByCommandName("Get-Sample");

            var match = Assert.Single(matches);
            Assert.Equal("Sample.Module", match.Id);
            Assert.Equal("1.2.3", match.Version);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                try
                {
                    Directory.Delete(rootPath, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class StubGalleryClient : IGalleryClient
    {
        private readonly IReadOnlyList<PackageEntry> _packages;

        public StubGalleryClient(IReadOnlyList<PackageEntry> packages)
        {
            _packages = packages;
        }

        public async IAsyncEnumerable<IReadOnlyList<PackageEntry>> GetPackagePagesAsync(DateTimeOffset checkpointUtc, int pageSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return _packages;
        }
    }

    [Fact]
    public async Task RunAsync_handles_dependency_without_version_range()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var settings = Options.Create(new SyncSettings
            {
                OutputDirectory = rootPath,
                StorageMode = "FileSystem",
                DatabaseName = "psgallery-cache.sqlite",
                LatestBlobName = "latest/psgallery-cache.zip",
                ManifestBlobName = "latest/psgallery-cache.manifest.json",
                BlobBaseUrl = "https://cdn.example.test/cache"
            });

            var publishedUtc = DateTimeOffset.Parse("2026-06-04T11:00:06Z");
            var service = new PackageCacheSyncService(
                new StubGalleryClient(new[]
                {
                    PackageEntry.Create(
                        "No.Range.Package",
                        "1.0.0",
                        "1.0.0",
                        "No Range Package",
                        "Dependency without explicit range.",
                        "Contoso",
                        "contoso",
                        publishedUtc,
                        publishedUtc,
                        false,
                        new[] { "Demo" },
                        "Any.Dependency")
                }),
                new FileSystemArtifactStore(rootPath, "latest/psgallery-cache.zip", "latest/psgallery-cache.manifest.json", "https://cdn.example.test/cache"),
                settings,
                new FakeClock(DateTimeOffset.Parse("2026-06-04T12:00:00Z")),
                NullLogger<PackageCacheSyncService>.Instance);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(1, result.ProcessedPackages);

            var dbPath = Path.Combine(rootPath, "psgallery-cache.sqlite");
            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version_range_raw FROM dependencies WHERE dependency_id = $dependencyId LIMIT 1;";
            command.Parameters.AddWithValue("$dependencyId", "Any.Dependency");
            var rawRange = command.ExecuteScalar() as string;

            Assert.NotNull(rawRange);
            Assert.Equal(string.Empty, rawRange);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                try
                {
                    Directory.Delete(rootPath, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class FakeClock : ISystemClock
    {
        public FakeClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
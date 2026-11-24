using System.IO.Compression;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalDependencyCache.Core;

/// <summary>
/// Cache-Control values for published artifacts. Versioned artifacts are content-addressed and
/// never change, so CDNs may cache them indefinitely; the latest alias and manifest change every
/// publish and must stay fresh.
/// </summary>
public static class ArtifactCacheControl
{
    public const string Immutable = "public, max-age=31536000, immutable";

    public const string Mutable = "public, max-age=300";
}

/// <summary>
/// Publish order for both stores: the versioned artifact (never overwritten), then the latest
/// alias, then the manifest, so the manifest only ever references a complete artifact. Old
/// versions beyond the retention count are pruned afterwards.
/// </summary>
public sealed class FileSystemArtifactStore : ICacheArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _rootDirectory;
    private readonly string _latestBlobName;
    private readonly string _manifestBlobName;
    private readonly string? _baseUrl;
    private readonly int _retainVersions;

    public FileSystemArtifactStore(string rootDirectory, string latestBlobName, string manifestBlobName, string? baseUrl, int retainVersions = 0)
    {
        _rootDirectory = rootDirectory;
        _latestBlobName = latestBlobName;
        _manifestBlobName = manifestBlobName;
        _baseUrl = baseUrl;
        _retainVersions = retainVersions;
    }

    public Task<bool> TryDownloadLatestAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var latestPath = Path.Combine(_rootDirectory, _latestBlobName.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(latestPath))
        {
            return Task.FromResult(false);
        }

        ArtifactZipUtilities.ExtractSingleFileArchive(latestPath, destinationPath);
        return Task.FromResult(true);
    }

    public async Task<CacheManifest> PublishAsync(string sourcePath, string version, string sha256, DateTimeOffset lastModifiedUtc, CancellationToken cancellationToken)
    {
        var artifactFileName = Path.GetFileName(_latestBlobName.Replace('/', Path.DirectorySeparatorChar));
        var versionsDirectory = Path.Combine(_rootDirectory, ArtifactVersions.Prefix.TrimEnd('/'));
        var versionedPath = Path.Combine(versionsDirectory, version, artifactFileName);
        if (!File.Exists(versionedPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(versionedPath) ?? ".");
            File.Copy(sourcePath, versionedPath);
        }

        var latestPath = Path.Combine(_rootDirectory, _latestBlobName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(latestPath) ?? ".");
        File.Copy(sourcePath, latestPath, overwrite: true);

        var artifactUrl = _baseUrl is null
            ? Path.GetFullPath(versionedPath)
            : $"{_baseUrl.TrimEnd('/')}/{ArtifactVersions.Prefix}{version}/{artifactFileName}";

        var manifest = new CacheManifest(artifactUrl, version, sha256, null, lastModifiedUtc);
        var manifestPath = Path.Combine(_rootDirectory, _manifestBlobName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? ".");

        await using (var stream = File.Create(manifestPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        }

        var existingVersions = new DirectoryInfo(versionsDirectory).GetDirectories()
            .Select(directory => (directory.Name, new DateTimeOffset(directory.CreationTimeUtc, TimeSpan.Zero)));
        foreach (var expired in ArtifactVersions.SelectExpired(existingVersions, version, _retainVersions))
        {
            Directory.Delete(Path.Combine(versionsDirectory, expired), recursive: true);
        }

        return manifest;
    }
}

public sealed class BlobArtifactStore : ICacheArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly BlobContainerClient _containerClient;
    private readonly string _latestBlobName;
    private readonly string _manifestBlobName;
    private readonly string _baseUrl;
    private readonly int _retainVersions;
    private readonly ILogger _logger;

    public BlobArtifactStore(BlobContainerClient containerClient, string latestBlobName, string manifestBlobName, string baseUrl, int retainVersions = 0, ILogger? logger = null)
    {
        _containerClient = containerClient;
        _latestBlobName = latestBlobName;
        _manifestBlobName = manifestBlobName;
        _baseUrl = baseUrl.TrimEnd('/');
        _retainVersions = retainVersions;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<bool> TryDownloadLatestAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var latestBlob = _containerClient.GetBlobClient(_latestBlobName);
        if (!await latestBlob.ExistsAsync(cancellationToken))
        {
            return false;
        }

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

        try
        {
            await latestBlob.DownloadToAsync(tempZipPath, cancellationToken);
            ArtifactZipUtilities.ExtractSingleFileArchive(tempZipPath, destinationPath);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }

        return true;
    }

    public async Task<CacheManifest> PublishAsync(string sourcePath, string version, string sha256, DateTimeOffset lastModifiedUtc, CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var artifactFileName = Path.GetFileName(_latestBlobName.Replace('/', Path.DirectorySeparatorChar));
        var versionedBlob = _containerClient.GetBlobClient($"{ArtifactVersions.Prefix}{version}/{artifactFileName}");

        try
        {
            await using var source = File.OpenRead(sourcePath);
            await versionedBlob.UploadAsync(source, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip", CacheControl = ArtifactCacheControl.Immutable },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // Version names include the content hash, so an existing blob already has these bytes.
        }

        await using (var source = File.OpenRead(sourcePath))
        {
            await _containerClient.GetBlobClient(_latestBlobName).UploadAsync(source, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip", CacheControl = ArtifactCacheControl.Mutable }
            }, cancellationToken);
        }

        var properties = await versionedBlob.GetPropertiesAsync(cancellationToken: cancellationToken);
        var manifest = new CacheManifest($"{_baseUrl}/{ArtifactVersions.Prefix}{version}/{artifactFileName}", version, sha256, properties.Value.ETag.ToString(), lastModifiedUtc);

        await _containerClient.GetBlobClient(_manifestBlobName).UploadAsync(
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions)),
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = ArtifactCacheControl.Mutable }
            },
            cancellationToken);

        await PruneVersionsAsync(version, cancellationToken);
        return manifest;
    }

    private async Task PruneVersionsAsync(string currentVersion, CancellationToken cancellationToken)
    {
        if (_retainVersions <= 0)
        {
            return;
        }

        var blobsByVersion = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var createdByVersion = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await foreach (var blob in _containerClient.GetBlobsAsync(prefix: ArtifactVersions.Prefix, cancellationToken: cancellationToken))
        {
            var version = blob.Name[ArtifactVersions.Prefix.Length..].Split('/', 2)[0];
            if (!blobsByVersion.TryGetValue(version, out var names))
            {
                blobsByVersion[version] = names = new List<string>();
            }

            names.Add(blob.Name);
            var created = blob.Properties.CreatedOn ?? blob.Properties.LastModified ?? DateTimeOffset.MinValue;
            createdByVersion[version] = createdByVersion.TryGetValue(version, out var existing) && existing > created ? existing : created;
        }

        var versions = createdByVersion.Select(pair => (pair.Key, pair.Value));
        foreach (var expired in ArtifactVersions.SelectExpired(versions, currentVersion, _retainVersions))
        {
            foreach (var name in blobsByVersion[expired])
            {
                try
                {
                    await _containerClient.DeleteBlobIfExistsAsync(name, cancellationToken: cancellationToken);
                }
                catch (RequestFailedException exception)
                {
                    // The new version is already published; a failed cleanup is retried on the next publish.
                    _logger.LogWarning(exception, "Failed to delete expired artifact {BlobName}.", name);
                }
            }
        }
    }
}

public static class ArtifactVersions
{
    public const string Prefix = "versions/";

    /// <summary>
    /// Returns versions to delete so that at most <paramref name="retain"/> remain, keeping the most
    /// recently created (versioned artifacts are never rewritten, so creation time is publish time)
    /// and always keeping <paramref name="currentVersion"/>.
    /// </summary>
    public static IReadOnlyList<string> SelectExpired(IEnumerable<(string Version, DateTimeOffset CreatedUtc)> versions, string currentVersion, int retain)
    {
        if (retain <= 0)
        {
            return Array.Empty<string>();
        }

        var others = versions
            .Where(version => version.Version != currentVersion)
            .OrderByDescending(version => version.CreatedUtc)
            .ThenByDescending(version => version.Version, StringComparer.Ordinal)
            .Select(version => version.Version)
            .ToList();

        return others.Skip(Math.Max(0, retain - 1)).ToList();
    }
}

internal static class ArtifactZipUtilities
{
    public static void CreateSingleFileArchive(string sourcePath, string archivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath) ?? ".");

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.SmallestSize);
    }

    public static void ExtractSingleFileArchive(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.SingleOrDefault()
            ?? throw new InvalidDataException($"Archive '{archivePath}' must contain exactly one entry.");

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        entry.ExtractToFile(destinationPath);
    }
}
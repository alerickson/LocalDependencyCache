using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using LocalDependencyCache.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var hostBuilder = new HostBuilder()
    .ConfigureAppConfiguration((context, configurationBuilder) =>
    {
        configurationBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        configurationBuilder.AddJsonFile("local.settings.json", optional: true, reloadOnChange: true);
        configurationBuilder.AddInMemoryCollection(LoadLocalSettingsValues());
        configurationBuilder.AddEnvironmentVariables();
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddOptions<SyncSettings>()
            .Bind(context.Configuration.GetSection("PackageCache"));

        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddHttpClient<IGalleryClient, GalleryClient>();
        services.AddSingleton<ICacheArtifactStore>(serviceProvider => CreateArtifactStore(serviceProvider));
        services.AddSingleton<PackageCacheSyncService>();
    });

using var host = hostBuilder.Build();

using (var startupScope = host.Services.CreateScope())
{
    LogResolvedPackageCacheSettings(startupScope.ServiceProvider);
}

if (args.Any(argument => string.Equals(argument, "--run-once", StringComparison.OrdinalIgnoreCase)))
{
    using var scope = host.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LocalRunOnce");
    var service = scope.ServiceProvider.GetRequiredService<PackageCacheSyncService>();
    var result = await service.RunAsync(CancellationToken.None);
    logger.LogInformation("Run-once completed (complete: {IsComplete}). Packages: {Packages}. Artifact version: {Version}. Artifact: {ArtifactPath}", result.IsComplete, result.ProcessedPackages, result.ArtifactVersion, result.ArtifactPath);
    return;
}

await host.RunAsync();

static void LogResolvedPackageCacheSettings(IServiceProvider serviceProvider)
{
    var settings = serviceProvider.GetRequiredService<IOptions<SyncSettings>>().Value;
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    logger.LogInformation(
        "Resolved PackageCache settings. Endpoint: {GalleryEndpoint}. PageSize: {PageSize}. FullSyncFromUtc: {FullSyncFromUtc}. InitialLookBackHours: {InitialLookBackHours}. MaxSyncDurationMinutes: {MaxSyncDurationMinutes}. MaxRetries: {MaxRetries}. StorageMode: {StorageMode}. OutputDirectory: {OutputDirectory}.",
        settings.GalleryEndpoint,
        settings.PageSize,
        string.IsNullOrWhiteSpace(settings.FullSyncFromUtc) ? "<empty>" : settings.FullSyncFromUtc,
        settings.InitialLookBackHours,
        settings.MaxSyncDurationMinutes,
        settings.MaxRetries,
        settings.StorageMode,
        settings.OutputDirectory);
}

static ICacheArtifactStore CreateArtifactStore(IServiceProvider serviceProvider)
{
    var settings = serviceProvider.GetRequiredService<IOptions<SyncSettings>>().Value;

    if (string.Equals(settings.StorageMode, "Blob", StringComparison.OrdinalIgnoreCase))
    {
        BlobContainerClient containerClient;

        if (!string.IsNullOrWhiteSpace(settings.BlobConnectionString) && !string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            containerClient = new BlobContainerClient(settings.BlobConnectionString, settings.BlobContainerName);
        }
        else if (!string.IsNullOrWhiteSpace(settings.BlobContainerUrl))
        {
            containerClient = new BlobContainerClient(new Uri(settings.BlobContainerUrl), new DefaultAzureCredential());
        }
        else
        {
            throw new InvalidOperationException("Blob storage mode requires PackageCache__BlobConnectionString and PackageCache__BlobContainerName, or PackageCache__BlobContainerUrl.");
        }

        var baseUrl = settings.BlobBaseUrl ?? settings.BlobContainerUrl ?? throw new InvalidOperationException("Blob storage mode requires PackageCache__BlobBaseUrl or PackageCache__BlobContainerUrl.");
        return new BlobArtifactStore(containerClient, settings.LatestBlobName, settings.ManifestBlobName, baseUrl);
    }

    return new FileSystemArtifactStore(settings.OutputDirectory, settings.LatestBlobName, settings.ManifestBlobName, settings.BlobBaseUrl);
}

static Dictionary<string, string?> LoadLocalSettingsValues()
{
    var localSettingsPath = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
    if (!File.Exists(localSettingsPath))
    {
        localSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    }

    if (!File.Exists(localSettingsPath))
    {
        return new Dictionary<string, string?>();
    }

    using var stream = File.OpenRead(localSettingsPath);
    using var document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("Values", out var valuesElement) || valuesElement.ValueKind != JsonValueKind.Object)
    {
        return new Dictionary<string, string?>();
    }

    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in valuesElement.EnumerateObject())
    {
        var value = property.Value.GetString();
        values[property.Name] = value;

        // Mirror local.settings env-style keys so Options binding can read sections like PackageCache.
        if (property.Name.Contains("__", StringComparison.Ordinal))
        {
            values[property.Name.Replace("__", ":", StringComparison.Ordinal)] = value;
        }
    }

    return values;
}
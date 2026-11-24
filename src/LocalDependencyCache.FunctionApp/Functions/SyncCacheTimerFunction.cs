using LocalDependencyCache.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LocalDependencyCache.FunctionApp.Functions;

public sealed class SyncCacheTimerFunction
{
    private readonly PackageCacheSyncService _syncService;
    private readonly ILogger<SyncCacheTimerFunction> _logger;

    public SyncCacheTimerFunction(PackageCacheSyncService syncService, ILogger<SyncCacheTimerFunction> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    [Function(nameof(SyncCacheTimerFunction))]
    public async Task RunAsync([TimerTrigger("%TimerSchedule%", RunOnStartup = false)] TimerInfo timerInfo, CancellationToken cancellationToken)
    {
        if (timerInfo.IsPastDue)
        {
            _logger.LogWarning("Sync timer is running past due.");
        }

        var result = await _syncService.RunAsync(cancellationToken);
        if (result is null)
        {
            _logger.LogWarning("Sync skipped: another run holds the sync lock.");
            return;
        }

        if (result.UnresolvedDependencies.Count > 0)
        {
            _logger.LogWarning("{Count} dependencies could not be resolved: {Dependencies}.", result.UnresolvedDependencies.Count, string.Join("; ", result.UnresolvedDependencies));
        }

        if (result.Published)
        {
            _logger.LogInformation("Sync published {Version}: {Packages} package versions ({Top} top packages).", result.ArtifactVersion, result.PackageCount, result.TopPackageCount);
        }
        else
        {
            _logger.LogInformation("Sync found no content changes; nothing published. Package versions: {Packages}.", result.PackageCount);
        }
    }
}
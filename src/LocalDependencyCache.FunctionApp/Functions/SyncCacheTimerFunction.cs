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
        if (result.IsComplete)
        {
            _logger.LogInformation("Sync completed. Packages: {Packages}. Artifact version: {Version}.", result.ProcessedPackages, result.ArtifactVersion);
        }
        else
        {
            _logger.LogWarning("Sync stopped at its time budget and published partial progress. Packages: {Packages}. Checkpoint: {Checkpoint}. The next run continues.", result.ProcessedPackages, result.CheckpointUtc);
        }
    }
}
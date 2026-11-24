using SyncForge.Core;

internal sealed class LocalWorker(SyncWorker worker, ILogger<LocalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await worker.RunDueAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "A rodada do worker local falhou e será retomada.");
            }
        }
    }
}

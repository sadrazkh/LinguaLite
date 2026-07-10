public sealed class BackupWorker(IServiceProvider services, ILogger<BackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAppStore>();
                var settings = await store.GetSettingsAsync();
                var interval = Math.Clamp(settings.BackupIntervalHours, 1, 168);
                var dueAt = (settings.LastBackupAttemptAt ?? DateTimeOffset.MinValue).AddHours(interval);

                if (settings.AdminTelegramChatId.HasValue && DateTimeOffset.UtcNow >= dueAt)
                {
                    var delivery = scope.ServiceProvider.GetRequiredService<DatabaseBackupDeliveryService>();
                    await delivery.RunNowAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backup worker cycle failed.");
            }

            await Task.Delay(PollDelay, stoppingToken);
        }
    }
}

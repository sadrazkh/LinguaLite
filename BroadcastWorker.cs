public sealed class BroadcastWorker(IServiceProvider services, ILogger<BroadcastWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAppStore>();
                var bot = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
                var settings = await store.GetSettingsAsync();
                if (!settings.BotEnabled)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var deliveries = await store.ClaimBroadcastDeliveriesAsync(20);
                if (deliveries.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                foreach (var delivery in deliveries)
                {
                    try
                    {
                        await bot.SendAdminMessageAsync(new UserProfile
                        {
                            Id = delivery.UserId,
                            TelegramChatId = delivery.ChatId
                        }, delivery.Message, settings);
                        await store.CompleteBroadcastDeliveryAsync(delivery, true, null);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Broadcast {JobId} delivery to {UserId} failed on attempt {Attempt}.", delivery.JobId, delivery.UserId, delivery.Attempt);
                        await store.CompleteBroadcastDeliveryAsync(delivery, false, ex.Message);
                    }

                    // Stay below Telegram's per-bot message throughput with headroom for normal bot traffic.
                    await Task.Delay(TimeSpan.FromMilliseconds(45), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Broadcast worker failed.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }
}

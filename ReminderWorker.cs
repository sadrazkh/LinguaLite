public sealed class ReminderWorker(IServiceProvider services, ILogger<ReminderWorker> logger) : BackgroundService
{
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
                if (settings.BotEnabled && settings.RemindersEnabled)
                {
                    await SendDueRemindersAsync(store, bot, settings);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reminder worker failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private static async Task SendDueRemindersAsync(IAppStore store, TelegramBotService bot, AppSettingsState settings)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await store.GetDueReminderCandidatesAsync(now, settings.ReminderHour);
        foreach (var candidate in candidates)
        {
            await bot.SendReminderAsync(candidate.User, candidate.DueCards, settings);
            await store.MarkReminderSentAsync(candidate.User.Id, now);
        }
    }
}

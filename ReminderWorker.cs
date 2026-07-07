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
        var users = await store.GetUsersAsync();
        foreach (var user in users)
        {
            if (!user.IsActive || !user.RemindersEnabled || !user.TelegramChatId.HasValue) continue;
            var reminderHour = user.ReminderHour ?? settings.ReminderHour;
            if (now.Hour != reminderHour) continue;
            if (user.LastReminderAt.HasValue && user.LastReminderAt.Value.UtcDateTime.Date == now.UtcDateTime.Date) continue;

            var deck = await store.GetDeckAsync(user.Id);
            var dueCount = deck.Cards.Count(card => !card.IsArchived && LeitnerSchedule.IsDue(card, now));
            if (dueCount <= 0) continue;

            await bot.SendReminderAsync(user, dueCount, settings);
            await store.MarkReminderSentAsync(user.Id, now);
        }
    }
}

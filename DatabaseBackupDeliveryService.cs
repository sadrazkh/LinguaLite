public sealed class DatabaseBackupDeliveryService(
    IAppStore store,
    DatabaseBackupService backupService,
    TelegramBotService bot,
    ILogger<DatabaseBackupDeliveryService> logger)
{
    public const long TelegramDocumentLimitBytes = 49L * 1024 * 1024;

    public async Task<BackupArtifact> RunNowAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync();
        if (!settings.AdminTelegramChatId.HasValue || settings.AdminTelegramChatId.Value == 0)
        {
            throw new InvalidOperationException("اول Telegram Chat ID ادمین را در پنل ذخیره کن.");
        }

        await store.UpdateSettingsAsync(current =>
        {
            current.LastBackupAttemptAt = DateTimeOffset.UtcNow;
            current.LastBackupStatus = "در حال ساخت بکاپ و ارسال به تلگرام";
        });

        try
        {
            var artifact = await backupService.CreateAsync("scheduled", cancellationToken);
            if (artifact.SizeBytes > TelegramDocumentLimitBytes)
            {
                throw new InvalidOperationException($"حجم بکاپ {FormatSize(artifact.SizeBytes)} است و از سقف ارسال سند تلگرام عبور می‌کند. برای دیتابیس بزرگ، بکاپ را به Object Storage منتقل کن.");
            }

            await bot.SendAdminDocumentAsync(settings.AdminTelegramChatId.Value, artifact.Path,
                $"LinguaLite backup\n{artifact.Provider} · {FormatSize(artifact.SizeBytes)} · {artifact.CreatedAt:yyyy-MM-dd HH:mm} UTC",
                cancellationToken);

            await store.UpdateSettingsAsync(current =>
            {
                current.LastBackupAt = DateTimeOffset.UtcNow;
                current.LastBackupAttemptAt = DateTimeOffset.UtcNow;
                current.LastBackupStatus = $"ارسال شد: {artifact.FileName} ({FormatSize(artifact.SizeBytes)})";
            });
            return artifact;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled database backup failed.");
            await store.UpdateSettingsAsync(current =>
            {
                current.LastBackupAttemptAt = DateTimeOffset.UtcNow;
                current.LastBackupStatus = $"ناموفق: {ex.Message}";
            });
            try
            {
                await bot.SendAdminTextAsync(settings.AdminTelegramChatId.Value, $"بکاپ LinguaLite ناموفق بود:\n{ex.Message}", settings);
            }
            catch (Exception notifyEx)
            {
                logger.LogWarning(notifyEx, "Could not notify admin about backup failure.");
            }
            throw;
        }
    }

    private static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024)} KB"
        : $"{bytes / 1024d / 1024d:0.0} MB";
}

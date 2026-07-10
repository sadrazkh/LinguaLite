using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using Npgsql;

public sealed record BackupArtifact(string Path, string FileName, long SizeBytes, DateTimeOffset CreatedAt, string Provider);

public sealed class DatabaseBackupService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IAppStore store,
    ILogger<DatabaseBackupService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const long MaxRestoreBytes = 1024L * 1024 * 1024;

    public async Task<BackupArtifact> CreateAsync(string reason, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(environment.ContentRootPath, "App_Data", "backups");
            Directory.CreateDirectory(directory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

            if (PostgresAppStore.HasConnectionString(configuration))
            {
                var output = Path.Combine(directory, $"lingualite-{stamp}-{SanitizeFileName(reason)}.dump");
                await RunPgDumpAsync(output, cancellationToken);
                return ToArtifact(output, "postgres");
            }

            if (store is not LocalFileAppStore localStore)
            {
                throw new InvalidOperationException("پشتیبان‌گیری فقط برای PostgreSQL یا ذخیره‌سازی محلی پشتیبانی می‌شود.");
            }

            var source = localStore.GetDatabasePath();
            await store.EnsureReadyAsync();
            var destination = Path.Combine(directory, $"lingualite-{stamp}-{SanitizeFileName(reason)}.json");
            await CopyFileAsync(source, destination, cancellationToken);
            return ToArtifact(destination, "local-file");
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<BackupArtifact> RestoreAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length <= 0) throw new InvalidOperationException("فایل بکاپ خالی است.");
        if (file.Length > MaxRestoreBytes) throw new InvalidOperationException("حجم فایل بکاپ برای ریستور از پنل بیش از حد مجاز است.");

        await Gate.WaitAsync(cancellationToken);
        string? uploadedPath = null;
        try
        {
            var directory = Path.Combine(environment.ContentRootPath, "App_Data", "backups");
            Directory.CreateDirectory(directory);
            var extension = PostgresAppStore.HasConnectionString(configuration) ? ".dump" : ".json";
            uploadedPath = Path.Combine(directory, $"restore-{Guid.NewGuid():N}{extension}");
            await using (var destination = File.Create(uploadedPath))
            {
                await file.CopyToAsync(destination, cancellationToken);
            }

            var safetyBackup = await CreateLockedAsync("before-restore", cancellationToken);
            if (PostgresAppStore.HasConnectionString(configuration))
            {
                await VerifyPgDumpAsync(uploadedPath, cancellationToken);
                NpgsqlConnection.ClearAllPools();
                await RunPgRestoreAsync(uploadedPath, cancellationToken);
                NpgsqlConnection.ClearAllPools();
            }
            else
            {
                if (store is not LocalFileAppStore localStore)
                {
                    throw new InvalidOperationException("ریستور فقط برای PostgreSQL یا ذخیره‌سازی محلی پشتیبانی می‌شود.");
                }

                await VerifyLocalBackupAsync(uploadedPath, cancellationToken);
                var target = localStore.GetDatabasePath();
                var staging = $"{target}.{Guid.NewGuid():N}.restore";
                await CopyFileAsync(uploadedPath, staging, cancellationToken);
                File.Move(staging, target, true);
            }

            logger.LogWarning("Database restore completed. Safety backup: {SafetyBackup}", safetyBackup.Path);
            return safetyBackup;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(uploadedPath) && File.Exists(uploadedPath)) File.Delete(uploadedPath);
            Gate.Release();
        }
    }

    private async Task<BackupArtifact> CreateLockedAsync(string reason, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(environment.ContentRootPath, "App_Data", "backups");
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        if (PostgresAppStore.HasConnectionString(configuration))
        {
            var output = Path.Combine(directory, $"lingualite-{stamp}-{SanitizeFileName(reason)}.dump");
            await RunPgDumpAsync(output, cancellationToken);
            return ToArtifact(output, "postgres");
        }

        if (store is not LocalFileAppStore localStore) throw new InvalidOperationException("ذخیره‌سازی فعلی قابل بکاپ نیست.");
        await store.EnsureReadyAsync();
        var destination = Path.Combine(directory, $"lingualite-{stamp}-{SanitizeFileName(reason)}.json");
        await CopyFileAsync(localStore.GetDatabasePath(), destination, cancellationToken);
        return ToArtifact(destination, "local-file");
    }

    private async Task RunPgDumpAsync(string output, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresAppStore.GetConnectionString(configuration));
        var result = await RunProcessAsync("pg_dump", new[]
        {
            "--format=custom", "--no-owner", "--no-privileges", $"--file={output}",
            $"--host={builder.Host}", $"--port={builder.Port}", $"--username={builder.Username}", $"--dbname={builder.Database}"
        }, builder.Password, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"pg_dump ناموفق بود: {result.Error}");
    }

    private async Task VerifyPgDumpAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("pg_restore", new[] { "--list", path }, null, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("فایل انتخاب‌شده یک بکاپ معتبر PostgreSQL نیست.");
    }

    private async Task RunPgRestoreAsync(string path, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresAppStore.GetConnectionString(configuration));
        var result = await RunProcessAsync("pg_restore", new[]
        {
            "--clean", "--if-exists", "--exit-on-error", "--no-owner", "--no-privileges", path,
            $"--host={builder.Host}", $"--port={builder.Port}", $"--username={builder.Username}", $"--dbname={builder.Database}"
        }, builder.Password, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"ریستور PostgreSQL ناموفق بود: {result.Error}");
    }

    private static async Task VerifyLocalBackupAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task<(int ExitCode, string Error)> RunProcessAsync(string fileName, IEnumerable<string> arguments, string? password, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(password)) startInfo.Environment["PGPASSWORD"] = password;

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"اجرای {fileName} ممکن نشد.");
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await errorTask);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException($"ابزار {fileName} در سرور نصب نیست. در Dockerfile پکیج postgresql-client باید نصب باشد.");
        }
    }

    private static BackupArtifact ToArtifact(string path, string provider) => new(path, Path.GetFileName(path), new FileInfo(path).Length, DateTimeOffset.UtcNow, provider);
    private static string SanitizeFileName(string value) => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
}

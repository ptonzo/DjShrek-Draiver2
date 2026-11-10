using System.Diagnostics;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Restores a registered driver backup only after manifest/hash verification and
/// an explicit caller confirmation. Uses Windows PnPUtil; never deletes Driver Store entries.
/// </summary>
public sealed class DriverBackupRestoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly DriverUpdateCatalogService _catalog;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DriverBackupRestoreService(DriverUpdateCatalogService? catalog = null, string? storageRoot = null)
    {
        _catalog = catalog ?? new DriverUpdateCatalogService();
        var root = Path.GetFullPath(storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DjShrek-Draiver"));
        Directory.CreateDirectory(root);
        _historyPath = Path.Combine(root, "backup-restore-history.json");
    }

    public async Task<DriverBackupRestoreResult> RestoreAsync(
        DriverBackupRecord backup, bool userConfirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (!userConfirmed)
            throw new InvalidOperationException("Восстановление не запущено: требуется явное подтверждение пользователя.");

        var root = Path.GetFullPath(backup.DirectoryPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Папка резервной копии не найдена: " + root);

        var result = new DriverBackupRestoreResult
        {
            BackupName = backup.Name,
            BackupDirectory = root,
            StartedUtc = DateTime.UtcNow
        };

        var verificationIssues = await _catalog.VerifyBackupAsync(root, cancellationToken);
        if (verificationIssues.Count > 0)
        {
            result.Outcome = "Отказ: проверка целостности не пройдена";
            result.Issues.AddRange(verificationIssues);
            result.Details = "Команда установки не запускалась. Исправьте резервную копию или создайте новую.";
            result.FinishedUtc = DateTime.UtcNow;
            await SaveResultAsync(result, cancellationToken);
            return result;
        }

        var infFiles = Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), "djshrek-backup-manifest.json", StringComparison.OrdinalIgnoreCase))
            .Take(10001).ToArray();
        if (infFiles.Length == 0 || infFiles.Length > 10000)
            throw new InvalidDataException("В резервной копии должно быть от 1 до 10000 INF-файлов.");

        var canonicalRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in infFiles)
        {
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Обнаружен INF за пределами резервной папки.");
        }

        result.VerifiedPackages = infFiles.Select(Path.GetFileName).ToList()!;
        result.InfPackageCount = infFiles.Length;

        cancellationToken.ThrowIfCancellationRequested();
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        var arguments = "/add-driver " + Quote(Path.Combine(root, "*.inf")) + " /subdirs /install";
        var start = new ProcessStartInfo
        {
            FileName = pnputil,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = root,
            WindowStyle = ProcessWindowStyle.Normal
        };

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows не запустила PnPUtil.");
            await process.WaitForExitAsync(cancellationToken);
            result.ExitCode = process.ExitCode;
            result.Outcome = process.ExitCode == 0 ? "Команда восстановления завершена" : "Команда восстановления завершилась с ошибкой";
            result.Details = process.ExitCode == 0
                ? "PnPUtil завершил команду. Фактическая активация драйверов должна быть подтверждена повторным сканированием устройств."
                : "PnPUtil вернул ненулевой код. Проверьте окно Windows и журнал устройств; активный драйвер не считается восстановленным.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            result.Outcome = "Отменено пользователем";
            result.Details = "Запрос повышения прав UAC был отклонён. Команда восстановления не выполнена.";
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "Ожидание прервано";
            result.Details = "Ожидание завершения процесса прервано. Результат работы PnPUtil может быть неизвестен.";
            throw;
        }
        finally
        {
            result.FinishedUtc = DateTime.UtcNow;
            await SaveResultAsync(result, CancellationToken.None);
        }

        return result;
    }

    public async Task<IReadOnlyList<DriverBackupRestoreResult>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_historyPath)) return Array.Empty<DriverBackupRestoreResult>();
            try
            {
                return (JsonSerializer.Deserialize<DriverBackupRestoreHistory>(
                    await File.ReadAllTextAsync(_historyPath, cancellationToken), JsonOptions)
                    ?? new DriverBackupRestoreHistory()).Records.OrderByDescending(x => x.StartedUtc).ToArray();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Журнал восстановления повреждён; исходный файл сохранён.", ex);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task SaveResultAsync(DriverBackupRestoreResult result, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var history = new DriverBackupRestoreHistory();
            if (File.Exists(_historyPath))
            {
                try
                {
                    history = JsonSerializer.Deserialize<DriverBackupRestoreHistory>(
                        await File.ReadAllTextAsync(_historyPath, token), JsonOptions) ?? new();
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Журнал восстановления повреждён; новая запись не сохранена.", ex);
                }
            }
            history.Records.Add(result);
            history.Records = history.Records.OrderByDescending(x => x.StartedUtc).Take(500).ToList();
            var temporary = _historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(history, JsonOptions), token);
                File.Move(temporary, _historyPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

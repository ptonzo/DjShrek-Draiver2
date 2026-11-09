using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

public sealed class DriverMaintenanceService
{
    private const int MaxRestorePackages = 10000;
    private static readonly TimeSpan DriverStoreTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SignatureCheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ElevatedOperationTimeout = TimeSpan.FromMinutes(10);

    public Task<string> ExportDriversAsync(string destination, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExportDrivers(destination);
        }, cancellationToken);

    public Task<string> InstallInfAsync(string infPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return InstallInf(infPath);
        }, cancellationToken);

    public Task<string> RestoreDriversAsync(string source, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RestoreDrivers(source);
        }, cancellationToken);

    public string ExportDrivers(string destination)
    {
        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(target);
        if (Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Папка резервной копии должна быть пустой.");
        var result = RunElevated("pnputil.exe", $"/export-driver * \"{target}\"");
        var infCount = Directory.EnumerateFiles(target, "*.inf", SearchOption.AllDirectories).Take(MaxRestorePackages + 1).Count();
        if (infCount == 0)
            throw new IOException("pnputil завершился без ошибки, но INF-пакеты в резервной папке не найдены. Проверьте журнал Windows.");
        if (infCount > MaxRestorePackages)
            throw new IOException("В резервной копии неожиданно много INF-пакетов. Проверьте содержимое вручную.");
        var manifestResult = new DriverBackupManifestService().Create(target);
        return result + Environment.NewLine + $"Проверка резервной копии: найдено INF-пакетов — {infCount}." +
            Environment.NewLine + manifestResult;
    }

    public string InstallInf(string infPath)
    {
        var full = Path.GetFullPath(infPath);
        if (!File.Exists(full) || !string.Equals(Path.GetExtension(full), ".inf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите существующий INF-файл драйвера.");
        ValidateSignedInfPackage(full);
        // Driver Booster-style safety stage: create a Windows restore point before
        // changing the Driver Store. If Windows cannot create it, abort installation.
        CreateSystemRestorePoint();
        var result = RunElevated("pnputil.exe", $"/add-driver \"{full}\" /install");
        return "Точка восстановления Windows создана перед установкой." + Environment.NewLine + result + Environment.NewLine +
            "Команда pnputil завершилась без ошибки. Это не подтверждает, что устройство уже использует этот драйвер: проверьте версию и состояние устройства в диспетчере устройств.";
    }

    public string RestoreDrivers(string source)
    {
        var root = Path.GetFullPath(source);
        if (!Directory.Exists(root)) throw new ArgumentException("Папка резервной копии не существует.");
        // New backups are verified byte-for-byte before any package reaches Driver Store.
        var integrityResult = new DriverBackupManifestService().VerifyIfPresent(root);
        var infFiles = Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories)
            .Take(MaxRestorePackages + 1).ToArray();
        if (infFiles.Length == 0) throw new ArgumentException("В выбранной папке не найдены INF-пакеты.");
        if (infFiles.Length > MaxRestorePackages)
            throw new ArgumentException($"В папке более {MaxRestorePackages} INF-файлов. Выберите конкретную папку резервной копии.");

        var invalid = new List<string>();
        foreach (var inf in infFiles)
        {
            try { ValidateSignedInfPackage(inf); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                if (invalid.Count < 20) invalid.Add(Path.GetFileName(inf) + ": " + ex.Message);
            }
        }
        if (invalid.Count > 0)
            throw new InvalidDataException("Восстановление остановлено: найдены пакеты без подтверждённой цифровой подписи каталога. Ничего не установлено." +
                Environment.NewLine + string.Join(Environment.NewLine, invalid));

        // Create a Windows restore point before importing any backup packages.
        CreateSystemRestorePoint();
        var result = RunElevated("pnputil.exe", $"/add-driver \"{Path.Combine(root, "*.inf")}\" /subdirs /install");
        return integrityResult + Environment.NewLine + result + Environment.NewLine +
            $"Проверка перед восстановлением: все {infFiles.Length} INF-пакетов имеют действительную подпись каталога. Повторно проверьте состояние устройств.";
    }

    public string ListDriverPackages()
    {
        var start = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить pnputil.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        WaitForExitOrKill(process, DriverStoreTimeout, "Получение списка пакетов Driver Store");
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"pnputil завершился с кодом {process.ExitCode}." : error);
        return string.IsNullOrWhiteSpace(output) ? "Windows не вернула список пакетов драйверов." : output;
    }

    public static void ValidateSignedInfPackage(string infPath)
    {
        var directory = Path.GetDirectoryName(infPath)!;
        var catalogNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(infPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;
            var match = Regex.Match(trimmed, @"^\s*CatalogFile(?:\.[A-Za-z0-9_.-]+)?\s*=\s*""?([^"";]+)", RegexOptions.IgnoreCase);
            if (match.Success) catalogNames.Add(match.Groups[1].Value.Trim());
        }
        if (catalogNames.Count == 0)
            throw new InvalidDataException("В INF не указан каталог цифровой подписи (CatalogFile).");
        var catalogs = catalogNames.Select(name => Path.GetFullPath(Path.Combine(directory, name))).ToArray();
        foreach (var catalog in catalogs)
        {
            var relative = Path.GetRelativePath(directory, catalog);
            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("INF ссылается на каталог за пределами своей папки.");
        }

        // INF files can declare OS-decorated CatalogFile entries. Validate every
        // referenced catalog that is actually present, rather than accepting the
        // first valid file while silently ignoring another invalid catalog.
        var existing = catalogs.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0)
            throw new FileNotFoundException("Подписанный CAT-файл из INF не найден рядом с пакетом.");

        foreach (var catalog in existing)
        {
            var status = GetCatalogSignatureStatus(catalog);
            if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Подпись каталога '{Path.GetFileName(catalog)}' не подтверждена (статус: {status}). Установка заблокирована.");
        }
    }

    public static void ValidateCatalogFileSignature(string catalogPath)
    {
        var full = Path.GetFullPath(catalogPath);
        if (!File.Exists(full)) throw new FileNotFoundException("CAT-файл не найден.", full);
        var status = GetCatalogSignatureStatus(full);
        if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Подпись каталога '{Path.GetFileName(full)}' не подтверждена (статус: {status}).");
    }

    private static string GetCatalogSignatureStatus(string catalogPath)
    {
        var escaped = catalogPath.Replace("'", "''");
        var script = $"$s=Get-AuthenticodeSignature -LiteralPath '{escaped}'; [Console]::Write($s.Status.ToString())";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось проверить подпись CAT-файла.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        WaitForExitOrKill(process, SignatureCheckTimeout, "Проверка цифровой подписи CAT-файла");
        var output = stdoutTask.GetAwaiter().GetResult().Trim();
        var error = stderrTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Проверка подписи завершилась ошибкой: " + error);
        return output;
    }

    private static void CreateSystemRestorePoint()
    {
        const string description = "DjShrek Driver Update";
        var escaped = description.Replace("'", "''");
        var script = "$ErrorActionPreference='Stop'; " +
                     "Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction SilentlyContinue; " +
                     $"Checkpoint-Computer -Description '{escaped}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить создание точки восстановления Windows.");
        WaitForExitOrKill(process, TimeSpan.FromMinutes(3), "Создание точки восстановления Windows");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows не смогла создать точку восстановления. Установка драйвера отменена. " +
                "Проверьте, включена ли защита системы и доступно ли место для точек восстановления.");
    }

    private static void WaitForExitOrKill(Process process, TimeSpan timeout, string operation)
    {
        var milliseconds = checked((int)timeout.TotalMilliseconds);
        if (process.WaitForExit(milliseconds)) return;

        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        throw new TimeoutException($"Операция «{operation}» превысила допустимое время ожидания ({timeout.TotalMinutes:0} мин.). Процесс остановлен. Повторите действие и проверьте состояние Windows.");
    }

    private static string RunElevated(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить системную утилиту.");
        WaitForExitOrKill(process, ElevatedOperationTimeout, "Системная операция pnputil");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Системная утилита завершилась с кодом {process.ExitCode}. Проверьте права администратора и журнал Windows.");
        return $"Операция pnputil завершена. Код возврата: {process.ExitCode}.";
    }
}
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

public sealed class DriverMaintenanceService
{
    private const int MaxRestorePackages = 10000;

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
        return result + Environment.NewLine + $"Проверка резервной копии: найдено INF-пакетов — {infCount}.";
    }

    public string InstallInf(string infPath)
    {
        var full = Path.GetFullPath(infPath);
        if (!File.Exists(full) || !string.Equals(Path.GetExtension(full), ".inf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите существующий INF-файл драйвера.");
        ValidateSignedInfPackage(full);
        var result = RunElevated("pnputil.exe", $"/add-driver \"{full}\" /install");
        var listing = ListDriverPackages();
        return result + Environment.NewLine +
            (listing.Contains(Path.GetFileName(full), StringComparison.OrdinalIgnoreCase)
                ? "Проверка Driver Store: исходное имя INF найдено в списке пакетов."
                : "Windows приняла команду установки. Имя исходного INF не найдено в выводе Driver Store; проверьте диспетчер устройств и журнал PnP.");
    }

    public string RestoreDrivers(string source)
    {
        var root = Path.GetFullPath(source);
        if (!Directory.Exists(root)) throw new ArgumentException("Папка резервной копии не существует.");
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

        var result = RunElevated("pnputil.exe", $"/add-driver \"{Path.Combine(root, "*.inf")}\" /subdirs /install");
        return result + Environment.NewLine +
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
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"pnputil завершился с кодом {process.ExitCode}." : error);
        return string.IsNullOrWhiteSpace(output) ? "Windows не вернула список пакетов драйверов." : output;
    }

    private static void ValidateSignedInfPackage(string infPath)
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
        if (catalogs.Any(path => !path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("INF ссылается на каталог за пределами своей папки.");
        var existing = catalogs.FirstOrDefault(File.Exists);
        if (existing is null) throw new FileNotFoundException("Подписанный CAT-файл из INF не найден рядом с пакетом.");
        var status = GetCatalogSignatureStatus(existing);
        if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Подпись каталога не подтверждена (статус: {status}). Установка заблокирована.");
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
        process.WaitForExit();
        var output = stdoutTask.GetAwaiter().GetResult().Trim();
        var error = stderrTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Проверка подписи завершилась ошибкой: " + error);
        return output;
    }

    private static string RunElevated(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить системную утилиту.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Системная утилита завершилась с кодом {process.ExitCode}. Проверьте права администратора и журнал Windows.");
        return $"Операция pnputil завершена. Код возврата: {process.ExitCode}.";
    }
}
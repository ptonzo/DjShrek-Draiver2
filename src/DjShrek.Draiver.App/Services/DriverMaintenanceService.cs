using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
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

    public Task<string> ExportDriverPackageAsync(
        string publishedInfName, string destination, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExportDriverPackage(publishedInfName, destination);
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

    public string ExportDriverPackage(string publishedInfName, string destination)
    {
        var publishedName = Path.GetFileName(publishedInfName?.Trim() ?? "");
        if (!Regex.IsMatch(publishedName, @"^oem[0-9]+\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Для точечной резервной копии требуется опубликованное имя пакета вида oem42.inf.");

        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(target);
        if (Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Папка точечной резервной копии должна быть пустой.");

        var result = RunElevated("pnputil.exe", $"/export-driver \"{publishedName}\" \"{target}\"");
        var infFiles = Directory.EnumerateFiles(target, "*.inf", SearchOption.AllDirectories).Take(2).ToArray();
        if (infFiles.Length == 0)
            throw new IOException("pnputil не экспортировал INF-пакет текущего драйвера.");
        if (infFiles.Length != 1)
            throw new IOException("Точечная резервная копия содержит не один INF-пакет. Откат неоднозначен; установка заблокирована.");
        var manifestResult = new DriverBackupManifestService().Create(target);
        return result + Environment.NewLine + manifestResult + Environment.NewLine + "Папка резервной копии: " + target;
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
        // Enumerate without following links even for legacy backups that have no manifest.
        var safeFiles = EnumerateFilesWithoutReparsePoints(root).ToArray();
        var integrityResult = new DriverBackupManifestService().VerifyIfPresent(root);
        var infFiles = safeFiles
            .Where(path => Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase))
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

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Резервная копия содержит reparse point; восстановление заблокировано.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    public static void ValidateSignedInfPackage(string infPath)
    {
        var fullInfPath = Path.GetFullPath(infPath);
        if (!File.Exists(fullInfPath) ||
            !Path.GetExtension(fullInfPath).Equals(".inf", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("INF-файл не найден.", fullInfPath);
        RejectPackageReparsePoints(fullInfPath, "INF");

        var directory = Path.GetDirectoryName(fullInfPath)!;
        var declarations = ReadCatalogDeclarations(fullInfPath);
        if (declarations.Count == 0)
            throw new InvalidDataException("В INF не указан каталог цифровой подписи (CatalogFile).");

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "NTarm64",
            Architecture.X64 => "NTamd64",
            Architecture.X86 => "NTx86",
            _ => throw new PlatformNotSupportedException("Архитектура Windows не поддерживается проверкой INF.")
        };

        var architectureSpecific = declarations
            .Where(x => x.Key.StartsWith("CatalogFile." + architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var generic = declarations.Where(x => x.Key.Equals("CatalogFile", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = architectureSpecific.Length > 0 ? architectureSpecific : generic;

        if (selected.Length == 0)
            throw new InvalidDataException(
                $"INF содержит CatalogFile, но не содержит каталога для архитектуры {architecture} и общего CatalogFile. Установка заблокирована.");

        var catalogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in selected)
        {
            var name = declaration.Value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("INF содержит пустое имя CatalogFile.");

            var catalog = Path.GetFullPath(Path.Combine(directory, name));
            var relative = Path.GetRelativePath(directory, catalog);
            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("INF ссылается на каталог за пределами своей папки.");

            RejectPackageReparsePoints(catalog, "CAT");
            catalogs.Add(catalog);
        }

        foreach (var catalog in catalogs)
        {
            if (!File.Exists(catalog))
                throw new FileNotFoundException(
                    $"CAT-файл, выбранный INF для архитектуры {architecture}, отсутствует: {Path.GetFileName(catalog)}",
                    catalog);

            var status = GetCatalogSignatureStatus(catalog);
            if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Подпись каталога '{Path.GetFileName(catalog)}' не подтверждена (статус: {status}). Установка заблокирована.");
        }
    }

    private static void RejectPackageReparsePoints(string filePath, string description)
    {
        var fullPath = Path.GetFullPath(filePath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException("Не удалось определить корень пути пакета.");

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(description +
                    " или один из родительских каталогов является reparse point; установка остановлена.");
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ReadCatalogDeclarations(string infPath)
    {
        const int maxInfBytes = 16 * 1024 * 1024;
        using var stream = new FileStream(infPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxInfBytes)
            throw new InvalidDataException("INF превышает допустимый размер 16 МБ.");

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);

        string text;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding((int)GetACP()).GetString(bytes);
            }
        }

        var result = new List<KeyValuePair<string, string>>();
        var inVersionSection = false;
        var pending = new StringBuilder();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } physicalLine)
        {
            var line = physicalLine.Trim().TrimStart('\uFEFF');
            var continues = line.EndsWith("\\", StringComparison.Ordinal);
            if (continues) line = line[..^1];
            pending.Append(line);
            if (continues) continue;

            var logicalLine = pending.ToString().Trim();
            pending.Clear();
            if (logicalLine.Length == 0 || logicalLine.StartsWith(';')) continue;

            if (logicalLine.StartsWith("[", StringComparison.Ordinal) &&
                logicalLine.EndsWith("]", StringComparison.Ordinal))
            {
                inVersionSection = logicalLine[1..^1].Trim().Equals("Version", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inVersionSection) continue;
            var separator = logicalLine.IndexOf('=');
            if (separator <= 0) continue;
            var key = logicalLine[..separator].Trim();
            if (!key.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Regex.IsMatch(key, @"^CatalogFile(?:\.[A-Za-z0-9_.-]+)?$", RegexOptions.IgnoreCase)) continue;
            var value = logicalLine[(separator + 1)..].Split(';', 2)[0].Trim().Trim('"');
            result.Add(new KeyValuePair<string, string>(key, value));
        }

        if (pending.Length > 0 && inVersionSection)
        {
            var logicalLine = pending.ToString().Trim();
            var separator = logicalLine.IndexOf('=');
            if (separator > 0)
            {
                var key = logicalLine[..separator].Trim();
                if (Regex.IsMatch(key, @"^CatalogFile(?:\.[A-Za-z0-9_.-]+)?$", RegexOptions.IgnoreCase))
                    result.Add(new KeyValuePair<string, string>(key, logicalLine[(separator + 1)..].Split(';', 2)[0].Trim().Trim('"')));
            }
        }
        return result;
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
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            throw new FileNotFoundException("Штатный Windows PowerShell не найден для проверки подписи CAT.", powershell);
        var start = new ProcessStartInfo(powershell, $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
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

    public static void CreateSystemRestorePoint()
    {
        const string description = "DjShrek Driver Update";
        var escaped = description.Replace("'", "''");
        var script = "$ErrorActionPreference='Stop'; " +
                     "Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction SilentlyContinue; " +
                     $"Checkpoint-Computer -Description '{escaped}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            throw new FileNotFoundException("Штатный Windows PowerShell не найден для создания точки восстановления.", powershell);
        var start = new ProcessStartInfo(powershell,
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
        var executable = string.Equals(file, "pnputil.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe")
            : file;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Не найдена системная утилита Windows.", executable);

        var start = new ProcessStartInfo(executable, arguments)
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
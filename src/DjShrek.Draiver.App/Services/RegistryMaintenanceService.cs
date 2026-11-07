using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

public sealed record RegistryFinding(
    string Hive, string SubKey, string ValueName, string ValueData,
    string Issue, string Severity, bool Repairable);

public sealed class RegistryMaintenanceService
{
    private static readonly (string Name, RegistryHive Hive, string Path)[] Locations =
    {
        ("HKCU", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        ("HKCU", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        ("HKLM", RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        ("HKLM", RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce")
    };

    public IReadOnlyList<RegistryFinding> Scan()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Проверка реестра доступна только в Windows 10/11.");

        var findings = new List<RegistryFinding>();
        foreach (var location in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(location.Path, writable: false);
                if (key is null) continue;

                foreach (var name in key.GetValueNames())
                {
                    object? raw;
                    try { raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        findings.Add(new(location.Name, location.Path, name, "", "Не удалось прочитать значение: " + ex.Message, "Предупреждение", false));
                        continue;
                    }

                    var data = raw as string ?? raw?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(data))
                    {
                        findings.Add(new(location.Name, location.Path, name, data, "Пустое или неподдерживаемое значение автозапуска.", "Ошибка", location.Name == "HKCU"));
                        continue;
                    }

                    var target = ExtractExecutable(Environment.ExpandEnvironmentVariables(data));
                    if (target is null)
                    {
                        if (data.StartsWith(@"\\", StringComparison.Ordinal) ||
                            data.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                            data.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
                            data.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                            data.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        findings.Add(new(location.Name, location.Path, name, data, "Не удалось однозначно определить запускаемый файл. Требуется ручная проверка.", "Предупреждение", false));
                        continue;
                    }

                    if (!Path.IsPathRooted(target))
                    {
                        findings.Add(new(location.Name, location.Path, name, data, "Путь запуска относительный или не распознан. Требуется ручная проверка.", "Предупреждение", false));
                        continue;
                    }

                    if (!File.Exists(target))
                        findings.Add(new(location.Name, location.Path, name, data, "Файл автозапуска не найден: " + target, "Ошибка", location.Name == "HKCU"));
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                findings.Add(new(location.Name, location.Path, "", "", "Нет доступа для чтения раздела: " + ex.Message, "Предупреждение", false));
            }
            catch (IOException ex)
            {
                findings.Add(new(location.Name, location.Path, "", "", "Ошибка чтения раздела: " + ex.Message, "Предупреждение", false));
            }
        }
        return findings;
    }

    public string Repair(RegistryFinding finding)
    {
        if (!finding.Repairable || finding.Hive != "HKCU")
            throw new InvalidOperationException("Автоматическое исправление доступно только для подтверждённой ошибки в HKCU.");
        return DeleteWithBackup(finding);
    }

    public string DeleteWithBackup(RegistryFinding finding)
    {
        var location = Locations.FirstOrDefault(x => x.Name == finding.Hive && x.Path == finding.SubKey);
        if (location.Path is null || string.IsNullOrWhiteSpace(finding.ValueName))
            throw new ArgumentException("Разрешено удалять только конкретное значение в Run/RunOnce.");

        // Avoid silent machine-wide edits. HKLM is report-only in this utility.
        if (finding.Hive != "HKCU")
            throw new InvalidOperationException("Записи HKLM доступны только для просмотра. Изменения системного автозапуска отключены.");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DjShrek-Draiver", "RegistryBackups");
        Directory.CreateDirectory(root);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safe = Regex.Replace(finding.ValueName, @"[^A-Za-z0-9_-]+", "_");
        var backup = Path.Combine(root, $"registry-{stamp}-{safe}.reg");
        var regPath = "HKEY_CURRENT_USER\\" + finding.SubKey;
        var export = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{regPath}\" \"{backup}\" /y")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Не удалось запустить reg.exe.");
        var stdout = export.StandardOutput.ReadToEnd();
        var stderr = export.StandardError.ReadToEnd();
        export.WaitForExit();
        if (export.ExitCode != 0 || !File.Exists(backup))
            throw new IOException("Не удалось создать резервную копию реестра: " + (stderr + stdout));

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(finding.SubKey, writable: true)
            ?? throw new IOException("Раздел реестра больше не существует.");
        var current = key.GetValue(finding.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
        if (!string.Equals(current, finding.ValueData, StringComparison.Ordinal))
            throw new InvalidOperationException("Запись изменилась после проверки. Повторите сканирование; реестр не изменён.");
        key.DeleteValue(finding.ValueName, throwOnMissingValue: true);
        return backup;
    }

    private static string? ExtractExecutable(string command)
    {
        var value = command.Trim();
        if (value.Length == 0) return null;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        var match = Regex.Match(value, @"^(.+?\.exe)(?:\s|$)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.Trim();
        var token = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token;
    }
}
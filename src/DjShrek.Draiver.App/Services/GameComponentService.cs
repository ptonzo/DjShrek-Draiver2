using Microsoft.Win32;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

public sealed record GameComponentStatus(
    string Name,
    string Version,
    string State,
    string Details,
    string OfficialDownloadUrl);

/// <summary>
/// Read-only inventory of common Windows game runtimes. It never downloads or installs components.
/// </summary>
public sealed class GameComponentService
{
    private static readonly Regex RuntimeLine = new(
        @"^Microsoft\.WindowsDesktop\.App\s+(?<version>[0-9.]+)\s+\[(?<path>.+)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public IReadOnlyList<GameComponentStatus> Scan()
    {
        return new[]
        {
            CheckVisualCpp("x64"),
            CheckVisualCpp("x86"),
            CheckDesktopRuntime(),
            CheckLegacyDirectX(),
            CheckPhysX(),
            CheckOpenAl()
        };
    }

    private static GameComponentStatus CheckVisualCpp(string architecture)
    {
        var path = architecture == "x64"
            ? @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
            : @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86";
        using var key = Registry.LocalMachine.OpenSubKey(path);
        if (key?.GetValue("Installed") is not int installed || installed != 1)
            return new("Microsoft Visual C++ Redistributable " + architecture, "Не определена",
                "Не обнаружен", "Пакет VC++ 2015–2022 не найден в стандартной записи реестра.",
                architecture == "x64"
                    ? "https://aka.ms/vs/17/release/vc_redist.x64.exe"
                    : "https://aka.ms/vs/17/release/vc_redist.x86.exe");

        var major = key.GetValue("VersionMajor") as int? ?? 0;
        var minor = key.GetValue("VersionMinor") as int? ?? 0;
        var build = key.GetValue("Bld") as int? ?? 0;
        var revision = key.GetValue("Rbld") as int? ?? 0;
        var version = $"{major}.{minor}.{build}.{revision}";
        return new("Microsoft Visual C++ Redistributable " + architecture, version,
            "Обнаружен", "Состояние прочитано из системной записи Visual C++ Runtime.",
            architecture == "x64"
                ? "https://aka.ms/vs/17/release/vc_redist.x64.exe"
                : "https://aka.ms/vs/17/release/vc_redist.x86.exe");
    }

    private static GameComponentStatus CheckDesktopRuntime()
    {
        try
        {
            var start = new ProcessStartInfo("dotnet.exe", "--list-runtimes")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is null)
                return new("Microsoft .NET Windows Desktop Runtime", "Не определена", "Не удалось проверить",
                    "dotnet.exe не запустился.", "https://dotnet.microsoft.com/en-us/download/dotnet/8.0");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new("Microsoft .NET Windows Desktop Runtime", "Не определена", "Проверка прервана",
                    "dotnet --list-runtimes превысил 5 секунд.", "https://dotnet.microsoft.com/en-us/download/dotnet/8.0");
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                return new("Microsoft .NET Windows Desktop Runtime", "Не определена", "Проверка недоступна",
                    string.IsNullOrWhiteSpace(error) ? "dotnet --list-runtimes завершился с ошибкой." : error,
                    "https://dotnet.microsoft.com/en-us/download/dotnet/8.0");

            var versions = output.Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(line => RuntimeLine.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0))
                .ToArray();

            return versions.Length == 0
                ? new("Microsoft .NET Windows Desktop Runtime", "Не обнаружен", "Не обнаружен",
                    "В выводе dotnet не найден Microsoft.WindowsDesktop.App. Для этой программы требуется .NET 8 Desktop Runtime.",
                    "https://dotnet.microsoft.com/en-us/download/dotnet/8.0")
                : new("Microsoft .NET Windows Desktop Runtime", string.Join(", ", versions), "Обнаружен",
                    "Найдены установленные версии Microsoft.WindowsDesktop.App.",
                    "https://dotnet.microsoft.com/en-us/download/dotnet/8.0");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new("Microsoft .NET Windows Desktop Runtime", "Не определена", "Проверка недоступна",
                ex.Message, "https://dotnet.microsoft.com/en-us/download/dotnet/8.0");
        }
    }

    private static GameComponentStatus CheckLegacyDirectX()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windows, "System32", "D3DX9_43.dll"),
            Path.Combine(windows, "SysWOW64", "D3DX9_43.dll"),
            Path.Combine(windows, "System32", "XAudio2_7.dll"),
            Path.Combine(windows, "SysWOW64", "XAudio2_7.dll")
        };
        var found = candidates.Where(File.Exists).Select(Path.GetFileName).Distinct().ToArray();
        return found.Length > 0
            ? new("Legacy DirectX components", "Файлы обнаружены", "Частично обнаружен",
                "Найдены отдельные legacy-компоненты: " + string.Join(", ", found) +
                ". Это не является полной проверкой DirectX или гарантией совместимости игры.",
                "https://www.microsoft.com/en-us/download/details.aspx?id=35")
            : new("Legacy DirectX components", "Не определена", "Не обнаружены выбранные файлы",
                "Не найдены D3DX9_43.dll и XAudio2_7.dll. Современный DirectX Windows при этом может быть установлен.",
                "https://www.microsoft.com/en-us/download/details.aspx?id=35");
    }

    private static GameComponentStatus CheckPhysX()
    {
        var names = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var path in names)
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var item = root.OpenSubKey(name);
                var display = item?.GetValue("DisplayName") as string;
                if (display?.Contains("PhysX", StringComparison.OrdinalIgnoreCase) != true) continue;
                var version = item?.GetValue("DisplayVersion") as string ?? "Версия не указана";
                return new("NVIDIA PhysX System Software", version, "Обнаружен",
                    display, "https://www.nvidia.com/en-us/drivers/");
            }
        }

        return new("NVIDIA PhysX System Software", "Не обнаружен", "Не обнаружен",
            "В стандартных разделах установленных программ пакет PhysX не найден. Он нужен не всем играм.",
            "https://www.nvidia.com/en-us/drivers/");
    }

    private static GameComponentStatus CheckOpenAl()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var files = new[]
        {
            Path.Combine(windows, "System32", "OpenAL32.dll"),
            Path.Combine(windows, "SysWOW64", "OpenAL32.dll")
        };
        var found = files.Where(File.Exists).ToArray();
        return found.Length > 0
            ? new("OpenAL", "Файл обнаружен", "Обнаружен",
                "Найдена системная библиотека OpenAL32.dll. Версия и полнота установки отдельно не подтверждены.",
                "https://www.openal.org/downloads/")
            : new("OpenAL", "Не обнаружен", "Не обнаружен",
                "OpenAL32.dll не найден в стандартных системных каталогах. Компонент требуется только отдельным играм.",
                "https://www.openal.org/downloads/");
    }
}
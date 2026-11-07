using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed class WindowsUpdateDriverService
{
    private const string SearchScript = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
$result = $searcher.Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
$items = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $result.Updates.Count; $i++) {
    $u = $result.Updates.Item($i)
    $driverDate = ''
    if ($null -ne $u.DriverVerDate) {
        $driverDate = ([datetime]$u.DriverVerDate).ToString('yyyy-MM-dd')
    }
    [void]$items.Add([pscustomobject]@{
        Title = $u.Title
        Manufacturer = $u.DriverManufacturer
        Model = $u.DriverModel
        Provider = $u.DriverProvider
        DriverClass = $u.DriverClass
        DriverDate = $driverDate
        UpdateId = $u.Identity.UpdateID
        Description = $u.Description
    })
}
ConvertTo-Json -InputObject @($items.ToArray()) -Depth 4 -Compress
";

    public async Task<IReadOnlyList<DriverUpdate>> SearchAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunPowerShellAsync(SearchScript, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<DriverUpdate>();

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };

        return items.Select(item => new DriverUpdate
        {
            Title = Read(item, "Title"),
            Manufacturer = Read(item, "Manufacturer"),
            Model = Read(item, "Model"),
            Provider = Read(item, "Provider"),
            DriverClass = Read(item, "DriverClass"),
            DriverDate = Read(item, "DriverDate"),
            UpdateId = Read(item, "UpdateId"),
            Description = Read(item, "Description")
        }).Where(item => !string.IsNullOrWhiteSpace(item.UpdateId))
          .OrderBy(item => item.Manufacturer)
          .ThenBy(item => item.Model)
          .ToArray();
    }

    public string InstallAvailable(IEnumerable<string> updateIds)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        var ids = updateIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => Guid.TryParse(id, out var value)
                ? value.ToString("D")
                : throw new ArgumentException("Получен некорректный идентификатор обновления Windows."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ids.Length == 0)
            throw new ArgumentException("Не выбраны обновления драйверов для установки.");

        var wanted = string.Join(",", ids.Select(id => "'" + id + "'"));
        var logPath = Path.Combine(Path.GetTempPath(), "DjShrek-WindowsUpdate-" + Guid.NewGuid().ToString("N") + ".log");
        var escapedLogPath = logPath.Replace("'", "''");

        var script = $@"
$ErrorActionPreference = 'Stop'
$wanted = @({wanted})
try {{
    $session = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    $search = $searcher.Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
    $selected = New-Object -ComObject Microsoft.Update.UpdateColl

    for ($i = 0; $i -lt $search.Updates.Count; $i++) {{
        $update = $search.Updates.Item($i)
        if ($wanted -notcontains $update.Identity.UpdateID.ToString().ToLowerInvariant()) {{ continue }}
        if (-not $update.EulaAccepted) {{
            throw ""Для пакета '$($update.Title)' сначала требуется принять условия Microsoft в настройках Windows Update. Приложение не принимает лицензионные условия от имени пользователя.""
        }}
        [void]$selected.Add($update)
    }}

    if ($selected.Count -eq 0) {{
        'Выбранные обновления больше не доступны. Повторите поиск.' | Set-Content -LiteralPath '{escapedLogPath}' -Encoding UTF8
        exit 0
    }}

    $downloader = $session.CreateUpdateDownloader()
    $downloader.Updates = $selected
    $downloadResult = $downloader.Download()

    $ready = New-Object -ComObject Microsoft.Update.UpdateColl
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Найдено выбранных пакетов: ' + $selected.Count)
    $lines.Add('Общий результат загрузки: ' + $downloadResult.ResultCode)

    for ($i = 0; $i -lt $selected.Count; $i++) {{
        $itemResult = $downloadResult.GetUpdateResult($i)
        if ([int]$itemResult.ResultCode -eq 2) {{
            [void]$ready.Add($selected.Item($i))
        }} else {{
            $lines.Add('Пропущено (загрузка не завершилась успешно): ' + $selected.Item($i).Title + '; код=' + $itemResult.ResultCode + '; HRESULT=' + ('0x{0:X8}' -f ([uint32]$itemResult.HResult)))
        }}
    }}

    if ($ready.Count -eq 0) {{
        $lines.Add('Ни один выбранный пакет не был успешно загружен. Установка не запускалась.')
        $lines | Set-Content -LiteralPath '{escapedLogPath}' -Encoding UTF8
        exit 1
    }}

    $installer = $session.CreateUpdateInstaller()
    $installer.Updates = $ready
    $installResult = $installer.Install()

    $lines.Add('Передано на установку: ' + $ready.Count)
    $lines.Add('Общий результат установки: ' + $installResult.ResultCode)
    $lines.Add('Требуется перезагрузка: ' + $installResult.RebootRequired)

    for ($i = 0; $i -lt $ready.Count; $i++) {{
        $itemResult = $installResult.GetUpdateResult($i)
        $lines.Add($ready.Item($i).Title + ' — код=' + $itemResult.ResultCode + '; HRESULT=' + ('0x{0:X8}' -f ([uint32]$itemResult.HResult)))
    }}

    $lines | Set-Content -LiteralPath '{escapedLogPath}' -Encoding UTF8
    if ([int]$installResult.ResultCode -ge 4) {{ exit 1 }}
}} catch {{
    ($_ | Out-String) | Set-Content -LiteralPath '{escapedLogPath}' -Encoding UTF8
    exit 1
}}
";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo(
            "powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить Windows Update Agent.");
            process.WaitForExit();

            var log = File.Exists(logPath)
                ? File.ReadAllText(logPath)
                : "Windows Update завершил процесс без диагностического отчёта.";

            if (process.ExitCode != 0)
                throw new InvalidOperationException(log.Trim());

            return log.Trim();
        }
        finally
        {
            try
            {
                if (File.Exists(logPath))
                    File.Delete(logPath);
            }
            catch
            {
                // Temporary diagnostic log cleanup is best-effort.
            }
        }
    }

    private static string Read(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ToString()
            : string.Empty;

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo(
            "powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить Windows PowerShell.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await stdoutTask;
        var error = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "Windows Update Agent не вернул список драйверов."
                    : error.Trim());

        return output.Trim();
    }
}
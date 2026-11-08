using System.Diagnostics;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Reads applicable driver updates from the Windows Update Agent.
/// This is a real Windows update source; unlike OEM search results, returned
/// entries are update objects known to Windows Update. This service only searches:
/// it never downloads or installs without a separate user-confirmed workflow.
/// </summary>
public sealed class WindowsUpdateDriverCatalogService
{
    private const int MaxResults = 200;

    public async Task<IReadOnlyList<DriverUpdateCandidate>> SearchAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Каталог Windows Update доступен только в Windows 10/11.");

        var script = """
$ErrorActionPreference = 'Stop'
try {
  $session = New-Object -ComObject Microsoft.Update.Session
  $searcher = $session.CreateUpdateSearcher()
  $result = $searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
  $items = @()
  for ($i = 0; $i -lt $result.Updates.Count; $i++) {
    $u = $result.Updates.Item($i)
    $id = ''
    try { $id = [string]$u.Identity.UpdateID } catch {}
    $manufacturer = ''
    $model = ''
    $hardwareId = ''
    $driverVersion = ''
    $driverDate = ''
    $driverClass = ''
    try { $manufacturer = [string]$u.DriverManufacturer } catch {}
    try { $model = [string]$u.DriverModel } catch {}
    try { $driverVersion = [string]$u.DriverVersion } catch {}
    try { $driverDate = ([datetime]$u.DriverVerDate).ToString('yyyy-MM-dd') } catch {}
    try { $driverClass = [string]$u.DriverClass } catch {}
    try {
      $ids = @($u.DriverHardwareID)
      if ($ids.Count -gt 0) { $hardwareId = [string]$ids[0] }
    } catch {}
    $items += [pscustomobject]@{
      UpdateId=$id; Title=[string]$u.Title; Manufacturer=$manufacturer
      Model=$model; HardwareId=$hardwareId; DriverVersion=$driverVersion
      DriverDate=$driverDate; DriverClass=$driverClass
      IsDownloaded=[bool]$u.IsDownloaded
      Description=[string]$u.Description
    }
  }
  ConvertTo-Json -InputObject @($items) -Depth 4 -Compress
} catch {
  [Console]::Error.WriteLine($_.Exception.Message)
  exit 2
}
""";

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* Process may have exited between the check and Kill. */ }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows Update Agent не смог выполнить поиск драйверов. " +
                (string.IsNullOrWhiteSpace(stderr) ? "Проверьте службу Windows Update." : stderr.Trim()));

        if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<DriverUpdateCandidate>();

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };

        return rows.Take(MaxResults).Select(item =>
        {
            string Read(string name) =>
                item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                    ? value.ToString()
                    : string.Empty;

            var updateId = Read("UpdateId");
            var title = Read("Title");
            var manufacturer = Read("Manufacturer");
            var model = Read("Model");
            var hardwareId = Read("HardwareId");
            return new DriverUpdateCandidate
            {
                UpdateId = updateId,
                Title = string.IsNullOrWhiteSpace(title) ? "Обновление драйвера Windows Update" : title,
                Manufacturer = manufacturer,
                Model = model,
                Provider = manufacturer,
                DeviceClass = Read("DriverClass"),
                HardwareId = hardwareId,
                DeviceName = string.IsNullOrWhiteSpace(model) ? title : model,
                CurrentVersion = "Текущая версия не сопоставлена",
                Compatibility = "Предложено Windows Update; перед установкой требуется проверка",
                AvailableVersion = Read("DriverVersion"),
                DriverVersionDate = Read("DriverDate"),
                Description = Read("Description"),
                IsDownloaded = bool.TryParse(Read("IsDownloaded"), out var downloaded) && downloaded,
                SourceUrl = string.Empty
            };
        }).Where(x => !string.IsNullOrWhiteSpace(x.UpdateId)).ToArray();
    }
}

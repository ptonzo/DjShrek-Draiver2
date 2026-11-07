using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed class DeviceInventoryService
{
    private const string DeviceScanScript = @"
$ErrorActionPreference = 'Stop'
$driverMap = @{}
Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | ForEach-Object {
    if (-not [string]::IsNullOrWhiteSpace([string]$_.DeviceID)) {
        $driverMap[[string]$_.DeviceID] = $_
    }
}

$devices = New-Object System.Collections.Generic.List[object]
Get-CimInstance Win32_PnPEntity | ForEach-Object {
    $device = $_
    $driver = $driverMap[[string]$device.PNPDeviceID]
    $problem = 'Нет'
    if ($null -ne $device.ConfigManagerErrorCode -and [int]$device.ConfigManagerErrorCode -ne 0) {
        $problem = [string]$device.ConfigManagerErrorCode
    }

    $driverDate = ''
    if ($null -ne $driver -and $null -ne $driver.DriverDate) {
        try { $driverDate = ([datetime]$driver.DriverDate).ToString('yyyy-MM-dd') } catch { $driverDate = [string]$driver.DriverDate }
    }

    [void]$devices.Add([pscustomobject]@{
        Name = [string]$device.Name
        Manufacturer = [string]$device.Manufacturer
        Class = [string]$device.PNPClass
        Status = [string]$device.Status
        Problem = $problem
        DriverProvider = if ($null -ne $driver) { [string]$driver.DriverProviderName } else { '' }
        DriverVersion = if ($null -ne $driver) { [string]$driver.DriverVersion } else { '' }
        DriverDate = $driverDate
        InfName = if ($null -ne $driver) { [string]$driver.InfName } else { '' }
        Signature = if ($null -ne $driver -and $driver.IsSigned) { 'Подписан' } elseif ($null -ne $driver) { 'Не подписан' } else { 'Нет данных' }
        InstanceId = [string]$device.PNPDeviceID
    })
}
ConvertTo-Json -InputObject @($devices.ToArray()) -Depth 4 -Compress
";

    private const string SystemSummaryScript = @"
$ErrorActionPreference = 'Stop'
$cpuValues = Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average
$cpu = if ($null -eq $cpuValues.Average) { 0 } else { [math]::Round($cpuValues.Average) }
$os = Get-CimInstance Win32_OperatingSystem
$memory = if ($os.TotalVisibleMemorySize -gt 0) {
    [math]::Round((($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100)
} else { 0 }
$computer = Get-CimInstance Win32_ComputerSystem
[pscustomobject]@{
    Cpu = $cpu
    Memory = $memory
    Os = $os.Caption
    Computer = $computer.Manufacturer + ' ' + $computer.Model
} | ConvertTo-Json -Compress
";

    public async Task<IReadOnlyList<DeviceInfo>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunPowerShellAsync(DeviceScanScript, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<DeviceInfo>();

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };

        return elements.Select(Parse)
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<(string Cpu, string Memory, string Os, string Computer)> GetSystemSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var output = await RunPowerShellAsync(SystemSummaryScript, cancellationToken);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        return (
            GetString(root, "Cpu") + "%",
            GetString(root, "Memory") + "%",
            GetString(root, "Os"),
            GetString(root, "Computer"));
    }

    private static DeviceInfo Parse(JsonElement element) => new()
    {
        Name = GetString(element, "Name"),
        Manufacturer = GetString(element, "Manufacturer"),
        Class = GetString(element, "Class"),
        Status = GetString(element, "Status"),
        Problem = GetString(element, "Problem"),
        DriverProvider = GetString(element, "DriverProvider"),
        DriverVersion = GetString(element, "DriverVersion"),
        DriverDate = GetString(element, "DriverDate"),
        InfName = GetString(element, "InfName"),
        Signature = GetString(element, "Signature"),
        InstanceId = GetString(element, "InstanceId")
    };

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var output = await stdoutTask;
        var error = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "Не удалось получить сведения Windows."
                    : error.Trim());

        return output.Trim();
    }
}
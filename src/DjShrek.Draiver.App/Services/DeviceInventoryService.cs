using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed class DeviceInventoryService
{
    private const string DeviceScanScript = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
[Console]::Error.WriteLine('DJSHREK_PROGRESS:5')
$driverMap = @{}
Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | ForEach-Object {
    if (-not [string]::IsNullOrWhiteSpace([string]$_.DeviceID)) {
        $driverMap[[string]$_.DeviceID] = $_
    }
}
[Console]::Error.WriteLine('DJSHREK_PROGRESS:25')

$pnpDevices = @(Get-CimInstance Win32_PnPEntity)
[Console]::Error.WriteLine('DJSHREK_PROGRESS:30')
$devices = New-Object System.Collections.Generic.List[object]
$totalDevices = [math]::Max(1, $pnpDevices.Count)
$deviceIndex = 0
foreach ($device in $pnpDevices) {
    $deviceIndex++
    if (($deviceIndex % 3) -eq 0 -or $deviceIndex -eq $pnpDevices.Count) {
        $scanPercent = 30 + [int](60 * $deviceIndex / $totalDevices)
        [Console]::Error.WriteLine('DJSHREK_PROGRESS:' + $scanPercent)
    }
    $deviceKey = [string]$device.PNPDeviceID
    $driver = $driverMap[$deviceKey]
    $problem = 'Нет'
    $problemDescription = 'Устройство работает без зарегистрированной ошибки.'
    if ($null -ne $device.ConfigManagerErrorCode -and [int]$device.ConfigManagerErrorCode -ne 0) {
        $problem = [string]$device.ConfigManagerErrorCode
        $problemDescription = switch ([int]$device.ConfigManagerErrorCode) {
            1  { 'Устройство настроено неправильно.'; break }
            10 { 'Устройство не запускается. Возможна проблема драйвера или оборудования.'; break }
            12 { 'Недостаточно свободных ресурсов для устройства.'; break }
            14 { 'Для применения изменений требуется перезагрузка компьютера.'; break }
            18 { 'Требуется переустановить драйвер устройства.'; break }
            19 { 'Конфигурация драйверов повреждена или содержит конфликт.'; break }
            22 { 'Устройство отключено.'; break }
            24 { 'Устройство отсутствует, работает неправильно или установлено не полностью.'; break }
            28 { 'Драйверы устройства не установлены.'; break }
            31 { 'Windows не может загрузить необходимые драйверы.'; break }
            32 { 'Служба драйвера отключена.'; break }
            37 { 'Драйвер не прошёл инициализацию.'; break }
            39 { 'Драйвер отсутствует или повреждён.'; break }
            40 { 'Windows не может получить доступ к устройству из-за неверного ключа службы.'; break }
            41 { 'Драйвер загружен, но устройство не обнаружено.'; break }
            43 { 'Windows остановила устройство после сообщения о проблеме.'; break }
            45 { 'Устройство сейчас не подключено к компьютеру.'; break }
            48 { 'Запуск драйвера заблокирован из-за известной проблемы совместимости.'; break }
            52 { 'Windows не может проверить цифровую подпись драйвера.'; break }
            default { 'Windows сообщает код проблемы устройства ' + $problem + '. Требуется дополнительная диагностика.' }
        }
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
        ProblemDescription = $problemDescription
        DriverProvider = if ($null -ne $driver) { [string]$driver.DriverProviderName } else { '' }
        DriverVersion = if ($null -ne $driver) { [string]$driver.DriverVersion } else { '' }
        DriverDate = $driverDate
        InfName = if ($null -ne $driver) { [string]$driver.InfName } else { '' }
        Signature = if ($null -ne $driver -and $driver.IsSigned) { 'Подписан' } elseif ($null -ne $driver) { 'Не подписан' } else { 'Нет данных' }
        InstanceId = [string]$device.PNPDeviceID
        HardwareIds = @((Get-PnpDeviceProperty -InstanceId $deviceKey -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data)
        CompatibleIds = @((Get-PnpDeviceProperty -InstanceId $deviceKey -KeyName 'DEVPKEY_Device_CompatibleIds' -ErrorAction SilentlyContinue).Data)
    })
}
[Console]::Error.WriteLine('DJSHREK_PROGRESS:95')
ConvertTo-Json -InputObject @($devices.ToArray()) -Depth 4 -Compress
";

    private const string SystemSummaryScript = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$cpuInfo = Get-CimInstance Win32_Processor | Select-Object -First 1
$cpu = 0
try {
    $sample = Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
    $cpu = [math]::Round([math]::Max(0, [math]::Min(100, $sample.CounterSamples[0].CookedValue)), 0)
} catch {
    $loads = Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average
    if ($null -ne $loads.Average) { $cpu = [math]::Round($loads.Average) }
}
$memoryTotal = [double]$os.TotalVisibleMemorySize
$memoryFree = [double]$os.FreePhysicalMemory
$memoryUsed = [math]::Max(0, $memoryTotal - $memoryFree)
$memoryPercent = if ($memoryTotal -gt 0) { [math]::Round(($memoryUsed / $memoryTotal) * 100, 0) } else { 0 }
$disk = Get-CimInstance Win32_LogicalDisk -Filter ""DeviceID='C:'""
$diskTotal = if ($disk -and $disk.Size) { [double]$disk.Size / 1GB } else { 0 }
$diskFree = if ($disk) { [double]$disk.FreeSpace / 1GB } else { 0 }
$diskUsed = [math]::Max(0, $diskTotal - $diskFree)
$diskPercent = if ($diskTotal -gt 0) { [math]::Round(($diskUsed / $diskTotal) * 100, 0) } else { 0 }
$gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
$uptime = New-TimeSpan -Start $os.LastBootUpTime -End (Get-Date)
$uptimeText = if ($uptime.Days -gt 0) { ""$($uptime.Days) дн. $($uptime.Hours) ч."" } else { ""$($uptime.Hours) ч. $($uptime.Minutes) мин."" }
[pscustomobject]@{
    Cpu = [double]$cpu
    Memory = [double]$memoryPercent
    MemoryUsedGb = [math]::Round($memoryUsed / 1MB, 1)
    MemoryTotalGb = [math]::Round($memoryTotal / 1MB, 1)
    Disk = [double]$diskPercent
    DiskUsedGb = [math]::Round($diskUsed, 1)
    DiskTotalGb = [math]::Round($diskTotal, 1)
    CpuName = [string]$cpuInfo.Name
    GpuName = [string]$gpu.Name
    Os = [string]$os.Caption + "" (сборка "" + [string]$os.BuildNumber + "")""
    Computer = [string]$computer.Manufacturer + "" "" + [string]$computer.Model
    Uptime = $uptimeText
} | ConvertTo-Json -Compress
";

    public async Task<IReadOnlyList<DeviceInfo>> ScanAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var output = await RunPowerShellAsync(DeviceScanScript, cancellationToken, progress);
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

    public async Task<SystemMetrics> GetSystemMetricsAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunPowerShellAsync(SystemSummaryScript, cancellationToken);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        static double Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) &&
            double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
        return new SystemMetrics(
            Number(root, "Cpu"), Number(root, "Memory"),
            Number(root, "MemoryUsedGb"), Number(root, "MemoryTotalGb"),
            Number(root, "Disk"), Number(root, "DiskUsedGb"), Number(root, "DiskTotalGb"),
            GetString(root, "CpuName"), GetString(root, "GpuName"),
            GetString(root, "Os"), GetString(root, "Computer"), GetString(root, "Uptime"));
    }

    private static DeviceInfo Parse(JsonElement element) => new()
    {
        Name = GetString(element, "Name"),
        Manufacturer = GetString(element, "Manufacturer"),
        Class = GetString(element, "Class"),
        Status = GetString(element, "Status"),
        Problem = GetString(element, "Problem"),
        ProblemDescription = GetString(element, "ProblemDescription"),
        DriverProvider = GetString(element, "DriverProvider"),
        DriverVersion = GetString(element, "DriverVersion"),
        DriverDate = GetString(element, "DriverDate"),
        InfName = GetString(element, "InfName"),
        Signature = GetString(element, "Signature"),
        InstanceId = GetString(element, "InstanceId"),
        HardwareIds = ReadArray(element, "HardwareIds"),
        CompatibleIds = ReadArray(element, "CompatibleIds")
    };

    private static string[] ReadArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static async Task<string> ReadProgressAndErrorsAsync(
        Process process,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var errors = new StringBuilder();
        while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
        {
            const string prefix = "DJSHREK_PROGRESS:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var value))
                    progress?.Report(Math.Clamp(value, 0, 99));
                continue;
            }

            if (errors.Length < 16_384)
                errors.AppendLine(line);
        }
        return errors.ToString();
    }

    private static async Task<string> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
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
        var stderrTask = ReadProgressAndErrorsAsync(process, progress, cancellationToken);

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
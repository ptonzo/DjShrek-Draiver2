using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Collects OEM model identifiers needed to select the correct manufacturer driver catalog.
/// Values are read-only; no registry or firmware settings are changed.
/// </summary>
public sealed class OemDeviceIdentityService
{
    public async Task<OemDeviceIdentity> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        var script = @"
$ErrorActionPreference = 'SilentlyContinue'
$cs = Get-CimInstance Win32_ComputerSystem
$csp = Get-CimInstance Win32_ComputerSystemProduct
$bb = Get-CimInstance Win32_BaseBoard
$bios = Get-CimInstance Win32_BIOS
$regPaths = @(
 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS',
 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation'
)
$reg = @{}
foreach ($path in $regPaths) {
  if (Test-Path $path) {
    $item = Get-ItemProperty $path
    foreach ($p in $item.PSObject.Properties) {
      if ($p.Name -notlike 'PS*' -and $null -ne $p.Value) { $reg[$p.Name] = [string]$p.Value }
    }
  }
}
[pscustomobject]@{
 Manufacturer = [string]$cs.Manufacturer
 Model = [string]$cs.Model
 SystemFamily = [string]$cs.SystemFamily
 ProductName = [string]$csp.Name
 ProductNumber = [string]$csp.SKUNumber
 IdentifyingNumber = [string]$csp.IdentifyingNumber
 SystemSku = [string]$cs.SystemSKUNumber
 BoardManufacturer = [string]$bb.Manufacturer
 BoardProduct = [string]$bb.Product
 BiosManufacturer = [string]$bios.Manufacturer
 BiosVersion = (@($bios.SMBIOSBIOSVersion) -join ' ')
 BiosSerial = [string]$bios.SerialNumber
 BiosRegistry = $reg
} | ConvertTo-Json -Depth 5 -Compress
";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = await outputTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Не удалось получить OEM-идентификаторы компьютера.");

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        string Read(string name) => root.TryGetProperty(name, out var value) ? value.ToString().Trim() : "";
        var registry = root.TryGetProperty("BiosRegistry", out var registryElement)
            ? registryElement : default;
        string ReadRegistry(params string[] names)
        {
            if (registry.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
            foreach (var name in names)
            {
                foreach (var property in registry.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var value = property.Value.GetString()?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
            }
            return "";
        }

        var productNumber = First(Read("ProductNumber"),
            ReadRegistry("ProductNumber", "SystemSKU", "SystemSku", "SKU", "System Product Name"));
        var model = First(Read("Model"), Read("ProductName"),
            ReadRegistry("SystemProductName", "SystemVersion", "Model", "SystemFamily"));
        var systemSku = First(Read("SystemSku"), ReadRegistry("SystemSKU", "SystemSku", "SKU"));
        var identity = new OemDeviceIdentity
        {
            Manufacturer = Read("Manufacturer"),
            Model = model,
            SystemFamily = Read("SystemFamily"),
            ProductName = Read("ProductName"),
            ProductNumber = productNumber,
            SystemSku = systemSku,
            BoardManufacturer = Read("BoardManufacturer"),
            BoardProduct = Read("BoardProduct"),
            BiosManufacturer = Read("BiosManufacturer"),
            BiosVersion = Read("BiosVersion")
        };
        identity.Vendor = ResolveVendor(identity.Manufacturer, identity.BoardManufacturer, identity.BiosManufacturer);
        identity.SearchModel = BuildSearchModel(identity);
        identity.OfficialSupportUrl = BuildSupportUrl(identity.Vendor);
        return identity;
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private static string ResolveVendor(params string[] values)
    {
        var all = string.Join(" ", values).ToLowerInvariant();
        if (all.Contains("acer")) return "Acer";
        if (all.Contains("micro-star") || all.Contains("msi")) return "MSI";
        if (all.Contains("gigabyte") || all.Contains("aorus")) return "Gigabyte";
        if (all.Contains("asrock")) return "ASRock";
        if (all.Contains("nvidia")) return "NVIDIA";
        if (all.Contains("asus")) return "ASUS";
        if (all.Contains("dell")) return "Dell";
        if (all.Contains("hewlett") || all.Contains("hewlett-packard") ||
            all.Contains("hp inc") || all.Contains("hp ") || all.StartsWith("hp")) return "HP";
        if (all.Contains("lenovo")) return "Lenovo";
        if (all.Contains("intel")) return "Intel";
        if (all.Contains("advanced micro") || all.Contains("amd")) return "AMD";
        if (all.Contains("realtek")) return "Realtek";
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "Unknown";
    }

    private static string BuildSearchModel(OemDeviceIdentity x)
    {
        var candidates = new[] { x.Model, x.ProductName, x.ProductNumber, x.SystemSku, x.BoardProduct };
        return string.Join(" ", candidates
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Regex.Replace(v.Trim(), @"\\s+", " "))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildSupportUrl(string vendor) => vendor switch
    {
        "Acer" => "https://www.acer.com/support",
        "MSI" => "https://www.msi.com/support",
        "NVIDIA" => "https://www.nvidia.com/Download/index.aspx",
        "ASUS" => "https://www.asus.com/support/download-center/",
        "Dell" => "https://www.dell.com/support/home/",
        "HP" => "https://support.hp.com/drivers",
        "Lenovo" => "https://pcsupport.lenovo.com/",
        "Gigabyte" => "https://www.gigabyte.com/Support",
        "ASRock" => "https://www.asrock.com/support/index.asp",
        "Intel" => "https://www.intel.com/content/www/us/en/download-center/home.html",
        "AMD" => "https://www.amd.com/en/support/download/drivers.html",
        "Realtek" => "https://www.realtek.com/en/downloads",
        _ => ""
    };
}

public sealed class OemDeviceIdentity
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string SystemFamily { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ProductNumber { get; init; } = "";
    public string SystemSku { get; init; } = "";
    public string BoardManufacturer { get; init; } = "";
    public string BoardProduct { get; init; } = "";
    public string BiosManufacturer { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public string Vendor { get; set; } = "";
    public string SearchModel { get; set; } = "";
    public string OfficialSupportUrl { get; set; } = "";
}

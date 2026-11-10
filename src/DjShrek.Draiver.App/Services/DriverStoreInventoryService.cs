using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Builds a read-only inventory of packages published in the Windows Driver Store.
/// It never deletes, stages, or installs packages.
/// </summary>
public sealed class DriverStoreInventoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DriverStoreInventoryService(string? storageRoot = null)
    {
        var root = Path.GetFullPath(storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DjShrek-Draiver"));
        Directory.CreateDirectory(root);
        _historyPath = Path.Combine(root, "driver-store-inventory.json");
    }

    public async Task<DriverStoreInventorySnapshot> CaptureAsync(
        IReadOnlyCollection<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var output = await RunPnPUtilAsync(cancellationToken);
        var snapshot = Parse(output.StandardOutput);
        if (output.ExitCode != 0)
            snapshot.Warnings.Add($"pnputil завершился с кодом {output.ExitCode}. Вывод может быть неполным.");
        if (string.IsNullOrWhiteSpace(output.StandardOutput))
            snapshot.Warnings.Add("Windows не вернула список Driver Store.");

        var byInf = devices.Where(d => !string.IsNullOrWhiteSpace(d.InfName))
            .GroupBy(d => NormalizeInf(d.InfName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var package in snapshot.Packages)
        {
            if (!byInf.TryGetValue(NormalizeInf(package.PublishedInfName), out var matched)) continue;
            package.IsUsedByPresentDevice = true;
            package.DeviceNames = matched.Select(d => d.Name).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            package.DeviceInstanceIds = matched.Select(d => d.InstanceId).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        snapshot.CapturedUtc = DateTime.UtcNow;
        snapshot.TotalPackages = snapshot.Packages.Count;
        snapshot.ThirdPartyPackages = snapshot.Packages.Count(p => p.IsThirdParty);
        snapshot.UsedPackages = snapshot.Packages.Count(p => p.IsUsedByPresentDevice);
        snapshot.UnusedThirdPartyPackages = snapshot.Packages.Count(p => p.IsThirdParty && !p.IsUsedByPresentDevice);
        await StoreSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<IReadOnlyList<DriverStoreInventorySnapshot>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_historyPath)) return Array.Empty<DriverStoreInventorySnapshot>();
            var json = await File.ReadAllTextAsync(_historyPath, cancellationToken);
            return JsonSerializer.Deserialize<List<DriverStoreInventorySnapshot>>(json, JsonOptions)
                ?.OrderByDescending(x => x.CapturedUtc).ToArray() ?? Array.Empty<DriverStoreInventorySnapshot>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("История Driver Store повреждена; исходный файл не перезаписан.", ex);
        }
        finally { _gate.Release(); }
    }

    public static DriverStoreInventorySnapshot Parse(string output)
    {
        var result = new DriverStoreInventorySnapshot();
        if (string.IsNullOrWhiteSpace(output)) return result;

        // pnputil emits one package block per Published Name / Опубликованное имя.
        var blocks = Regex.Split(output, @"(?=^\s*(?:Published Name|Опубликованное имя)\s*:)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var block in blocks)
        {
            var published = Field(block, "Published Name", "Опубликованное имя");
            if (string.IsNullOrWhiteSpace(published)) continue;
            var original = Field(block, "Original Name", "Исходное имя");
            var provider = Field(block, "Provider Name", "Имя поставщика");
            var className = Field(block, "Class Name", "Имя класса");
            var classGuid = Field(block, "Class GUID", "GUID класса");
            var signer = Field(block, "Signer Name", "Имя подписывающего");
            var versionText = Field(block, "Driver Version", "Версия драйвера");
            var dateText = Field(block, "Driver Date", "Дата драйвера");
            var date = ParseDate(dateText, versionText, out var version);
            var isInbox = block.Contains("Inbox", StringComparison.OrdinalIgnoreCase)
                || block.Contains("Входящий", StringComparison.OrdinalIgnoreCase);
            result.Packages.Add(new DriverStorePackage
            {
                PublishedInfName = published.Trim(),
                OriginalInfName = original.Trim(),
                ProviderName = provider.Trim(),
                ClassName = className.Trim(),
                ClassGuid = classGuid.Trim(),
                SignerName = signer.Trim(),
                DriverVersion = version,
                DriverDate = date,
                IsInbox = isInbox,
                IsThirdParty = !isInbox
            });
        }

        result.Packages = result.Packages
            .GroupBy(p => NormalizeInf(p.PublishedInfName), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(p => p.ProviderName).ThenBy(p => p.PublishedInfName).ToList();
        result.TotalPackages = result.Packages.Count;
        result.ThirdPartyPackages = result.Packages.Count(p => p.IsThirdParty);
        return result;
    }

    private static string Field(string block, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(block, @"^\s*" + Regex.Escape(label) + @"\s*:\s*(.*?)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return "";
    }

    private static DateTime? ParseDate(string dateText, string versionText, out string version)
    {
        version = versionText.Trim();
        var match = Regex.Match(versionText, @"(?<date>\d{1,2}/\d{1,2}/\d{4})\s+(?<version>[\d.]+)");
        if (match.Success)
        {
            version = match.Groups["version"].Value;
            if (DateTime.TryParse(match.Groups["date"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return parsed;
        }
        if (DateTime.TryParse(dateText, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local)) return local;
        if (DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant)) return invariant;
        return null;
    }

    private static string NormalizeInf(string value) => Path.GetFileName(value.Trim()).ToUpperInvariant();

    private async Task StoreSnapshotAsync(DriverStoreInventorySnapshot snapshot, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var history = new List<DriverStoreInventorySnapshot>();
            if (File.Exists(_historyPath))
            {
                try
                {
                    history = JsonSerializer.Deserialize<List<DriverStoreInventorySnapshot>>(
                        await File.ReadAllTextAsync(_historyPath, token), JsonOptions) ?? new();
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("История Driver Store повреждена; новый снимок не сохранён.", ex);
                }
            }
            history.Add(snapshot);
            history = history.OrderByDescending(x => x.CapturedUtc).Take(60).OrderBy(x => x.CapturedUtc).ToList();
            var temporary = _historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(history, JsonOptions), token);
                File.Move(temporary, _historyPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private static async Task<(string StandardOutput, int ExitCode)> RunPnPUtilAsync(CancellationToken token)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"),
            Arguments = "/enum-drivers",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        var text = await stdout;
        var error = await stderr;
        if (!string.IsNullOrWhiteSpace(error)) text += Environment.NewLine + error;
        return (text, process.ExitCode);
    }
}

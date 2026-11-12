using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Persistent driver update queue and auditable backup catalog.</summary>
public sealed class DriverUpdateCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly string _backupRoot;
    private readonly string _installationHistoryPath;

    public DriverUpdateCatalogService(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DjShrek-Draiver"));
        _catalogPath = Path.Combine(_root, "driver-update-catalog.json");
        _backupRoot = Path.Combine(_root, "Backups");
        _installationHistoryPath = Path.Combine(_root, "installation-history.json");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_backupRoot);
    }

    public async Task<IReadOnlyList<DriverUpdateRecord>> GetRecordsAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return (await LoadCatalogAsync(token)).Records.OrderByDescending(x => x.UpdatedUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<DriverUpdateRecord> UpsertCandidateAsync(DeviceInfo device, string provider,
        string title, string packageUrl, string sourcePage, string candidateVersion = "",
        string candidateDate = "", CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Для каталога разрешены только HTTPS-ссылки на пакеты.");
        await _gate.WaitAsync(token);
        try
        {
            var catalog = await LoadCatalogAsync(token);
            var hardware = device.HardwareIds.Concat(device.CompatibleIds)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var record = catalog.Records.FirstOrDefault(x =>
                string.Equals(x.DeviceInstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.PackageUrl, packageUrl, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                record = new DriverUpdateRecord
                {
                    DeviceName = device.Name, DeviceInstanceId = device.InstanceId,
                    HardwareIds = hardware, Provider = provider, PackageTitle = title,
                    PackageUrl = packageUrl, SourcePage = sourcePage,
                    InstalledVersion = device.DriverVersion, CreatedUtc = DateTime.UtcNow
                };
                catalog.Records.Add(record);
            }
            record.Provider = First(provider, record.Provider);
            record.PackageTitle = First(title, record.PackageTitle);
            record.CandidateVersion = First(candidateVersion, record.CandidateVersion);
            record.CandidateDate = First(candidateDate, record.CandidateDate);
            record.UpdatedUtc = DateTime.UtcNow;
            await SaveCatalogAsync(catalog, token);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateStageAsync(string recordId, DriverPackageStageResult stage, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        await _gate.WaitAsync(token);
        try
        {
            var catalog = await LoadCatalogAsync(token);
            var record = Find(catalog, recordId);
            record.PackagePath = stage.PackagePath;
            record.StagingDirectory = stage.StagingDirectory;
            record.ManifestPath = stage.ManifestPath;
            record.Sha256 = stage.Sha256;
            record.State = stage.Matches.Count == 0 ? nameof(DriverCandidateState.Incompatible) :
                stage.Matches.Any(x => x.SignatureValid == true && x.Verdict.Contains("совп", StringComparison.OrdinalIgnoreCase))
                    ? nameof(DriverCandidateState.ReadyForInstall) : nameof(DriverCandidateState.Staged);
            record.Reasons = stage.Matches.SelectMany(x => x.Reasons).Distinct().Take(30).ToArray();
            record.Compatibility = stage.Matches.Count == 0 ? "Совпадения INF не найдены" : string.Join("; ", stage.Matches.Select(x => x.Verdict).Distinct());
            record.SignatureStatus = stage.Matches.Count == 0 ? "Не определена" :
                stage.Matches.All(x => x.SignatureValid == true) ? "Подпись подтверждена" : "Требуется проверка";
            record.UpdatedUtc = DateTime.UtcNow;
            await SaveCatalogAsync(catalog, token);
        }
        finally { _gate.Release(); }
    }

    public async Task SetStateAsync(string recordId, DriverCandidateState state, IEnumerable<string>? reasons = null,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var catalog = await LoadCatalogAsync(token);
            var record = Find(catalog, recordId);
            record.State = state.ToString();
            if (reasons is not null) record.Reasons = reasons.Where(x => !string.IsNullOrWhiteSpace(x)).Take(30).ToArray();
            record.UpdatedUtc = DateTime.UtcNow;
            await SaveCatalogAsync(catalog, token);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string recordId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var catalog = await LoadCatalogAsync(token);
            catalog.Records.RemoveAll(x => x.Id == recordId);
            await SaveCatalogAsync(catalog, token);
        }
        finally { _gate.Release(); }
    }


    public async Task<IReadOnlyList<DriverInstallationRecord>> GetInstallationHistoryAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(_installationHistoryPath)) return Array.Empty<DriverInstallationRecord>();
            try
            {
                return (JsonSerializer.Deserialize<List<DriverInstallationRecord>>(
                    await File.ReadAllTextAsync(_installationHistoryPath, token), JsonOptions)
                    ?? new List<DriverInstallationRecord>())
                    .OrderByDescending(x => x.TimestampUtc).ToArray();
            }
            catch (JsonException)
            {
                throw new InvalidDataException("Журнал установок повреждён. Исходный файл сохранён; перезапись отменена.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task AddInstallationRecordAsync(DriverInstallationRecord record, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(token);
        try
        {
            List<DriverInstallationRecord> history;
            if (File.Exists(_installationHistoryPath))
            {
                try
                {
                    history = JsonSerializer.Deserialize<List<DriverInstallationRecord>>(
                        await File.ReadAllTextAsync(_installationHistoryPath, token), JsonOptions) ?? new();
                }
                catch (JsonException)
                {
                    throw new InvalidDataException("Журнал установок повреждён. Новая запись не сохранена, исходный файл не изменён.");
                }
            }
            else history = new();
            history.Add(record);
            if (history.Count > 2000) history = history.OrderByDescending(x => x.TimestampUtc).Take(2000).ToList();
            await WriteAtomicAsync(_installationHistoryPath, JsonSerializer.Serialize(history, JsonOptions), token);
        }
        finally { _gate.Release(); }
    }

    public async Task<DriverBackupRecord> RegisterBackupAsync(string backupDirectory, string name,
        CancellationToken token = default)
    {
        var root = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories).Take(10001).ToArray();
        if (files.Length == 0 || files.Length > 10000)
            throw new InvalidDataException("Резервная копия должна содержать от 1 до 10000 INF-пакетов.");
        var record = new DriverBackupRecord { Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root) : name,
            DirectoryPath = root, CreatedUtc = DateTime.UtcNow };
        long total = 0;
        foreach (var inf in files)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(inf);
            var hash = await HashFileAsync(inf, token);
            total = checked(total + info.Length);
            var metadata = ReadInfMetadata(inf);
            record.Packages.Add(new DriverBackupPackage
            {
                InfName = Path.GetFileName(inf), RelativePath = Path.GetRelativePath(root, inf),
                SizeBytes = info.Length, Sha256 = hash, Provider = metadata.Provider,
                ClassName = metadata.ClassName, DriverVersion = metadata.DriverVersion, DriverDate = metadata.DriverDate
            });
        }
        record.PackageCount = record.Packages.Count;
        record.TotalBytes = total;
        record.ManifestPath = Path.Combine(root, "djshrek-backup-manifest.json");
        await File.WriteAllTextAsync(record.ManifestPath, JsonSerializer.Serialize(record, JsonOptions), token);
        var indexPath = Path.Combine(_backupRoot, "backup-index.json");
        await _gate.WaitAsync(token);
        try
        {
            var all = File.Exists(indexPath)
                ? JsonSerializer.Deserialize<List<DriverBackupRecord>>(await File.ReadAllTextAsync(indexPath, token), JsonOptions) ?? new()
                : new List<DriverBackupRecord>();
            all.RemoveAll(x => string.Equals(x.DirectoryPath, root, StringComparison.OrdinalIgnoreCase));
            all.Add(record);
            await WriteAtomicAsync(indexPath, JsonSerializer.Serialize(all, JsonOptions), token);
        }
        finally { _gate.Release(); }
        return record;
    }

    public async Task<IReadOnlyList<DriverBackupRecord>> GetBackupsAsync(CancellationToken token = default)
    {
        var indexPath = Path.Combine(_backupRoot, "backup-index.json");
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(indexPath)) return Array.Empty<DriverBackupRecord>();
            return JsonSerializer.Deserialize<List<DriverBackupRecord>>(await File.ReadAllTextAsync(indexPath, token), JsonOptions)
                ?? new List<DriverBackupRecord>();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> VerifyBackupAsync(string backupDirectory, CancellationToken token = default)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(backupDirectory), "djshrek-backup-manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Манифест резервной копии не найден.", manifestPath);
        var manifest = JsonSerializer.Deserialize<DriverBackupRecord>(await File.ReadAllTextAsync(manifestPath, token), JsonOptions)
            ?? throw new InvalidDataException("Манифест резервной копии повреждён.");
        var issues = new List<string>();
        foreach (var package in manifest.Packages)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(manifest.DirectoryPath, package.RelativePath));
            if (!path.StartsWith(manifest.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            { issues.Add(package.InfName + ": файл отсутствует или путь недопустим."); continue; }
            var hash = await HashFileAsync(path, token);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(package.Sha256)))
                issues.Add(package.InfName + ": SHA-256 не совпадает.");
        }
        return issues;
    }

    private async Task<DriverUpdateCatalogDocument> LoadCatalogAsync(CancellationToken token)
    {
        if (!File.Exists(_catalogPath)) return new DriverUpdateCatalogDocument();
        try { return JsonSerializer.Deserialize<DriverUpdateCatalogDocument>(await File.ReadAllTextAsync(_catalogPath, token), JsonOptions)
            ?? new DriverUpdateCatalogDocument(); }
        catch (JsonException) { throw new InvalidDataException("Каталог драйверов повреждён. Исходный файл сохранён; автоматическая перезапись отменена."); }
    }

    private Task SaveCatalogAsync(DriverUpdateCatalogDocument catalog, CancellationToken token)
    {
        catalog.UpdatedUtc = DateTime.UtcNow;
        return WriteAtomicAsync(_catalogPath, JsonSerializer.Serialize(catalog, JsonOptions), token);
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, token);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static DriverUpdateRecord Find(DriverUpdateCatalogDocument catalog, string id) =>
        catalog.Records.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Запись каталога не найдена.");

    private static string First(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static (string Provider, string ClassName, string DriverVersion, string DriverDate) ReadInfMetadata(string path)
    {
        string provider = "", className = "", version = "", date = "";
        foreach (var line in File.ReadLines(path).Take(3000))
        {
            var text = line.Trim();
            if (text.StartsWith(';') || text.Length == 0) continue;
            var m = Regex.Match(text, @"^(Provider|Class|DriverVer)\s*=\s*(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "provider": provider = m.Groups[2].Value.Trim().Trim('"'); break;
                case "class": className = m.Groups[2].Value.Trim().Trim('"'); break;
                case "driverver":
                    var parts = m.Groups[2].Value.Split(',', 2);
                    if (parts.Length == 2) { date = parts[0].Trim(); version = parts[1].Trim(); }
                    break;
            }
        }
        return (provider, className, version, date);
    }
}

using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed record InfPackageInspection(
    string InfPath,
    string Provider,
    string ClassName,
    string DriverVersion,
    string DriverDate,
    string CatalogFile,
    string CatalogSignature,
    string InfSha256,
    string CatalogSha256,
    string MatchKind,
    string MatchedHardwareId,
    bool ArchitectureApplicable,
    IReadOnlyList<string> SupportedArchitectures,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> Warnings)
{
    public bool IsInstallableForDevice =>
        ArchitectureApplicable &&
        CatalogSignature.Equals("Valid", StringComparison.OrdinalIgnoreCase) &&
        MatchKind == "Hardware ID";
}

/// <summary>
/// Reads a local driver INF and determines whether its Models sections contain
/// an exact PnP ID for the selected device. This does not install or download files.
/// </summary>
public sealed class DriverInfPackageInspector
{
    private static readonly Regex SectionRegex = new(@"^\s*\[([^\]]+)\]\s*(?:;.*)?$", RegexOptions.Compiled);
    private static readonly Regex KeyValueRegex = new(@"^\s*([^=;]+?)\s*=\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex HardwareIdRegex = new(@"^(?:PCI|USB|HDAUDIO|ACPI|ROOT|SWD|BTH|DISPLAY|SCSI|SD|HID|USBSTOR|IDE|INTEL|AMD|NVME|VEN|\{)[^\s,;]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CatalogRegex = new(@"^CatalogFile(?:\.([A-Za-z0-9_.-]+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int MaxInfBytes = 16 * 1024 * 1024;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    public InfPackageInspection Inspect(string infPath, DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var fullPath = Path.GetFullPath(infPath);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".inf", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Указан несуществующий INF-файл драйвера.", fullPath);

        var sections = ParseSections(fullPath);
        var warnings = new List<string>();
        var version = GetSection(sections, "Version");
        var provider = ExpandString(GetValue(version, "Provider"), sections);
        var className = ExpandString(GetValue(version, "Class"), sections);
        var driverVersion = GetValue(version, "DriverVer");
        var (driverDate, driverVersionNumber) = ParseDriverVersion(driverVersion);
        var catalog = ResolveCatalog(version, sections, warnings);
        var architectures = GetSupportedArchitectures(sections);
        var architectureApplicable = IsArchitectureApplicable(architectures);

        var ids = ExtractModelHardwareIds(sections);
        var hardwareIds = (device.HardwareIds ?? Array.Empty<string>()).Select(NormalizeId)
            .Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compatibleIds = (device.CompatibleIds ?? Array.Empty<string>()).Select(NormalizeId)
            .Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var exact = ids.FirstOrDefault(hardwareIds.Contains);
        var compatible = exact is null ? ids.FirstOrDefault(compatibleIds.Contains) : null;
        var matchKind = exact is not null ? "Hardware ID" : compatible is not null ? "Compatible ID" : "No match";
        var matched = exact ?? compatible ?? "";

        if (ids.Count == 0) warnings.Add("В INF не удалось извлечь идентификаторы из Models-секций.");
        if (matchKind == "Compatible ID") warnings.Add("Совпадение только по Compatible ID недостаточно для автоматической установки.");
        if (!architectureApplicable) warnings.Add("INF не содержит секции, применимой к архитектуре этой Windows.");
        if (string.IsNullOrWhiteSpace(catalog)) warnings.Add("В INF не найден CatalogFile для проверки подписи.");

        var catalogPath = string.IsNullOrWhiteSpace(catalog) ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, catalog));
        string signature = "Not checked";
        string catalogHash = "";
        if (catalogPath is not null && File.Exists(catalogPath))
        {
            try
            {
                DriverMaintenanceService.ValidateSignedInfPackage(fullPath);
                DriverMaintenanceService.ValidateCatalogFileSignature(catalogPath);
                signature = "Valid";
                catalogHash = ComputeSha256(catalogPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
            {
                signature = "Invalid";
                warnings.Add("Проверка CAT-подписи не пройдена: " + ex.Message);
            }
        }
        else if (catalogPath is not null)
        {
            signature = "Missing";
            warnings.Add("CAT-файл, указанный в INF, отсутствует в папке пакета.");
        }

        if (string.IsNullOrWhiteSpace(provider)) warnings.Add("В INF не указан Provider.");
        if (string.IsNullOrWhiteSpace(driverVersionNumber)) warnings.Add("В INF не удалось прочитать DriverVer.");

        return new InfPackageInspection(
            fullPath, provider, className, driverVersionNumber, driverDate, catalog ?? "", signature,
            ComputeSha256(fullPath), catalogHash, matchKind, matched, architectureApplicable,
            architectures, ids, warnings);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in ReadLogicalInfLines(path))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var section = SectionRegex.Match(line);
            if (section.Success)
            {
                var name = section.Groups[1].Value.Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[name] = current;
                continue;
            }
            if (current is null) continue;
            var pair = KeyValueRegex.Match(line);
            if (pair.Success) current[pair.Groups[1].Value.Trim()] = pair.Groups[2].Value.Trim().Trim('"');
        }
        return result;
    }

    private static IEnumerable<string> ReadLogicalInfLines(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxInfBytes)
            throw new InvalidDataException($"INF превышает допустимый размер {MaxInfBytes / (1024 * 1024)} МБ.");

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaxInfBytes)
                throw new InvalidDataException($"INF превышает допустимый размер {MaxInfBytes / (1024 * 1024)} МБ.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        string text;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try
            {
                // Prefer UTF-8 when valid; otherwise use the active Windows ANSI code page.
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding((int)GetACP()).GetString(bytes);
            }
        }

        var pending = new StringBuilder();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } physicalLine)
        {
            var line = physicalLine.TrimEnd();
            var continues = line.EndsWith("\\", StringComparison.Ordinal);
            if (continues) line = line[..^1];
            pending.Append(line);
            if (continues) continue;

            yield return pending.ToString();
            pending.Clear();
        }

        if (pending.Length > 0)
            yield return pending.ToString();
    }

    private static Dictionary<string, string> GetSection(Dictionary<string, Dictionary<string, string>> sections, string name) =>
        sections.TryGetValue(name, out var section) ? section : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string GetValue(Dictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) ? value : "";

    private static string ExpandString(string value, Dictionary<string, Dictionary<string, string>> sections)
    {
        var strings = GetSection(sections, "Strings");
        return Regex.Replace(value, @"%([^%]+)%", m =>
            strings.TryGetValue(m.Groups[1].Value, out var replacement) ? replacement : m.Value);
    }

    private static (string Date, string Version) ParseDriverVersion(string value)
    {
        var parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return ("", "");
        return (parts[0], parts[1]);
    }

    private static string? ResolveCatalog(Dictionary<string, string> version, Dictionary<string, Dictionary<string, string>> sections, List<string> warnings)
    {
        var entries = version.Where(x => CatalogRegex.IsMatch(x.Key)).ToArray();
        if (entries.Length == 0) return null;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "NTarm64",
            Architecture.X64 => "NTamd64",
            _ => "NTx86"
        };
        var selected = entries.Where(x => x.Key.StartsWith("CatalogFile." + arch, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Key.Length).FirstOrDefault();
        selected = selected.Key is not null ? selected : entries.FirstOrDefault(x => !x.Key.Contains('.', StringComparison.Ordinal));
        var name = selected.Value?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            warnings.Add("CatalogFile не содержит имени CAT-файла.");
            return null;
        }
        return name;
    }

    private static IReadOnlyList<string> GetSupportedArchitectures(Dictionary<string, Dictionary<string, string>> sections)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections.Keys)
        {
            var match = Regex.Match(section, @"(?:^|\.)(NTamd64|NTx86|NTarm64)(?:\.|$)", RegexOptions.IgnoreCase);
            if (match.Success) values.Add(match.Groups[1].Value);
        }
        return values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsArchitectureApplicable(IReadOnlyList<string> architectures)
    {
        if (architectures.Count == 0) return true; // undecorated legacy INF; not proof of OS-version compatibility
        var expected = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "NTarm64",
            Architecture.X64 => "NTamd64",
            _ => "NTx86"
        };
        return architectures.Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractModelHardwareIds(Dictionary<string, Dictionary<string, string>> sections)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manufacturer = GetSection(sections, "Manufacturer");
        foreach (var entry in manufacturer)
        {
            var targets = entry.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (targets.Length == 0) continue;
            modelSectionNames.Add(targets[0]);
            foreach (var decoration in targets.Skip(1))
                modelSectionNames.Add(targets[0] + "." + decoration);
        }

        foreach (var sectionName in modelSectionNames)
        {
            if (!sections.TryGetValue(sectionName, out var models)) continue;
            foreach (var value in models.Values)
            {
                var tokens = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens.Skip(1))
                {
                    var id = NormalizeId(token);
                    if (id.Length > 0 && HardwareIdRegex.IsMatch(id)) result.Add(id);
                }
            }
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeId(string value) => value.Trim().Trim('"').Replace('/', '\\');

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

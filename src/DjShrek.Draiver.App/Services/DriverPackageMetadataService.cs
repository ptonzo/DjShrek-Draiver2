using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed record DriverPackageMetadata(
    string InfPath,
    string Provider,
    string ClassName,
    string DriverVersion,
    DateTime? DriverDate,
    string CatalogFile,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string Architecture,
    int MinWindowsBuild,
    int MaxWindowsBuild);

public sealed record DriverPackageMatch(
    DriverPackageMetadata Package,
    DeviceInfo Device,
    bool HardwareIdMatch,
    bool ArchitectureMatch,
    bool WindowsBuildMatch,
    bool SignatureValid,
    string VersionAssessment,
    string Verdict,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Reads driver metadata from a local INF and evaluates applicability without installing anything.
/// </summary>
public sealed class DriverPackageMetadataService
{
    private static readonly Regex SectionRegex = new(@"^\\s*\\[([^\\]]+)\\]\\s*$", RegexOptions.Compiled);
    private static readonly Regex VersionRegex = new(@"^\\s*([^=;]+?)\\s*=\\s*(.*?)\\s*$", RegexOptions.Compiled);

    public DriverPackageMetadata ReadInf(string infPath)
    {
        var fullPath = Path.GetFullPath(infPath);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".inf", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("INF-файл драйвера не найден.", fullPath);

        var sections = ParseInf(File.ReadAllLines(fullPath));
        string Get(string section, string key) =>
            sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : "";

        var provider = Unquote(Get("Version", "Provider"));
        var className = Unquote(Get("Version", "Class"));
        var catalog = Unquote(Get("Version", "CatalogFile"));
        if (string.IsNullOrWhiteSpace(catalog))
            catalog = sections.Where(x => x.Key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => x.Value.Where(v => v.Key.StartsWith("CatalogFile.", StringComparison.OrdinalIgnoreCase)))
                .Select(v => Unquote(v.Value)).FirstOrDefault() ?? "";

        var versionText = Unquote(Get("Version", "DriverVer"));
        var (date, version) = ParseDriverVer(versionText);

        var hardware = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            if (!section.Key.StartsWith("Models", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var value in section.Value.Values)
            {
                var parts = SplitInfCsv(value);
                if (parts.Count < 2) continue;
                foreach (var id in parts.Skip(1))
                {
                    var normalized = Unquote(id).Trim();
                    if (normalized.Length == 0) continue;
                    if (normalized.Contains("SUBSYS_", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("VEN_", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("VID_", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("HDAUDIO", StringComparison.OrdinalIgnoreCase))
                        hardware.Add(normalized);
                    else if (normalized.Contains('\\'))
                        compatible.Add(normalized);
                }
            }
        }

        var (minBuild, maxBuild) = ReadWindowsDecorations(sections.Keys);
        var architecture = ResolveArchitecture(sections.Keys);
        return new DriverPackageMetadata(
            fullPath, provider, className, version, date, catalog,
            hardware.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            compatible.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            architecture, minBuild, maxBuild);
    }

    public DriverPackageMatch Match(DriverPackageMetadata package, DeviceInfo device)
    {
        var signatureValid = TryValidateSignature(package.InfPath, out var signatureError);
        return Match(package, device, signatureValid, signatureError);
    }

    private static DriverPackageMatch Match(
        DriverPackageMetadata package, DeviceInfo device, bool signatureValid, string signatureError)
    {
        var ids = device.HardwareIds.Concat(device.CompatibleIds)
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packageIds = package.HardwareIds.Concat(package.CompatibleIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedId = packageIds.FirstOrDefault(candidate => ids.Contains(candidate));
        var hardwareMatch = matchedId is not null;

        var arch = RuntimeInformation.OSArchitecture;
        var archMatch = package.Architecture.Equals(arch.ToString(), StringComparison.OrdinalIgnoreCase);
        var build = GetWindowsBuild();
        var buildMatch = build >= package.MinWindowsBuild && build <= package.MaxWindowsBuild;

        var reasons = new List<string>();
        if (!hardwareMatch) reasons.Add("В INF не найден точный Hardware ID или Compatible ID устройства.");
        if (!archMatch) reasons.Add($"Архитектура пакета {package.Architecture} не совпадает с {arch}.");
        if (!buildMatch) reasons.Add($"Сборка Windows {build} вне диапазона {package.MinWindowsBuild}–{package.MaxWindowsBuild}.");
        if (string.IsNullOrWhiteSpace(package.CatalogFile)) reasons.Add("В INF не указан CatalogFile.");
        if (!signatureValid) reasons.Add("Подпись CAT не подтверждена: " + signatureError);
        var versionAssessment = CompareVersions(device.DriverVersion, package.DriverVersion);
        if (versionAssessment.StartsWith("Версия кандидата ниже", StringComparison.Ordinal))
            reasons.Add(versionAssessment);
        var verdict = hardwareMatch && archMatch && buildMatch &&
                      !string.IsNullOrWhiteSpace(package.CatalogFile) && signatureValid &&
                      !versionAssessment.StartsWith("Версия кандидата ниже", StringComparison.Ordinal)
            ? "Кандидат прошёл проверки совместимости и подписи"
            : "Пакет требует внимания: см. причины";
        return new DriverPackageMatch(package, device, hardwareMatch, archMatch, buildMatch,
            signatureValid, versionAssessment, verdict, reasons);
    }

    public IReadOnlyList<DriverPackageMatch> MatchFolder(string folder, IEnumerable<DeviceInfo> devices)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories).Take(501).ToArray();
        if (files.Length > 500) throw new InvalidDataException("В папке более 500 INF-файлов. Укажите более узкую папку.");
        var results = new List<DriverPackageMatch>();
        foreach (var file in files)
        {
            DriverPackageMetadata metadata;
            try { metadata = ReadInf(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var signatureValid = TryValidateSignature(metadata.InfPath, out var signatureError);
            foreach (var device in devices)
                results.Add(Match(metadata, device, signatureValid, signatureError));
        }
        return results.OrderByDescending(x => x.HardwareIdMatch && x.ArchitectureMatch && x.WindowsBuildMatch)
            .ThenBy(x => x.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CompareVersions(string installed, string candidate)
    {
        if (string.IsNullOrWhiteSpace(installed)) return "Текущая версия драйвера не определена.";
        if (!Version.TryParse(installed, out var current) || !Version.TryParse(candidate, out var next))
            return "Сравнение версий недоступно: формат версии не распознан.";
        var comparison = next.CompareTo(current);
        return comparison > 0 ? $"Доступна более новая версия: {installed} → {candidate}."
            : comparison < 0 ? $"Версия кандидата ниже установленной: {installed} → {candidate}."
            : $"Версия совпадает: {installed}.";
    }

    private static bool TryValidateSignature(string infPath, out string error)
    {
        try
        {
            DriverMaintenanceService.ValidateSignedInfPackage(infPath);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseInf(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var section = SectionRegex.Match(line);
            if (section.Success)
            {
                current = section.Groups[1].Value.Trim();
                if (!result.ContainsKey(current))
                    result[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (current is null) continue;
            var entry = VersionRegex.Match(line);
            if (entry.Success) result[current][entry.Groups[1].Value.Trim()] = entry.Groups[2].Value.Trim();
        }
        return result;
    }

    private static List<string> SplitInfCsv(string value)
    {
        var output = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in value)
        {
            if (ch == '"') quoted = !quoted;
            if (ch == ',' && !quoted) { output.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(ch);
        }
        output.Add(current.ToString().Trim());
        return output;
    }

    private static string Unquote(string value) => value.Trim().Trim('"');

    private static (DateTime? Date, string Version) ParseDriverVer(string value)
    {
        var parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
        DateTime? date = null;
        if (parts.Length > 0 && DateTime.TryParseExact(parts[0], ["M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = parsed;
        return (date, parts.Length > 1 ? Unquote(parts[1]) : "");
    }

    private static (int Min, int Max) ReadWindowsDecorations(IEnumerable<string> sections)
    {
        var builds = sections
            .SelectMany(section => Regex.Matches(section, @"\.\.\.(\\d{4,})").Cast<Match>())
            .Select(match => ParseBuild(match.Groups[1].Value))
            .Where(build => build > 0)
            .ToArray();
        return builds.Length == 0 ? (0, int.MaxValue) : (builds.Min(), int.MaxValue);
    }

    private static string ResolveArchitecture(IEnumerable<string> sections)
    {
        var names = sections.ToArray();
        var hasAmd64 = names.Any(x => x.Contains("NTamd64", StringComparison.OrdinalIgnoreCase));
        var hasArm64 = names.Any(x => x.Contains("NTarm64", StringComparison.OrdinalIgnoreCase));
        var hasX86 = names.Any(x => x.Contains("NTx86", StringComparison.OrdinalIgnoreCase));
        if (!hasAmd64 && !hasArm64 && !hasX86) return RuntimeInformation.OSArchitecture.ToString();
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 when hasAmd64 => "X64",
            Architecture.Arm64 when hasArm64 => "Arm64",
            Architecture.X86 when hasX86 => "X86",
            _ => "Unsupported"
        };
    }

    private static int ParseBuild(string value) => int.TryParse(value, out var build) ? build : 0;

    private static int GetWindowsBuild()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion");
        var value = key?.GetValue("CurrentBuildNumber")?.ToString();
        return int.TryParse(value, out var build) ? build : 0;
    }
}

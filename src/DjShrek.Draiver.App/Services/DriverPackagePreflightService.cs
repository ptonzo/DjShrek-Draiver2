using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Performs a non-installing preflight assessment of extracted driver packages.
/// It never launches package installers or changes the Windows Driver Store.
/// </summary>
public sealed class DriverPackagePreflightService
{
    private const int MaximumInfBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> SupportedArchitectures =
        new(StringComparer.OrdinalIgnoreCase) { "NTx86", "NTamd64", "NTarm64" };

    public async Task<DriverPackagePreflightResult> InspectAsync(
        string packageDirectory,
        string expectedHardwareId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
            return DriverPackagePreflightResult.Failed("Каталог пакета не найден.");

        var root = Path.GetFullPath(packageDirectory);
        var infFiles = EnumerateFilesWithoutReparsePoints(root)
            .Where(x => Path.GetExtension(x).Equals(".inf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (infFiles.Length == 0)
            return DriverPackagePreflightResult.Failed("В распакованном пакете не найден INF-файл.");
        if (infFiles.Length > 256)
            return DriverPackagePreflightResult.Failed("Слишком много INF-файлов для предварительной проверки.");

        var results = new List<DriverInfPreflight>();
        foreach (var infPath in infFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(infPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                results.Add(DriverInfPreflight.Rejected(infPath, "INF является reparse point."));
                continue;
            }
            if (info.Length <= 0 || info.Length > MaximumInfBytes)
            {
                results.Add(DriverInfPreflight.Rejected(infPath, "Размер INF вне допустимого диапазона."));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(infPath, cancellationToken);
            var text = DecodeInf(bytes);
            if (text is null)
            {
                results.Add(DriverInfPreflight.Rejected(infPath, "Не удалось безопасно определить кодировку INF."));
                continue;
            }

            var sections = ParseSections(text);
            var version = GetSection(sections, "Version");
            if (version.Count == 0)
            {
                results.Add(DriverInfPreflight.Rejected(infPath, "Отсутствует секция [Version]."));
                continue;
            }

            var provider = GetValue(version, "Provider");
            var catalogName = GetValue(version, "CatalogFile");
            var className = GetValue(version, "Class");
            var classGuid = GetValue(version, "ClassGuid");
            var manufacturerLines = GetSection(sections, "Manufacturer");
            var modelsSections = ResolveModelsSections(sections, manufacturerLines, GetCurrentArchitecture());
            var modelIds = modelsSections.SelectMany(x => x.Value)
                .SelectMany(ParseHardwareIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var exactMatch = !string.IsNullOrWhiteSpace(expectedHardwareId) &&
                modelIds.Contains(expectedHardwareId.Trim(), StringComparer.OrdinalIgnoreCase);
            var catPath = ResolveCatalogPath(root, infPath, catalogName);
            var catExists = catPath is not null && File.Exists(catPath);
            var catSignature = catExists ? VerifyCatalogSignature(catPath!) : SignatureAssessment.Missing;
            var infSignature = VerifyInfSignature(infPath);

            results.Add(new DriverInfPreflight(
                infPath,
                true,
                exactMatch,
                provider,
                className,
                classGuid,
                catalogName,
                catPath ?? "",
                catExists,
                catSignature.Status,
                catSignature.Signer,
                infSignature.Status,
                modelsSections.Keys.ToArray(),
                exactMatch
                    ? "Найдено точное совпадение Hardware ID; требуется проверка совместимости и установка."
                    : "INF прочитан, но точное совпадение Hardware ID не найдено."));
        }

        var matched = results.Where(x => x.IsReadable && x.HasExactHardwareIdMatch).ToArray();
        return new DriverPackagePreflightResult(
            matched.Length > 0,
            matched.Length > 0
                ? "Предварительно найден INF с точным Hardware ID. Это не является разрешением на установку."
                : "Не найден INF с точным Hardware ID; автоматическая установка не рекомендуется.",
            results);
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Пакет содержит reparse point; поиск INF остановлен.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static string? DecodeInf(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static Dictionary<string, List<string>> ParseSections(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var pending = new StringBuilder();
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                current = line[1..^1].Trim();
                if (!result.ContainsKey(current)) result[current] = new List<string>();
                continue;
            }
            if (current is null) continue;
            pending.Append(line);
            if (line.EndsWith("\\", StringComparison.Ordinal))
            {
                pending.Length--;
                continue;
            }
            result[current].Add(pending.ToString());
            pending.Clear();
        }
        Flush();
        return result;

        void Flush()
        {
            if (current is not null && pending.Length > 0)
                result[current].Add(pending.ToString());
            pending.Clear();
        }
    }

    private static List<string> GetSection(Dictionary<string, List<string>> sections, string name) =>
        sections.TryGetValue(name, out var lines) ? lines : new List<string>();

    private static string GetValue(IEnumerable<string> lines, string key)
    {
        foreach (var line in lines)
        {
            var index = line.IndexOf('=');
            if (index > 0 && line[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(index + 1)..].Split(';')[0].Trim().Trim('"');
        }
        return "";
    }

    private static Dictionary<string, List<string>> ResolveModelsSections(
        Dictionary<string, List<string>> sections, List<string> manufacturers, string architecture)
    {
        var resolved = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in manufacturers)
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var sectionToken = line[(index + 1)..].Split(';')[0].Trim().Trim('"');
            var sectionName = sectionToken + "." + architecture;
            if (sections.TryGetValue(sectionName, out var decorated))
                resolved[sectionName] = decorated;
            else if (sections.TryGetValue(sectionToken, out var generic))
                resolved[sectionToken] = generic;
        }
        return resolved;
    }

    private static IEnumerable<string> ParseHardwareIds(string line)
    {
        var index = line.IndexOf('=');
        if (index < 0) yield break;
        var rhs = line[(index + 1)..];
        foreach (var token in rhs.Split(','))
        {
            var value = token.Trim().Trim('"');
            if (value.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase))
                yield return value;
        }
    }

    private static string GetCurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "NTx86",
        Architecture.X64 => "NTamd64",
        Architecture.Arm64 => "NTarm64",
        _ => throw new PlatformNotSupportedException("Архитектура Windows не поддерживается.")
    };

    private static string? ResolveCatalogPath(string root, string infPath, string catalogName)
    {
        if (string.IsNullOrWhiteSpace(catalogName) || Path.IsPathRooted(catalogName) ||
            catalogName.Contains(':') || catalogName.Contains('/') || catalogName.Contains('\\'))
            return null;
        var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(infPath)!, catalogName));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            return null;
        return candidate;
    }

    private static SignatureAssessment VerifyCatalogSignature(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return new SignatureAssessment("EmbeddedSignerCertificatePresent", cert.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch (CryptographicException)
        {
            return SignatureAssessment.Invalid;
        }
        catch (Exception)
        {
            return SignatureAssessment.Invalid;
        }
    }

    private static SignatureAssessment VerifyInfSignature(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return new SignatureAssessment("EmbeddedSignerCertificatePresent", cert.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch { return SignatureAssessment.NotIndividuallySigned; }
    }

    private sealed record SignatureAssessment(string Status, string Signer)
    {
        public static SignatureAssessment Missing => new("CatalogMissing", "");
        public static SignatureAssessment Invalid => new("NoEmbeddedSignerCertificate", "");
        public static SignatureAssessment NotIndividuallySigned => new("INFNotIndividuallySigned", "");
    }
}

public sealed record DriverInfPreflight(
    string InfPath,
    bool IsReadable,
    bool HasExactHardwareIdMatch,
    string Provider,
    string DeviceClass,
    string ClassGuid,
    string CatalogFile,
    string CatalogPath,
    bool CatalogExists,
    string CatalogCertificateStatus,
    string CatalogSigner,
    string InfCertificateStatus,
    IReadOnlyList<string> ApplicableModelSections,
    string Message)
{
    public static DriverInfPreflight Rejected(string path, string reason) =>
        new(path, false, false, "", "", "", "", "", false, "NotInspected", "", "NotInspected",
            Array.Empty<string>(), reason);
}

public sealed record DriverPackagePreflightResult(
    bool HasExactHardwareIdMatch,
    string Summary,
    IReadOnlyList<DriverInfPreflight> InfResults)
{
    public static DriverPackagePreflightResult Failed(string message) =>
        new(false, message, Array.Empty<DriverInfPreflight>());
}

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Reads HP's official HPClientDriverPackCatalog CAB/XML and maps SoftPaq driver packs
/// to the current HP platform ID and Windows architecture. A model pack is not a
/// per-device driver match; every contained INF still requires normal preflight.
/// </summary>
public sealed class HpDriverPackCatalogService
{
    private const string CatalogUrl =
        "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab";
    private const long MaxCabBytes = 96L * 1024 * 1024;
    private const long MaxXmlBytes = 256L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromMinutes(3) };

    static HpDriverPackCatalogService() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");

    public async Task<IReadOnlyList<OemDriverPackCandidate>> FindCurrentModelPacksAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = GetCurrentIdentity();
        if (string.IsNullOrWhiteSpace(identity.SystemId))
            return Array.Empty<OemDriverPackCandidate>();

        var root = Path.Combine(Path.GetTempPath(), "DjShrek-Draiver", "hp-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cabPath = Path.Combine(root, "HPClientDriverPackCatalog.cab");
        var xmlPath = Path.Combine(root, "HPClientDriverPackCatalog.xml");
        try
        {
            await DownloadCatalogAsync(cabPath, cancellationToken);
            await VerifyHpSignatureAsync(cabPath, cancellationToken);
            await ExtractCabAsync(cabPath, xmlPath, cancellationToken);
            if (new FileInfo(xmlPath).Length > MaxXmlBytes)
                throw new InvalidDataException("Каталог HP превышает допустимый размер XML.");

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxXmlBytes, IgnoreComments = true };
            using var reader = XmlReader.Create(xmlPath, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            return ParseCatalog(document, identity);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    internal static IReadOnlyList<OemDriverPackCandidate> ParseCatalog(
        XDocument document, HpMachineIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "HPClientDriverPackCatalog");
        if (root is null) return Array.Empty<OemDriverPackCandidate>();

        var softPaqs = root.Descendants().Where(x => x.Name.LocalName == "SoftPaq")
            .Select(x => new
            {
                Id = Value(x, "ID"),
                Name = Value(x, "Name"),
                Version = Value(x, "Version"),
                Url = Value(x, "URL"),
                Date = Value(x, "ReleaseDate")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var results = new List<OemDriverPackCandidate>();
        foreach (var pack in root.Descendants().Where(x => x.Name.LocalName == "ProductOSDriverPack"))
        {
            var systemIds = pack.DescendantsAndSelf()
                .Where(x => x.Name.LocalName == "SystemID")
                .Select(x => x.Value.Trim())
                .Where(x => x.Length > 0).ToArray();
            var exactId = systemIds.Contains(identity.SystemId, StringComparer.OrdinalIgnoreCase);
            if (!exactId) continue;

            var osName = Value(pack, "OSName");
            if (!IsSupportedWindows(osName, identity.OperatingSystem)) continue;
            var softPaqId = Value(pack, "SoftPaqID");
            if (!softPaqs.TryGetValue(softPaqId, out var softPaq)) continue;
            if (!TryBuildSoftPaqUri(softPaq.Url, out var packageUri))
                continue;

            var model = Value(pack, "SystemName");
            if (string.IsNullOrWhiteSpace(model)) model = identity.Model;
            var releaseDate = softPaq.Date;
            results.Add(new OemDriverPackCandidate(
                "HP", model, identity.SystemId, osName,
                Value(root, "DateReleased"), softPaq.Version, releaseDate,
                packageUri, CatalogUrl,
                $"Точное совпадение HP SystemID {identity.SystemId}; ОС: {osName}; SoftPaq {softPaq.Id}. Пакет модели, не отдельного устройства."));
        }

        return results
            .GroupBy(x => x.PackageUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => x.ReleaseDate, StringComparer.OrdinalIgnoreCase)
            .Take(20).ToArray();
    }

    private static HpMachineIdentity GetCurrentIdentity()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var systemId = key?.GetValue("SystemSKU")?.ToString()?.Trim() ?? "";
        var model = key?.GetValue("SystemProductName")?.ToString()?.Trim() ?? Environment.MachineName;
        var os = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
        return new HpMachineIdentity(systemId, model, os);
    }

    private static bool IsSupportedWindows(string catalogOs, string currentOs)
    {
        if (string.IsNullOrWhiteSpace(catalogOs)) return false;
        if (!catalogOs.Contains("64", StringComparison.OrdinalIgnoreCase) &&
            !catalogOs.Contains("x64", StringComparison.OrdinalIgnoreCase))
            return false;
        if (catalogOs.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
            return currentOs.Equals("Windows 11", StringComparison.OrdinalIgnoreCase);
        if (catalogOs.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            return currentOs.Equals("Windows 10", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryBuildSoftPaqUri(string value, out Uri packageUri)
    {
        packageUri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var source))
            return false;
        if (source.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase) &&
            source.Host.Equals("ftp.hp.com", StringComparison.OrdinalIgnoreCase))
            source = new UriBuilder(source) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (source.Scheme != Uri.UriSchemeHttps ||
            !(source.Host.Equals("ftp.hp.com", StringComparison.OrdinalIgnoreCase) ||
              source.Host.Equals("hpia.hpcloud.hp.com", StringComparison.OrdinalIgnoreCase)))
            return false;
        packageUri = source;
        return true;
    }

    private static string Value(XElement element, string localName) =>
        element.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName.Equals(localName,
            StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

    private static async Task DownloadCatalogAsync(string path, CancellationToken token)
    {
        using var response = await Client.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("Перенаправление каталога HP запрещено.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxCabBytes)
            throw new InvalidDataException("CAB-каталог HP превышает 96 МБ.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > MaxCabBytes) throw new InvalidDataException("CAB-каталог HP превышает 96 МБ.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static async Task VerifyHpSignatureAsync(string cabPath, CancellationToken token)
    {
        const string script = "$s=Get-AuthenticodeSignature -LiteralPath $env:DJSHREK_HP_CAB; " +
            "[pscustomobject]@{Status=[string]$s.Status;Signer=[string]$s.SignerCertificate.Subject}|ConvertTo-Json -Compress";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["DJSHREK_HP_CAB"] = cabPath;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось проверить подпись каталога HP.");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidDataException("Ошибка проверки подписи HP: " + error);
        using var json = JsonDocument.Parse(output);
        var status = json.RootElement.GetProperty("Status").GetString();
        var signer = json.RootElement.GetProperty("Signer").GetString();
        if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(signer) || !signer.Contains("HP", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Подпись каталога HP не прошла проверку Windows или издатель не HP.");
    }

    private static async Task ExtractCabAsync(string cabPath, string xmlPath, CancellationToken token)
    {
        var expand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
        if (!File.Exists(expand)) throw new FileNotFoundException("Не найдена системная утилита expand.exe.", expand);
        var start = new ProcessStartInfo(expand) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-F:HPClientDriverPackCatalog.xml");
        start.ArgumentList.Add(cabPath);
        start.ArgumentList.Add(xmlPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось распаковать каталог HP.");
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(xmlPath))
            throw new InvalidDataException("Не удалось извлечь XML-каталог HP: " + error);
    }

    internal sealed record HpMachineIdentity(string SystemId, string Model, string OperatingSystem);
}

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>Reads Dell's signed DriverPackCatalog CAB and matches exact BIOS/System IDs.</summary>
public sealed class DellDriverPackCatalogService
{
    public const string CatalogUrl = "https://downloads.dell.com/catalog/DriverPackCatalog.cab";
    private const long MaxCabBytes = 96L * 1024 * 1024;
    private const long MaxXmlBytes = 256L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromMinutes(3) };

    static DellDriverPackCatalogService()
    {
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DjShrek-Draiver", "2.0"));
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.ms-cab-compressed,application/octet-stream");
    }

    public async Task<IReadOnlyList<OemDriverPackCandidate>> FindCurrentModelPacksAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityAsync(cancellationToken);
        if (!identity.Manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<OemDriverPackCandidate>();
        if (string.IsNullOrWhiteSpace(identity.SystemId))
            throw new InvalidDataException("Dell System ID не определён через SMBIOS; точное сопоставление пакета невозможно.");

        var root = Path.Combine(Path.GetTempPath(), "DjShrek-Draiver", "DellCatalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cabPath = Path.Combine(root, "DriverPackCatalog.cab");
            using (var response = await Client.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var final = response.RequestMessage?.RequestUri;
                if (final is null || final.Scheme != Uri.UriSchemeHttps ||
                    !final.Host.Equals("downloads.dell.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Каталог Dell перенаправлен на неразрешённый адрес.");
                if (response.Content.Headers.ContentLength is > MaxCabBytes)
                    throw new InvalidDataException("CAB-каталог Dell превышает лимит 96 МБ.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(cabPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxCabBytes) throw new InvalidDataException("CAB-каталог Dell превышает лимит 96 МБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            await VerifyCatalogSignatureAsync(cabPath, cancellationToken);

            var xmlPath = Path.Combine(root, "DriverPackCatalog.xml");
            var expand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
            if (!File.Exists(expand)) throw new FileNotFoundException("Не найден системный expand.exe.", expand);
            var start = new ProcessStartInfo(expand)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(cabPath);
            start.ArgumentList.Add(xmlPath);
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось распаковать CAB Dell."))
            {
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var error = await errorTask;
                _ = await outputTask;
                if (process.ExitCode != 0 || !File.Exists(xmlPath))
                    throw new InvalidDataException("Не удалось извлечь XML из CAB Dell: " + error);
            }
            if (new FileInfo(xmlPath).Length > MaxXmlBytes)
                throw new InvalidDataException("XML-каталог Dell превышает лимит 256 МБ.");

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes };
            using var reader = XmlReader.Create(xmlPath, settings);
            var document = XDocument.Load(reader);
            var results = new List<OemDriverPackCandidate>();
            foreach (var pack in document.Descendants().Where(x => x.Name.LocalName == "DriverPackage"))
            {
                var systemIds = pack.Descendants().Where(x => x.Name.LocalName == "Model")
                    .Select(x => (string?)x.Attribute("systemID") ?? (string?)x.Element("systemID") ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                if (!systemIds.Contains(identity.SystemId, StringComparer.OrdinalIgnoreCase)) continue;

                var url = BuildPackageUri(document, pack);
                if (url is null) continue;
                var osArch = (string?)pack.Attribute("osArch") ?? "";
                if (!osArch.Equals("x64", StringComparison.OrdinalIgnoreCase)) continue;
                var osVendor = (string?)pack.Attribute("osVendor") ?? "";
                var osName = string.Join(" ", new[] {
                    osVendor, (string?)pack.Attribute("majorVersion") ?? "",
                    (string?)pack.Attribute("minorVersion") ?? ""
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var version = string.Join(".", new[] {
                    (string?)pack.Attribute("majorVersion") ?? "",
                    (string?)pack.Attribute("minorVersion") ?? "",
                    (string?)pack.Attribute("spMajorVersion") ?? "",
                    (string?)pack.Attribute("spMinorVersion") ?? ""
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var release = (string?)pack.Attribute("releaseDate") ?? (string?)pack.Attribute("dateTime") ?? "";
                results.Add(new OemDriverPackCandidate(
                    "Dell", (string?)pack.Attribute("name") ?? identity.Model, identity.SystemId,
                    string.IsNullOrWhiteSpace(osName) ? "ОС не указана" : osName,
                    (string?)document.Root?.Attribute("Version") ?? "Dell DriverPackCatalog",
                    "Пакет драйверов для модели; версия отдельного драйвера не опубликована",
                    release, url, CatalogUrl,
                    $"Точное совпадение Dell BIOS/System ID {identity.SystemId}; архитектура каталога: {osArch}; INF-пакета ещё не проверены"));
            }
            return results.DistinctBy(x => x.PackageUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }


    private static async Task VerifyCatalogSignatureAsync(string cabPath, CancellationToken token)
    {
        // Get-AuthenticodeSignature delegates signature and certificate-chain validation to Windows.
        // The file path is passed through an environment variable, never interpolated into PowerShell.
        const string script = "$s=Get-AuthenticodeSignature -LiteralPath $env:DJSHREK_DELL_CAB; " +
            "[pscustomobject]@{Status=[string]$s.Status;StatusMessage=[string]$s.StatusMessage;Signer=[string]$s.SignerCertificate.Subject}|ConvertTo-Json -Compress";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell,
            "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["DJSHREK_DELL_CAB"] = cabPath;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось проверить цифровую подпись CAB Dell.");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidDataException("Windows не смогла проверить подпись CAB Dell: " + error);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        var status = root.TryGetProperty("Status", out var statusValue) ? statusValue.GetString() : null;
        var signer = root.TryGetProperty("Signer", out var signerValue) ? signerValue.GetString() : null;
        var message = root.TryGetProperty("StatusMessage", out var messageValue) ? messageValue.GetString() : null;
        if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(signer) || !signer.Contains("Dell", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Подпись/цепочка CAB Dell не прошла проверку Windows или издатель не Dell. " +
                $"Статус: {status ?? "неизвестен"}; издатель: {signer ?? "неизвестен"}; {message}");
    }

    private static Uri? BuildPackageUri(XDocument document, XElement pack)
    {
        var path = (string?)pack.Element("path") ?? (string?)pack.Attribute("path");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var baseLocation = (string?)document.Root?.Attribute("baseLocation");
        if (string.IsNullOrWhiteSpace(baseLocation)) return null;
        if (!Uri.TryCreate(baseLocation.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("downloads.dell.com", StringComparison.OrdinalIgnoreCase) ||
            !new[] { ".cab", ".exe", ".zip" }.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase))
            return null;
        return uri;
    }

    private static async Task<(string Manufacturer, string SystemId, string Model)> GetIdentityAsync(CancellationToken token)
    {
        const string script = "$cs=Get-CimInstance Win32_ComputerSystem; [pscustomobject]@{Manufacturer=[string]$cs.Manufacturer;Model=[string]$cs.Model;SystemId=[string]$cs.SystemSKUNumber}|ConvertTo-Json -Compress";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })
            ?? throw new InvalidOperationException("Не удалось получить SMBIOS System ID Dell.");
        var output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException("Ошибка чтения SMBIOS System ID Dell.");
        using var json = JsonDocument.Parse(output);
        var r = json.RootElement;
        return (r.GetProperty("Manufacturer").GetString() ?? "", r.GetProperty("SystemId").GetString() ?? "", r.GetProperty("Model").GetString() ?? "");
    }
}

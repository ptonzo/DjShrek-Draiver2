using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Downloads packages discovered on OEM pages, independent catalogs, or public cloud hosts.
/// Discovery is not a trust verdict: callers must inspect INF/CAT, hardware IDs and signatures
/// before presenting an installation action. Windows Update APIs are intentionally not used.
/// </summary>
public sealed class CatalogDriverPackageDownloader
{
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxPageBytes = 12 * 1024 * 1024;
    private static readonly string[] PackageExtensions = { ".exe", ".msi", ".zip", ".cab" };
    private static readonly Regex LinkRegex = new(
        """<a\b[^>]*href\s*=\s*["']([^"']+)["'][^>]*>(.*?)</a\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    static CatalogDriverPackageDownloader()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/octet-stream;q=0.9,*/*;q=0.7");
    }

    public async Task<IReadOnlyList<Uri>> FindPackageLinksAsync(
        DriverUpdateCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!IsAllowedCatalogPage(candidate.SourceUrl))
            throw new InvalidDataException("Страница источника не прошла проверку HTTPS и домена каталога.");

        // Machine-readable OEM catalogs may provide a direct package URI.
        // Accept it only when HTTPS, vendor-domain provenance, and file extension agree.
        if (!string.IsNullOrWhiteSpace(candidate.PackageUrl))
        {
            if (!Uri.TryCreate(candidate.PackageUrl, UriKind.Absolute, out var directPackage) ||
                directPackage.Scheme != Uri.UriSchemeHttps ||
                !InternetDriverCatalogService.IsOfficialVendorUrl(directPackage.AbsoluteUri) ||
                !PackageExtensions.Contains(Path.GetExtension(directPackage.AbsolutePath),
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Прямая ссылка OEM-каталога не прошла проверку HTTPS, домена и расширения.");
            return new[] { directPackage };
        }

        using var response = await SendFollowingPublicRedirectsAsync(
            new Uri(candidate.SourceUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var pageUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Не удалось определить конечный адрес страницы.");
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase))
            return LooksLikePackage(pageUri, response.Content.Headers.ContentDisposition)
                ? new[] { pageUri } : Array.Empty<Uri>();

        var html = await ReadBoundedTextAsync(response.Content, MaxPageBytes, cancellationToken);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LinkRegex.Matches(html))
        {
            var raw = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (!Uri.TryCreate(pageUri, raw, out var link) || link.Scheme != Uri.UriSchemeHttps) continue;
            var label = WebUtility.HtmlDecode(TagRegex.Replace(match.Groups[2].Value, " "));
            var downloadLabel = Regex.IsMatch(label, "скачать|download|загрузить|получить файл", RegexOptions.IgnoreCase);
            var cloudHost = IsKnownCloudHost(link.Host);
            if (!LooksLikePackage(link, null) && !downloadLabel && !cloudHost) continue;
            if (!found.Add(link.AbsoluteUri)) continue;
        }

        return found.Select(x => new Uri(x)).Take(50).ToArray();
    }

    public async Task<(string FilePath, string Sha256, long Length, string FinalUrl)> DownloadAsync(
        Uri packageUri, string destinationFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        if (packageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Разрешены только HTTPS-ссылки на пакеты.");

        using var response = await SendFollowingPublicRedirectsAsync(
            packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Не удалось определить конечный адрес загрузки.");
        if (response.Content.Headers.ContentLength is > MaxPackageBytes)
            throw new InvalidDataException("Размер пакета превышает допустимые 2 ГБ.");

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? Path.GetFileName(finalUri.LocalPath);
        fileName = WebUtility.UrlDecode((fileName ?? "").Trim().Trim('"'));
        var extension = Path.GetExtension(fileName);
        if (!PackageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            extension = GuessExtension(response.Content.Headers.ContentType?.MediaType);
        if (!PackageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер не сообщил поддерживаемый тип файла. Загрузка остановлена.");

        var target = Path.GetFullPath(destinationFilePath);
        if (!PackageExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
            target += extension;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes) throw new InvalidDataException("Загрузка остановлена: превышен лимит 2 ГБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (total == 0) throw new InvalidDataException("Сервер вернул пустой файл.");
            }

            await ValidatePackageSignatureAsync(temporary, extension, cancellationToken);
            await using var verify = File.OpenRead(temporary);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            File.Move(temporary, target, true);
            return (target, hash, new FileInfo(target).Length, finalUri.AbsoluteUri);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public static bool IsAllowedCatalogPage(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        // Google/Dropbox/OneDrive/etc. are download destinations, not catalog page origins.
        if (IsKnownCloudHost(uri.Host)) return false;
        return InternetDriverCatalogService.IsOfficialVendorUrl(value) ||
               InternetDriverCatalogService.IsIndependentCatalogUrl(value);
    }

    private static async Task<HttpResponseMessage> SendFollowingPublicRedirectsAsync(
        Uri startUri, HttpCompletionOption completion, CancellationToken token)
    {
        var current = startUri;
        for (var redirect = 0; redirect <= 8; redirect++)
        {
            if (current.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Перенаправление на HTTP или другой протокол заблокировано.");
            await EnsurePublicHostAsync(current.Host, token);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await Client.SendAsync(request, completion, token);
            var redirectStatus = response.StatusCode is HttpStatusCode.MovedPermanently or
                HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            if (!redirectStatus) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("Перенаправление без адреса.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new InvalidDataException("Превышено допустимое число перенаправлений.");
    }

    private static async Task EnsurePublicHostAsync(string host, CancellationToken token)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = new[] { literal };
        else addresses = await Dns.GetHostAddressesAsync(host, token);
        if (addresses.Length == 0 || addresses.Any(IsNonPublicAddress))
            throw new InvalidDataException("Адрес источника указывает на локальную или непубличную сеть.");
    }

    private static bool IsNonPublicAddress(IPAddress address)
    {
        // DNS may return IPv4-mapped IPv6 addresses; classify the embedded IPv4
        // address too, otherwise private/loopback targets could bypass this guard.
        if (address.IsIPv4MappedToIPv6)
            return IsNonPublicAddress(address.MapToIPv4());

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
            address.IsIPv6UniqueLocal || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                   b[0] >= 224;
        }
        return address.IsIPv6UniqueLocal || address.Equals(IPAddress.IPv6Any);
    }

    private static bool IsKnownCloudHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return new[] { "drive.google.com", "docs.google.com", "dropbox.com", "www.dropbox.com",
            "1drv.ms", "onedrive.live.com", "sharepoint.com", "mega.nz", "mediafire.com",
            "www.mediafire.com", "yadi.sk", "disk.yandex.ru", "cloud.mail.ru", "pixeldrain.com" }
            .Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private static bool LooksLikePackage(Uri uri, ContentDispositionHeaderValue? disposition)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName ?? uri.AbsolutePath;
        return PackageExtensions.Contains(Path.GetExtension(name.Trim('"')), StringComparer.OrdinalIgnoreCase);
    }

    private static string GuessExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "application/zip" or "application/x-zip-compressed" => ".zip",
        "application/vnd.ms-cab-compressed" => ".cab",
        "application/x-msdownload" or "application/vnd.microsoft.portable-executable" => ".exe",
        "application/x-msi" => ".msi",
        _ => ""
    };

    private static async Task ValidatePackageSignatureAsync(
        string path, string extension, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header.AsMemory(), cancellationToken);
        var valid = extension.ToLowerInvariant() switch
        {
            ".zip" => read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                      ((header[2] == 0x03 && header[3] == 0x04) ||
                       (header[2] == 0x05 && header[3] == 0x06) ||
                       (header[2] == 0x07 && header[3] == 0x08)),
            ".cab" => read >= 4 && header[0] == 0x4D && header[1] == 0x53 &&
                      header[2] == 0x43 && header[3] == 0x46,
            ".exe" => read >= 2 && header[0] == 0x4D && header[1] == 0x5A,
            ".msi" => read >= 8 && header[0] == 0xD0 && header[1] == 0xCF &&
                      header[2] == 0x11 && header[3] == 0xE0 && header[4] == 0xA1 &&
                      header[5] == 0xB1 && header[6] == 0x1A && header[7] == 0xE1,
            _ => false
        };
        if (!valid)
            throw new InvalidDataException($"Содержимое файла не соответствует заявленному формату {extension}. Пакет не сохранён.");
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaxPageBytes)
            throw new InvalidDataException("Страница каталога превышает лимит 12 МБ.");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException("Превышен лимит чтения страницы 12 МБ.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
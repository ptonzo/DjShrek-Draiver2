using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Resolves actual driver package links from an official vendor support page and
/// downloads the original package. It never uses Windows Update or Microsoft Update Catalog.
/// </summary>
public sealed class OfficialDriverPackageDownloader
{
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxSupportPageBytes = 12 * 1024 * 1024;
    private static readonly string[] PackageExtensions = { ".exe", ".msi", ".zip", ".cab", ".7z" };
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    static OfficialDriverPackageDownloader()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/1.8");
        Client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/octet-stream;q=0.9,*/*;q=0.7");
    }

    public async Task<IReadOnlyList<Uri>> FindPackageLinksAsync(string supportPageUrl, CancellationToken cancellationToken = default)
    {
        if (!InternetDriverCatalogService.IsOfficialVendorUrl(supportPageUrl))
            throw new InvalidDataException("Источник не входит в список официальных сайтов производителей.");

        using var response = await GetTrustedResponseAsync(new Uri(supportPageUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalPage || !IsTrustedVendorHost(finalPage.Host))
            throw new InvalidDataException("Страница перенаправила на посторонний домен. Загрузка заблокирована.");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase))
            return IsPackageUri(finalPage) ? new[] { finalPage } : Array.Empty<Uri>();

        var html = await ReadBoundedTextAsync(response.Content, MaxSupportPageBytes, cancellationToken);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, @"(?:href|data-download-url)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase))
        {
            var raw = WebUtility.HtmlDecode(match.Groups[1].Value).Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(finalPage, raw, out var link) || link.Scheme != Uri.UriSchemeHttps) continue;
            if (!IsTrustedVendorHost(link.Host) || !IsPackageUri(link)) continue;
            found.Add(link.AbsoluteUri);
        }

        return found.Select(x => new Uri(x)).Take(40).ToArray();
    }

    public async Task<(string FilePath, string Sha256, long Length)> DownloadAsync(
        Uri packageUri, string destinationFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        if (packageUri.Scheme != Uri.UriSchemeHttps || !IsTrustedVendorHost(packageUri.Host) || !IsPackageUri(packageUri))
            throw new InvalidDataException("Ссылка на пакет не прошла проверку домена, HTTPS и расширения.");

        using var response = await GetTrustedResponseAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || !IsTrustedVendorHost(finalUri.Host))
            throw new InvalidDataException("Загрузка перенаправлена на недоверенный домен.");
        if (response.Content.Headers.ContentLength is > MaxPackageBytes)
            throw new InvalidDataException("Размер пакета превышает допустимые 2 ГБ.");

        var fileName = Path.GetFileName(finalUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 180)
            fileName = "driver-package" + Path.GetExtension(finalUri.LocalPath);
        fileName = Regex.Replace(fileName, @"[^A-Za-z0-9А-Яа-я._() -]", "_");
        if (!PackageExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер вернул файл с неподдерживаемым расширением.");

        var target = Path.GetFullPath(destinationFilePath);
        if (!PackageExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Выбранный путь имеет неподдерживаемое расширение.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes) throw new InvalidDataException("Загрузка остановлена: пакет превышает 2 ГБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (total == 0) throw new InvalidDataException("Производитель вернул пустой файл.");
            }
            File.Move(temporary, target, overwrite: true);
            await using var verify = File.OpenRead(target);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            return (target, hash, new FileInfo(target).Length);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetTrustedResponseAsync(
        Uri startUri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var current = startUri;
        for (var redirect = 0; redirect <= 8; redirect++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !IsTrustedVendorHost(current.Host))
                throw new InvalidDataException("Переход на адрес вне списка официальных доменов заблокирован.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await Client.SendAsync(request, completionOption, cancellationToken);
            var isRedirect = response.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            if (!isRedirect) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidDataException("Сервер вернул перенаправление без адреса.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidDataException("Превышено допустимое число перенаправлений производителя.");
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new InvalidDataException("Страница производителя слишком большая для безопасного анализа.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("Чтение страницы остановлено: превышен лимит 12 МБ.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool IsPackageUri(Uri uri) =>
        PackageExtensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    private static bool IsTrustedVendorHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return new[] { "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
            "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
            "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com", "surface.com",
            "logitech.com", "chicony.com", "steelseries.com", "razer.com", "corsair.com",
            "apple.com", "google.com", "yubico.com", "steampowered.com", "nintendo.com",
            "playstation.com", "pixart.com", "logi.com", "samsung.com", "canon.com",
            "epson.com", "brother.com", "huawei.com", "zte.com.cn", "mi.com",
            "ftdichip.com", "silabs.com", "prolific.com.tw", "wch-ic.com", "arduino.cc" }
            .Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }
}

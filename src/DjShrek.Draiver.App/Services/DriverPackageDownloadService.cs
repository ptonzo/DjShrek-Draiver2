using System.Net;
using System.Security.Cryptography;

namespace DjShrek.Draiver.App.Services;

public sealed record DriverDownloadResult(string FilePath, long SizeBytes, string Sha256, string SourceHost);

/// <summary>
/// Downloads only HTTPS URLs on approved manufacturer domains, with size and redirect limits.
/// It never installs downloaded files.
/// </summary>
public sealed class DriverPackageDownloadService
{
    private const long MaxPackageBytes = 1024L * 1024 * 1024;
    private static readonly string[] AllowedDomains =
    [
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com",
        "microsoft.com", "windows.net"
    ];

    public async Task<DriverDownloadResult> DownloadAsync(
        string sourceUrl, string destinationFolder, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri.Host))
            throw new InvalidDataException("Разрешены только HTTPS-ссылки с официальных доменов производителей.");

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await SendWithValidatedRedirectsAsync(client, uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxPackageBytes)
            throw new InvalidDataException("Размер пакета превышает ограничение 1 ГБ.");

        var root = Path.GetFullPath(destinationFolder);
        Directory.CreateDirectory(root);
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160)
            name = "driver-package-" + Guid.NewGuid().ToString("N") + ".bin";
        name = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var destination = Path.Combine(root, name);
        if (File.Exists(destination))
            destination = Path.Combine(root, Path.GetFileNameWithoutExtension(name) + "-" +
                Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(name));

        var temporary = destination + ".download";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes)
                        throw new InvalidDataException("Загрузка остановлена: размер пакета превысил 1 ГБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            var info = new FileInfo(temporary);
            if (info.Length == 0) throw new InvalidDataException("Сервер вернул пустой файл.");
            byte[] hash;
            await using (var verifyStream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                hash = await SHA256.HashDataAsync(verifyStream, cancellationToken);
            File.Move(temporary, destination);
            return new DriverDownloadResult(destination, info.Length, Convert.ToHexString(hash), uri.Host);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpClient client, Uri initial, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var hop = 0; hop <= 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || hop == 5)
                    throw new InvalidDataException("Недопустимое перенаправление сервера загрузки.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(current.Host))
                    throw new InvalidDataException("Перенаправление на неофициальный домен заблокировано.");
                continue;
            }
            return response;
        }
        throw new InvalidDataException("Превышено число перенаправлений.");
    }

    private static bool IsAllowedHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return AllowedDomains.Any(domain =>
            host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }
}

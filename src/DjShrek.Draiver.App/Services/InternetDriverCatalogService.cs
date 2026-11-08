using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Searches public vendor pages using device Hardware IDs.
/// Search results are references only; this service never installs a package.
/// </summary>
public sealed class InternetDriverCatalogService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(18)
    };

    static InternetDriverCatalogService()
    {
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DjShrek-Draiver", "1.8"));
    }

    private static readonly string[] OfficialDomains =
    {
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com"
    };

    public async Task<IReadOnlyList<DriverUpdateCandidate>> SearchAsync(
        IEnumerable<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        var output = new List<DriverUpdateCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = GetIds(device).Take(2).ToArray();
            foreach (var id in ids)
            {
                var query = Uri.EscapeDataString($"driver download \"{id}\" Windows 10 OR Windows 11");
                var url = "https://www.bing.com/search?format=rss&q=" + query;
                try
                {
                    using var response = await Client.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var xml = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
                    foreach (var item in xml.Descendants("item"))
                    {
                        var link = (string?)item.Element("link") ?? "";
                        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                            uri.Scheme != Uri.UriSchemeHttps || !IsOfficial(uri.Host) || !seen.Add(uri.AbsoluteUri))
                            continue;
                        var title = WebUtility.HtmlDecode((string?)item.Element("title") ?? "Страница драйвера производителя");
                        var description = WebUtility.HtmlDecode((string?)item.Element("description") ?? "");
                        output.Add(new DriverUpdateCandidate
                        {
                            UpdateId = uri.AbsoluteUri,
                            Title = title,
                            Manufacturer = device.Manufacturer,
                            Model = device.Name,
                            Provider = uri.Host,
                            DeviceClass = device.Class,
                            HardwareId = id,
                            DeviceName = device.Name,
                            CurrentVersion = device.DriverVersion,
                            Compatibility = "Совпадение Hardware ID в поисковом запросе; пакет требует проверки",
                            AvailableVersion = "Проверьте версию на сайте производителя",
                            Description = description,
                            SourceUrl = uri.AbsoluteUri
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* A failed query must not stop searches for other devices. */ }
            }
        }
        return output.OrderBy(x => x.DeviceName).ThenBy(x => x.Provider).ToArray();
    }

    private static IEnumerable<string> GetIds(DeviceInfo device) =>
        (device.HardwareIds ?? Array.Empty<string>())
            .Concat(device.CompatibleIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length >= 8)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsOfficial(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return OfficialDomains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }
}

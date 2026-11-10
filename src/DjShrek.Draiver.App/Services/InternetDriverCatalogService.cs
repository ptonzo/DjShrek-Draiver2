using System.Net;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed record InternetDriverSearchResult(
    string DeviceName, string Manufacturer, string DriverProvider,
    string HardwareId, string Title, string Description, string Url,
    string MatchNote);

/// <summary>
/// Discovers driver pages on manufacturer domains using actual PnP hardware identifiers.
/// Search results are links for manual review, not verified installable packages.
/// </summary>
public sealed class InternetDriverCatalogService
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(18) };

    private static readonly string[] OfficialDomains =
    {
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com"
    };

    public async Task<IReadOnlyList<InternetDriverSearchResult>> SearchAsync(
        IEnumerable<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        var output = new List<InternetDriverSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = device.HardwareIds
                .Concat(device.CompatibleIds)
                .Where(IsHardwareId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3);

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                            uri.Scheme != Uri.UriSchemeHttps || !IsOfficial(uri.Host) ||
                            !seen.Add(device.InstanceId + "|" + uri.AbsoluteUri))
                            continue;

                        output.Add(new InternetDriverSearchResult(
                            device.Name, device.Manufacturer, device.DriverProvider, id,
                            WebUtility.HtmlDecode((string?)item.Element("title") ?? "Страница производителя"),
                            WebUtility.HtmlDecode((string?)item.Element("description") ?? ""),
                            uri.AbsoluteUri,
                            "Найдено по Hardware ID; точная совместимость и версия требуют подтверждения на сайте производителя."));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException) { }
                catch (System.Xml.XmlException) { }
            }
        }
        return output.OrderBy(x => x.DeviceName).ThenBy(x => x.Manufacturer).ToArray();
    }

    private static bool IsHardwareId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= 8 &&
        value.Contains('\\') && (value.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase));

    private static bool IsOfficial(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return OfficialDomains.Any(domain =>
            host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }
}

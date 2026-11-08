using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Discovers official vendor support pages from Plug and Play identifiers.
/// Search hits are leads only: this service does not claim package compatibility,
/// download binaries, or install drivers.
/// </summary>
public sealed class InternetDriverCatalogService
{
    private const int MaxIdsPerDevice = 3;
    private const int MaxResultsPerDevice = 8;
    private const int MaxTotalResults = 120;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly string[] OfficialDomains =
    {
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com",
        "microsoft.com", "surface.com"
    };

    static InternetDriverCatalogService()
    {
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DjShrek-Draiver", "1.8"));
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, */*;q=0.8");
    }

    public async Task<IReadOnlyList<DriverUpdateCandidate>> SearchAsync(
        IEnumerable<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var output = new List<DriverUpdateCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices.Where(d => d is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deviceCount = 0;
            foreach (var hardwareId in GetIds(device).Take(MaxIdsPerDevice))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Count >= MaxTotalResults) return Rank(output);
                var query = BuildQuery(device, hardwareId);
                var requestUrl = "https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(query);

                try
                {
                    using var response = await Client.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode) continue;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var xml = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
                    foreach (var item in xml.Descendants().Where(x => x.Name.LocalName == "item"))
                    {
                        if (deviceCount >= MaxResultsPerDevice || output.Count >= MaxTotalResults) break;
                        var rawLink = (string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "link");
                        if (!TryGetOfficialUri(rawLink, out var uri) || !seen.Add(uri.AbsoluteUri)) continue;

                        var title = Clean((string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "title"));
                        var description = Clean((string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "description"));
                        if (string.IsNullOrWhiteSpace(title)) title = "Страница поддержки производителя";

                        output.Add(new DriverUpdateCandidate
                        {
                            UpdateId = uri.AbsoluteUri,
                            Title = title,
                            Manufacturer = device.Manufacturer,
                            Model = device.Name,
                            Provider = uri.Host,
                            DeviceClass = device.Class,
                            HardwareId = hardwareId,
                            DeviceName = device.Name,
                            CurrentVersion = device.DriverVersion,
                            Compatibility = "Найдено упоминание Hardware ID; совместимость пакета не подтверждена",
                            AvailableVersion = "Не подтверждена — проверьте на сайте производителя",
                            Description = description,
                            SourceUrl = uri.AbsoluteUri
                        });
                        deviceCount++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { /* One slow request must not block other devices. */ }
                catch (HttpRequestException) { /* Continue with other identifiers. */ }
                catch (System.Xml.XmlException) { /* Ignore malformed search response. */ }
            }
        }
        return Rank(output);
    }

    private static string BuildQuery(DeviceInfo device, string hardwareId)
    {
        var vendor = GetVendorDomain(device, hardwareId);
        var parts = new List<string> { "driver", "download", hardwareId, "(Windows 10 OR Windows 11)" };
        if (!string.IsNullOrWhiteSpace(vendor)) parts.Add("site:" + vendor);
        else parts.Add("(site:intel.com OR site:nvidia.com OR site:amd.com OR site:dell.com OR site:hp.com OR site:lenovo.com OR site:asus.com OR site:acer.com OR site:msi.com OR site:realtek.com)");
        return string.Join(" ", parts);
    }

    private static string? GetVendorDomain(DeviceInfo device, string hardwareId)
    {
        var text = (device.Manufacturer + " " + device.DriverProvider + " " + hardwareId).ToLowerInvariant();
        if (text.Contains("nvidia") || hardwareId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "nvidia.com";
        if (text.Contains("amd") || text.Contains("advanced micro") || hardwareId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "amd.com";
        if (text.Contains("intel") || hardwareId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "intel.com";
        if (text.Contains("realtek") || hardwareId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase)) return "realtek.com";
        if (text.Contains("qualcomm") || hardwareId.Contains("VEN_168C", StringComparison.OrdinalIgnoreCase)) return "qualcomm.com";
        if (text.Contains("broadcom") || hardwareId.Contains("VEN_14E4", StringComparison.OrdinalIgnoreCase)) return "broadcom.com";
        if (text.Contains("mediatek") || hardwareId.Contains("VEN_14C3", StringComparison.OrdinalIgnoreCase)) return "mediatek.com";
        if (text.Contains("dell") || text.Contains("alienware")) return "dell.com";
        if (text.Contains("hewlett") || text.Contains(" hp ")) return "hp.com";
        if (text.Contains("lenovo") || text.Contains("thinkpad")) return "lenovo.com";
        if (text.Contains("asus")) return "asus.com";
        if (text.Contains("acer")) return "acer.com";
        if (text.Contains("msi") || text.Contains("micro-star")) return "msi.com";
        return null;
    }

    private static IEnumerable<string> GetIds(DeviceInfo device) =>
        (device.HardwareIds ?? Array.Empty<string>())
            .Concat(device.CompatibleIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length >= 8)
            .Select(x => x.Trim().Trim('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Contains("&SUBSYS_", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Contains("&REV_", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Count(c => c == '&'));

    private static bool TryGetOfficialUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !IsOfficial(parsed.Host))
            return false;
        uri = parsed;
        return true;
    }

    public static bool IsOfficialVendorUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return IsOfficial(uri.Host);
    }

    private static bool IsOfficial(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return OfficialDomains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private static string Clean(string? value) =>
        WebUtility.HtmlDecode(value ?? string.Empty).Trim();

    private static IReadOnlyList<DriverUpdateCandidate> Rank(List<DriverUpdateCandidate> items) =>
        items.OrderBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.HardwareId.Contains("&SUBSYS_", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Finds official vendor support pages using the device's most specific Plug and Play IDs.
/// Search results are discovery leads only; they are never treated as verified driver packages.
/// </summary>
public sealed class InternetDriverCatalogService
{
    private const int MaxIdsPerDevice = 6;
    private const int MaxResultsPerDevice = 12;
    private const int MaxTotalResults = 160;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly string[] OfficialDomains =
    {
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com",
        "downloadmirror.intel.com", "download.nvidia.com", "drivers.amd.com",
        "ftp.hp.com", "download.lenovo.com", "dlcdnets.asus.com",
        "global-download.acer.com", "download.msi.com", "download.gigabyte.com",
        "download.realtek.com", "microsoft.com", "surface.com", "logitech.com",
        "chicony.com", "steelseries.com", "razer.com", "corsair.com", "apple.com",
        "google.com", "yubico.com", "steampowered.com", "nintendo.com",
        "playstation.com", "pixart.com", "logi.com", "samsung.com", "canon.com",
        "epson.com", "brother.com", "huawei.com", "zte.com.cn", "mi.com",
        "ftdichip.com", "silabs.com", "prolific.com.tw", "wch-ic.com", "arduino.cc"
    };

    static InternetDriverCatalogService()
    {
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DjShrek-Draiver", "2.0"));
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9");
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
                    // Do not follow redirects from the search endpoint. A redirect is not
                    // needed for RSS discovery and could otherwise leave the trusted host.
                    if (!response.IsSuccessStatusCode) continue;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var xml = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
                    foreach (var item in xml.Descendants().Where(x => x.Name.LocalName == "item"))
                    {
                        if (deviceCount >= MaxResultsPerDevice || output.Count >= MaxTotalResults) break;
                        var rawLink = (string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "link");
                        if (!TryGetOfficialUri(rawLink, out var uri)) continue;

                        var deviceKey = string.IsNullOrWhiteSpace(device.InstanceId)
                            ? device.Name + "|" + device.Manufacturer
                            : device.InstanceId;
                        if (!seen.Add(deviceKey + "|" + uri.AbsoluteUri)) continue;

                        var title = Clean((string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "title"));
                        var description = Clean((string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "description"));
                        if (string.IsNullOrWhiteSpace(title)) title = "Страница поддержки производителя";
                        var published = ParsePublishedDate((string?)item.Elements().FirstOrDefault(x => x.Name.LocalName == "pubDate"));
                        var isExactHardwareId = (device.HardwareIds ?? Array.Empty<string>())
                            .Contains(hardwareId, StringComparer.OrdinalIgnoreCase);

                        var identity = DeviceHardwareIdParser.Parse(hardwareId);
                        output.Add(new DriverUpdateCandidate
                        {
                            BusType = identity?.Bus ?? "",
                            VendorId = identity?.VendorId ?? "",
                            DeviceId = identity?.DeviceId ?? "",
                            SubsystemId = identity?.SubsystemId ?? "",
                            RevisionId = identity?.RevisionId ?? "",
                            HardwareIdentitySummary = identity?.Summary ?? hardwareId,
                            UpdateId = uri.AbsoluteUri,
                            Title = title,
                            Manufacturer = device.Manufacturer,
                            Model = device.Name,
                            Provider = uri.Host,
                            DeviceClass = device.Class,
                            HardwareId = hardwareId,
                            MatchType = isExactHardwareId ? "Hardware ID" : "Compatible ID",
                            DeviceName = device.Name,
                            CurrentVersion = device.DriverVersion,
                            Compatibility = DriverUpdateCandidate.HardwareIdSearchNotice,
                            AvailableVersion = "Не подтверждена — проверьте на сайте производителя",
                            Description = description,
                            SearchQuery = query,
                            PublishedDate = published,
                            Evidence = isExactHardwareId
                                ? "Точный Hardware ID использован в поисковом запросе; страница производителя ещё не подтверждает поддержку устройства."
                                : "Compatible ID использован в поисковом запросе; этого недостаточно для подтверждения совместимости.",
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
        var parts = new List<string>
        {
            "driver download",
            "\"" + hardwareId + "\"",
            "(Windows 10 OR Windows 11)"
        };
        if (!string.IsNullOrWhiteSpace(device.Name))
            parts.Add("\"" + LimitQueryText(device.Name, 90) + "\"");
        if (!string.IsNullOrWhiteSpace(vendor))
            parts.Add("site:" + vendor);
        else
            parts.Add("(site:intel.com OR site:nvidia.com OR site:amd.com OR site:dell.com OR site:hp.com OR site:lenovo.com OR site:asus.com OR site:acer.com OR site:msi.com OR site:realtek.com)");
        return string.Join(" ", parts);
    }

    private static string LimitQueryText(string value, int maxLength) =>
        new(value.Where(c => !char.IsControl(c) && c != '"').Take(maxLength).ToArray());

    internal static string? GetVendorDomain(DeviceInfo device, string hardwareId)
    {
        var identity = DeviceHardwareIdParser.Parse(hardwareId);
        var vendorDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["10DE"] = "nvidia.com", ["1002"] = "amd.com", ["1022"] = "amd.com",
            ["8086"] = "intel.com", ["8087"] = "intel.com", ["10EC"] = "realtek.com",
            ["0BDA"] = "realtek.com", ["14E4"] = "broadcom.com", ["168C"] = "qualcomm.com",
            ["0CF3"] = "qualcomm.com", ["17CB"] = "qualcomm.com", ["14C3"] = "mediatek.com",
            ["0E8D"] = "mediatek.com", ["046D"] = "logitech.com", ["045E"] = "microsoft.com",
            ["0B05"] = "asus.com", ["1043"] = "asus.com", ["17AA"] = "lenovo.com",
            ["103C"] = "hp.com", ["04F2"] = "chicony.com", ["1038"] = "steelseries.com",
            ["1532"] = "razer.com", ["1B1C"] = "corsair.com", ["05AC"] = "apple.com",
            ["18D1"] = "google.com", ["1050"] = "yubico.com", ["28DE"] = "steampowered.com",
            ["057E"] = "nintendo.com", ["054C"] = "playstation.com", ["093A"] = "pixart.com",
            ["03F0"] = "hp.com", ["04E8"] = "samsung.com", ["04A9"] = "canon.com",
            ["04B8"] = "epson.com", ["04F9"] = "brother.com", ["12D1"] = "huawei.com",
            ["19D2"] = "zte.com.cn", ["2717"] = "mi.com", ["0403"] = "ftdichip.com",
            ["10C4"] = "silabs.com", ["067B"] = "prolific.com.tw", ["1A86"] = "wch-ic.com",
            ["2341"] = "arduino.cc"
        };
        if (identity is not null && vendorDomains.TryGetValue(identity.VendorId, out var idDomain))
            return idDomain;

        var text = NormalizeVendorText(device.Manufacturer + " " + device.DriverProvider);
        bool HasToken(string token) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.Ordinal);
        bool HasPhrase(string phrase) => text.Contains(phrase, StringComparison.Ordinal);

        if (hardwareId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || HasToken("nvidia")) return "nvidia.com";
        if (hardwareId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || HasToken("amd") || HasPhrase("advanced micro devices")) return "amd.com";
        if (hardwareId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || HasToken("intel")) return "intel.com";
        if (hardwareId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase) || HasToken("realtek")) return "realtek.com";
        if (hardwareId.Contains("VEN_168C", StringComparison.OrdinalIgnoreCase) || HasToken("qualcomm") || HasToken("atheros")) return "qualcomm.com";
        if (hardwareId.Contains("VEN_14E4", StringComparison.OrdinalIgnoreCase) || HasToken("broadcom")) return "broadcom.com";
        if (hardwareId.Contains("VEN_14C3", StringComparison.OrdinalIgnoreCase) || HasToken("mediatek")) return "mediatek.com";
        if (HasToken("dell") || HasToken("alienware")) return "dell.com";
        if (HasToken("hp") || HasPhrase("hewlett packard")) return "hp.com";
        if (HasToken("lenovo") || HasToken("thinkpad")) return "lenovo.com";
        if (HasToken("asus")) return "asus.com";
        if (HasToken("acer")) return "acer.com";
        if (HasToken("msi") || HasPhrase("micro star")) return "msi.com";
        if (HasToken("gigabyte") || HasToken("aorus")) return "gigabyte.com";
        return null;
    }

    private static string NormalizeVendorText(string value)
    {
        var normalized = new string(value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IEnumerable<string> GetIds(DeviceInfo device)
    {
        // Exact Hardware IDs are always searched before broader Compatible IDs.
        var exact = (device.HardwareIds ?? Array.Empty<string>())
            .Where(IsUsableId)
            .Select(NormalizeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IdSpecificity);
        var compatible = (device.CompatibleIds ?? Array.Empty<string>())
            .Where(IsUsableId)
            .Select(NormalizeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IdSpecificity);
        return exact.Concat(compatible).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUsableId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Trim('"').Length >= 8;

    private static string NormalizeId(string value) => value.Trim().Trim('"');

    private static int IdSpecificity(string value) =>
        (value.Contains("&SUBSYS_", StringComparison.OrdinalIgnoreCase) ? 100 : 0) +
        (value.Contains("&REV_", StringComparison.OrdinalIgnoreCase) ? 20 : 0) +
        value.Count(c => c == '&');

    private static string ParsePublishedDate(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        return "";
    }

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
            .ThenBy(x => x.MatchType == "Hardware ID" ? 0 : 1)
            .ThenByDescending(x => IdSpecificity(x.HardwareId))
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.PublishedDate, StringComparer.Ordinal)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
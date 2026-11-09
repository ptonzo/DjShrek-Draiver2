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
    private readonly DriverRuCatalogProvider _driverRuProvider = new();

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly string[] CatalogDomains =
    {
        "driver.ru", "driverguide.com", "flydrivers.com", "driverpack.io",
        "station-drivers.com", "driverscollection.com", "driverscape.com"
    };

    private static readonly string[] OfficialDomains =
    {
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com",
        "downloadmirror.intel.com", "dsadata.intel.com", "download.nvidia.com", "drivers.amd.com",
        "ftp.hp.com", "hpcloud.hp.com", "download.lenovo.com", "dlcdnets.asus.com",
        "global-download.acer.com", "download.msi.com", "download.gigabyte.com",
        "download.realtek.com", "microsoft.com", "surface.com", "logitech.com",
        "chicony.com", "steelseries.com", "razer.com", "corsair.com", "apple.com",
        "google.com", "yubico.com", "steampowered.com", "nintendo.com",
        "playstation.com", "pixart.com", "logi.com", "samsung.com", "canon.com",
        "epson.com", "brother.com", "huawei.com", "zte.com.cn", "mi.com",
        "ftdichip.com", "silabs.com", "prolific.com.tw", "wch-ic.com", "arduino.cc", "marvell.com", "redhat.com"
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
        IReadOnlyList<DriverVendorCatalogEntry> signedEntries;
        try
        {
            signedEntries = await new SignedVendorCatalogManifestService()
                .DownloadAndVerifyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Offline operation retains the built-in curated registry.
            signedEntries = new DriverVendorCatalogRegistry().GetAll();
        }

        var output = new List<DriverUpdateCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices.Where(d => d is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deviceCount = 0;
            var primaryDriverRuId = GetIds(device).FirstOrDefault();
            foreach (var hardwareId in GetIds(device).Take(MaxIdsPerDevice))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Count >= MaxTotalResults) return Rank(output);

                // Query Driver.ru directly once per device using its most-specific Hardware ID.
                // Its records are surfaced as independent, unverified catalog leads.
                if (string.Equals(hardwareId, primaryDriverRuId, StringComparison.OrdinalIgnoreCase))
                {
                    var driverRuResults = await _driverRuProvider.SearchAsync(device, hardwareId, cancellationToken);
                    foreach (var driverRuResult in driverRuResults)
                    {
                        if (deviceCount >= MaxResultsPerDevice || output.Count >= MaxTotalResults) break;
                        var deviceKey = string.IsNullOrWhiteSpace(device.InstanceId)
                            ? device.Name + "|" + device.Manufacturer
                            : device.InstanceId;
                        if (!seen.Add(deviceKey + "|" + driverRuResult.UpdateId)) continue;
                        output.Add(driverRuResult);
                        deviceCount++;
                    }
                }

                var query = BuildQuery(device, hardwareId, signedEntries);
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
                        if (!TryGetCatalogUri(rawLink, out var uri)) continue;

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
                            SourceType = IsOfficial(uri.Host) ? "OfficialManufacturer" : "IndependentCatalog",
                            SourceTrust = IsOfficial(uri.Host) ? "OfficialDomain_UnverifiedPackage" : "IndependentCatalog_UnverifiedPackage",
                            DeviceClass = device.Class,
                            HardwareId = hardwareId,
                            InstanceId = device.InstanceId,
                            MatchType = isExactHardwareId ? "Hardware ID" : "Compatible ID",
                            DeviceName = device.Name,
                            CurrentVersion = device.DriverVersion,
                            Compatibility = DriverUpdateCandidate.HardwareIdSearchNotice,
                            AvailableVersion = "Не указана в поисковом результате",
                            VersionComparison = "Версия пакета в поисковом результате не указана — сравнение невозможно.",
                            Description = description,
                            SearchQuery = query,
                            PublishedDate = published,
                            Evidence = isExactHardwareId
                                ? "Точный Hardware ID использован в поисковом запросе. Результат каталога/сайта — только зацепка; совместимость пакета не подтверждена."
                                : "Compatible ID использован в поисковом запросе. Это только зацепка; совместимость пакета не подтверждена.",
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

    private static string BuildQuery(
        DeviceInfo device, string hardwareId, IReadOnlyList<DriverVendorCatalogEntry> availableEntries)
    {
        var vendor = GetVendorDomain(device, hardwareId, availableEntries);
        var parts = new List<string>
        {
            "driver download",
            "\"" + hardwareId + "\"",
            "(Windows 10 OR Windows 11)"
        };
        if (!string.IsNullOrWhiteSpace(device.Name))
            parts.Add("\"" + LimitQueryText(device.Name, 90) + "\"");
        var sourceSites = new List<string>();
        if (!string.IsNullOrWhiteSpace(vendor)) sourceSites.Add("site:" + vendor);
        else sourceSites.Add("(site:intel.com OR site:nvidia.com OR site:amd.com OR site:dell.com OR site:hp.com OR site:lenovo.com OR site:asus.com OR site:acer.com OR site:msi.com OR site:realtek.com)");
        sourceSites.Add("(site:driver.ru OR site:driverguide.com OR site:flydrivers.com OR site:driverpack.io OR site:station-drivers.com OR site:driverscollection.com OR site:driverscape.com)");
        parts.Add("(" + string.Join(" OR ", sourceSites) + ")");
        return string.Join(" ", parts);
    }

    private static string LimitQueryText(string value, int maxLength) =>
        new(value.Where(c => !char.IsControl(c) && c != '"').Take(maxLength).ToArray());

    internal static string? GetVendorDomain(
        DeviceInfo device, string hardwareId, IReadOnlyList<DriverVendorCatalogEntry>? availableEntries = null)
    {
        var registry = new DriverVendorCatalogRegistry();
        var vendor = availableEntries is null
            ? registry.Resolve(hardwareId, device.Manufacturer, device.DriverProvider)
            : registry.Resolve(hardwareId, device.Manufacturer, device.DriverProvider, availableEntries);
        return vendor?.SearchDomain;
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

    private static bool TryGetCatalogUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            (!IsOfficial(parsed.Host) && !IsIndependentCatalog(parsed.Host)))
            return false;
        uri = parsed;
        return true;
    }

    public static bool IsIndependentCatalogUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return IsIndependentCatalog(uri.Host);
    }

    private static bool IsIndependentCatalog(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return CatalogDomains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
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
            .ThenBy(x => x.SourceType == "OfficialManufacturer" ? 0 : 1)
            .ThenByDescending(x => x.PublishedDate, StringComparer.Ordinal)
            .ThenByDescending(x => IdSpecificity(x.HardwareId))
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
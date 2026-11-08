using System.Net;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Finds manufacturer-published support/download pages using the device's real PnP Hardware IDs.
/// A search result is a lead, not proof that a newer compatible package exists.
/// </summary>
public sealed class InternetDriverCatalogService
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string[] OfficialDomains =
    {
        "intel.com", "nvidia.com", "amd.com", "dell.com", "hp.com",
        "lenovo.com", "asus.com", "acer.com", "msi.com", "gigabyte.com",
        "realtek.com", "qualcomm.com", "mediatek.com", "broadcom.com",
        "microsoft.com", "microsoft.com"
    };

    public async Task<IReadOnlyList<DriverSearchResult>> SearchAsync(
        IEnumerable<DeviceInfo> devices, CancellationToken cancellationToken = default)
    {
        var output = new List<DriverSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedDevices = devices
            .Where(d => d.HardwareIds is { Length: > 0 })
            .Take(30)
            .ToArray();

        foreach (var device in selectedDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = device.HardwareIds
                .Where(IsUsefulHardwareId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var queryText = $"\"{id}\" driver download";
                var url = "https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(queryText);
                try
                {
                    using var response = await Client.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var xml = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

                    foreach (var item in xml.Descendants().Where(x => x.Name.LocalName == "item"))
                    {
                        var link = item.Elements().FirstOrDefault(x => x.Name.LocalName == "link")?.Value?.Trim() ?? "";
                        if (!Uri.TryCreate(link, UriKind.Absolute, out var resultUri) ||
                            resultUri.Scheme != Uri.UriSchemeHttps ||
                            !IsOfficialHost(resultUri.Host))
                            continue;

                        var key = device.InstanceId + "|" + resultUri.AbsoluteUri;
                        if (!seen.Add(key)) continue;

                        var title = WebUtility.HtmlDecode(item.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value ?? "Страница производителя");
                        var description = WebUtility.HtmlDecode(item.Elements().FirstOrDefault(x => x.Name.LocalName == "description")?.Value ?? "");
                        output.Add(new DriverSearchResult
                        {
                            DeviceName = device.Name,
                            DeviceClass = device.Class,
                            CurrentProvider = device.DriverProvider,
                            CurrentVersion = device.DriverVersion,
                            HardwareId = id,
                            PageTitle = title,
                            Snippet = description,
                            Host = resultUri.Host,
                            Url = resultUri.AbsoluteUri,
                            Verification = "Официальный домен; совместимость и версия требуют проверки на странице производителя"
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException) { /* A failed search for one ID must not cancel other devices. */ }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (System.Xml.XmlException) { }
                await Task.Delay(250, cancellationToken);
            }
        }

        return output
            .OrderBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUsefulHardwareId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length >= 10 &&
        id.Contains('\\') && !id.Contains("REV_", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return OfficialDomains.Any(domain =>
            host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }
}

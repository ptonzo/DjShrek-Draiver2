using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Curated directory of official driver support portals. It identifies where to search,
/// not whether a particular package exists or is compatible with a device.
/// </summary>
public sealed record DriverVendorCatalogEntry(
    string Id,
    string DisplayName,
    string SearchDomain,
    string SupportUrl,
    IReadOnlySet<string> VendorIds,
    IReadOnlySet<string> DeviceFamilies);

public sealed class DriverVendorCatalogRegistry
{
    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly DriverVendorCatalogEntry[] Entries =
    [
        Entry("nvidia", "NVIDIA", "nvidia.com", "https://www.nvidia.com/Download/index.aspx",
            ["10DE", "0955"], ["graphics", "gpu"]),
        Entry("amd", "AMD", "amd.com", "https://www.amd.com/en/support/download/drivers.html",
            ["1002", "1022"], ["graphics", "chipset"]),
        Entry("intel", "Intel", "intel.com", "https://www.intel.com/content/www/us/en/download-center/home.html",
            ["8086", "8087"], ["graphics", "wifi", "bluetooth", "chipset"]),
        Entry("realtek", "Realtek", "realtek.com", "https://www.realtek.com/Download/Index",
            ["10EC", "0BDA"], ["audio", "network", "wifi", "bluetooth", "card-reader"]),
        Entry("qualcomm", "Qualcomm", "qualcomm.com", "https://www.qualcomm.com/support",
            ["168C", "0CF3", "17CB", "1969"], ["wifi", "bluetooth", "network"]),
        Entry("mediatek", "MediaTek", "mediatek.com", "https://www.mediatek.com/",
            ["14C3", "0E8D"], ["wifi", "bluetooth", "chipset"]),
        Entry("broadcom", "Broadcom", "broadcom.com", "https://www.broadcom.com/support",
            ["14E4"], ["network", "wifi", "bluetooth"]),
        Entry("dell", "Dell", "dell.com", "https://www.dell.com/support/home/",
            [], ["oem"]),
        Entry("hp", "HP", "hp.com", "https://support.hp.com/drivers",
            ["103C", "03F0"], ["oem"]),
        Entry("lenovo", "Lenovo", "lenovo.com", "https://pcsupport.lenovo.com/",
            ["17AA"], ["oem"]),
        Entry("asus", "ASUS", "asus.com", "https://www.asus.com/support/download-center/",
            ["1043", "0B05"], ["oem"]),
        Entry("acer", "Acer", "acer.com", "https://www.acer.com/support",
            [], ["oem"]),
        Entry("msi", "MSI", "msi.com", "https://www.msi.com/support/download",
            [], ["oem"]),
        Entry("gigabyte", "GIGABYTE", "gigabyte.com", "https://www.gigabyte.com/Support",
            [], ["oem"]),
        Entry("microsoft", "Microsoft", "microsoft.com", "https://www.microsoft.com/en-us/download",
            ["045E"], ["oem", "peripheral"]),
        Entry("logitech", "Logitech", "logitech.com", "https://support.logi.com/",
            ["046D"], ["peripheral", "mouse", "keyboard", "webcam", "audio"])
    ];

    private static readonly IReadOnlyDictionary<string, DriverVendorCatalogEntry> ByVendorId =
        Entries.SelectMany(entry => entry.VendorIds.Select(id => (id, entry)))
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().entry,
                StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DriverVendorCatalogEntry> GetAll() => Entries;

    public static IReadOnlyList<DriverVendorCatalogEntry> MergeSignedEntries(
        IEnumerable<DriverVendorCatalogEntry> signedEntries)
    {
        ArgumentNullException.ThrowIfNull(signedEntries);
        var merged = Entries.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in signedEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.DisplayName) ||
                string.IsNullOrWhiteSpace(entry.SearchDomain) ||
                !Uri.TryCreate(entry.SupportUrl, UriKind.Absolute, out var supportUri) ||
                supportUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(supportUri.Host, entry.SearchDomain, StringComparison.OrdinalIgnoreCase) &&
                !supportUri.Host.EndsWith("." + entry.SearchDomain, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Запись подписанного каталога источников содержит некорректные поля.");
            merged[entry.Id] = entry;
        }
        return merged.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }


    public DriverVendorCatalogEntry? Resolve(string? hardwareId, string? manufacturer = null, string? driverProvider = null)
    {
        var identity = DeviceHardwareIdParser.Parse(hardwareId);
        if (identity is not null &&
            !string.IsNullOrWhiteSpace(identity.VendorId) &&
            ByVendorId.TryGetValue(identity.VendorId, out var byId))
            return byId;

        var text = Normalize((manufacturer ?? string.Empty) + " " + (driverProvider ?? string.Empty));
        bool Has(string token) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.Ordinal);

        if (Has("nvidia")) return Find("nvidia");
        if (Has("amd") || text.Contains("advanced micro devices", StringComparison.Ordinal)) return Find("amd");
        if (Has("intel")) return Find("intel");
        if (Has("realtek")) return Find("realtek");
        if (Has("qualcomm") || Has("atheros")) return Find("qualcomm");
        if (Has("mediatek")) return Find("mediatek");
        if (Has("broadcom")) return Find("broadcom");
        if (Has("dell") || Has("alienware")) return Find("dell");
        if (Has("hp") || text.Contains("hewlett packard", StringComparison.Ordinal)) return Find("hp");
        if (Has("lenovo") || Has("thinkpad") || Has("thinkcentre")) return Find("lenovo");
        if (Has("asus") || Has("asustek")) return Find("asus");
        if (Has("acer")) return Find("acer");
        if (Has("msi") || text.Contains("micro star", StringComparison.Ordinal)) return Find("msi");
        if (Has("gigabyte") || Has("aorus")) return Find("gigabyte");
        if (Has("microsoft") || Has("surface")) return Find("microsoft");
        return null;
    }

    public DriverVendorCatalogEntry? Resolve(
        string? hardwareId, string? manufacturer, string? driverProvider,
        IReadOnlyList<DriverVendorCatalogEntry> availableEntries)
    {
        ArgumentNullException.ThrowIfNull(availableEntries);
        var identity = DeviceHardwareIdParser.Parse(hardwareId);
        if (!string.IsNullOrWhiteSpace(identity?.VendorId))
        {
            var byId = availableEntries.FirstOrDefault(entry =>
                entry.VendorIds.Contains(identity.VendorId));
            if (byId is not null) return byId;
        }

        var builtIn = Resolve(hardwareId, manufacturer, driverProvider);
        if (builtIn is not null)
            return availableEntries.FirstOrDefault(entry =>
                entry.Id.Equals(builtIn.Id, StringComparison.OrdinalIgnoreCase)) ?? builtIn;

        var normalized = Normalize((manufacturer ?? string.Empty) + " " + (driverProvider ?? string.Empty));
        return availableEntries.FirstOrDefault(entry =>
            normalized.Contains(Normalize(entry.DisplayName), StringComparison.Ordinal));
    }

    public DriverVendorCatalogEntry? Find(string id) =>
        Entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    private static DriverVendorCatalogEntry Entry(
        string id, string name, string domain, string url, string[] vendorIds, string[] families) =>
        new(id, name, domain, url,
            new HashSet<string>(vendorIds, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(families, StringComparer.OrdinalIgnoreCase));

    private static string Normalize(string value) =>
        string.Join(" ", new string(value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

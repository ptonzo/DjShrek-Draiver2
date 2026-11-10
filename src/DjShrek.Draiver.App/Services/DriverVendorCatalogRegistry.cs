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
            ["046D"], ["peripheral", "mouse", "keyboard", "webcam", "audio"]),
        Entry("chicony", "Chicony", "chicony.com", "https://www.chicony.com/",
            ["04F2"], ["peripheral", "camera", "keyboard"]),
        Entry("steelseries", "SteelSeries", "steelseries.com", "https://steelseries.com/gg",
            ["1038"], ["peripheral", "mouse", "keyboard", "audio"]),
        Entry("razer", "Razer", "razer.com", "https://www.razer.com/pc",
            ["1532"], ["peripheral", "mouse", "keyboard", "audio"]),
        Entry("corsair", "Corsair", "corsair.com", "https://www.corsair.com/support",
            ["1B1C"], ["peripheral", "mouse", "keyboard", "audio"]),
        Entry("apple", "Apple", "apple.com", "https://support.apple.com/downloads",
            ["05AC"], ["peripheral", "mobile"]),
        Entry("samsung", "Samsung", "samsung.com", "https://www.samsung.com/support/",
            ["04E8"], ["mobile", "storage", "peripheral"]),
        Entry("canon", "Canon", "canon.com", "https://www.usa.canon.com/support",
            ["04A9"], ["printer", "scanner", "camera"]),
        Entry("epson", "Epson", "epson.com", "https://epson.com/Support",
            ["04B8"], ["printer", "scanner"]),
        Entry("brother", "Brother", "brother.com", "https://support.brother.com/",
            ["04F9"], ["printer", "scanner"]),
        Entry("huawei", "Huawei", "huawei.com", "https://consumer.huawei.com/en/support/",
            ["12D1"], ["mobile", "network"]),
        Entry("zte", "ZTE", "zte.com.cn", "https://support.ztedevices.com/",
            ["19D2"], ["mobile", "network"]),
        Entry("xiaomi", "Xiaomi", "mi.com", "https://www.mi.com/global/support/",
            ["2717"], ["mobile", "peripheral"]),
        Entry("ftdi", "FTDI", "ftdichip.com", "https://ftdichip.com/drivers/",
            ["0403"], ["usb", "serial", "interface"]),
        Entry("silicon-labs", "Silicon Labs", "silabs.com", "https://www.silabs.com/developers/usb-to-uart-bridge-vcp-drivers",
            ["10C4"], ["usb", "serial", "interface"]),
        Entry("prolific", "Prolific", "prolific.com.tw", "https://www.prolific.com.tw/US/ShowProduct.aspx?p_id=225&pcid=41",
            ["067B"], ["usb", "serial", "interface"]),
        Entry("wch", "WCH", "wch-ic.com", "https://www.wch-ic.com/downloads/CH341SER_EXE.html",
            ["1A86"], ["usb", "serial", "interface"]),
        Entry("arduino", "Arduino", "arduino.cc", "https://www.arduino.cc/en/software",
            ["2341"], ["usb", "development"]),
        Entry("yubico", "Yubico", "yubico.com", "https://www.yubico.com/support/download/",
            ["1050"], ["security", "peripheral"]),
        Entry("valve", "Valve", "steampowered.com", "https://help.steampowered.com/",
            ["28DE"], ["peripheral", "controller"]),
        Entry("nintendo", "Nintendo", "nintendo.com", "https://en-americas-support.nintendo.com/",
            ["057E"], ["peripheral", "controller"]),
        Entry("sony", "Sony", "playstation.com", "https://www.playstation.com/support/",
            ["054C"], ["peripheral", "controller"]),
        Entry("google", "Google", "google.com", "https://support.google.com/",
            ["18D1"], ["mobile", "peripheral"]),
        Entry("cisco", "Cisco", "cisco.com", "https://www.cisco.com/c/en/us/support/index.html",
            ["13B1"], ["network"]),
        Entry("marvell", "Marvell", "marvell.com", "https://www.marvell.com/support.html",
            ["1B4B"], ["network", "storage"]),
        Entry("virtio", "Red Hat", "redhat.com", "https://access.redhat.com/",
            ["1AF4"], ["virtual", "network", "storage"]),
        Entry("asmedia", "ASMedia", "asmedia.com.tw", "https://www.asmedia.com.tw/eng/support/downloads",
            ["1B21"], ["usb", "sata", "storage"]),
        Entry("jmicron", "JMicron", "jmicron.com", "https://www.jmicron.com/Support",
            ["197B"], ["storage", "sata", "usb"]),
        Entry("via", "VIA Technologies", "via-labs.com", "https://www.viatech.com/en/support/",
            ["1106"], ["chipset", "audio", "network"]),
        Entry("sis", "Silicon Integrated Systems", "sis.com", "https://www.sis.com/support/",
            ["1039"], ["chipset", "graphics"]),
        Entry("creative", "Creative", "creative.com", "https://support.creative.com/",
            ["1102"], ["audio"]),
        Entry("cirrus", "Cirrus Logic", "cirrus.com", "https://www.cirrus.com/support/",
            ["1013"], ["audio"]),
        Entry("synaptics", "Synaptics", "synaptics.com", "https://www.synaptics.com/products",
            ["06CB"], ["touchpad", "input"]),
        Entry("elan", "ELAN", "emc.com.tw", "https://www.emc.com.tw/eng/",
            ["04F3"], ["touchpad", "input"]),
        Entry("goodix", "Goodix", "goodix.com", "https://www.goodix.com/en/support",
            ["27C6"], ["touchpad", "biometric", "input"]),
        Entry("pixart", "PixArt", "pixart.com", "https://www.pixart.com/",
            ["093A"], ["mouse", "camera", "peripheral"]),
        Entry("cypress", "Cypress", "infineon.com", "https://www.infineon.com/cms/en/product/universal-serial-bus/",
            ["04B4"], ["usb", "interface"]),
        Entry("stmicro", "STMicroelectronics", "st.com", "https://www.st.com/content/st_com/en/support.html",
            ["0483"], ["usb", "interface", "sensor"]),
        Entry("nxp", "NXP", "nxp.com", "https://www.nxp.com/support:SUPPORTHOME",
            ["1FC9"], ["usb", "interface", "network"]),
        Entry("espressif", "Espressif", "espressif.com", "https://www.espressif.com/en/support/download",
            ["303A"], ["usb", "development", "network"]),
        Entry("nordic", "Nordic Semiconductor", "nordicsemi.com", "https://www.nordicsemi.com/Products/Development-tools",
            ["1915"], ["usb", "bluetooth", "development"]),
        Entry("phison", "Phison", "phison.com", "https://www.phison.com/en/support",
            ["1987"], ["storage", "nvme"]),
        Entry("silicon-motion", "Silicon Motion", "siliconmotion.com", "https://www.siliconmotion.com/Support",
            ["090C"], ["storage", "usb"]),
        Entry("kingston", "Kingston", "kingston.com", "https://www.kingston.com/support",
            ["0951"], ["storage", "usb"]),
        Entry("western-digital", "Western Digital", "westerndigital.com", "https://support-en.wd.com/",
            ["1058"], ["storage", "usb"]),
        Entry("seagate", "Seagate", "seagate.com", "https://www.seagate.com/support/",
            ["0BC2"], ["storage", "usb"])
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
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.Id) ||
                entry.Id.Length > 80 ||
                !entry.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                string.IsNullOrWhiteSpace(entry.DisplayName) ||
                entry.DisplayName.Length > 120 ||
                string.IsNullOrWhiteSpace(entry.SearchDomain) ||
                entry.SearchDomain.Length > 253 ||
                !Uri.TryCreate("https://" + entry.SearchDomain, UriKind.Absolute, out var domainUri) ||
                !string.Equals(domainUri.Host, entry.SearchDomain, StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(entry.SupportUrl, UriKind.Absolute, out var supportUri) ||
                supportUri.Scheme != Uri.UriSchemeHttps ||
                !IsHostWithinDomain(supportUri.Host, entry.SearchDomain) ||
                entry.VendorIds is null || entry.DeviceFamilies is null ||
                entry.VendorIds.Count > 64 || entry.DeviceFamilies.Count > 32 ||
                entry.VendorIds.Any(id => string.IsNullOrWhiteSpace(id) ||
                    id.Length is not (4 or 8) || !id.All(Uri.IsHexDigit)) ||
                entry.DeviceFamilies.Any(family => string.IsNullOrWhiteSpace(family) || family.Length > 40))
                throw new InvalidDataException("Запись подписанного каталога источников содержит некорректные поля.");

            var safe = entry with
            {
                Id = entry.Id.Trim(),
                DisplayName = entry.DisplayName.Trim(),
                SearchDomain = domainUri.IdnHost.ToLowerInvariant(),
                SupportUrl = supportUri.GetLeftPart(UriPartial.Path),
                VendorIds = new HashSet<string>(entry.VendorIds.Select(x => x.ToUpperInvariant()),
                    StringComparer.OrdinalIgnoreCase),
                DeviceFamilies = new HashSet<string>(entry.DeviceFamilies.Select(x => x.Trim()),
                    StringComparer.OrdinalIgnoreCase)
            };
            merged[safe.Id] = safe;
        }

        var duplicateVendorIds = merged.Values
            .SelectMany(entry => entry.VendorIds.Select(id => (id, entry.Id)))
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToArray();
        if (duplicateVendorIds.Length > 0)
            throw new InvalidDataException("Подписанный каталог содержит неоднозначные Vendor ID: " +
                string.Join(", ", duplicateVendorIds.Select(x => x.Key)));

        return merged.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsHostWithinDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);


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

        // Keep manufacturer/provider fallback extensible as the curated registry grows.
        // Longest display names win so compound names such as "Silicon Labs" resolve
        // before a shorter overlapping token.
        return Entries
            .OrderByDescending(entry => Normalize(entry.DisplayName).Length)
            .FirstOrDefault(entry =>
            {
                var displayName = Normalize(entry.DisplayName);
                return displayName.Length > 0 &&
                       (" " + text + " ").Contains(" " + displayName + " ", StringComparison.Ordinal);
            });
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
        if (normalized.Length == 0) return null;

        // Match complete normalized names, not arbitrary substrings (for example,
        // "HP" must not match an unrelated provider string containing those letters).
        return availableEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName))
            .OrderByDescending(entry => Normalize(entry.DisplayName).Length)
            .FirstOrDefault(entry =>
            {
                var displayName = Normalize(entry.DisplayName);
                return displayName.Length > 0 &&
                       (" " + normalized + " ").Contains(" " + displayName + " ", StringComparison.Ordinal);
            });
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

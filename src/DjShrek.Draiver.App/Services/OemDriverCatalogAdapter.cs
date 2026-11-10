using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Builds manufacturer-restricted catalog searches from OEM model data and exact PnP IDs.
/// Search results are discovery links; compatibility must be confirmed by the manufacturer.
/// </summary>
public sealed class OemDriverCatalogAdapter
{
    private static readonly (string Token, string Domain, string Label)[] VendorDomains =
    [
        ("Acer", "acer.com", "Acer"),
        ("MSI", "msi.com", "MSI"),
        ("ASUS", "asus.com", "ASUS"),
        ("Dell", "dell.com", "Dell"),
        ("HP", "hp.com", "HP"),
        ("Lenovo", "lenovo.com", "Lenovo"),
        ("Gigabyte", "gigabyte.com", "Gigabyte"),
        ("ASRock", "asrock.com", "ASRock"),
        ("NVIDIA", "nvidia.com", "NVIDIA"),
        ("Intel", "intel.com", "Intel"),
        ("AMD", "amd.com", "AMD"),
        ("Realtek", "realtek.com", "Realtek"),
        ("Qualcomm", "qualcomm.com", "Qualcomm"),
        ("MediaTek", "mediatek.com", "MediaTek"),
        ("Broadcom", "broadcom.com", "Broadcom")
    ];

    public IReadOnlyList<OemCatalogTarget> BuildTargets(
        OemDeviceIdentity identity,
        IEnumerable<DeviceInfo> devices)
    {
        var targets = new List<OemCatalogTarget>();
        var model = First(identity.ProductNumber, identity.SearchModel, identity.Model,
            identity.ProductName, identity.SystemSku, identity.BoardProduct);

        var oem = VendorDomains.FirstOrDefault(x =>
            identity.Vendor.Contains(x.Token, StringComparison.OrdinalIgnoreCase) ||
            identity.Manufacturer.Contains(x.Token, StringComparison.OrdinalIgnoreCase) ||
            identity.BoardManufacturer.Contains(x.Token, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(oem.Domain) && !string.IsNullOrWhiteSpace(model))
            Add(targets, $"{oem.Label} — каталог по модели", oem.Domain, model, "OEM model/ProductNumber");

        foreach (var device in devices)
        {
            var providerText = First(device.DriverProvider, device.Manufacturer, device.Name);
            var provider = VendorDomains.FirstOrDefault(x =>
                providerText.Contains(x.Token, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(provider.Domain)) continue;

            var ids = device.HardwareIds
                .Concat(device.CompatibleIds)
                .Where(IsUsefulHardwareId)
                .Take(2)
                .ToArray();

            if (ids.Length > 0)
            {
                foreach (var id in ids)
                    Add(targets, $"{provider.Label} — поиск по Hardware ID", provider.Domain, id, "HardwareID");
            }
            else
            {
                Add(targets, $"{provider.Label} — поиск по модели устройства", provider.Domain,
                    device.Name, "DeviceName");
            }
        }

        return targets
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(40)
            .ToArray();
    }

    private static bool IsUsefulHardwareId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase));

    private static void Add(List<OemCatalogTarget> list, string name, string domain, string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var search = $"site:{domain} driver {query}";
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(search);
        list.Add(new OemCatalogTarget(name, url, key, query));
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
}

public sealed record OemCatalogTarget(string Name, string Url, string MatchKey, string Query);

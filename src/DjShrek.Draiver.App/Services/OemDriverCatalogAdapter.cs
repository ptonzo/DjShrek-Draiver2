namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Creates manufacturer-restricted web searches using the exact OEM model or PnP identifier.
/// Results are discovery links and must be checked on the vendor site before installation.
/// </summary>
public sealed class OemDriverCatalogAdapter
{
    public IReadOnlyList<OemCatalogTarget> BuildTargets(
        OemDeviceIdentity identity,
        IEnumerable<Models.DeviceInfo> devices)
    {
        var targets = new List<OemCatalogTarget>();
        var model = First(identity.ProductNumber, identity.SearchModel, identity.Model, identity.ProductName);

        if (identity.Vendor.Equals("Acer", StringComparison.OrdinalIgnoreCase))
            Add(targets, "Acer — поиск по ProductNumber", "acer.com", model, "ProductNumber");

        if (identity.Vendor.Equals("MSI", StringComparison.OrdinalIgnoreCase) ||
            identity.BoardManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
            Add(targets, "MSI — поиск по модели платы", "msi.com", First(identity.BoardProduct, model), "BoardProduct");

        foreach (var gpu in devices.Where(d =>
                     d.Manufacturer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                     d.DriverProvider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                     d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            var hardwareId = gpu.HardwareIds.FirstOrDefault() ?? gpu.InstanceId;
            Add(targets, "NVIDIA — поиск драйвера видеокарты", "nvidia.com",
                First(gpu.Name, hardwareId), "GPU/HardwareID");
        }

        foreach (var device in devices)
        {
            var provider = device.DriverProvider;
            var domain = provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel.com"
                : provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "amd.com"
                : provider.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ? "realtek.com"
                : "";
            var id = device.HardwareIds.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(id))
                Add(targets, provider + " — поиск по Hardware ID", domain, id, "HardwareID");
        }

        return targets
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

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

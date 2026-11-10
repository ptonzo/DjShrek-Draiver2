using System.Web;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Builds manufacturer-specific catalog entry points from collected OEM identity and PnP IDs.
/// Search links are discovery targets, not proof that a driver is compatible.
/// </summary>
public sealed class OemDriverCatalogAdapter
{
    public IReadOnlyList<OemCatalogTarget> BuildTargets(
        OemDeviceIdentity identity,
        IEnumerable<Models.DeviceInfo> devices)
    {
        var targets = new List<OemCatalogTarget>();
        var model = First(identity.SearchModel, identity.Model, identity.ProductName, identity.ProductNumber);
        var vendor = identity.Vendor;

        if (vendor.Equals("Acer", StringComparison.OrdinalIgnoreCase))
        {
            Add(targets, "Acer Support", "https://www.acer.com/support",
                First(identity.ProductNumber, model), "ProductNumber");
            Add(targets, "Acer Drivers", "https://www.acer.com/us-en/support/drivers-and-manuals",
                model, "Model");
        }

        if (vendor.Equals("MSI", StringComparison.OrdinalIgnoreCase) ||
            identity.BoardManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
        {
            Add(targets, "MSI Support", "https://www.msi.com/support",
                First(identity.BoardProduct, model), "BoardProduct");
        }

        if (vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            devices.Any(d => d.Manufacturer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                             d.DriverProvider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var gpu in devices.Where(d =>
                         d.Manufacturer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                         d.DriverProvider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
            {
                var hardwareId = gpu.HardwareIds.FirstOrDefault() ?? gpu.InstanceId;
                Add(targets, "NVIDIA Driver Search", "https://www.nvidia.com/Download/index.aspx",
                    First(gpu.Name, hardwareId), "GPU/HardwareID");
            }
        }

        foreach (var device in devices)
        {
            var id = device.HardwareIds.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var provider = device.DriverProvider;
            var baseUrl = provider.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                ? "https://www.intel.com/content/www/us/en/support/detect.html"
                : provider.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    ? "https://www.amd.com/en/support/download/drivers.html"
                    : "";
            if (!string.IsNullOrEmpty(baseUrl))
                Add(targets, provider + " Driver Support", baseUrl, id, "HardwareID");
        }

        return targets
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    private static void Add(List<OemCatalogTarget> list, string name, string baseUrl, string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var separator = baseUrl.Contains('?') ? '&' : '?';
        var url = baseUrl + separator + "q=" + Uri.EscapeDataString(query);
        list.Add(new OemCatalogTarget(name, url, key, query));
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
}

public sealed record OemCatalogTarget(string Name, string Url, string MatchKey, string Query);

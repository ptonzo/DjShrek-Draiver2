using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Adds an explicit OEM/component-vendor support route when catalog discovery
/// produced no result hosted by that device vendor. A portal is never a package
/// candidate and never establishes driver compatibility.
/// </summary>
public sealed class DriverVendorPortalFallbackService
{
    private readonly DriverVendorCatalogRegistry _registry = new();

    public IReadOnlyList<DriverUpdateCandidate> CreateMissingPortals(
        IEnumerable<DeviceInfo> devices,
        IEnumerable<DriverUpdateCandidate> discoveredCandidates)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(discoveredCandidates);

        var candidates = discoveredCandidates.Where(x => x is not null).ToArray();
        var output = new List<DriverUpdateCandidate>();

        foreach (var device in devices.Where(x => x is not null))
        {
            var vendor = (device.HardwareIds ?? Array.Empty<string>())
                .Select(id => _registry.Resolve(id, device.Manufacturer, device.DriverProvider))
                .FirstOrDefault(x => x is not null)
                ?? _registry.Resolve(null, device.Manufacturer, device.DriverProvider);
            if (vendor is null || !Uri.TryCreate(vendor.SupportUrl, UriKind.Absolute, out var supportUri) ||
                supportUri.Scheme != Uri.UriSchemeHttps)
                continue;

            var alreadyHasVendorRoute = candidates.Any(candidate =>
                SameDevice(candidate, device) &&
                (IsWithinDomain(candidate.SourceUrl, vendor.SearchDomain) ||
                 IsWithinDomain(candidate.PackageUrl, vendor.SearchDomain)));
            if (alreadyHasVendorRoute) continue;

            var hardwareId = (device.HardwareIds ?? Array.Empty<string>())
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? "";
            var identity = !string.IsNullOrWhiteSpace(device.InstanceId)
                ? device.InstanceId
                : hardwareId + "|" + device.Name + "|" + device.Manufacturer;

            output.Add(new DriverUpdateCandidate
            {
                UpdateId = "vendor-portal:" + vendor.Id + ":" + identity,
                Title = vendor.DisplayName + " — официальный портал поддержки: " + device.Name,
                Manufacturer = vendor.DisplayName,
                Model = device.Name,
                Provider = vendor.DisplayName + " Support",
                DeviceClass = device.Class,
                HardwareId = hardwareId,
                InstanceId = device.InstanceId,
                MatchType = "OEM/component support portal",
                DeviceName = device.Name,
                CurrentVersion = device.DriverVersion,
                Compatibility = "Определён производитель устройства; конкретный драйвер и совместимость пакета не получены",
                AvailableVersion = "Не получена",
                VersionComparison = "Сравнение невозможно: каталог не вернул конкретный пакет",
                Description = "Официальный портал производителя для ручного выбора модели и ОС. Это ссылка поддержки, а не найденный драйвер.",
                Evidence = "PnP Hardware ID / Manufacturer / Driver Provider; маршрутизация через встроенный реестр производителей",
                CandidateScope = "SupportPortal",
                EvidenceLevel = "Официальный портал производителя; конкретный пакет не получен",
                SourceType = "OfficialManufacturer",
                SourceTrust = "OfficialSupportPortal",
                SourceUrl = supportUri.AbsoluteUri,
                PackageStatus = "Откройте портал и выберите точную модель; пакет не загружен"
            });
        }

        return output;
    }

    private static bool SameDevice(DriverUpdateCandidate candidate, DeviceInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.InstanceId))
            return string.Equals(candidate.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase);
        var ids = (device.HardwareIds ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(candidate.HardwareId) && ids.Contains(candidate.HardwareId);
    }

    private static bool IsWithinDomain(string? value, string domain)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}

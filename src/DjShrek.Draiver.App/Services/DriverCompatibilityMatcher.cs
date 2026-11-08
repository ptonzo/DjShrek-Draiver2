using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Matches Windows Update metadata to enumerated Plug and Play devices.
/// Only complete, case-insensitive hardware/compatible ID equality is accepted.
/// </summary>
public static class DriverCompatibilityMatcher
{
    public static DeviceInfo? FindExactMatch(IEnumerable<DeviceInfo> devices, string? candidateHardwareIds)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (string.IsNullOrWhiteSpace(candidateHardwareIds)) return null;

        var candidateIds = candidateHardwareIds
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (candidateIds.Count == 0) return null;

        return devices.FirstOrDefault(device =>
            device.HardwareIds.Concat(device.CompatibleIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(Normalize)
                .Any(candidateIds.Contains));
    }

    private static string Normalize(string value) => value.Trim().Trim('"');
}

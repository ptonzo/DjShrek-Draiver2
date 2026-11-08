using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Matches Windows Update metadata to enumerated Plug and Play devices.
/// Only complete, case-insensitive hardware/compatible ID equality is accepted.
/// Ambiguous matches are rejected so a package is never silently assigned to
/// an arbitrary device when the same compatible ID is shared by several devices.
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

        var matches = devices
            .Where(device => device.HardwareIds.Concat(device.CompatibleIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(Normalize)
                .Any(candidateIds.Contains))
            .Take(2)
            .ToArray();

        // A shared compatible ID is not sufficient to identify one physical device.
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Normalize(string value) => value.Trim().Trim('"');
}

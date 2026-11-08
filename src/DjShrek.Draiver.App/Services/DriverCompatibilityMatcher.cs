using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Associates an internet search hit with a Plug and Play device.
/// Only complete, case-insensitive identifier equality is accepted.
/// Ambiguous matches are rejected rather than assigned to an arbitrary device.
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
            .Where(device => (device.HardwareIds ?? Array.Empty<string>())
                .Concat(device.CompatibleIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(Normalize)
                .Any(candidateIds.Contains))
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Normalize(string value) => value.Trim().Trim('"');
}

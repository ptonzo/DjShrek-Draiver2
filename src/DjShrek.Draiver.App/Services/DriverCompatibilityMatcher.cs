using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Associates a catalog hit with one exact Plug and Play device.
/// An InstanceId is preferred; legacy records fall back to unique complete ID equality.
/// Ambiguous matches are rejected.
/// </summary>
public static class DriverCompatibilityMatcher
{
    public static DeviceInfo? FindExactMatch(
        IEnumerable<DeviceInfo> devices, string? candidateHardwareIds, string? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (string.IsNullOrWhiteSpace(candidateHardwareIds)) return null;

        var candidateIds = candidateHardwareIds
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateIds.Count == 0) return null;

        var snapshot = devices.Where(device => device is not null).ToArray();
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            var exactInstance = snapshot.Where(device =>
                string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (exactInstance.Length != 1) return null;
            return HasIdentifier(exactInstance[0], candidateIds) ? exactInstance[0] : null;
        }

        var matches = snapshot.Where(device => HasIdentifier(device, candidateIds)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool HasIdentifier(DeviceInfo device, HashSet<string> candidateIds) =>
        (device.HardwareIds ?? Array.Empty<string>())
        .Concat(device.CompatibleIds ?? Array.Empty<string>())
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(Normalize)
        .Any(candidateIds.Contains);

    private static string Normalize(string value) => value.Trim().Trim('"');
}

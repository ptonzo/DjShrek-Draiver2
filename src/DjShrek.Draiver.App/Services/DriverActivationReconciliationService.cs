using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Compares the requested package with the currently enumerated PnP driver.
/// This is an observation-only operation and does not alter devices or packages.
/// </summary>
public sealed class DriverActivationReconciliationService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DriverActivationReconciliationService(string? storageRoot = null)
    {
        var root = Path.GetFullPath(storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DjShrek-Draiver"));
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "driver-activation-reconciliation.json");
    }

    public async Task<DriverActivationReconciliation> ReconcileAsync(
        string instanceId, string expectedProvider, string expectedVersion, string expectedInfName,
        IReadOnlyCollection<DeviceInfo> currentDevices, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Не указан Instance ID устройства.", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(currentDevices);
        var actual = currentDevices.FirstOrDefault(d =>
            string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

        var result = new DriverActivationReconciliation
        {
            DeviceInstanceId = instanceId,
            ExpectedProvider = expectedProvider ?? "",
            ExpectedVersion = expectedVersion ?? "",
            ExpectedInfName = expectedInfName ?? ""
        };

        if (actual is null)
        {
            result.Outcome = "Устройство не обнаружено";
            result.Explanation = "Устройство отсутствует в текущем PnP-снимке. Нельзя подтвердить активацию; оно может быть отключено или временно недоступно.";
            result.RequiresRestartAssessment = true;
        }
        else
        {
            result.DeviceName = actual.Name;
            result.ActualProvider = actual.DriverProvider;
            result.ActualVersion = actual.DriverVersion;
            result.ActualInfName = actual.InfName;
            result.DeviceStatus = actual.Status;
            result.ProblemCode = actual.Problem;
            var providerKnown = !string.IsNullOrWhiteSpace(expectedProvider) && !string.IsNullOrWhiteSpace(actual.DriverProvider);
            var versionKnown = !string.IsNullOrWhiteSpace(expectedVersion) && !string.IsNullOrWhiteSpace(actual.DriverVersion);
            var infKnown = !string.IsNullOrWhiteSpace(expectedInfName) && !string.IsNullOrWhiteSpace(actual.InfName);
            var providerMatches = !providerKnown || Normalize(expectedProvider) == Normalize(actual.DriverProvider);
            var versionMatches = !versionKnown || VersionsEqual(expectedVersion, actual.DriverVersion);
            var infMatches = !infKnown || string.Equals(Path.GetFileName(expectedInfName), Path.GetFileName(actual.InfName), StringComparison.OrdinalIgnoreCase);
            var deviceOk = string.IsNullOrWhiteSpace(actual.Problem) || actual.Problem == "0";
            deviceOk &= string.IsNullOrWhiteSpace(actual.Status) || actual.Status.Equals("OK", StringComparison.OrdinalIgnoreCase);

            if (!providerKnown && !versionKnown && !infKnown)
            {
                result.Outcome = "Недостаточно данных для сверки";
                result.Explanation = "В выбранном пакете или инвентаризации отсутствуют поля для надёжного сравнения.";
            }
            else if (!deviceOk)
            {
                result.Outcome = "Драйвер не подтверждён: устройство сообщает об ошибке";
                result.Explanation = $"Текущее состояние устройства: {actual.Status}; код PnP: {actual.Problem}.";
            }
            else if (providerMatches && versionMatches && infMatches)
            {
                result.Outcome = providerKnown && versionKnown && infKnown
                    ? "Активация подтверждена по поставщику, версии и INF"
                    : "Частичное совпадение: часть полей отсутствует";
                result.Explanation = "Текущий PnP-инвентарь соответствует доступным параметрам целевого пакета. Перезагрузка всё ещё может потребоваться.";
            }
            else
            {
                result.Outcome = "Активный драйвер не совпадает с целевым пакетом";
                var mismatches = new List<string>();
                if (!providerMatches) mismatches.Add("поставщик");
                if (!versionMatches) mismatches.Add("версия");
                if (!infMatches) mismatches.Add("INF");
                result.Explanation = "Не совпадают поля: " + string.Join(", ", mismatches) +
                    ". Возможно, Windows оставила предыдущий драйвер или выбрала другой совместимый пакет.";
            }
            result.RequiresRestartAssessment = true;
        }

        result.CapturedUtc = DateTime.UtcNow;
        await StoreAsync(result, token);
        return result;
    }

    public async Task<IReadOnlyList<DriverActivationReconciliation>> GetHistoryAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(_path)) return Array.Empty<DriverActivationReconciliation>();
            try
            {
                return (JsonSerializer.Deserialize<DriverActivationHistory>(await File.ReadAllTextAsync(_path, token), Options)
                    ?? new DriverActivationHistory()).Records.OrderByDescending(x => x.CapturedUtc).ToArray();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("История сверки активации повреждена; исходный файл сохранён.", ex);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task StoreAsync(DriverActivationReconciliation record, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var history = new DriverActivationHistory();
            if (File.Exists(_path))
            {
                try { history = JsonSerializer.Deserialize<DriverActivationHistory>(await File.ReadAllTextAsync(_path, token), Options) ?? new(); }
                catch (JsonException ex) { throw new InvalidDataException("История сверки активации повреждена; запись не сохранена.", ex); }
            }
            history.Records.Add(record);
            history.Records = history.Records.OrderByDescending(x => x.CapturedUtc).Take(1000).ToList();
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(history, Options), token);
                File.Move(temp, _path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _gate.Release(); }
    }

    private static string Normalize(string value) => value.Trim().Trim('"').ToUpperInvariant();
    private static bool VersionsEqual(string expected, string actual)
    {
        var left = expected.Trim();
        var right = actual.Trim();
        if (Version.TryParse(left, out var lv) && Version.TryParse(right, out var rv)) return lv == rv;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

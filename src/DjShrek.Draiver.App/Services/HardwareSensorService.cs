using LibreHardwareMonitor.Hardware;

namespace DjShrek.Draiver.App.Services;

public sealed record HardwareMetrics(string CpuTemperature, string GpuTemperature,
    string GpuLoad, string GpuName, string Note);

public sealed class HardwareSensorService
{
    public HardwareMetrics ReadMetrics()
    {
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        try
        {
            computer.Open();
            var cpuTemperatures = new List<float>();
            var gpuTemperatures = new List<float>();
            var gpuLoads = new List<float>();
            var gpuName = "";
            foreach (var hardware in computer.Hardware)
                ReadHardwareSafely(hardware, cpuTemperatures, gpuTemperatures, gpuLoads, ref gpuName);

            var cpu = cpuTemperatures.Where(x => x >= 0 && x < 120).DefaultIfEmpty(-1).Max();
            var gpu = gpuTemperatures.Where(x => x >= 0 && x < 120).DefaultIfEmpty(-1).Max();
            var load = gpuLoads.Where(x => x >= 0 && x <= 100).DefaultIfEmpty(-1).Max();
            return new HardwareMetrics(
                cpu >= 0 ? $"{cpu:0} °C" : "Нет данных",
                gpu >= 0 ? $"{gpu:0} °C" : "Нет данных",
                load >= 0 ? $"{load:0}%" : "Н/Д",
                gpuName,
                "Показания зависят от модели оборудования, драйверов и доступности датчиков.");
        }
        catch (Exception ex)
        {
            return new HardwareMetrics("Нет данных", "Нет данных", "Н/Д", "",
                "Аппаратные датчики недоступны: " + ex.Message);
        }
        finally
        {
            try { computer.Close(); } catch { /* Sensor provider cleanup must not crash the UI. */ }
        }
    }

    private static void ReadHardwareSafely(IHardware hardware, List<float> cpuTemps,
        List<float> gpuTemps, List<float> gpuLoads, ref string gpuName)
    {
        try { ReadHardware(hardware, cpuTemps, gpuTemps, gpuLoads, ref gpuName); }
        catch
        {
            // Some motherboard/GPU sensor endpoints fail independently; continue with other devices.
        }
    }

    private static void ReadHardware(IHardware hardware, List<float> cpuTemps,
        List<float> gpuTemps, List<float> gpuLoads, ref string gpuName)
    {
        hardware.Update();
        var isGpu = hardware.HardwareType.ToString().StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
        if (isGpu && string.IsNullOrWhiteSpace(gpuName)) gpuName = hardware.Name;
        foreach (var sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue) continue;
            if (sensor.SensorType == SensorType.Temperature)
            {
                if (isGpu) gpuTemps.Add(sensor.Value.Value);
                else if (hardware.HardwareType == HardwareType.Cpu) cpuTemps.Add(sensor.Value.Value);
            }
            if (isGpu && sensor.SensorType == SensorType.Load) gpuLoads.Add(sensor.Value.Value);
        }
        foreach (var child in hardware.SubHardware)
            ReadHardware(child, cpuTemps, gpuTemps, gpuLoads, ref gpuName);
    }
}
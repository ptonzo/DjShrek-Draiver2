using LibreHardwareMonitor.Hardware;
namespace DjShrek.Draiver.App.Services;
public sealed class HardwareSensorService
{
 public (string Cpu,string Gpu,string Note) ReadTemperatures()
 {
  try {
   using var computer=new Computer { IsCpuEnabled=true, IsGpuEnabled=true };
   computer.Open();
   var readings=new List<(string Name,float Value)>();
   foreach(var hw in computer.Hardware) ReadHardware(hw,readings);
   var cpu=readings.FirstOrDefault(x=>x.Name.Contains("CPU Package",StringComparison.OrdinalIgnoreCase)||x.Name.Contains("Tctl",StringComparison.OrdinalIgnoreCase)||x.Name.Contains("Tdie",StringComparison.OrdinalIgnoreCase)||x.Name.Equals("CPU",StringComparison.OrdinalIgnoreCase));
   var gpu=readings.FirstOrDefault(x=>x.Name.Contains("GPU Core",StringComparison.OrdinalIgnoreCase)||x.Name.Equals("GPU",StringComparison.OrdinalIgnoreCase));
   return (cpu.Value>0?$"{cpu.Value:0} °C":"Нет данных",gpu.Value>0?$"{gpu.Value:0} °C":"Нет данных","Данные аппаратных датчиков; поддержка зависит от модели и драйвера.");
  } catch(Exception ex) { return ("Нет данных","Нет данных","Датчики недоступны: "+ex.Message); }
 }
 private static void ReadHardware(IHardware hw,List<(string Name,float Value)> list)
 {
  hw.Update();
  foreach(var s in hw.Sensors) if(s.SensorType==SensorType.Temperature&&s.Value.HasValue) list.Add((s.Name,s.Value.Value));
  foreach(var child in hw.SubHardware) ReadHardware(child,list);
 }
}
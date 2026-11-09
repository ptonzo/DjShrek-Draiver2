namespace DjShrek.Draiver.App.Models;
public sealed class DeviceInfo
{
 public string Name { get; set; } = "";
 public string Manufacturer { get; set; } = "";
 public string Class { get; set; } = "";
 public string Status { get; set; } = "";
 public string Problem { get; set; } = "";
 public string ProblemDescription { get; set; } = "";
 public string DriverProvider { get; set; } = "";
 public string DriverVersion { get; set; } = "";
 public string DriverDate { get; set; } = "";
 public string InfName { get; set; } = "";
 public string Signature { get; set; } = "";
 public string InstanceId { get; set; } = "";
 public string[] HardwareIds { get; set; } = Array.Empty<string>();
 public string[] CompatibleIds { get; set; } = Array.Empty<string>();
}
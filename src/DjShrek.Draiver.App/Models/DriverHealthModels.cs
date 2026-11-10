namespace DjShrek.Draiver.App.Models;
public sealed class DriverHealthFinding
{
 public string DeviceName { get; set; } = ""; public string InstanceId { get; set; } = "";
 public string Provider { get; set; } = ""; public string DriverVersion { get; set; } = "";
 public string InfName { get; set; } = ""; public string Severity { get; set; } = "Информация";
 public string Category { get; set; } = ""; public string Description { get; set; } = "";
 public string Recommendation { get; set; } = "";
 public string Fingerprint => InstanceId + "|" + Category + "|" + Description;
}
public sealed class DriverHealthSnapshot
{
 public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
 public int TotalDevices { get; set; } public int HealthyDevices { get; set; }
 public int ProblemDevices { get; set; } public int MissingDriverDevices { get; set; }
 public int UnsignedDriverDevices { get; set; }
 public List<DriverHealthFinding> Findings { get; set; } = new();
 public List<string> NewIssues { get; set; } = new(); public List<string> ResolvedIssues { get; set; } = new();
}
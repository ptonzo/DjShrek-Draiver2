namespace DjShrek.Draiver.App.Models;

public sealed class DriverActivationReconciliation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string DeviceName { get; set; } = "";
    public string DeviceInstanceId { get; set; } = "";
    public string ExpectedProvider { get; set; } = "";
    public string ExpectedVersion { get; set; } = "";
    public string ExpectedInfName { get; set; } = "";
    public string ActualProvider { get; set; } = "";
    public string ActualVersion { get; set; } = "";
    public string ActualInfName { get; set; } = "";
    public string DeviceStatus { get; set; } = "";
    public string ProblemCode { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Explanation { get; set; } = "";
    public bool RequiresRestartAssessment { get; set; }
}

public sealed class DriverActivationHistory
{
    public List<DriverActivationReconciliation> Records { get; set; } = new();
}

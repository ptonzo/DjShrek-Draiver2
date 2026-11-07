namespace DjShrek.Draiver.App.Models;
public sealed class DriverUpdate
{
 public string Title { get; set; } = "";
 public string Manufacturer { get; set; } = "";
 public string Model { get; set; } = "";
 public string Provider { get; set; } = "";
 public string DriverClass { get; set; } = "";
 public string DriverDate { get; set; } = "";
 public string UpdateId { get; set; } = "";
 public string Description { get; set; } = "";
 public bool IsSelected { get; set; }
}

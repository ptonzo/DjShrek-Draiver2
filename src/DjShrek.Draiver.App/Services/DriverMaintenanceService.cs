using System.Diagnostics;
namespace DjShrek.Draiver.App.Services;
public sealed class DriverMaintenanceService
{
 public string ExportDrivers(string destination)
 {
  Directory.CreateDirectory(destination);
  return RunElevated("pnputil.exe",$"/export-driver * \"{destination}\"");
 }
 public string InstallInf(string infPath)
 {
  if(!File.Exists(infPath)||!string.Equals(Path.GetExtension(infPath),".inf",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Выберите существующий INF-файл драйвера.");
  return RunElevated("pnputil.exe",$"/add-driver \"{infPath}\" /install");
 }
 private static string RunElevated(string file,string arguments)
 {
  var start=new ProcessStartInfo(file,arguments){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Normal};
  using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить системную утилиту.");
  process.WaitForExit(); return $"pnputil завершился с кодом {process.ExitCode}. Подробности доступны в окне Windows.";
 }
}
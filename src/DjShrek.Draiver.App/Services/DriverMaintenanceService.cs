using System.Diagnostics;
using System.IO;
using System.Text;
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
 public string ListDriverPackages()
 {
  var start=new ProcessStartInfo("pnputil.exe","/enum-drivers"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить pnputil.");
  var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();process.WaitForExit();
  if(process.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)?$"pnputil завершился с кодом {process.ExitCode}.":error);
  return string.IsNullOrWhiteSpace(output)?"Windows не вернула список пакетов драйверов.":output;
 }
 private static string RunElevated(string file,string arguments)
 {
  var start=new ProcessStartInfo(file,arguments){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Normal};
  using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить системную утилиту.");
  process.WaitForExit();
  if(process.ExitCode!=0)throw new InvalidOperationException($"Системная утилита завершилась с кодом {process.ExitCode}. Проверьте права администратора и журнал Windows.");
  return $"Операция завершена. Код возврата pnputil: {process.ExitCode}.";
 }
}
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DjShrek.Draiver.App.Models;
namespace DjShrek.Draiver.App.Services;
public sealed class WindowsUpdateDriverService
{
 private const string SearchScript = @"
$ErrorActionPreference='Stop'
$session=New-Object -ComObject Microsoft.Update.Session
$searcher=$session.CreateUpdateSearcher()
$result=$searcher.Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
$items=@()
for($i=0;$i -lt $result.Updates.Count;$i++){
 $u=$result.Updates.Item($i)
 $items += [pscustomobject]@{
  Title=$u.Title
  Manufacturer=$u.DriverManufacturer
  Model=$u.DriverModel
  Version=$u.DriverVerVersion
  DriverDate=if($u.DriverVerDate){$u.DriverVerDate.ToString('yyyy-MM-dd')}else{''}
  UpdateId=$u.Identity.UpdateID
  Description=$u.Description
 }
}
ConvertTo-Json -InputObject @($items) -Depth 4 -Compress
";
 public async Task<IReadOnlyList<DriverUpdate>> SearchAsync(CancellationToken cancellationToken=default)
 {
  var output=await RunPowerShellAsync(SearchScript,cancellationToken);
  if(string.IsNullOrWhiteSpace(output))return Array.Empty<DriverUpdate>();
  using var doc=JsonDocument.Parse(output);var root=doc.RootElement;
  var rows=root.ValueKind==JsonValueKind.Array?root.EnumerateArray().ToArray():new[]{root};
  return rows.Select(e=>new DriverUpdate{
   Title=Read(e,"Title"),Manufacturer=Read(e,"Manufacturer"),Model=Read(e,"Model"),
   Version=Read(e,"Version"),DriverDate=Read(e,"DriverDate"),UpdateId=Read(e,"UpdateId"),Description=Read(e,"Description")
  }).Where(x=>!string.IsNullOrWhiteSpace(x.UpdateId)).ToArray();
 }
 public string InstallAvailable()
 {
  var logPath=Path.Combine(Path.GetTempPath(),"DjShrek-WindowsUpdate-"+Guid.NewGuid().ToString("N")+".log");
  var escaped=logPath.Replace("'","''");
  var script=$@"
$ErrorActionPreference='Stop'
try {{
 $session=New-Object -ComObject Microsoft.Update.Session
 $searcher=$session.CreateUpdateSearcher()
 $result=$searcher.Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
 $collection=New-Object -ComObject Microsoft.Update.UpdateColl
 for($i=0;$i -lt $result.Updates.Count;$i++) {{
  $u=$result.Updates.Item($i)
  if(-not $u.EulaAccepted){{$u.AcceptEula()}}
  [void]$collection.Add($u)
 }}
 if($collection.Count -eq 0){{'Новых драйверов для установки не найдено.' | Set-Content -LiteralPath '{escaped}' -Encoding UTF8; exit 0}}
 $downloader=$session.CreateUpdateDownloader()
 $downloader.Updates=$collection
 $downloadResult=$downloader.Download()
 $installer=$session.CreateUpdateInstaller()
 $installer.Updates=$collection
 $installResult=$installer.Install()
 $lines=@('Найдено пакетов: '+$collection.Count,'Код загрузки: '+$downloadResult.ResultCode,'Код установки: '+$installResult.ResultCode,'Требуется перезагрузка: '+$installResult.RebootRequired)
 for($i=0;$i -lt $collection.Count;$i++){{$lines+=($collection.Item($i).Title+' — '+$installResult.GetUpdateResult($i).ResultCode)}}
 $lines | Set-Content -LiteralPath '{escaped}' -Encoding UTF8
}} catch {{ ($_ | Out-String) | Set-Content -LiteralPath '{escaped}' -Encoding UTF8; exit 1 }}
";
  var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
  var start=new ProcessStartInfo("powershell.exe",$"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
  try{
   using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить Windows Update Agent.");
   process.WaitForExit();
   var log=File.Exists(logPath)?File.ReadAllText(logPath):"Утилита завершилась без диагностического отчёта.";
   if(process.ExitCode!=0)throw new InvalidOperationException(log);
   return log.Trim();
  }finally{try{if(File.Exists(logPath))File.Delete(logPath);}catch{}}
 }
 private static string Read(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind!=JsonValueKind.Null?v.ToString():"";
 private static async Task<string> RunPowerShellAsync(string script,CancellationToken token)
 {
  var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
  var start=new ProcessStartInfo("powershell.exe",$"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
  using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить Windows PowerShell.");
  var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
  await process.WaitForExitAsync(token);var output=await stdout;var error=await stderr;
  if(process.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)?"Windows Update Agent не вернул список драйверов.":error.Trim());
  return output.Trim();
 }
}

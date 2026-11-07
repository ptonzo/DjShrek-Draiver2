using System.Diagnostics;
using System.Text.Json;
using DjShrek.Draiver.App.Models;
namespace DjShrek.Draiver.App.Services;
public sealed class DeviceInventoryService
{
 public async Task<IReadOnlyList<DeviceInfo>> ScanAsync(CancellationToken cancellationToken = default)
 {
  const string script = "Get-CimInstance Win32_PnPEntity | Select-Object @{n='Name';e={$_.Name}},@{n='Class';e={$_.PNPClass}},@{n='Status';e={$_.Status}},@{n='InstanceId';e={$_.PNPDeviceID}},@{n='Problem';e={if ($_.ConfigManagerErrorCode -eq 0) {'Нет'} else {$_.ConfigManagerErrorCode}}} | ConvertTo-Json -Depth 3";
  var output = await RunPowerShellAsync(script, cancellationToken);
  using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
  var root = document.RootElement;
  var devices = new List<DeviceInfo>();
  if (root.ValueKind == JsonValueKind.Array) foreach (var item in root.EnumerateArray()) devices.Add(Parse(item));
  else if (root.ValueKind == JsonValueKind.Object) devices.Add(Parse(root));
  return devices.OrderBy(d => d.Name).ToArray();
 }
 public async Task<(string Cpu,string Memory,string Os)> GetSystemSummaryAsync(CancellationToken cancellationToken = default)
 {
  const string script = "$cpu=(Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average; $os=Get-CimInstance Win32_OperatingSystem; [pscustomobject]@{Cpu=[math]::Round($cpu); Memory=[math]::Round((($os.TotalVisibleMemorySize-$os.FreePhysicalMemory)/$os.TotalVisibleMemorySize)*100); Os=$os.Caption} | ConvertTo-Json -Compress";
  var output = await RunPowerShellAsync(script,cancellationToken);
  using var doc=JsonDocument.Parse(output); var root=doc.RootElement;
  return (GetString(root,"Cpu")+"%",GetString(root,"Memory")+"%",GetString(root,"Os"));
 }
 private static DeviceInfo Parse(JsonElement e)=>new(){Name=GetString(e,"Name"),Class=GetString(e,"Class"),Status=GetString(e,"Status"),InstanceId=GetString(e,"InstanceId"),Problem=GetString(e,"Problem")};
 private static string GetString(JsonElement e,string name)
 {
  if(!e.TryGetProperty(name,out var value)||value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)return "";
  return value.ValueKind==JsonValueKind.String?value.GetString()??"":value.ToString();
 }
 private static async Task<string> RunPowerShellAsync(string script,CancellationToken token)
 {
  var encoded=Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
  var start=new ProcessStartInfo{FileName="powershell.exe",Arguments=$"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
  using var process=new Process{StartInfo=start}; process.Start();
  var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
  await process.WaitForExitAsync(token); var result=await stdout; var error=await stderr;
  if(process.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)?"Не удалось получить сведения Windows.":error.Trim());
  return result.Trim();
 }
}
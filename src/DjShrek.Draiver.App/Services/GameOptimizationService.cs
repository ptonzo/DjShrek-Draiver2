using Microsoft.Win32;
using System.Diagnostics;
using System.Text.RegularExpressions;
namespace DjShrek.Draiver.App.Services;
public sealed class GameOptimizationService
{
 const string StatePath=@"Software\DjShrek-Draiver\GameProfile";
 const string GameBarPath=@"Software\Microsoft\GameBar";
 public string Enable()
 {
  using var state=Registry.CurrentUser.CreateSubKey(StatePath)??throw new InvalidOperationException("Не удалось сохранить настройки.");
  if(state.GetValue("Saved") is null) {
   state.SetValue("PreviousPowerPlan",GetPlan());
   using var old=Registry.CurrentUser.OpenSubKey(GameBarPath);
   Save(state,"PreviousAuto",old?.GetValue("AutoGameModeEnabled"));
   Save(state,"PreviousAllow",old?.GetValue("AllowAutoGameMode"));
   state.SetValue("Saved",1);
  }
  using(var key=Registry.CurrentUser.CreateSubKey(GameBarPath)) {
   key?.SetValue("AutoGameModeEnabled",1,RegistryValueKind.DWord);
   key?.SetValue("AllowAutoGameMode",1,RegistryValueKind.DWord);
  }
  RunPowerCfg("/setactive SCHEME_MIN");
  return "Игровой профиль включён: Windows Game Mode и высокая производительность. Службы и процессы не отключались.";
 }
 public string Restore()
 {
  using var state=Registry.CurrentUser.OpenSubKey(StatePath,true);
  if(state?.GetValue("Saved") is null) return "Сохранённого профиля нет.";
  using(var key=Registry.CurrentUser.CreateSubKey(GameBarPath)) {
   RestoreValue(key,"AutoGameModeEnabled",state,"PreviousAuto");
   RestoreValue(key,"AllowAutoGameMode",state,"PreviousAllow");
  }
  var plan=state.GetValue("PreviousPowerPlan") as string;
  if(!string.IsNullOrWhiteSpace(plan)&&Regex.IsMatch(plan,"^[0-9a-fA-F-]{36}$")) RunPowerCfg("/setactive "+plan);
  foreach(var n in new[]{"Saved","PreviousPowerPlan","PreviousAuto","PreviousAllow"}) state.DeleteValue(n,false);
  return "Предыдущая схема питания и параметры Game Mode восстановлены.";
 }
 static void Save(RegistryKey key,string name,object? value) {
  if(value is int i) key.SetValue(name,i,RegistryValueKind.DWord);
  else if(value is string s) key.SetValue(name,s,RegistryValueKind.String);
  else key.SetValue(name,"__MISSING__",RegistryValueKind.String);
 }
 static void RestoreValue(RegistryKey? key,string name,RegistryKey state,string saved) {
  if(key is null)return; var v=state.GetValue(saved);
  if(v is string s&&s=="__MISSING__")key.DeleteValue(name,false);
  else if(v is not null)key.SetValue(name,v);
 }
 static string GetPlan() {
  var output=RunPowerCfg("/getactivescheme");
  var m=Regex.Match(output,"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
  if(!m.Success)throw new InvalidOperationException("Не удалось определить текущую схему питания.");
  return m.Value;
 }
 static string RunPowerCfg(string args) {
  using var p=Process.Start(new ProcessStartInfo("powercfg.exe",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true})??throw new InvalidOperationException("Не удалось запустить powercfg.");
  var output=p.StandardOutput.ReadToEnd();var error=p.StandardError.ReadToEnd();p.WaitForExit();
  if(p.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)?output:error);
  return output;
 }
}
using System.Text.Json;
using DjShrek.Draiver.App.Models;
namespace DjShrek.Draiver.App.Services;
public sealed class DriverHealthAnalysisService
{
 private static readonly JsonSerializerOptions Options = new() { WriteIndented=true, PropertyNameCaseInsensitive=true };
 private readonly string _path; private readonly SemaphoreSlim _gate=new(1,1);
 public DriverHealthAnalysisService(string? root=null) { var folder=Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DjShrek-Draiver")); Directory.CreateDirectory(folder); _path=Path.Combine(folder,"driver-health-history.json"); }
 public async Task<DriverHealthSnapshot> AnalyzeAndStoreAsync(IReadOnlyCollection<DeviceInfo> devices,CancellationToken token=default)
 {
  ArgumentNullException.ThrowIfNull(devices); var s=new DriverHealthSnapshot { TotalDevices=devices.Count };
  foreach(var d in devices) {
   token.ThrowIfCancellationRequested(); int.TryParse(d.Problem,out var code);
   bool problem=code!=0 || (!string.IsNullOrWhiteSpace(d.Status)&&!d.Status.Equals("OK",StringComparison.OrdinalIgnoreCase));
   bool missing=string.IsNullOrWhiteSpace(d.InfName)||string.IsNullOrWhiteSpace(d.DriverVersion);
   bool unsigned=d.Signature.Contains("не подпис",StringComparison.OrdinalIgnoreCase)||d.Signature.Contains("unsigned",StringComparison.OrdinalIgnoreCase);
   if(!problem&&!missing&&!unsigned){s.HealthyDevices++;continue;}
   if(problem){s.ProblemDevices++;s.Findings.Add(Make(d,code is 10 or 28 or 31 or 39 or 43 or 52?"Критично":"Предупреждение","Состояние Plug and Play",Explain(code),"Проверьте код в диспетчере устройств и пакет по Hardware ID. Не удаляйте текущий драйвер до подготовки замены."));}
   if(missing){s.MissingDriverDevices++;s.Findings.Add(Make(d,"Предупреждение","Данные драйвера","В инвентаризации отсутствует INF или версия; это может быть отсутствие драйвера либо неполные сведения Windows.","Сверьте Hardware ID и найдите пакет на официальном сайте производителя."));}
   if(unsigned){s.UnsignedDriverDevices++;s.Findings.Add(Make(d,"Критично","Подпись драйвера","Windows помечает драйвер как неподписанный.","Используйте подписанный пакет производителя; не устанавливайте случайные обновления."));}
  }
  await _gate.WaitAsync(token); try { var h=await Load(token); var prev=h.LastOrDefault()?.Findings.Select(x=>x.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase)??new(); var now=s.Findings.Select(x=>x.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase); s.NewIssues=s.Findings.Where(x=>!prev.Contains(x.Fingerprint)).Select(x=>x.DeviceName+": "+x.Category).Distinct().Take(100).ToList(); if(h.Count>0)s.ResolvedIssues=h.Last().Findings.Where(x=>!now.Contains(x.Fingerprint)).Select(x=>x.DeviceName+": "+x.Category).Distinct().Take(100).ToList(); h.Add(s); if(h.Count>100)h=h.OrderByDescending(x=>x.CapturedUtc).Take(100).OrderBy(x=>x.CapturedUtc).ToList(); await Save(h,token); } finally {_gate.Release();}
  return s;
 }
 public async Task<IReadOnlyList<DriverHealthSnapshot>> GetHistoryAsync(CancellationToken token=default){await _gate.WaitAsync(token);try{return (await Load(token)).OrderByDescending(x=>x.CapturedUtc).ToArray();}finally{_gate.Release();}}
 private async Task<List<DriverHealthSnapshot>> Load(CancellationToken t){if(!File.Exists(_path))return new();try{return JsonSerializer.Deserialize<List<DriverHealthSnapshot>>(await File.ReadAllTextAsync(_path,t),Options)??new();}catch(JsonException){throw new InvalidDataException("История состояния драйверов повреждена. Исходный файл сохранён; перезапись отменена.");}}
 private async Task Save(List<DriverHealthSnapshot> h,CancellationToken t){var tmp=_path+"."+Guid.NewGuid().ToString("N")+".tmp";try{await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(h,Options),t);File.Move(tmp,_path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
 private static DriverHealthFinding Make(DeviceInfo d,string severity,string category,string description,string recommendation)=>new(){DeviceName=d.Name,InstanceId=d.InstanceId,Provider=d.DriverProvider,DriverVersion=d.DriverVersion,InfName=d.InfName,Severity=severity,Category=category,Description=description,Recommendation=recommendation};
 private static string Explain(int c)=>c switch{1=>"Устройство настроено неправильно (код 1).",10=>"Устройство не может запуститься (код 10).",12=>"Недостаточно ресурсов (код 12).",14=>"Требуется перезагрузка (код 14).",18=>"Windows предлагает переустановить драйвер (код 18).",19=>"Ошибка конфигурации реестра устройства (код 19).",22=>"Устройство отключено (код 22).",24=>"Устройство отсутствует или работает неправильно (код 24).",28=>"Драйвер не установлен (код 28).",31=>"Windows не может загрузить драйвер (код 31).",32=>"Служба драйвера отключена (код 32).",37=>"Ошибка инициализации драйвера (код 37).",39=>"Windows не может загрузить драйвер (код 39).",43=>"Windows остановила устройство (код 43).",45=>"Устройство не подключено (код 45).",48=>"Запуск заблокирован политикой Windows (код 48).",52=>"Не подтверждена цифровая подпись (код 52).",_=>c>0?"Код ошибки Windows: "+c+".":"Windows сообщает о проблемном состоянии устройства."};
}
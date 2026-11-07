using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;
namespace DjShrek.Draiver.App;
public partial class MainWindow : Window
{
 private readonly DeviceInventoryService _inventory=new();
 private readonly DriverMaintenanceService _maintenance=new();
 private readonly WindowsUpdateDriverService _windowsUpdate=new();
 private readonly HardwareSensorService _sensors=new();
 private readonly GameOptimizationService _gameProfile=new();
 private readonly OemCatalogService _oemCatalog=new();
 private OemSupport? _detectedOem;
 private readonly ObservableCollection<DriverUpdate> _driverUpdates=new();
 private readonly ObservableCollection<DeviceInfo> _devices=new();
 private readonly DispatcherTimer _monitorTimer=new(){Interval=TimeSpan.FromSeconds(5)};
 private bool _scanRunning;
 public MainWindow()
 {
  InitializeComponent();DevicesGrid.ItemsSource=_devices;UpdatesGrid.ItemsSource=_driverUpdates;
  Loaded+=async(_,_)=>{await RefreshAllAsync();DetectOem();_monitorTimer.Tick+=async(_,_)=>await RefreshMetricsAsync();_monitorTimer.Start();};
  Closed+=(_,_)=>_monitorTimer.Stop();
 }
 private void ShowPanel(string title,string subtitle,string panel)
 {
  PageTitle.Text=title;PageSubtitle.Text=subtitle;
  DashboardPanel.Visibility=panel=="dashboard"?Visibility.Visible:Visibility.Collapsed;
  DevicesPanel.Visibility=panel=="devices"?Visibility.Visible:Visibility.Collapsed;
  DriversPanel.Visibility=panel=="drivers"?Visibility.Visible:Visibility.Collapsed;
  DiagnosticsPanel.Visibility=panel=="diagnostics"?Visibility.Visible:Visibility.Collapsed;
 }
 private void Dashboard_Click(object sender,RoutedEventArgs e)=>ShowPanel("Обзор системы","Мониторинг и инструменты обслуживания","dashboard");
 private void Devices_Click(object sender,RoutedEventArgs e)=>ShowPanel("Устройства","Инвентаризация Plug and Play и экспорт отчёта","devices");
 private void Drivers_Click(object sender,RoutedEventArgs e)=>ShowPanel("Драйверы и резервная копия","Driver Store, резервное копирование и обновления Windows","drivers");
 private void Diagnostics_Click(object sender,RoutedEventArgs e)=>ShowPanel("Диагностика","Проверка состояния устройств без автоматического изменения системы","diagnostics");
 private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAllAsync();
 private async void Scan_Click(object sender,RoutedEventArgs e)=>await ScanDevicesAsync();
 private async Task RefreshMetricsAsync()
 {
  try{var summary=await _inventory.GetSystemSummaryAsync();CpuValue.Text=summary.Cpu;MemoryValue.Text=summary.Memory;OsValue.Text=summary.Os;ComputerValue.Text=summary.Computer;var t=await Task.Run(()=>_sensors.ReadTemperatures());CpuTempValue.Text=t.Cpu;GpuTempValue.Text=t.Gpu;TemperatureNote.Text=t.Note;}
  catch(Exception ex){StatusText.Text="Мониторинг: "+ex.Message;}
 }
 private async Task RefreshAllAsync()
 {
  if(_scanRunning)return;_scanRunning=true;
  try{
   StatusText.Text="Получение сведений о системе...";
   var summaryTask=_inventory.GetSystemSummaryAsync();var devicesTask=_inventory.ScanAsync();
   var summary=await summaryTask;var devices=await devicesTask;
   CpuValue.Text=summary.Cpu;MemoryValue.Text=summary.Memory;OsValue.Text=summary.Os;ComputerValue.Text=summary.Computer;ApplyDevices(devices);
   StatusText.Text=$"Обновлено: устройств {devices.Count}. Автообновление CPU/RAM — каждые 5 секунд.";
  }catch(Exception ex){StatusText.Text="Не удалось получить часть сведений.";MessageBox.Show(ex.Message,"Ошибка чтения Windows",MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{_scanRunning=false;}
 }
 private async Task ScanDevicesAsync()
 {
  if(_scanRunning)return;_scanRunning=true;
  try{StatusText.Text="Сканирование устройств...";var devices=await _inventory.ScanAsync();ApplyDevices(devices);StatusText.Text=$"Сканирование завершено. Найдено устройств: {devices.Count}.";ShowPanel("Устройства","Фактическая инвентаризация Plug and Play","devices");}
  catch(Exception ex){StatusText.Text="Ошибка сканирования.";MessageBox.Show(ex.Message,"Сканирование",MessageBoxButton.OK,MessageBoxImage.Error);}
  finally{_scanRunning=false;}
 }
 private void ApplyDevices(IReadOnlyList<DeviceInfo> devices)
 {
  _devices.Clear();foreach(var d in devices)_devices.Add(d);DeviceCount.Text=devices.Count.ToString(CultureInfo.InvariantCulture);
  var errors=devices.Count(d=>!string.IsNullOrWhiteSpace(d.Problem)&&d.Problem!="Нет"&&d.Problem!="0");
  HealthSummary.Text=errors==0?$"Проверено устройств: {devices.Count}. Ошибки конфигурации не обнаружены.":$"Проверено устройств: {devices.Count}. Устройств с кодом конфигурационной ошибки: {errors}. Откройте раздел «Устройства» и включите фильтр «Только с ошибками».";
  FilterDevices();
 }
 private void DeviceSearch_TextChanged(object sender,RoutedEventArgs e)=>FilterDevices();
 private void FilterDevices()
 {
  if(DevicesGrid==null)return;
  var query=DeviceSearch?.Text?.Trim()??"";var errorsOnly=ErrorsOnlyCheck?.IsChecked==true;
  DevicesGrid.ItemsSource=_devices.Where(d=>(!errorsOnly||(!string.IsNullOrWhiteSpace(d.Problem)&&d.Problem!="Нет"&&d.Problem!="0"))&&(query.Length==0||d.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||d.Manufacturer.Contains(query,StringComparison.OrdinalIgnoreCase)||d.Class.Contains(query,StringComparison.OrdinalIgnoreCase)||d.DriverProvider.Contains(query,StringComparison.OrdinalIgnoreCase)||d.DriverVersion.Contains(query,StringComparison.OrdinalIgnoreCase)||d.InfName.Contains(query,StringComparison.OrdinalIgnoreCase)||d.InstanceId.Contains(query,StringComparison.OrdinalIgnoreCase))).ToList();
 }
 private void ExportDevices_Click(object sender,RoutedEventArgs e)
 {
  var dialog=new SaveFileDialog{Title="Сохранить отчёт устройств",Filter="CSV (*.csv)|*.csv",FileName="DjShrek-device-report.csv",DefaultExt=".csv"};
  if(dialog.ShowDialog(this)!=true)return;
  try{
   static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
   var sb=new StringBuilder();sb.AppendLine("Устройство;Производитель;Класс;Состояние;Код ошибки;Поставщик драйвера;Версия драйвера;Дата драйвера;INF;Подпись;Instance ID");
   foreach(var d in _devices)sb.AppendLine(string.Join(";",new[]{d.Name,d.Manufacturer,d.Class,d.Status,d.Problem,d.DriverProvider,d.DriverVersion,d.DriverDate,d.InfName,d.Signature,d.InstanceId}.Select(Q)));
   File.WriteAllText(dialog.FileName,sb.ToString(),new UTF8Encoding(true));StatusText.Text="Отчёт сохранён: "+dialog.FileName;
  }catch(Exception ex){MessageBox.Show(ex.Message,"Экспорт CSV",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void Backup_Click(object sender,RoutedEventArgs e)
 {
  var dialog=new OpenFolderDialog{Title="Выберите папку для резервной копии драйверов",Multiselect=false};
  if(dialog.ShowDialog(this)!=true)return;
  if(MessageBox.Show($"Экспортировать пакеты драйверов в папку?\n{dialog.FolderName}","Подтверждение резервного копирования",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
  try{var result=_maintenance.ExportDrivers(dialog.FolderName);StatusText.Text=result;MessageBox.Show(result,"Резервное копирование",MessageBoxButton.OK,MessageBoxImage.Information);}
  catch(System.ComponentModel.Win32Exception){StatusText.Text="Операция отменена или не удалось получить права администратора.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Резервное копирование",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void RestoreDrivers_Click(object sender,RoutedEventArgs e)
 {
  var dialog=new OpenFolderDialog{Title="Выберите папку с ранее экспортированными драйверами",Multiselect=false};
  if(dialog.ShowDialog(this)!=true)return;
  try{
   var infCount=Directory.EnumerateFiles(dialog.FolderName,"*.inf",SearchOption.AllDirectories).Take(10001).Count();
   if(infCount==0){MessageBox.Show("В выбранной папке не найдены INF-файлы.","Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Information);return;}
   if(infCount>10000){MessageBox.Show("В папке слишком много INF-файлов. Выберите конкретную папку резервной копии.","Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
   if(MessageBox.Show($"Найдено INF-файлов: {infCount}. Передать пакеты Windows для установки?\n\n{dialog.FolderName}\n\nWindows выберет только совместимые пакеты.","Подтверждение восстановления",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
   var result=_maintenance.RestoreDrivers(dialog.FolderName);StatusText.Text=result;MessageBox.Show(result,"Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Information);
  }
  catch(System.ComponentModel.Win32Exception){StatusText.Text="Восстановление отменено в UAC.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void Install_Click(object sender,RoutedEventArgs e)
 {
  var dialog=new OpenFileDialog{Title="Выберите INF-файл драйвера",Filter="Файлы драйверов (*.inf)|*.inf",CheckFileExists=true};
  if(dialog.ShowDialog(this)!=true)return;
  if(MessageBox.Show($"Передать Windows пакет для установки?\n\n{dialog.FileName}\n\nПродолжайте только если доверяете источнику и проверили совместимость.","Подтверждение установки драйвера",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  try{var result=_maintenance.InstallInf(dialog.FileName);StatusText.Text=result;MessageBox.Show(result,"Установка драйвера",MessageBoxButton.OK,MessageBoxImage.Information);}
  catch(System.ComponentModel.Win32Exception){StatusText.Text="Установка отменена пользователем в UAC.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Установка драйвера",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private async void AutoUpdateDrivers_Click(object sender,RoutedEventArgs e)
 {
  if(_scanRunning)return;
  _scanRunning=true;
  try{
   ShowPanel("Драйверы и резервная копия","Автоматический поиск и установка совместимых драйверов","drivers");
   UpdatesCountText.Text="1/3: Сканирование оборудования...";
   StatusText.Text="Проверка устройств Windows...";
   var devices=await _inventory.ScanAsync();
   ApplyDevices(devices);
   var problemDevices=devices.Where(d=>!string.IsNullOrWhiteSpace(d.Problem)&&d.Problem!="Нет"&&d.Problem!="0").ToArray();
   UpdatesCountText.Text=$"Оборудование проверено: {devices.Count} устройств; с кодами ошибок: {problemDevices.Length}. 2/3: Поиск совместимых драйверов в Windows Update...";
   StatusText.Text="Поиск обновлений драйверов на серверах Microsoft...";
   var updates=await _windowsUpdate.SearchAsync();
   _driverUpdates.Clear();
   foreach(var item in updates){item.IsSelected=true;_driverUpdates.Add(item);}
   if(updates.Count==0){
    UpdatesCountText.Text="Новых совместимых драйверов через Windows Update не найдено. Это не означает, что у производителя нет более новых версий.";
    StatusText.Text=UpdatesCountText.Text;
    MessageBox.Show(UpdatesCountText.Text+"\n\nПроверьте также официальный сайт производителя устройства.", "Автоматический поиск драйверов",MessageBoxButton.OK,MessageBoxImage.Information);
    return;
   }
   var list=string.Join(Environment.NewLine,updates.Take(12).Select((u,i)=>$"• {u.Title}"));
   if(updates.Count>12)list+=Environment.NewLine+$"… и ещё {updates.Count-12} пак.";
   var prompt=$"Windows Update нашёл {updates.Count} совместимых обновлений драйверов.\n\n{list}\n\nПрограмма скачает и установит все найденные пакеты через Windows Update Agent. Потребуется подтверждение UAC. Возможна перезагрузка. Продолжить?";
   if(MessageBox.Show(prompt,"Подтверждение автоматической установки",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes){
    UpdatesCountText.Text=$"Найдено обновлений: {updates.Count}. Установка отменена; список оставлен для выбора.";
    StatusText.Text="Установка отменена пользователем.";
    return;
   }
   UpdatesCountText.Text=$"3/3: Загрузка и установка {updates.Count} пакетов. Не выключайте компьютер...";
   StatusText.Text="Windows Update скачивает и устанавливает драйверы. Подтвердите запрос UAC, если он появится.";
   var log=await Task.Run(()=>_windowsUpdate.InstallAvailable(updates.Select(u=>u.UpdateId).ToArray()));
   UpdatesCountText.Text="Обработка пакетов завершена. Выполняется повторная проверка устройств...";
   StatusText.Text="Повторное сканирование после установки...";
   var after=await _inventory.ScanAsync();
   ApplyDevices(after);
   var remaining=after.Count(d=>!string.IsNullOrWhiteSpace(d.Problem)&&d.Problem!="Нет"&&d.Problem!="0");
   UpdatesCountText.Text=$"Завершено. Было найдено пакетов: {updates.Count}. Устройств с кодами ошибок после установки: {remaining}. Смотрите отчёт Windows Update ниже.";
   StatusText.Text="Автоматическая установка драйверов завершена.";
   MessageBox.Show(log+"\n\nПовторная проверка устройств: "+after.Count+"; устройств с кодами ошибок: "+remaining+". Если Windows сообщила о необходимости перезагрузки, сохраните работу и перезагрузите ПК.", "Результат автоматического обновления",MessageBoxButton.OK,MessageBoxImage.Information);
   _driverUpdates.Clear();
  }catch(System.ComponentModel.Win32Exception){StatusText.Text="Установка отменена в запросе UAC.";UpdatesCountText.Text="Пользователь отменил запрос прав администратора. Драйверы не установлены.";}
  catch(Exception ex){StatusText.Text="Не удалось завершить автоматическое обновление.";UpdatesCountText.Text="Ошибка автоматического обновления. Проверьте Windows Update и повторите попытку.";MessageBox.Show(ex.Message,"Автоматическая установка драйверов",MessageBoxButton.OK,MessageBoxImage.Error);}
  finally{_scanRunning=false;}
 }
 private async void SearchWindowsDriverUpdates_Click(object sender,RoutedEventArgs e)
 {
  try{
   UpdatesCountText.Text="Идёт поиск через Windows Update Agent...";
   var updates=await _windowsUpdate.SearchAsync();_driverUpdates.Clear();foreach(var item in updates)_driverUpdates.Add(item);
   UpdatesCountText.Text=updates.Count==0?"Доступные обновления драйверов не найдены.":$"Найдено обновлений: {updates.Count}. Отметьте галочками пакеты для установки.";
   StatusText.Text=UpdatesCountText.Text;
  }catch(Exception ex){UpdatesCountText.Text="Не удалось выполнить поиск.";MessageBox.Show(ex.Message,"Поиск обновлений драйверов",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private async void InstallWindowsDriverUpdates_Click(object sender,RoutedEventArgs e)
 {
  var selected=_driverUpdates.Where(u=>u.IsSelected).ToArray();
  if(selected.Length==0){MessageBox.Show("Отметьте галочками нужные пакеты в списке обновлений.","Windows Update",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  if(MessageBox.Show($"Установить выбранные обновления драйверов: {selected.Length}?\n\nБудет использован системный Windows Update Agent. Возможна необходимость перезагрузки.","Подтверждение установки",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  try{
   StatusText.Text="Запуск установки через Windows Update Agent...";
   var result=await Task.Run(()=>_windowsUpdate.InstallAvailable(selected.Select(u=>u.UpdateId).ToArray()));
   MessageBox.Show(result,"Результат установки драйверов",MessageBoxButton.OK,MessageBoxImage.Information);
   StatusText.Text="Установка через Windows Update завершена. Повторите поиск для проверки результата.";
   UpdatesCountText.Text="Установка завершена. Выполните повторный поиск.";
   _driverUpdates.Clear();
  }catch(System.ComponentModel.Win32Exception){StatusText.Text="Установка отменена пользователем в UAC.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Установка обновлений драйверов",MessageBoxButton.OK,MessageBoxImage.Error);StatusText.Text="Ошибка установки обновлений.";}
 }
 private void DetectOem()
 {
  _detectedOem=_oemCatalog.Detect(ComputerValue.Text??"");
  OemDetectText.Text=_detectedOem is null?"Производитель не распознан автоматически. Выберите каталог вручную.":$"Обнаружен производитель: {_detectedOem.Name}. Перед загрузкой проверьте точную модель устройства.";
 }
 private void OpenDetectedOem_Click(object sender,RoutedEventArgs e)
 {
  if(_detectedOem is null){OpenOemCatalog_Click(sender,e);return;}
  try{_oemCatalog.Open(_detectedOem);}catch(Exception ex){MessageBox.Show(ex.Message,"Каталог драйверов",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void OpenOemCatalog_Click(object sender,RoutedEventArgs e)
 {
  var items=_oemCatalog.GetAll();var menu=string.Join(Environment.NewLine,items.Select((item,index)=>$"{index+1}. {item.Name}"));
  var answer=Microsoft.VisualBasic.Interaction.InputBox("Введите номер каталога:\n"+menu,"Официальные каталоги","1");
  if(!int.TryParse(answer,out var number)||number<1||number>items.Count)return;
  try{_oemCatalog.Open(items[number-1]);}catch(Exception ex){MessageBox.Show(ex.Message,"Каталог драйверов",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void EnableGameProfile_Click(object sender,RoutedEventArgs e)
 {
  try{GameProfileText.Text=_gameProfile.Enable();StatusText.Text="Игровой профиль включён.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Игровой профиль",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void RestoreGameProfile_Click(object sender,RoutedEventArgs e)
 {
  try{GameProfileText.Text=_gameProfile.Restore();StatusText.Text="Настройки игрового профиля восстановлены.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Восстановление профиля",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void WindowsUpdate_Click(object sender,RoutedEventArgs e)
 {
  try{Process.Start(new ProcessStartInfo("ms-settings:windowsupdate-optionalupdates"){UseShellExecute=true});StatusText.Text="Открыт раздел необязательных обновлений Windows. Проверьте категорию «Обновления драйверов».";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Windows Update",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void ListPackages_Click(object sender,RoutedEventArgs e)
 {
  try{PackagesText.Text=_maintenance.ListDriverPackages();StatusText.Text="Список пакетов Driver Store обновлён.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Driver Store",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
}
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
 private readonly ObservableCollection<DeviceInfo> _devices=new();
 private readonly DispatcherTimer _monitorTimer=new(){Interval=TimeSpan.FromSeconds(5)};
 private bool _scanRunning;
 public MainWindow()
 {
  InitializeComponent();DevicesGrid.ItemsSource=_devices;
  Loaded+=async(_,_)=>{await RefreshAllAsync();_monitorTimer.Tick+=async(_,_)=>await RefreshMetricsAsync();_monitorTimer.Start();};
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
  try{var summary=await _inventory.GetSystemSummaryAsync();CpuValue.Text=summary.Cpu;MemoryValue.Text=summary.Memory;OsValue.Text=summary.Os;}
  catch(Exception ex){StatusText.Text="Мониторинг: "+ex.Message;}
 }
 private async Task RefreshAllAsync()
 {
  if(_scanRunning)return;_scanRunning=true;
  try{
   StatusText.Text="Получение сведений о системе...";
   var summaryTask=_inventory.GetSystemSummaryAsync();var devicesTask=_inventory.ScanAsync();
   var summary=await summaryTask;var devices=await devicesTask;
   CpuValue.Text=summary.Cpu;MemoryValue.Text=summary.Memory;OsValue.Text=summary.Os;ApplyDevices(devices);
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
  DevicesGrid.ItemsSource=_devices.Where(d=>(!errorsOnly||(!string.IsNullOrWhiteSpace(d.Problem)&&d.Problem!="Нет"&&d.Problem!="0"))&&(query.Length==0||d.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||d.Class.Contains(query,StringComparison.OrdinalIgnoreCase)||d.InstanceId.Contains(query,StringComparison.OrdinalIgnoreCase))).ToList();
 }
 private void ExportDevices_Click(object sender,RoutedEventArgs e)
 {
  var dialog=new SaveFileDialog{Title="Сохранить отчёт устройств",Filter="CSV (*.csv)|*.csv",FileName="DjShrek-device-report.csv",DefaultExt=".csv"};
  if(dialog.ShowDialog(this)!=true)return;
  try{
   static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
   var sb=new StringBuilder();sb.AppendLine("Устройство;Класс;Состояние;Код ошибки;Instance ID");
   foreach(var d in _devices)sb.AppendLine(string.Join(";",new[]{d.Name,d.Class,d.Status,d.Problem,d.InstanceId}.Select(Q)));
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
  var infCount=Directory.EnumerateFiles(dialog.FolderName,"*.inf",SearchOption.AllDirectories).Take(10001).Count();
  if(infCount==0){MessageBox.Show("В выбранной папке не найдены INF-файлы.","Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  if(infCount>10000){MessageBox.Show("В папке слишком много INF-файлов. Выберите конкретную папку резервной копии.","Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
  if(MessageBox.Show($"Найдено INF-файлов: {infCount}. Передать пакеты Windows для установки?\n\n{dialog.FolderName}\n\nWindows выберет только совместимые пакеты.","Подтверждение восстановления",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  try{var result=_maintenance.RestoreDrivers(dialog.FolderName);StatusText.Text=result;MessageBox.Show(result,"Восстановление драйверов",MessageBoxButton.OK,MessageBoxImage.Information);}
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
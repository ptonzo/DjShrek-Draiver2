using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using DjShrek.Draiver.App.Models;
using DjShrek.Draiver.App.Services;
namespace DjShrek.Draiver.App;
public partial class MainWindow : Window
{
 private readonly DeviceInventoryService _inventory=new();
 private readonly DriverMaintenanceService _maintenance=new();
 private readonly ObservableCollection<DeviceInfo> _devices=new();
 public MainWindow(){InitializeComponent();DevicesGrid.ItemsSource=_devices;Loaded+=async(_,_)=>await RefreshAllAsync();}
 private void ShowPanel(string title,string subtitle,string panel)
 {
  PageTitle.Text=title;PageSubtitle.Text=subtitle;
  DashboardPanel.Visibility=panel=="dashboard"?Visibility.Visible:Visibility.Collapsed;
  DevicesPanel.Visibility=panel=="devices"?Visibility.Visible:Visibility.Collapsed;
  DriversPanel.Visibility=panel=="drivers"?Visibility.Visible:Visibility.Collapsed;
  DiagnosticsPanel.Visibility=panel=="diagnostics"?Visibility.Visible:Visibility.Collapsed;
 }
 private void Dashboard_Click(object sender,RoutedEventArgs e)=>ShowPanel("Обзор системы","Состояние компьютера и инструменты обслуживания","dashboard");
 private void Devices_Click(object sender,RoutedEventArgs e)=>ShowPanel("Устройства","Фактическая инвентаризация Plug and Play","devices");
 private void Drivers_Click(object sender,RoutedEventArgs e)=>ShowPanel("Драйверы и резервная копия","Штатные инструменты Windows для обслуживания драйверов","drivers");
 private void Diagnostics_Click(object sender,RoutedEventArgs e)=>ShowPanel("Диагностика","Проверка состояния устройств без изменения системы","diagnostics");
 private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAllAsync();
 private async void Scan_Click(object sender,RoutedEventArgs e)=>await ScanDevicesAsync();
 private async Task RefreshAllAsync()
 {
  try{
   StatusText.Text="Получение сведений о системе...";
   var summaryTask=_inventory.GetSystemSummaryAsync();var devicesTask=_inventory.ScanAsync();
   var summary=await summaryTask;var devices=await devicesTask;
   CpuValue.Text=summary.Cpu;MemoryValue.Text=summary.Memory;OsValue.Text=summary.Os;DeviceCount.Text=devices.Count.ToString();
   _devices.Clear();foreach(var d in devices)_devices.Add(d);
   StatusText.Text=$"Обновлено: устройств обнаружено {devices.Count}.";
  }catch(Exception ex){StatusText.Text="Не удалось получить часть сведений.";MessageBox.Show(ex.Message,"Ошибка чтения сведений Windows",MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
 private async Task ScanDevicesAsync()
 {
  try{
   StatusText.Text="Сканирование устройств...";var devices=await _inventory.ScanAsync();
   _devices.Clear();foreach(var d in devices)_devices.Add(d);DeviceCount.Text=devices.Count.ToString();
   StatusText.Text=$"Сканирование завершено. Найдено устройств: {devices.Count}.";
   ShowPanel("Устройства","Фактическая инвентаризация Plug and Play","devices");
  }catch(Exception ex){StatusText.Text="Ошибка сканирования.";MessageBox.Show(ex.Message,"Сканирование",MessageBoxButton.OK,MessageBoxImage.Error);}
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
 private void Install_Click(object sender,RoutedEventArgs e)
 {
  var dialog=new OpenFileDialog{Title="Выберите INF-файл драйвера",Filter="Файлы драйверов (*.inf)|*.inf",CheckFileExists=true};
  if(dialog.ShowDialog(this)!=true)return;
  var answer=MessageBox.Show($"Передать Windows пакет для установки?\n\n{dialog.FileName}\n\nПродолжайте только если доверяете источнику и проверили совместимость.","Подтверждение установки драйвера",MessageBoxButton.YesNo,MessageBoxImage.Warning);
  if(answer!=MessageBoxResult.Yes)return;
  try{var result=_maintenance.InstallInf(dialog.FileName);StatusText.Text=result;MessageBox.Show(result,"Установка драйвера",MessageBoxButton.OK,MessageBoxImage.Information);}
  catch(System.ComponentModel.Win32Exception){StatusText.Text="Установка отменена пользователем в UAC.";}
  catch(Exception ex){MessageBox.Show(ex.Message,"Установка драйвера",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
}
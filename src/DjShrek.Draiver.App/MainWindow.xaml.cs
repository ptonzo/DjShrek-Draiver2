using System.Collections.ObjectModel;
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
    private readonly DeviceInventoryService _inventory = new();
    private readonly DriverMaintenanceService _maintenance = new();
    private readonly HardwareSensorService _sensors = new();
    private readonly GameOptimizationService _gameProfile = new();
    private readonly OemCatalogService _oemCatalog = new();
    private readonly RegistryMaintenanceService _registryService = new();
    private readonly JunkCleanupService _junkService = new();
    private readonly WindowsDriverUpdateService _windowsDriverUpdates = new();

        private readonly ObservableCollection<DeviceInfo> _devices = new();
    private readonly ObservableCollection<DriverUpdateCandidate> _driverUpdates = new();
    private readonly ObservableCollection<RegistryFinding> _registryFindings = new();
    private readonly ObservableCollection<JunkFileItem> _junkFiles = new();

    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private OemSupport? _detectedOem;
    private bool _scanRunning;
    private bool _metricsRunning;
    private bool _initializing = true;
    private bool _syncingThemes;
    private string _currentPanel = "dashboard";
    private string _language = "ru";

    public MainWindow()
    {
        InitializeComponent();

        DevicesGrid.ItemsSource = _devices;
        DriverUpdatesGrid.ItemsSource = _driverUpdates;
        RegistryGrid.ItemsSource = _registryFindings;
        JunkGrid.ItemsSource = _junkFiles;

        var selectedTheme = ThemeManager.LoadPreference();
        ThemeSelector.SelectedValue = selectedTheme;
        SettingsThemeSelector.SelectedValue = selectedTheme;
        var theme = ThemeManager.Themes.FirstOrDefault(x => x.Id == selectedTheme) ?? ThemeManager.Themes.First(x => x.Id == "02");
        ThemeManager.Apply(theme);
        ThemeStatusText.Text = "Тема: " + theme.Name;

        _language = LoadLanguage();
        LanguageSelector.SelectedItem = LanguageSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _language, StringComparison.OrdinalIgnoreCase))
            ?? LanguageSelector.Items[0];
        _initializing = false;
        ApplyLanguage();
        AppendEvent("Программа запущена. Мониторинг инициализирован.");

        Loaded += async (_, _) =>
        {
            DetectOem();
            await RefreshMetricsAsync();
            _monitorTimer.Tick += async (_, _) => await RefreshMetricsAsync();
            _monitorTimer.Start();
        };
        Closed += (_, _) => _monitorTimer.Stop();
    }

    private void AppendEvent(string message)
    {
        if (EventLogList is null) return;
        EventLogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  |  {message}");
        while (EventLogList.Items.Count > 30) EventLogList.Items.RemoveAt(EventLogList.Items.Count - 1);
    }

    private static string LanguagePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DjShrek-Draiver", "language.txt");

    private static string LoadLanguage()
    {
        try { return File.Exists(LanguagePath) && File.ReadAllText(LanguagePath).Trim() == "en" ? "en" : "ru"; }
        catch { return "ru"; }
    }

    private void SaveLanguage()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);
            File.WriteAllText(LanguagePath, _language);
        }
        catch { /* Language remains active for this session. */ }
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingThemes || ThemeSelector.SelectedItem is not AppTheme theme) return;
        ApplyTheme(theme, SettingsThemeSelector);
    }

    private void SettingsThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingThemes || SettingsThemeSelector.SelectedItem is not AppTheme theme) return;
        ApplyTheme(theme, ThemeSelector);
    }

    private void ApplyTheme(AppTheme theme, ComboBox otherSelector)
    {
        _syncingThemes = true;
        try
        {
            ThemeManager.Apply(theme);
            otherSelector.SelectedValue = theme.Id;
            ThemeStatusText.Text = "Тема: " + theme.Name;
            StatusText.Text = "Оформление изменено: " + theme.Name;
            ShowPanel(_currentPanel);
        }
        finally { _syncingThemes = false; }
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageSelector.SelectedItem is not ComboBoxItem item) return;
        _language = item.Tag?.ToString() ?? "ru";
        SaveLanguage();
        ApplyLanguage();
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = new Dictionary<string,string> {
                ["Home"]="Home", ["Devices and drivers"]="Devices and drivers", ["Driver updates"]="Driver updates",
                ["Registry diagnostics"]="Registry diagnostics", ["Computer cleanup"]="Computer cleanup", ["Settings"]="Settings",
                ["Computer monitoring and maintenance tools"]="Computer monitoring and maintenance tools",
                ["Plug and Play inventory, error filter and driver backup"]="Plug and Play inventory, error filter and driver backup",
                ["Find available updates through Windows Update and install a selected package"]="Find available updates through Windows Update and install a selected package",
                ["Windows startup entries diagnostics without silent changes"]="Windows startup entries diagnostics without silent changes",
                ["Conservative cleanup of old temporary files"]="Conservative cleanup of old temporary files",
                ["Application appearance and preferences"]="Application appearance and preferences"
            },
            ["de"] = new Dictionary<string,string> {
                ["Home"]="Startseite", ["Devices and drivers"]="Geräte und Treiber", ["Driver updates"]="Treiberaktualisierungen",
                ["Registry diagnostics"]="Registrierungsdiagnose", ["Computer cleanup"]="Computerbereinigung", ["Settings"]="Einstellungen",
                ["Computer monitoring and maintenance tools"]="Computerüberwachung und Wartungswerkzeuge",
                ["Plug and Play inventory, error filter and driver backup"]="Plug-and-Play-Inventar, Fehlerfilter und Treibersicherung",
                ["Find available driver updates through Windows Update and install the selected package"]="Search available driver updates through Windows Update and install the selected package",
                ["Windows startup entries diagnostics without silent changes"]="Diagnose der Windows-Autostart-Einträge ohne stille Änderungen",
                ["Conservative cleanup of old temporary files"]="Sichere Bereinigung alter temporärer Dateien",
                ["Application appearance and preferences"]="Darstellung und Einstellungen der Anwendung"
            },
            ["fr"] = new Dictionary<string,string> {
                ["Home"]="Accueil", ["Devices and drivers"]="Périphériques et pilotes", ["Driver updates"]="Mises à jour des pilotes",
                ["Registry diagnostics"]="Diagnostic du registre", ["Computer cleanup"]="Nettoyage de l’ordinateur", ["Settings"]="Paramètres",
                ["Computer monitoring and maintenance tools"]="Surveillance et maintenance de l’ordinateur",
                ["Plug and Play inventory, error filter and driver backup"]="Inventaire Plug-and-Play, filtre d’erreurs et sauvegarde des pilotes",
                ["Find available driver updates through Windows Update and install the selected package"]="Verfügbare Treiberupdates über Windows Update suchen und das ausgewählte Paket installieren",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostic du démarrage Windows sans modification silencieuse",
                ["Conservative cleanup of old temporary files"]="Nettoyage prudent des anciens fichiers temporaires",
                ["Application appearance and preferences"]="Apparence et préférences de l’application"
            },
            ["es"] = new Dictionary<string,string> {
                ["Home"]="Inicio", ["Devices and drivers"]="Dispositivos y controladores", ["Driver updates"]="Actualizaciones de controladores",
                ["Registry diagnostics"]="Diagnóstico del registro", ["Computer cleanup"]="Limpieza del equipo", ["Settings"]="Configuración",
                ["Computer monitoring and maintenance tools"]="Supervisión y mantenimiento del equipo",
                ["Plug and Play inventory, error filter and driver backup"]="Inventario Plug and Play, filtro de errores y copia de controladores",
                ["Find available driver updates through Windows Update and install the selected package"]="Rechercher les mises à jour de pilotes via Windows Update et installer le paquet sélectionné",
                ["Windows startup entries diagnostics without silent changes"]="Diagnóstico del inicio de Windows sin cambios silenciosos",
                ["Conservative cleanup of old temporary files"]="Limpieza prudente de archivos temporales antiguos",
                ["Application appearance and preferences"]="Apariencia y preferencias de la aplicación"
            },
            ["pt_BR"] = new Dictionary<string,string> {
                ["Home"]="Início", ["Devices and drivers"]="Dispositivos e drivers", ["Driver updates"]="Atualizações de drivers",
                ["Registry diagnostics"]="Diagnóstico do registro", ["Computer cleanup"]="Limpeza do computador", ["Settings"]="Configurações",
                ["Computer monitoring and maintenance tools"]="Monitoramento e manutenção do computador",
                ["Plug and Play inventory, error filter and driver backup"]="Inventário Plug and Play, filtro de erros e backup de drivers",
                ["Find available driver updates through Windows Update and install the selected package"]="Buscar actualizaciones de controladores mediante Windows Update e instalar el paquete seleccionado",
                ["Windows startup entries diagnostics without silent changes"]="Diagnóstico da inicialização do Windows sem alterações silenciosas",
                ["Conservative cleanup of old temporary files"]="Limpeza segura de arquivos temporários antigos",
                ["Application appearance and preferences"]="Aparência e preferências do aplicativo"
            },
            ["it"] = new Dictionary<string,string> {
                ["Home"]="Home", ["Devices and drivers"]="Dispositivi e driver", ["Driver updates"]="Aggiornamenti driver",
                ["Registry diagnostics"]="Diagnostica del registro", ["Computer cleanup"]="Pulizia del computer", ["Settings"]="Impostazioni",
                ["Computer monitoring and maintenance tools"]="Monitoraggio e manutenzione del computer",
                ["Plug and Play inventory, error filter and driver backup"]="Inventario Plug and Play, filtro errori e backup driver",
                ["Find available driver updates through Windows Update and install the selected package"]="Buscar atualizações de drivers pelo Windows Update e instalar o pacote selecionado",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostica dell’avvio di Windows senza modifiche silenziose",
                ["Conservative cleanup of old temporary files"]="Pulizia prudente dei vecchi file temporanei",
                ["Application appearance and preferences"]="Aspetto e preferenze dell’applicazione"
            },
            ["pl"] = new Dictionary<string,string> {
                ["Home"]="Strona główna", ["Devices and drivers"]="Urządzenia i sterowniki", ["Driver updates"]="Aktualizacje sterowników",
                ["Registry diagnostics"]="Diagnostyka rejestru", ["Computer cleanup"]="Czyszczenie komputera", ["Settings"]="Ustawienia",
                ["Computer monitoring and maintenance tools"]="Monitorowanie komputera i narzędzia konserwacji",
                ["Plug and Play inventory, error filter and driver backup"]="Lista urządzeń Plug and Play, filtr błędów i kopia sterowników",
                ["Find available driver updates through Windows Update and install the selected package"]="Cercare gli aggiornamenti dei driver tramite Windows Update e installare il pacchetto selezionato",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostyka autostartu Windows bez cichych zmian",
                ["Conservative cleanup of old temporary files"]="Bezpieczne czyszczenie starych plików tymczasowych",
                ["Application appearance and preferences"]="Wygląd i preferencje aplikacji"
            },
            ["tr"] = new Dictionary<string,string> {
                ["Home"]="Ana Sayfa", ["Devices and drivers"]="Aygıtlar ve Sürücüler", ["Driver updates"]="Sürücü Güncellemeleri",
                ["Registry diagnostics"]="Kayıt Defteri Tanılama", ["Computer cleanup"]="Bilgisayar Temizliği", ["Settings"]="Ayarlar",
                ["Computer monitoring and maintenance tools"]="Bilgisayar izleme ve bakım araçları",
                ["Plug and Play inventory, error filter and driver backup"]="Tak ve Çalıştır envanteri, hata filtresi ve sürücü yedekleme",
                ["Find available driver updates through Windows Update and install the selected package"]="Wyszukaj aktualizacje sterowników przez Windows Update i zainstaluj wybrany pakiet",
                ["Windows startup entries diagnostics without silent changes"]="Windows başlangıç girdilerini sessiz değişiklik yapmadan tanılama",
                ["Conservative cleanup of old temporary files"]="Eski geçici dosyaların güvenli temizliği",
                ["Application appearance and preferences"]="Uygulama görünümü ve tercihleri"
            },
            ["uk"] = new Dictionary<string,string> {
                ["Home"]="Головна", ["Devices and drivers"]="Пристрої та драйвери", ["Driver updates"]="Оновлення драйверів",
                ["Registry diagnostics"]="Діагностика реєстру", ["Computer cleanup"]="Очищення комп’ютера", ["Settings"]="Налаштування",
                ["Computer monitoring and maintenance tools"]="Моніторинг комп’ютера та засоби обслуговування",
                ["Plug and Play inventory, error filter and driver backup"]="Інвентаризація Plug and Play, фільтр помилок і резервна копія драйверів",
                ["Find available driver updates through Windows Update and install the selected package"]="Windows Update üzerinden sürücü güncellemelerini bul ve seçilen paketi yükle",
                ["Windows startup entries diagnostics without silent changes"]="Перевірка автозапуску Windows без непомітних змін",
                ["Conservative cleanup of old temporary files"]="Безпечне очищення старих тимчасових файлів",
                ["Application appearance and preferences"]="Вигляд і параметри програми"
            },
            ["zh_CN"] = new Dictionary<string,string> {
                ["Home"]="主页", ["Devices and drivers"]="设备和驱动程序", ["Driver updates"]="驱动程序更新",
                ["Registry diagnostics"]="注册表诊断", ["Computer cleanup"]="电脑清理", ["Settings"]="设置",
                ["Computer monitoring and maintenance tools"]="电脑监控和维护工具",
                ["Plug and Play inventory, error filter and driver backup"]="即插即用设备清单、错误筛选和驱动备份",
                ["Find available driver updates through Windows Update and install the selected package"]="Пошук оновлень драйверів через Windows Update з установленням вибраного пакета",
                ["Windows startup entries diagnostics without silent changes"]="检查 Windows 启动项，不进行静默修改",
                ["Conservative cleanup of old temporary files"]="安全清理旧临时文件",
                ["Application appearance and preferences"]="应用外观和偏好设置"
            },
            ["ja"] = new Dictionary<string,string> {
                ["Home"]="ホーム", ["Devices and drivers"]="デバイスとドライバー", ["Driver updates"]="ドライバーの更新",
                ["Registry diagnostics"]="レジストリ診断", ["Computer cleanup"]="PC のクリーンアップ", ["Settings"]="設定",
                ["Computer monitoring and maintenance tools"]="PC の監視とメンテナンスツール",
                ["Plug and Play inventory, error filter and driver backup"]="Plug and Play インベントリ、エラーフィルター、ドライバーのバックアップ",
                ["Find available driver updates through Windows Update and install the selected package"]="通过 Windows Update 查找驱动程序更新并安装所选软件包",
                ["Windows startup entries diagnostics without silent changes"]="サイレント変更を行わずに Windows のスタートアップを診断",
                ["Conservative cleanup of old temporary files"]="古い一時ファイルを安全にクリーンアップ",
                ["Application appearance and preferences"]="アプリケーションの外観と設定"
            }
        };

    private string Tr(string russian, string english)
    {
        if (_language == "ru") return russian;
        return Translations.TryGetValue(_language, out var language) &&
               language.TryGetValue(english, out var translated) ? translated : english;
    }

    private void ApplyLanguage()
    {
        DashboardNavButton.Content = Tr("Главная", "Home");
        DevicesNavButton.Content = Tr("Драйверы и устройства", "Devices and drivers");
        UpdatesNavButton.Content = Tr("Обновления драйверов", "Driver updates");
        RegistryNavButton.Content = Tr("Проверка реестра", "Registry check");
        CleanupNavButton.Content = Tr("Очистка компьютера", "Computer cleanup");
        SettingsNavButton.Content = Tr("Настройки", "Settings");
        ShowPanel(_currentPanel);
    }

    private void ShowPanel(string panel)
    {
        _currentPanel = panel;
        DashboardPanel.Visibility = panel == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        DevicesPanel.Visibility = panel == "devices" ? Visibility.Visible : Visibility.Collapsed;
        DriversPanel.Visibility = panel == "drivers" ? Visibility.Visible : Visibility.Collapsed;
        RegistryPanel.Visibility = panel == "registry" ? Visibility.Visible : Visibility.Collapsed;
        CleanupPanel.Visibility = panel == "cleanup" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == "settings" ? Visibility.Visible : Visibility.Collapsed;

        var titles = panel switch
        {
            "devices" => (Tr("Драйверы и устройства", "Devices and drivers"), Tr("Инвентаризация Plug and Play, фильтр ошибок и резервное копирование", "Plug and Play inventory, error filter and driver backup")),
            "drivers" => (Tr("Обновления драйверов", "Driver updates"), Tr("Поиск доступных обновлений через Windows Update с установкой выбранного пакета", "Find available driver updates through Windows Update and install the selected package")),
            "registry" => (Tr("Проверка реестра", "Registry diagnostics"), Tr("Проверка разделов автозапуска Windows без скрытых изменений", "Windows startup entries diagnostics without silent changes")),
            "cleanup" => (Tr("Очистка компьютера", "Computer cleanup"), Tr("Безопасная очистка старых временных файлов", "Conservative cleanup of old temporary files")),
            "settings" => (Tr("Настройки", "Settings"), Tr("Оформление и параметры приложения", "Application appearance and preferences")),
            _ => (Tr("Главная", "Home"), Tr("Мониторинг компьютера и инструменты обслуживания", "PC monitoring and maintenance tools"))
        };
        PageTitle.Text = titles.Item1;
        PageSubtitle.Text = titles.Item2;

        var theme = ThemeManager.Themes.FirstOrDefault(x => x.Id == ThemeManager.LoadPreference()) ?? ThemeManager.Themes[0];
        var active = ActiveNavigationButton(panel);
        foreach (var button in new[] { DashboardNavButton, DevicesNavButton, UpdatesNavButton, RegistryNavButton, CleanupNavButton, SettingsNavButton })
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(button == active ? theme.Accent : theme.Navigation);
            button.Background = new System.Windows.Media.SolidColorBrush(color);
            button.BorderBrush = button == active
                ? (System.Windows.Media.Brush)Application.Current.Resources["AccentSecondaryBrush"]
                : (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"];
        }
    }

    private Button ActiveNavigationButton(string panel) => panel switch
    {
        "devices" => DevicesNavButton,
        "drivers" => UpdatesNavButton,
        "registry" => RegistryNavButton,
        "cleanup" => CleanupNavButton,
        "settings" => SettingsNavButton,
        _ => DashboardNavButton
    };

    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowPanel("dashboard");
    private void Devices_Click(object sender, RoutedEventArgs e) => ShowPanel("devices");
    private void Drivers_Click(object sender, RoutedEventArgs e) => ShowPanel("drivers");
    private void Registry_Click(object sender, RoutedEventArgs e) => ShowPanel("registry");
    private void Cleanup_Click(object sender, RoutedEventArgs e) => ShowPanel("cleanup");
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPanel("settings");

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private async void Scan_Click(object sender, RoutedEventArgs e) { await ScanDevicesAsync(); }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning) return;
        StartScanButton.IsEnabled = false;
        StartupScanStatusText.Text = "Сканирование оборудования и установленных драйверов...";
        try
        {
            if (!await ScanDevicesAsync())
            {
                StartupScanStatusText.Text = "Сканирование устройств завершилось с ошибкой. Открывается основной интерфейс.";
                return;
            }

            StartupScanStatusText.Text = $"Устройств проверено: {_devices.Count}. Поиск доступных обновлений драйверов...";
            await SearchDriverUpdatesAsync();
            StartupScanStatusText.Text = "Проверка завершена. Открывается DjShrek-Draiver.";
        }
        finally
        {
            StartupScanOverlay.Visibility = Visibility.Collapsed;
            StartScanButton.IsEnabled = true;
        }
    }

    private async Task RefreshAllAsync()
    {
        if (_scanRunning) return;
        _scanRunning = true;
        StatusText.Text = "Сбор показателей и сканирование устройств Windows...";
        try
        {
            var metricsTask = _inventory.GetSystemMetricsAsync();
            var devicesTask = _inventory.ScanAsync();
            var hardwareTask = Task.Run(() => _sensors.ReadMetrics());

            try { ApplySystemMetrics(await metricsTask); }
            catch (Exception ex) { StatusText.Text = "Мониторинг: " + ex.Message; }

            var devices = await devicesTask;
            ApplyDevices(devices);
            ApplyHardwareMetrics(await hardwareTask);
            StatusText.Text = $"Готово. Обнаружено устройств: {devices.Count}. Мониторинг обновляется каждые 5 секунд.";
            AppendEvent($"Первичное сканирование: обнаружено устройств — {devices.Count}.");
            DetectOem();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось завершить первичное сканирование.";
            MessageBox.Show(ex.Message, "DjShrek-Draiver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _scanRunning = false; }
    }

    private async Task RefreshMetricsAsync()
    {
        if (_metricsRunning || !IsLoaded) return;
        _metricsRunning = true;
        try
        {
            var metricsTask = _inventory.GetSystemMetricsAsync();
            var hardwareTask = Task.Run(() => _sensors.ReadMetrics());
            ApplySystemMetrics(await metricsTask);
            ApplyHardwareMetrics(await hardwareTask);
        }
        catch (Exception ex) { StatusText.Text = "Мониторинг временно недоступен: " + ex.Message; }
        finally { _metricsRunning = false; }
    }

    private void ApplySystemMetrics(SystemMetrics metrics)
    {
        CpuValue.Text = $"{metrics.CpuPercent:0}%";
        MemoryValue.Text = $"{metrics.MemoryPercent:0}%";
        DiskValue.Text = $"{metrics.DiskPercent:0}%";
        CpuProgress.Value = ClampPercent(metrics.CpuPercent);
        MemoryProgress.Value = ClampPercent(metrics.MemoryPercent);
        DiskProgress.Value = ClampPercent(metrics.DiskPercent);
        MemoryDetail.Text = $"{metrics.MemoryUsedGb:0.0} / {metrics.MemoryTotalGb:0.0} ГБ";
        DiskDetail.Text = $"{metrics.DiskUsedGb:0.0} / {metrics.DiskTotalGb:0.0} ГБ";
        OsValue.Text = metrics.Os;
        ComputerValue.Text = metrics.Computer;
        CpuNameValue.Text = "Процессор: " + (metrics.CpuName.Length == 0 ? "Не определён" : metrics.CpuName);
        GpuNameValue.Text = "Видеокарта: " + (metrics.GpuName.Length == 0 ? "Не определена" : metrics.GpuName);
        UptimeValue.Text = "Время работы: " + (metrics.Uptime.Length == 0 ? "Нет данных" : metrics.Uptime);
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);

    private void ApplyHardwareMetrics(HardwareMetrics metrics)
    {
        CpuTempValue.Text = "CPU  ·  " + metrics.CpuTemperature;
        GpuTempValue.Text = "GPU  ·  " + metrics.GpuTemperature;
        GpuValue.Text = metrics.GpuLoad;
        GpuProgress.Value = metrics.GpuLoad.EndsWith('%') &&
                            double.TryParse(metrics.GpuLoad.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var load)
            ? ClampPercent(load) : 0;
        GpuDetail.Text = metrics.GpuName.Length > 0 ? metrics.GpuName : "Использование GPU";
        TemperatureNote.Text = metrics.Note;
    }

    private async Task<bool> ScanDevicesAsync()
    {
        if (_scanRunning) return false;
        _scanRunning = true;
        try
        {
            StatusText.Text = "Сканирование Plug and Play...";
            var devices = await _inventory.ScanAsync();
            ApplyDevices(devices);
            StatusText.Text = $"Сканирование завершено. Устройств: {devices.Count}.";
            AppendEvent($"Сканирование устройств завершено: {devices.Count}.");
            ShowPanel("devices");
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Сканирование не завершено.";
            MessageBox.Show(ex.Message, "Сканирование устройств", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { _scanRunning = false; }
    }

    private void ApplyDevices(IReadOnlyList<DeviceInfo> devices)
    {
        _devices.Clear();
        foreach (var device in devices) _devices.Add(device);
        var errors = devices.Count(IsProblemDevice);
        HeroDeviceCountText.Text = $"Устройства: {devices.Count}";
        HeroIssueCountText.Text = $"Проблемы: {errors}";
        HeroScanStatusText.Text = errors == 0 ? "Критические ошибки не обнаружены" : "Обнаружены устройства, требующие внимания";
        HealthSummary.Text = errors == 0
            ? $"Проверено устройств: {devices.Count}. Конфигурационные ошибки не обнаружены."
            : $"Проверено устройств: {devices.Count}. Проблемных устройств: {errors}. Откройте раздел «Драйверы и устройства» и включите фильтр ошибок.";
        FilterDevices();
    }

    private static bool IsProblemDevice(DeviceInfo d) =>
        !string.IsNullOrWhiteSpace(d.Problem) && d.Problem != "Нет" && d.Problem != "0";

    private void DeviceSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterDevices();
    private void DeviceFilter_Changed(object sender, RoutedEventArgs e) => FilterDevices();

    private void FilterDevices()
    {
        if (DevicesGrid is null) return;
        var query = DeviceSearch?.Text?.Trim() ?? "";
        var errorsOnly = ErrorsOnlyCheck?.IsChecked == true;
        DevicesGrid.ItemsSource = _devices.Where(d =>
            (!errorsOnly || IsProblemDevice(d)) &&
            (query.Length == 0 ||
             d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.Class.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.Status.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.DriverProvider.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.DriverVersion.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.InfName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.InstanceId.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void ExportDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0)
        {
            MessageBox.Show("Сначала выполните сканирование устройств.", "Экспорт CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить отчёт устройств",
            Filter = "CSV (*.csv)|*.csv",
            FileName = "DjShrek-device-report.csv",
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
            var builder = new StringBuilder();
            builder.AppendLine("Устройство;Производитель;Класс;Состояние;Код ошибки;Поставщик драйвера;Версия;Дата;INF;Подпись;Instance ID;Hardware IDs");
            foreach (var d in _devices)
            {
                builder.AppendLine(string.Join(";", new[]
                {
                    d.Name,d.Manufacturer,d.Class,d.Status,d.Problem,d.DriverProvider,d.DriverVersion,
                    d.DriverDate,d.InfName,d.Signature,d.InstanceId,string.Join("|",d.HardwareIds)
                }.Select(Quote)));
            }
            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            StatusText.Text = "CSV-отчёт сохранён: " + dialog.FileName;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Экспорт CSV", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите пустую папку для резервной копии драйверов", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Directory.Exists(dialog.FolderName) && Directory.EnumerateFileSystemEntries(dialog.FolderName).Any())
            {
                MessageBox.Show("Для резервной копии выберите пустую папку. Существующие файлы не будут перезаписаны.",
                    "Резервное копирование", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show($"Экспортировать сторонние пакеты драйверов в папку?\n\n{dialog.FolderName}",
                    "Подтверждение резервного копирования", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var result = _maintenance.ExportDrivers(dialog.FolderName);
            StatusText.Text = result;
            AppendEvent("Создана резервная копия драйверов: " + dialog.FolderName);
            MessageBox.Show(result, "Резервное копирование", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Операция отменена в запросе UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Резервное копирование", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreDrivers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку с ранее экспортированными драйверами", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var count = Directory.EnumerateFiles(dialog.FolderName, "*.inf", SearchOption.AllDirectories).Take(10001).Count();
            if (count == 0)
            {
                MessageBox.Show("В выбранной папке не найдены INF-файлы.", "Восстановление драйверов", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (count > 10000)
            {
                MessageBox.Show("В папке слишком много INF-файлов. Выберите конкретную папку резервной копии.",
                    "Восстановление драйверов", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show($"Найдено INF-файлов: {count}. Передать совместимые пакеты Windows для установки?\n\n{dialog.FolderName}",
                    "Подтверждение восстановления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var result = _maintenance.RestoreDrivers(dialog.FolderName);
            StatusText.Text = result;
            AppendEvent("Выполнено восстановление драйверов из " + dialog.FolderName);
            MessageBox.Show(result, "Восстановление драйверов", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Восстановление отменено в UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Восстановление драйверов", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите INF-файл драйвера",
            Filter = "Файлы драйверов (*.inf)|*.inf",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show($"Передать Windows пакет для установки?\n\n{dialog.FileName}\n\nПродолжайте только если проверили источник и совместимость.",
                "Подтверждение установки драйвера", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = _maintenance.InstallInf(dialog.FileName);
            StatusText.Text = result;
            MessageBox.Show(result, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Установка отменена в UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void AutoUpdateDrivers_Click(object sender, RoutedEventArgs e)
    {
        await SearchDriverUpdatesAsync();
    }

    private async Task SearchDriverUpdatesAsync()
    {
        if (_scanRunning) return;
        _scanRunning = true;
        SearchDriverUpdatesButton.IsEnabled = false;
        InstallSelectedDriverButton.IsEnabled = false;
        DriverUpdatesSummaryText.Text = "Подключение к Windows Update Agent. Проверка доступных драйверов...";
        try
        {
            ShowPanel("drivers");
            var candidates = await _windowsDriverUpdates.SearchAsync();
            _driverUpdates.Clear();
            foreach (var candidate in candidates)
            {
                var hardwareIds = candidate.HardwareId
                    .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var device = _devices.FirstOrDefault(item =>
                    item.HardwareIds.Concat(item.CompatibleIds).Any(id =>
                        hardwareIds.Any(candidateId =>
                            id.Contains(candidateId, StringComparison.OrdinalIgnoreCase) ||
                            candidateId.Contains(id, StringComparison.OrdinalIgnoreCase))));
                _driverUpdates.Add(new DriverUpdateCandidate
                {
                    UpdateId = candidate.UpdateId,
                    Title = candidate.Title,
                    Manufacturer = candidate.Manufacturer,
                    Model = candidate.Model,
                    Provider = candidate.Provider,
                    DeviceClass = candidate.DeviceClass,
                    HardwareId = candidate.HardwareId,
                    CurrentVersion = device?.DriverVersion ?? "Не сопоставлена",
                    Compatibility = device is null ? "Предложено Windows Update; ID не сопоставлен" : "Hardware ID совпадает",
                    AvailableVersion = candidate.AvailableVersion,
                    DriverVersionDate = candidate.DriverVersionDate,
                    Description = candidate.Description,
                    IsDownloaded = candidate.IsDownloaded
                });
            }
            DriverUpdatesSummaryText.Text = candidates.Count == 0
                ? "Windows Update не вернул доступных обновлений драйверов. Это не означает, что для всех устройств существуют самые новые OEM-драйверы."
                : $"Найдено обновлений Windows Update: {candidates.Count}. Выберите строку, чтобы просмотреть и установить конкретный пакет.";
            UpdatesCountText.Text = $"Найдено обновлений: {candidates.Count}";
            StatusText.Text = DriverUpdatesSummaryText.Text;
            AppendEvent($"Windows Update Agent: найдено обновлений драйверов — {candidates.Count}.");
        }
        catch (Exception ex)
        {
            DriverUpdatesSummaryText.Text = "Поиск не завершён. Проверьте подключение и службу Windows Update.";
            UpdatesCountText.Text = "Ошибка поиска драйверов.";
            MessageBox.Show(ex.Message, "Поиск обновлений драйверов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SearchDriverUpdatesButton.IsEnabled = true;
            _scanRunning = false;
        }
    }

    private void DriverUpdatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstallSelectedDriverButton.IsEnabled = DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate;
    }

    private async void InstallSelectedDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverUpdatesGrid.SelectedItem is not DriverUpdateCandidate candidate) return;
        var details = string.Join(Environment.NewLine, new[]
        {
            candidate.Title,
            string.IsNullOrWhiteSpace(candidate.Manufacturer) ? null : "Производитель: " + candidate.Manufacturer,
            string.IsNullOrWhiteSpace(candidate.Model) ? null : "Модель: " + candidate.Model,
            string.IsNullOrWhiteSpace(candidate.Provider) ? null : "Поставщик: " + candidate.Provider,
            string.IsNullOrWhiteSpace(candidate.DriverVersionDate) ? null : "Дата версии: " + candidate.DriverVersionDate
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (MessageBox.Show(
            "Установить только выбранное обновление через Windows Update Agent?\n\n" + details +
            "\n\nПеред продолжением убедитесь, что это нужное устройство. Windows может запросить перезагрузку.",
            "Подтверждение установки драйвера", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        InstallSelectedDriverButton.IsEnabled = false;
        SearchDriverUpdatesButton.IsEnabled = false;
        DriverUpdatesSummaryText.Text = "Загрузка и установка выбранного драйвера...";
        try
        {
            var result = await _windowsDriverUpdates.InstallAsync(candidate.UpdateId);
            DriverUpdatesSummaryText.Text = result;
            StatusText.Text = result;
            AppendEvent("Результат установки драйвера: " + result);
            MessageBox.Show(result, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Information);
            await SearchDriverUpdatesAsync();
        }
        catch (Exception ex)
        {
            DriverUpdatesSummaryText.Text = "Установка не подтверждена Windows Update Agent.";
            MessageBox.Show(ex.Message, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SearchDriverUpdatesButton.IsEnabled = true;
            InstallSelectedDriverButton.IsEnabled = DriverUpdatesGrid.SelectedItem is DriverUpdateCandidate;
        }
    }

    private void DetectOem()
    {
        _detectedOem = _oemCatalog.Detect(ComputerValue.Text ?? "");
        OemDetectText.Text = _detectedOem is null
            ? "Производитель компьютера не распознан автоматически. Выберите производителя вручную."
            : $"Обнаружен производитель: {_detectedOem.Name}. Перед загрузкой проверьте точную модель устройства.";
    }

    private void OpenDetectedOem_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedOem is null) { OpenOemCatalog_Click(sender, e); return; }
        try { _oemCatalog.Open(_detectedOem); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Каталог драйверов", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenOemCatalog_Click(object sender, RoutedEventArgs e)
    {
        var items = _oemCatalog.GetAll();
        var menu = string.Join(Environment.NewLine, items.Select((item, index) => $"{index + 1}. {item.Name}"));
        var answer = Microsoft.VisualBasic.Interaction.InputBox("Введите номер каталога:\n" + menu, "Официальные каталоги производителей", "1");
        if (!int.TryParse(answer, out var number) || number < 1 || number > items.Count) return;
        try { _oemCatalog.Open(items[number - 1]); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Каталог драйверов", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void EnableGameProfile_Click(object sender, RoutedEventArgs e)
    {
        try { GameProfileText.Text = _gameProfile.Enable(); StatusText.Text = "Игровой профиль Windows включён."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Игровой профиль", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreGameProfile_Click(object sender, RoutedEventArgs e)
    {
        try { GameProfileText.Text = _gameProfile.Restore(); StatusText.Text = "Прежние параметры игрового профиля восстановлены."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Восстановление профиля", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ListPackages_Click(object sender, RoutedEventArgs e)
    {
        try { PackagesText.Text = _maintenance.ListDriverPackages(); StatusText.Text = "Список пакетов Driver Store обновлён."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Driver Store", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RegistryScan_Click(object sender, RoutedEventArgs e)
    {
        RegistrySummaryText.Text = "Проверка четырёх разделов Run/RunOnce...";
        try
        {
            var findings = await Task.Run(() => _registryService.Scan());
            _registryFindings.Clear();
            foreach (var finding in findings) _registryFindings.Add(finding);
            var errors = findings.Count(x => x.Severity == "Ошибка");
            RegistrySummaryText.Text = findings.Count == 0
                ? "Подозрительных записей не обнаружено. Реестр не изменялся."
                : $"Найдено замечаний: {findings.Count}; подтверждённых ошибок: {errors}. HKLM доступен только для просмотра.";
            StatusText.Text = "Проверка автозапуска завершена. Изменений в реестре не выполнялось.";
            AppendEvent($"Проверка Run/RunOnce: найдено замечаний — {findings.Count}.");
        }
        catch (Exception ex)
        {
            RegistrySummaryText.Text = "Проверка не завершена.";
            MessageBox.Show(ex.Message, "Проверка реестра", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RegistryRepair_Click(object sender, RoutedEventArgs e)
    {
        if (RegistryGrid.SelectedItem is not RegistryFinding finding)
        {
            MessageBox.Show("Сначала выберите строку с ошибкой.", "Проверка реестра", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!finding.Repairable)
        {
            MessageBox.Show("Автоматическое исправление доступно только для подтверждённой ошибки в HKCU. Системные записи HKLM доступны только для просмотра.",
                "Проверка реестра", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
            $"Удалить только выбранную запись автозапуска?\n\nРаздел: {finding.Hive}\\{finding.SubKey}\nИмя: {finding.ValueName}\nЗначение: {finding.ValueData}\n\nПеред изменением будет сохранена резервная копия .reg.",
            "Подтверждение исправления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var backup = await Task.Run(() => _registryService.Repair(finding));
            AppendEvent("Исправлена запись автозапуска; backup: " + backup);
            MessageBox.Show("Запись удалена. Резервная копия:\n" + backup, "Исправление реестра", MessageBoxButton.OK, MessageBoxImage.Information);
            await Task.Run(() => { });
            RegistryScan_Click(sender, e);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Исправление реестра", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void JunkScan_Click(object sender, RoutedEventArgs e)
    {
        JunkSummaryText.Text = "Идёт проверка временных папок. Файлы не удаляются...";
        try
        {
            var files = await Task.Run(() => _junkService.Scan(minimumAgeDays: 7));
            _junkFiles.Clear();
            foreach (var file in files) _junkFiles.Add(file);
            var total = files.Sum(x => x.Size);
            JunkSummaryText.Text = $"Найдено файлов старше 7 дней: {files.Count}. Потенциально освобождаемо: {JunkFileItem.FormatBytes(total)}. Отметьте файлы для удаления.";
            StatusText.Text = "Проверка временных файлов завершена. Ничего не удалено.";
            AppendEvent($"Проверка временных файлов: найдено файлов — {files.Count}, объём — {JunkFileItem.FormatBytes(total)}.");
        }
        catch (Exception ex)
        {
            JunkSummaryText.Text = "Проверка не завершена.";
            MessageBox.Show(ex.Message, "Очистка компьютера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void JunkClean_Click(object sender, RoutedEventArgs e)
    {
        JunkGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        JunkGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _junkFiles.Where(x => x.Selected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Отметьте файлы, которые требуется удалить.", "Очистка компьютера", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var amount = selected.Sum(x => x.Size);
        if (MessageBox.Show(
            $"Удалить {selected.Length} временных файлов старше 7 дней?\nПотенциально освободится: {JunkFileItem.FormatBytes(amount)}.\n\nЗагрузки, документы, фотографии и браузерные данные не затрагиваются.",
            "Подтверждение очистки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Task.Run(() => _junkService.Clean(selected, minimumAgeDays: 7));
            JunkSummaryText.Text = $"Удалено: {result.DeletedFiles}; освобождено: {JunkFileItem.FormatBytes(result.FreedBytes)}; пропущено: {result.SkippedFiles}.";
            AppendEvent($"Очистка завершена: удалено файлов — {result.DeletedFiles}; освобождено — {JunkFileItem.FormatBytes(result.FreedBytes)}.");
            if (result.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(10)), "Очистка завершена с замечаниями", MessageBoxButton.OK, MessageBoxImage.Warning);
            await Task.Delay(100);
            var remaining = await Task.Run(() => _junkService.Scan(minimumAgeDays: 7));
            _junkFiles.Clear();
            foreach (var file in remaining) _junkFiles.Add(file);
            StatusText.Text = "Очистка завершена. Повторное сканирование выполнено.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Очистка компьютера", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
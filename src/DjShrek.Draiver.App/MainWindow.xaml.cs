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
    private readonly LenovoDriverPackCatalogService _lenovoCatalog = new();
    private readonly RegistryMaintenanceService _registryService = new();
    private readonly JunkCleanupService _junkService = new();
    private readonly InternetDriverCatalogService _internetDriverCatalog = new();
    private readonly DriverInfPackageInspector _infPackageInspector = new();
    private readonly DriverFolderScannerService _driverFolderScanner = new();
    private readonly OemPackageExtractionService _packageExtraction = new();

        private readonly ObservableCollection<DeviceInfo> _devices = new();
    private readonly ObservableCollection<DriverUpdateCandidate> _driverUpdates = new();
    private readonly ObservableCollection<RegistryFinding> _registryFindings = new();
    private readonly ObservableCollection<JunkFileItem> _junkFiles = new();

    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private OemSupport? _detectedOem;
    private bool _scanRunning;
    private bool _metricsRunning;
    private bool _maintenanceRunning;
    private bool _initializing = true;
    private bool _syncingThemes;
    private string _currentPanel = "dashboard";
    private string _language = "ru";

    public MainWindow()
    {
        InitializeComponent();

        DevicesGrid.ItemsSource = _devices;
        DriverUpdatesGrid.ItemsSource = _driverUpdates;
        ConfigureDriverIgnoreMenu();
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

    private void DetectOem()
    {
        string manufacturer = "";
        string product = "";
        string baseboard = "";
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            manufacturer = bios?.GetValue("SystemManufacturer")?.ToString()?.Trim() ?? "";
            product = bios?.GetValue("SystemProductName")?.ToString()?.Trim() ?? "";
            baseboard = bios?.GetValue("BaseBoardManufacturer")?.ToString()?.Trim() ?? "";
        }
        catch (UnauthorizedAccessException)
        {
            // OEM detection is optional; a restricted registry must not stop the dashboard.
        }
        catch (System.Security.SecurityException)
        {
            // OEM detection is optional; a restricted registry must not stop the dashboard.
        }

        _detectedOem = _oemCatalog.Detect(manufacturer + " " + product + " " + baseboard);
        OemDetectText.Text = _detectedOem is null
            ? $"Производитель компьютера не распознан. BIOS: {manufacturer}; модель: {product}. Можно выбрать каталог вручную."
            : $"Обнаружен производитель компьютера: {_detectedOem.Name}. Модель: {product}. Откроется официальный сайт поддержки производителя.";
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
                ["Search official manufacturer websites by device Hardware ID; manually verify packages"]="Search official manufacturer websites by device Hardware ID; manually verify packages",
                ["Windows startup entries diagnostics without silent changes"]="Windows startup entries diagnostics without silent changes",
                ["Conservative cleanup of old temporary files"]="Conservative cleanup of old temporary files",
                ["Application appearance and preferences"]="Application appearance and preferences"
            },
            ["de"] = new Dictionary<string,string> {
                ["Home"]="Startseite", ["Devices and drivers"]="Geräte und Treiber", ["Driver updates"]="Treiberaktualisierungen",
                ["Registry diagnostics"]="Registrierungsdiagnose", ["Computer cleanup"]="Computerbereinigung", ["Settings"]="Einstellungen",
                ["Computer monitoring and maintenance tools"]="Computerüberwachung und Wartungswerkzeuge",
                ["Plug and Play inventory, error filter and driver backup"]="Plug-and-Play-Inventar, Fehlerfilter und Treibersicherung",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Search manufacturer websites by Hardware ID; manually verify packages",
                ["Windows startup entries diagnostics without silent changes"]="Diagnose der Windows-Autostart-Einträge ohne stille Änderungen",
                ["Conservative cleanup of old temporary files"]="Sichere Bereinigung alter temporärer Dateien",
                ["Application appearance and preferences"]="Darstellung und Einstellungen der Anwendung"
            },
            ["fr"] = new Dictionary<string,string> {
                ["Home"]="Accueil", ["Devices and drivers"]="Périphériques et pilotes", ["Driver updates"]="Mises à jour des pilotes",
                ["Registry diagnostics"]="Diagnostic du registre", ["Computer cleanup"]="Nettoyage de l’ordinateur", ["Settings"]="Paramètres",
                ["Computer monitoring and maintenance tools"]="Surveillance et maintenance de l’ordinateur",
                ["Plug and Play inventory, error filter and driver backup"]="Inventaire Plug-and-Play, filtre d’erreurs et sauvegarde des pilotes",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Herstellerseiten per Hardware-ID durchsuchen; Pakete manuell prüfen",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostic du démarrage Windows sans modification silencieuse",
                ["Conservative cleanup of old temporary files"]="Nettoyage prudent des anciens fichiers temporaires",
                ["Application appearance and preferences"]="Apparence et préférences de l’application"
            },
            ["es"] = new Dictionary<string,string> {
                ["Home"]="Inicio", ["Devices and drivers"]="Dispositivos y controladores", ["Driver updates"]="Actualizaciones de controladores",
                ["Registry diagnostics"]="Diagnóstico del registro", ["Computer cleanup"]="Limpieza del equipo", ["Settings"]="Configuración",
                ["Computer monitoring and maintenance tools"]="Supervisión y mantenimiento del equipo",
                ["Plug and Play inventory, error filter and driver backup"]="Inventario Plug and Play, filtro de errores y copia de controladores",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Rechercher les sites des fabricants par identifiant matériel; vérifier manuellement",
                ["Windows startup entries diagnostics without silent changes"]="Diagnóstico del inicio de Windows sin cambios silenciosos",
                ["Conservative cleanup of old temporary files"]="Limpieza prudente de archivos temporales antiguos",
                ["Application appearance and preferences"]="Apariencia y preferencias de la aplicación"
            },
            ["pt_BR"] = new Dictionary<string,string> {
                ["Home"]="Início", ["Devices and drivers"]="Dispositivos e drivers", ["Driver updates"]="Atualizações de drivers",
                ["Registry diagnostics"]="Diagnóstico do registro", ["Computer cleanup"]="Limpeza do computador", ["Settings"]="Configurações",
                ["Computer monitoring and maintenance tools"]="Monitoramento e manutenção do computador",
                ["Plug and Play inventory, error filter and driver backup"]="Inventário Plug and Play, filtro de erros e backup de drivers",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Buscar sitios de fabricantes por ID de hardware; verificar manualmente",
                ["Windows startup entries diagnostics without silent changes"]="Diagnóstico da inicialização do Windows sem alterações silenciosas",
                ["Conservative cleanup of old temporary files"]="Limpeza segura de arquivos temporários antigos",
                ["Application appearance and preferences"]="Aparência e preferências do aplicativo"
            },
            ["it"] = new Dictionary<string,string> {
                ["Home"]="Home", ["Devices and drivers"]="Dispositivi e driver", ["Driver updates"]="Aggiornamenti driver",
                ["Registry diagnostics"]="Diagnostica del registro", ["Computer cleanup"]="Pulizia del computer", ["Settings"]="Impostazioni",
                ["Computer monitoring and maintenance tools"]="Monitoraggio e manutenzione del computer",
                ["Plug and Play inventory, error filter and driver backup"]="Inventario Plug and Play, filtro errori e backup driver",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Buscar sites dos fabricantes por ID de hardware; verificar manualmente",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostica dell’avvio di Windows senza modifiche silenziose",
                ["Conservative cleanup of old temporary files"]="Pulizia prudente dei vecchi file temporanei",
                ["Application appearance and preferences"]="Aspetto e preferenze dell’applicazione"
            },
            ["pl"] = new Dictionary<string,string> {
                ["Home"]="Strona główna", ["Devices and drivers"]="Urządzenia i sterowniki", ["Driver updates"]="Aktualizacje sterowników",
                ["Registry diagnostics"]="Diagnostyka rejestru", ["Computer cleanup"]="Czyszczenie komputera", ["Settings"]="Ustawienia",
                ["Computer monitoring and maintenance tools"]="Monitorowanie komputera i narzędzia konserwacji",
                ["Plug and Play inventory, error filter and driver backup"]="Lista urządzeń Plug and Play, filtr błędów i kopia sterowników",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Cercare i siti dei produttori tramite ID hardware; verificare manualmente",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostyka autostartu Windows bez cichych zmian",
                ["Conservative cleanup of old temporary files"]="Bezpieczne czyszczenie starych plików tymczasowych",
                ["Application appearance and preferences"]="Wygląd i preferencje aplikacji"
            },
            ["tr"] = new Dictionary<string,string> {
                ["Home"]="Ana Sayfa", ["Devices and drivers"]="Aygıtlar ve Sürücüler", ["Driver updates"]="Sürücü Güncellemeleri",
                ["Registry diagnostics"]="Kayıt Defteri Tanılama", ["Computer cleanup"]="Bilgisayar Temizliği", ["Settings"]="Ayarlar",
                ["Computer monitoring and maintenance tools"]="Bilgisayar izleme ve bakım araçları",
                ["Plug and Play inventory, error filter and driver backup"]="Tak ve Çalıştır envanteri, hata filtresi ve sürücü yedekleme",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Szukaj stron producentów według identyfikatora sprzętu; sprawdź ręcznie",
                ["Windows startup entries diagnostics without silent changes"]="Windows başlangıç girdilerini sessiz değişiklik yapmadan tanılama",
                ["Conservative cleanup of old temporary files"]="Eski geçici dosyaların güvenli temizliği",
                ["Application appearance and preferences"]="Uygulama görünümü ve tercihleri"
            },
            ["uk"] = new Dictionary<string,string> {
                ["Home"]="Головна", ["Devices and drivers"]="Пристрої та драйвери", ["Driver updates"]="Оновлення драйверів",
                ["Registry diagnostics"]="Діагностика реєстру", ["Computer cleanup"]="Очищення комп’ютера", ["Settings"]="Налаштування",
                ["Computer monitoring and maintenance tools"]="Моніторинг комп’ютера та засоби обслуговування",
                ["Plug and Play inventory, error filter and driver backup"]="Інвентаризація Plug and Play, фільтр помилок і резервна копія драйверів",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Donanım kimliğine göre üretici sitelerinde ara; paketleri elle doğrula",
                ["Windows startup entries diagnostics without silent changes"]="Перевірка автозапуску Windows без непомітних змін",
                ["Conservative cleanup of old temporary files"]="Безпечне очищення старих тимчасових файлів",
                ["Application appearance and preferences"]="Вигляд і параметри програми"
            },
            ["zh_CN"] = new Dictionary<string,string> {
                ["Home"]="主页", ["Devices and drivers"]="设备和驱动程序", ["Driver updates"]="驱动程序更新",
                ["Registry diagnostics"]="注册表诊断", ["Computer cleanup"]="电脑清理", ["Settings"]="设置",
                ["Computer monitoring and maintenance tools"]="电脑监控和维护工具",
                ["Plug and Play inventory, error filter and driver backup"]="即插即用设备清单、错误筛选和驱动备份",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="Пошук сайтів виробників за апаратним ID; ручна перевірка",
                ["Windows startup entries diagnostics without silent changes"]="检查 Windows 启动项，不进行静默修改",
                ["Conservative cleanup of old temporary files"]="安全清理旧临时文件",
                ["Application appearance and preferences"]="应用外观和偏好设置"
            },
            ["ja"] = new Dictionary<string,string> {
                ["Home"]="ホーム", ["Devices and drivers"]="デバイスとドライバー", ["Driver updates"]="ドライバーの更新",
                ["Registry diagnostics"]="レジストリ診断", ["Computer cleanup"]="PC のクリーンアップ", ["Settings"]="設定",
                ["Computer monitoring and maintenance tools"]="PC の監視とメンテナンスツール",
                ["Plug and Play inventory, error filter and driver backup"]="Plug and Play インベントリ、エラーフィルター、ドライバーのバックアップ",
                ["Search manufacturer websites by Hardware ID; manually verify packages"]="按硬件 ID 搜索制造商网站；手动验证驱动程序",
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
            "drivers" => (Tr("Обновления драйверов", "Driver updates"), Tr("Поиск страниц производителей по Hardware ID с ручной проверкой пакетов", "Search manufacturer websites by Hardware ID; manually verify packages")),
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
            button.Foreground = button == active
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(theme.Text));
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
            // Reuse the guarded monitoring path instead of opening a second sensor
            // provider while the five-second dashboard refresh may already be running.
            await RefreshMetricsAsync();

            var devices = await _inventory.ScanAsync();
            ApplyDevices(devices);
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
            // Await both providers before releasing the guard. If one provider fails,
            // the other may still be using hardware resources in the background.
            var metricsTask = _inventory.GetSystemMetricsAsync();
            var hardwareTask = Task.Run(() => _sensors.ReadMetrics());
            await Task.WhenAll(metricsTask, hardwareTask);
            ApplySystemMetrics(await metricsTask);
            ApplyHardwareMetrics(await hardwareTask);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Мониторинг временно недоступен: " + ex.Message;
            AppendEvent("Не удалось обновить мониторинг: " + ex.GetType().Name);
        }
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
        SetScanProgress(0, "Подготовка проверки устройств...");
        try
        {
            StatusText.Text = "Сканирование Plug and Play...";
            var progress = new Progress<int>(value =>
            {
                SetScanProgress(value, value < 30
                    ? "Получение списка драйверов Windows..."
                    : value < 90 ? "Проверка устройств и Hardware ID..."
                    : "Обработка результатов сканирования...");
            });
            var devices = await _inventory.ScanAsync(progress);
            ApplyDevices(devices);
            SetScanProgress(100, $"Проверка завершена · устройств: {devices.Count}");
            StatusText.Text = $"Сканирование завершено. Устройств: {devices.Count}.";
            AppendEvent($"Сканирование устройств завершено: {devices.Count}.");
            ShowPanel("devices");
            return true;
        }
        catch (Exception ex)
        {
            SetScanProgress(0, "Сканирование прервано из-за ошибки.");
            StatusText.Text = "Сканирование не завершено.";
            MessageBox.Show(ex.Message, "Сканирование устройств", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { _scanRunning = false; }
    }

    private void SetScanProgress(int percentage, string status)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        ScanPercentText.Text = $"{percentage}%";
        StartupScanPercentCaption.Text = $"{percentage}% · {(percentage == 100 ? "ГОТОВО" : "СКАНИРОВАНИЕ")}";
        StartupScanStatusText.Text = status;

        const double center = 180;
        const double radius = 174;
        var geometry = new StreamGeometry();
        if (percentage > 0)
        {
            using (var context = geometry.Open())
            {
                var start = new Point(center, center - radius);
                var angle = (-90d + 3.6d * percentage) * Math.PI / 180d;
                var end = new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
                context.BeginFigure(start, false, false);
                context.ArcTo(end, new Size(radius, radius), 0,
                    percentage > 50, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
        }
        ScanProgressArc.Data = geometry;
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

    private async void Backup_Click(object sender, RoutedEventArgs e)
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
            if (_maintenanceRunning) return;
            _maintenanceRunning = true;
            var result = await _maintenance.ExportDriversAsync(dialog.FolderName);
            StatusText.Text = result;
            AppendEvent("Создана резервная копия драйверов: " + dialog.FolderName);
            MessageBox.Show(result, "Резервное копирование", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Операция отменена в запросе UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Резервное копирование", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _maintenanceRunning = false; }
    }

    private async void RestoreDrivers_Click(object sender, RoutedEventArgs e)
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
            if (_maintenanceRunning) return;
            _maintenanceRunning = true;
            var result = await _maintenance.RestoreDriversAsync(dialog.FolderName);
            StatusText.Text = result;
            AppendEvent("Выполнено восстановление драйверов из " + dialog.FolderName);
            MessageBox.Show(result, "Восстановление драйверов", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Восстановление отменено в UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Восстановление драйверов", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _maintenanceRunning = false; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DeviceInfo device)
        {
            MessageBox.Show("Сначала выберите конкретное устройство на вкладке «Устройства». Установка без выбранного устройства запрещена.",
                "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Выберите INF-файл пакета для выбранного устройства",
            Filter = "Файлы драйверов (*.inf)|*.inf",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_maintenanceRunning) return;

        try
        {
            _maintenanceRunning = true;
            StatusText.Text = "Проверка INF, Hardware ID, архитектуры и подписи каталога...";
            var report = await Task.Run(() => _infPackageInspector.Inspect(dialog.FileName, device));
            var warnings = report.Warnings.Count == 0
                ? "Замечаний нет."
                : string.Join(Environment.NewLine, report.Warnings.Select(x => "• " + x));
            var details =
                $"Устройство: {device.Name}{Environment.NewLine}" +
                $"INF: {Path.GetFileName(report.InfPath)}{Environment.NewLine}" +
                $"Поставщик: {report.Provider}{Environment.NewLine}" +
                $"Версия: {report.DriverVersion} ({report.DriverDate}){Environment.NewLine}" +
                $"Совпадение: {report.MatchKind} — {report.MatchedHardwareId}{Environment.NewLine}" +
                $"Архитектура: {(report.ArchitectureApplicable ? "подходит" : "не подходит")}{Environment.NewLine}" +
                $"Подпись CAT: {report.CatalogSignature}{Environment.NewLine}" +
                $"SHA-256 INF: {report.InfSha256}{Environment.NewLine}" +
                $"SHA-256 CAT: {(string.IsNullOrWhiteSpace(report.CatalogSha256) ? "не рассчитан" : report.CatalogSha256)}{Environment.NewLine}{Environment.NewLine}" +
                $"Замечания:{Environment.NewLine}{warnings}";

            if (!report.IsInstallableForDevice)
            {
                MessageBox.Show(details + Environment.NewLine + Environment.NewLine +
                    "Установка заблокирована: пакет не прошёл обязательные проверки.",
                    "Пакет не допущен к установке", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "Установка заблокирована проверкой INF-пакета.";
                AppendEvent($"Блокировка установки INF для {device.Name}: {report.MatchKind}, CAT={report.CatalogSignature}.");
                return;
            }

            if (MessageBox.Show(details + Environment.NewLine + Environment.NewLine +
                    "Совпадение Hardware ID точное, CAT-подпись действительна. Передать пакет Windows для установки?",
                    "Подтверждение установки драйвера", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var result = await _maintenance.InstallInfAsync(dialog.FileName);
            StatusText.Text = result;
            AppendEvent($"Установка INF для {device.Name}: {result}");
            MessageBox.Show(result, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Установка отменена в UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _maintenanceRunning = false; }
    }

}
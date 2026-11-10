using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
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
    private readonly DriverHealthAnalysisService _driverHealth = new();
    private readonly ObservableCollection<DriverHealthFinding> _driverHealthFindings = new();
    private readonly HardwareSensorService _sensors = new();
    private readonly GameOptimizationService _gameProfile = new();
    private readonly OemCatalogService _oemCatalog = new();
    private readonly OemDeviceIdentityService _oemIdentityService = new();
    private readonly OemDriverCatalogAdapter _oemCatalogAdapter = new();
    private readonly InternetDriverCatalogService _internetCatalog = new();
    private readonly DriverPackageMetadataService _packageMetadata = new();
    private readonly DriverPackageHashService _packageHash = new();
    private readonly DriverPackageDownloadService _packageDownloader = new();
    private readonly OfficialDriverPackageCandidateService _packageCandidates = new();
    private readonly DriverPackageIntakeService _packageIntake = new();
    private readonly DriverUpdateCatalogService _driverCatalog = new();
    private readonly ObservableCollection<DriverUpdateRecord> _driverCandidates = new();
    private readonly ObservableCollection<DriverBackupRecord> _driverBackups = new();\n    private readonly ObservableCollection<DriverInstallationRecord> _installationHistory = new();
    private OemDeviceIdentity? _oemIdentity;
    private bool _oemDetectRunning;
    private readonly RegistryMaintenanceService _registryService = new();
    private readonly JunkCleanupService _junkService = new();

    private readonly ObservableCollection<DeviceInfo> _devices = new();
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
        DriverHealthGrid.ItemsSource = _driverHealthFindings;
        RegistryGrid.ItemsSource = _registryFindings;
        JunkGrid.ItemsSource = _junkFiles;
        DriverCatalogGrid.ItemsSource = _driverCandidates;
        DriverBackupGrid.ItemsSource = _driverBackups;\n        InstallationHistoryGrid.ItemsSource = _installationHistory;

        var selectedTheme = ThemeManager.LoadPreference();
        ThemeSelector.SelectedValue = selectedTheme;
        SettingsThemeSelector.SelectedValue = selectedTheme;
        var theme = ThemeManager.Themes.FirstOrDefault(x => x.Id == selectedTheme) ?? ThemeManager.Themes[0];
        ThemeManager.Apply(theme);
        HeroArtwork.SetTheme(theme);
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
            await RefreshDriverCatalogAsync();
            await RefreshBackupCatalogAsync();
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
            HeroArtwork.SetTheme(theme);
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
                ["Online search for compatible packages by hardware IDs"]="Online search for compatible packages by hardware IDs",
                ["Windows startup entries diagnostics without silent changes"]="Windows startup entries diagnostics without silent changes",
                ["Conservative cleanup of old temporary files"]="Conservative cleanup of old temporary files",
                ["Application appearance and preferences"]="Application appearance and preferences"
            },
            ["de"] = new Dictionary<string,string> {
                ["Home"]="Startseite", ["Devices and drivers"]="Geräte und Treiber", ["Driver updates"]="Treiberaktualisierungen",
                ["Registry diagnostics"]="Registrierungsdiagnose", ["Computer cleanup"]="Computerbereinigung", ["Settings"]="Einstellungen",
                ["Computer monitoring and maintenance tools"]="Computerüberwachung und Wartungswerkzeuge",
                ["Plug and Play inventory, error filter and driver backup"]="Plug-and-Play-Inventar, Fehlerfilter und Treibersicherung",
                ["Online search for compatible packages by hardware IDs"]="Online-Suche nach kompatiblen Paketen anhand der Hardware-IDs",
                ["Windows startup entries diagnostics without silent changes"]="Diagnose der Windows-Autostart-Einträge ohne stille Änderungen",
                ["Conservative cleanup of old temporary files"]="Sichere Bereinigung alter temporärer Dateien",
                ["Application appearance and preferences"]="Darstellung und Einstellungen der Anwendung"
            },
            ["fr"] = new Dictionary<string,string> {
                ["Home"]="Accueil", ["Devices and drivers"]="Périphériques et pilotes", ["Driver updates"]="Mises à jour des pilotes",
                ["Registry diagnostics"]="Diagnostic du registre", ["Computer cleanup"]="Nettoyage de l’ordinateur", ["Settings"]="Paramètres",
                ["Computer monitoring and maintenance tools"]="Surveillance et maintenance de l’ordinateur",
                ["Plug and Play inventory, error filter and driver backup"]="Inventaire Plug-and-Play, filtre d’erreurs et sauvegarde des pilotes",
                ["Online search for compatible packages by hardware IDs"]="Recherche en ligne de packages compatibles par identifiants matériels",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostic du démarrage Windows sans modification silencieuse",
                ["Conservative cleanup of old temporary files"]="Nettoyage prudent des anciens fichiers temporaires",
                ["Application appearance and preferences"]="Apparence et préférences de l’application"
            },
            ["es"] = new Dictionary<string,string> {
                ["Home"]="Inicio", ["Devices and drivers"]="Dispositivos y controladores", ["Driver updates"]="Actualizaciones de controladores",
                ["Registry diagnostics"]="Diagnóstico del registro", ["Computer cleanup"]="Limpieza del equipo", ["Settings"]="Configuración",
                ["Computer monitoring and maintenance tools"]="Supervisión y mantenimiento del equipo",
                ["Plug and Play inventory, error filter and driver backup"]="Inventario Plug and Play, filtro de errores y copia de controladores",
                ["Online search for compatible packages by hardware IDs"]="Búsqueda en línea de paquetes compatibles por ID de hardware",
                ["Windows startup entries diagnostics without silent changes"]="Diagnóstico del inicio de Windows sin cambios silenciosos",
                ["Conservative cleanup of old temporary files"]="Limpieza prudente de archivos temporales antiguos",
                ["Application appearance and preferences"]="Apariencia y preferencias de la aplicación"
            },
            ["pt_BR"] = new Dictionary<string,string> {
                ["Home"]="Início", ["Devices and drivers"]="Dispositivos e drivers", ["Driver updates"]="Atualizações de drivers",
                ["Registry diagnostics"]="Diagnóstico do registro", ["Computer cleanup"]="Limpeza do computador", ["Settings"]="Configurações",
                ["Computer monitoring and maintenance tools"]="Monitoramento e manutenção do computador",
                ["Plug and Play inventory, error filter and driver backup"]="Inventário Plug and Play, filtro de erros e backup de drivers",
                ["Online search for compatible packages by hardware IDs"]="Pesquisa on-line de pacotes compatíveis por IDs de hardware",
                ["Windows startup entries diagnostics without silent changes"]="Diagnóstico da inicialização do Windows sem alterações silenciosas",
                ["Conservative cleanup of old temporary files"]="Limpeza segura de arquivos temporários antigos",
                ["Application appearance and preferences"]="Aparência e preferências do aplicativo"
            },
            ["it"] = new Dictionary<string,string> {
                ["Home"]="Home", ["Devices and drivers"]="Dispositivi e driver", ["Driver updates"]="Aggiornamenti driver",
                ["Registry diagnostics"]="Diagnostica del registro", ["Computer cleanup"]="Pulizia del computer", ["Settings"]="Impostazioni",
                ["Computer monitoring and maintenance tools"]="Monitoraggio e manutenzione del computer",
                ["Plug and Play inventory, error filter and driver backup"]="Inventario Plug and Play, filtro errori e backup driver",
                ["Online search for compatible packages by hardware IDs"]="Ricerca online di pacchetti compatibili tramite ID hardware",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostica dell’avvio di Windows senza modifiche silenziose",
                ["Conservative cleanup of old temporary files"]="Pulizia prudente dei vecchi file temporanei",
                ["Application appearance and preferences"]="Aspetto e preferenze dell’applicazione"
            },
            ["pl"] = new Dictionary<string,string> {
                ["Home"]="Strona główna", ["Devices and drivers"]="Urządzenia i sterowniki", ["Driver updates"]="Aktualizacje sterowników",
                ["Registry diagnostics"]="Diagnostyka rejestru", ["Computer cleanup"]="Czyszczenie komputera", ["Settings"]="Ustawienia",
                ["Computer monitoring and maintenance tools"]="Monitorowanie komputera i narzędzia konserwacji",
                ["Plug and Play inventory, error filter and driver backup"]="Lista urządzeń Plug and Play, filtr błędów i kopia sterowników",
                ["Online search for compatible packages by hardware IDs"]="Wyszukiwanie zgodnych pakietów według identyfikatorów sprzętu",
                ["Windows startup entries diagnostics without silent changes"]="Diagnostyka autostartu Windows bez cichych zmian",
                ["Conservative cleanup of old temporary files"]="Bezpieczne czyszczenie starych plików tymczasowych",
                ["Application appearance and preferences"]="Wygląd i preferencje aplikacji"
            },
            ["tr"] = new Dictionary<string,string> {
                ["Home"]="Ana Sayfa", ["Devices and drivers"]="Aygıtlar ve Sürücüler", ["Driver updates"]="Sürücü Güncellemeleri",
                ["Registry diagnostics"]="Kayıt Defteri Tanılama", ["Computer cleanup"]="Bilgisayar Temizliği", ["Settings"]="Ayarlar",
                ["Computer monitoring and maintenance tools"]="Bilgisayar izleme ve bakım araçları",
                ["Plug and Play inventory, error filter and driver backup"]="Tak ve Çalıştır envanteri, hata filtresi ve sürücü yedekleme",
                ["Online search for compatible packages by hardware IDs"]="Donanım kimlikleriyle uyumlu paketleri çevrimiçi arama",
                ["Windows startup entries diagnostics without silent changes"]="Windows başlangıç girdilerini sessiz değişiklik yapmadan tanılama",
                ["Conservative cleanup of old temporary files"]="Eski geçici dosyaların güvenli temizliği",
                ["Application appearance and preferences"]="Uygulama görünümü ve tercihleri"
            },
            ["uk"] = new Dictionary<string,string> {
                ["Home"]="Головна", ["Devices and drivers"]="Пристрої та драйвери", ["Driver updates"]="Оновлення драйверів",
                ["Registry diagnostics"]="Діагностика реєстру", ["Computer cleanup"]="Очищення комп’ютера", ["Settings"]="Налаштування",
                ["Computer monitoring and maintenance tools"]="Моніторинг комп’ютера та засоби обслуговування",
                ["Plug and Play inventory, error filter and driver backup"]="Інвентаризація Plug and Play, фільтр помилок і резервна копія драйверів",
                ["Online search for compatible packages by hardware IDs"]="Онлайн-пошук сумісних пакетів за апаратними ідентифікаторами",
                ["Windows startup entries diagnostics without silent changes"]="Перевірка автозапуску Windows без непомітних змін",
                ["Conservative cleanup of old temporary files"]="Безпечне очищення старих тимчасових файлів",
                ["Application appearance and preferences"]="Вигляд і параметри програми"
            },
            ["zh_CN"] = new Dictionary<string,string> {
                ["Home"]="主页", ["Devices and drivers"]="设备和驱动程序", ["Driver updates"]="驱动程序更新",
                ["Registry diagnostics"]="注册表诊断", ["Computer cleanup"]="电脑清理", ["Settings"]="设置",
                ["Computer monitoring and maintenance tools"]="电脑监控和维护工具",
                ["Plug and Play inventory, error filter and driver backup"]="即插即用设备清单、错误筛选和驱动备份",
                ["Online search for compatible packages by hardware IDs"]="通过硬件 ID 在线搜索兼容驱动包",
                ["Windows startup entries diagnostics without silent changes"]="检查 Windows 启动项，不进行静默修改",
                ["Conservative cleanup of old temporary files"]="安全清理旧临时文件",
                ["Application appearance and preferences"]="应用外观和偏好设置"
            },
            ["ja"] = new Dictionary<string,string> {
                ["Home"]="ホーム", ["Devices and drivers"]="デバイスとドライバー", ["Driver updates"]="ドライバーの更新",
                ["Registry diagnostics"]="レジストリ診断", ["Computer cleanup"]="PC のクリーンアップ", ["Settings"]="設定",
                ["Computer monitoring and maintenance tools"]="PC の監視とメンテナンスツール",
                ["Plug and Play inventory, error filter and driver backup"]="Plug and Play インベントリ、エラーフィルター、ドライバーのバックアップ",
                ["Online search for compatible packages by hardware IDs"]="ハードウェア ID による互換パッケージのオンライン検索",
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
            "drivers" => (Tr("Обновления драйверов", "Driver updates"), Tr("Онлайн-поиск совместимых пакетов по аппаратным идентификаторам", "Online search for compatible packages by hardware IDs")),
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
        StartupScanOverlay.Visibility = Visibility.Collapsed;
        try
        {
            if (!await ScanDevicesAsync()) return;
            if (_devices.Count == 0) return;

            var errors = _devices.Count(IsProblemDevice);
            var summary = $"Проверено устройств: {_devices.Count}. Устройств с ошибками: {errors}.";
            var answer = MessageBox.Show(
                summary + "\\n\\nТеперь запустить проверку актуальности драйверов через Snappy Driver Installer Origin? Будут загружены индексы и подобраны совместимые варианты. Автоматическая установка отключена.",
                "Проверка актуальности драйверов", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                await LaunchDriverUpdatesAsync();
        }
        finally
        {
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
            await RefreshDriverHealthAsync(devices);
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


    private async Task RefreshDriverHealthAsync(IReadOnlyCollection<DeviceInfo> devices)
    {
        var report = await _driverHealth.AnalyzeAndStoreAsync(devices);
        _driverHealthFindings.Clear();
        foreach (var finding in report.Findings.OrderByDescending(x => x.Severity == "Критично"))
            _driverHealthFindings.Add(finding);
        DriverHealthSummaryText.Text = $"Устройств: {report.TotalDevices} | Без замечаний: {report.HealthyDevices} | Проблемы PnP: {report.ProblemDevices} | Нет данных о драйвере: {report.MissingDriverDevices} | Неподписанные: {report.UnsignedDriverDevices}";
        DriverHealthChangesText.Text = $"Новые замечания: {report.NewIssues.Count}; устранено с прошлого сканирования: {report.ResolvedIssues.Count}; отчёт сохранён локально.";
        AppendEvent($"Анализ состояния драйверов: замечаний {report.Findings.Count}, новых {report.NewIssues.Count}, устранено {report.ResolvedIssues.Count}.");
    }

    private async void RefreshDriverHealth_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0) { MessageBox.Show("Сначала выполните сканирование устройств.", "Состояние драйверов", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try { await RefreshDriverHealthAsync(_devices.ToArray()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Состояние драйверов", MessageBoxButton.OK, MessageBoxImage.Error); }
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
            await RefreshDriverHealthAsync(devices);
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

    private void MatchLocalDriverPackages_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0)
        {
            MessageBox.Show("Сначала выполните сканирование устройств.", "Сопоставление драйверов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку с распакованными пакетами драйверов",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var matches = _packageMetadata.MatchFolder(dialog.FolderName, _devices);
            var relevant = matches.Where(x => x.HardwareIdMatch || x.Reasons.Count > 0)
                .Take(80).ToArray();
            if (relevant.Length == 0)
            {
                MessageBox.Show("В выбранной папке не найдены INF-пакеты для анализа.",
                    "Сопоставление драйверов", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var report = string.Join(Environment.NewLine + Environment.NewLine, relevant.Select(x =>
                $"{x.Device.Name}\nINF: {Path.GetFileName(x.Package.InfPath)}\n" +
                $"Поставщик: {x.Package.Provider}; версия: {x.Package.DriverVersion}; дата: {x.Package.DriverDate:yyyy-MM-dd}\n" +
                $"Hardware ID: {(x.HardwareIdMatch ? "СОВПАДАЕТ" : "нет точного совпадения")}\n" +
                $"Архитектура: {(x.ArchitectureMatch ? "подходит" : "не подходит")}; Windows build: {(x.WindowsBuildMatch ? "подходит" : "не подходит")}\n" +
                $"Подпись CAT: {(x.SignatureValid ? "действительна" : "НЕ ПОДТВЕРЖДЕНА")}\n" +
                $"Версии: установлена {x.Device.DriverVersion}; кандидат {x.Package.DriverVersion}. {x.VersionAssessment}\n" +
                $"{x.Verdict}" + (x.Reasons.Count > 0 ? "\nПричины: " + string.Join("; ", x.Reasons) : "")));
            MessageBox.Show(report, "Сопоставление локальных пакетов драйверов (без установки)",
                MessageBoxButton.OK, MessageBoxImage.Information);
            AppendEvent($"Выполнено сопоставление локальных INF: результатов {relevant.Length}; установка не запускалась.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Сопоставление драйверов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CalculatePackageHashes_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку с драйверными пакетами",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            UpdatesCountText.Text = "Расчёт SHA-256 для файлов пакетов...";
            var hashes = await _packageHash.CalculateFolderAsync(dialog.FolderName);
            if (hashes.Count == 0)
            {
                MessageBox.Show("Доступные файлы для расчёта хэша не найдены.",
                    "Контроль целостности", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var report = string.Join(Environment.NewLine, hashes.Select(x =>
                $"{Path.GetRelativePath(dialog.FolderName, x.FilePath)} | {x.SizeBytes} байт | SHA-256: {x.Sha256}"));
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить манифест SHA-256",
                Filter = "Текстовый файл (*.txt)|*.txt",
                FileName = "driver-sha256-manifest.txt"
            };
            if (save.ShowDialog(this) == true)
                await File.WriteAllTextAsync(save.FileName, report, new System.Text.UTF8Encoding(false));

            UpdatesCountText.Text = $"Рассчитаны SHA-256: {hashes.Count} файлов.";
            AppendEvent($"Сформирован SHA-256 манифест: файлов {hashes.Count}; установка не выполнялась.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Контроль целостности", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DownloadOfficialPackage_Click(object sender, RoutedEventArgs e)
    {
        var url = Microsoft.VisualBasic.Interaction.InputBox(
            "Вставьте прямую HTTPS-ссылку на файл драйвера с официального домена производителя.\n" +
            "Страницы поиска и сторонние файлообменники не принимаются.",
            "Загрузка официального пакета драйвера", "");
        if (string.IsNullOrWhiteSpace(url)) return;

        var folder = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для сохранения драйверного пакета",
            Multiselect = false
        };
        if (folder.ShowDialog(this) != true) return;

        try
        {
            UpdatesCountText.Text = "Загрузка пакета с проверкой домена и размера...";
            var result = await _packageDownloader.DownloadAsync(url.Trim(), folder.FolderName);
            UpdatesCountText.Text = $"Загружен файл: {Path.GetFileName(result.FilePath)}";
            AppendEvent($"Пакет загружен с {result.SourceHost}; {result.SizeBytes} байт; SHA-256 {result.Sha256}.");
            MessageBox.Show(
                $"Файл: {result.FilePath}\nРазмер: {result.SizeBytes} байт\nSHA-256: {result.Sha256}\n\n" +
                "Файл сохранён, но не установлен. Перед установкой выполните сопоставление INF и проверку подписи CAT.",
                "Загрузка драйвера завершена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            UpdatesCountText.Text = "Загрузка пакета не выполнена.";
            MessageBox.Show(ex.Message, "Загрузка драйвера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            var backupRecord = _driverCatalog.RegisterBackupAsync(dialog.FolderName, Path.GetFileName(dialog.FolderName)).GetAwaiter().GetResult();
            _ = RefreshBackupCatalogAsync();
            StatusText.Text = result + Environment.NewLine + $"В каталоге резервных копий: {backupRecord.PackageCount} пакетов; SHA-256 записан в манифест.";
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
        if (_scanRunning) return;
        await LaunchDriverUpdatesAsync();
    }

    private async Task RefreshDriverCatalogAsync()
    {
        var records = await _driverCatalog.GetRecordsAsync();
        _driverCandidates.Clear();
        foreach (var record in records) _driverCandidates.Add(record);
        DriverCatalogSummaryText.Text = $"В каталоге: {records.Count}; готовы к рассмотрению: {records.Count(x => x.IsActionable)}; требуют анализа/извлечения: {records.Count(x => x.State == nameof(DriverCandidateState.Staged))}.";
    }

    private async Task RefreshBackupCatalogAsync()
    {
        var records = await _driverCatalog.GetBackupsAsync();
        _driverBackups.Clear();
        foreach (var record in records.OrderByDescending(x => x.CreatedUtc)) _driverBackups.Add(record);
    }


    private async Task RefreshInstallationHistoryAsync()
    {
        var records = await _driverCatalog.GetInstallationHistoryAsync();
        _installationHistory.Clear();
        foreach (var record in records) _installationHistory.Add(record);
    }

    private async void RefreshDriverCatalog_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshDriverCatalogAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Каталог обновлений", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RefreshBackupCatalog_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshBackupCatalogAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RemoveDriverCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (DriverCatalogGrid.SelectedItem is not DriverUpdateRecord selected) return;
        if (MessageBox.Show($"Удалить запись кандидата «{selected.PackageTitle}» из каталога? Файлы пакета и манифест не удаляются.",
            "Каталог обновлений", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _driverCatalog.RemoveAsync(selected.Id);
            await RefreshDriverCatalogAsync();
            AppendEvent("Удалена запись кандидата из каталога: " + selected.PackageTitle);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Каталог обновлений", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void InstallSelectedCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (DriverCatalogGrid.SelectedItem is not DriverUpdateRecord selected)
        {
            MessageBox.Show("Выберите кандидата в каталоге обновлений.", "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.State != nameof(DriverCandidateState.ReadyForInstall))
        {
            MessageBox.Show("Установка заблокирована: кандидат не имеет статуса ReadyForInstall. Сначала завершите распаковку, сопоставление INF и проверку подписи CAT.",
                "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var device = _devices.FirstOrDefault(x => string.Equals(x.InstanceId, selected.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            MessageBox.Show("Устройство отсутствует в текущей инвентаризации. Выполните сканирование устройств.",
                "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var expanded = Path.Combine(selected.StagingDirectory, "expanded");
        if (!Directory.Exists(expanded))
        {
            MessageBox.Show("В пакете нет распакованной папки INF. Для этого формата требуется поддерживаемое извлечение.",
                "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var matches = _packageMetadata.MatchFolder(expanded, new[] { device });
            var applicable = matches.Where(x => x.SignatureValid == true &&
                x.HardwareIdMatch && x.ArchitectureMatch && x.WindowsBuildMatch).ToArray();
            if (applicable.Length == 0)
                throw new InvalidDataException("Не найден INF, одновременно соответствующий Hardware ID, архитектуре, сборке Windows и действительной подписи CAT.");
            var chosen = applicable.OrderByDescending(x => x.VersionAssessment, StringComparer.OrdinalIgnoreCase).First();
            var inf = Path.GetFullPath(chosen.Package.InfPath);
            if (!inf.StartsWith(Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь INF выходит за пределы каталога распаковки.");
            var answer = MessageBox.Show(
                $"Устройство: {device.Name}\nТекущий драйвер: {device.DriverVersion}\nНовый драйвер: {chosen.Package.DriverVersion}\nINF: {inf}\n\nПодпись CAT подтверждена. Совместимость по Hardware ID, архитектуре и Windows подтверждена. Передать пакет Windows для установки?",
                "Подтверждение установки драйвера", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            StatusText.Text = "Запуск установки через pnputil. Windows запросит права администратора.";
            var result = await Task.Run(() => _maintenance.InstallInf(inf));
            await _driverCatalog.SetStateAsync(selected.Id, DriverCandidateState.Installed,
                new[] { "Команда pnputil завершилась успешно; фактическую активную версию следует подтвердить после обновления инвентаризации." });
            await _driverCatalog.AddInstallationRecordAsync(new DriverInstallationRecord
            {
                DeviceName = device.Name, DeviceInstanceId = device.InstanceId, Provider = selected.Provider,
                PreviousVersion = device.DriverVersion, RequestedVersion = chosen.Package.DriverVersion,
                InfPath = inf, CandidateId = selected.Id, Outcome = "Команда pnputil завершилась успешно",
                RebootStatus = "Не определён — требуется оценка Windows",
                Details = result
            });
            await RefreshDriverCatalogAsync();
            await RefreshInstallationHistoryAsync();
            AppendEvent($"В журнал записан результат передачи пакета Windows: {device.Name}; INF {Path.GetFileName(inf)}.");
            MessageBox.Show(result, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception) { StatusText.Text = "Установка отменена в запросе UAC."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Установка драйвера", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenDriverManifest_Click(object sender, RoutedEventArgs e)
    {
        if (DriverCatalogGrid.SelectedItem is not DriverUpdateRecord selected) return;
        var path = File.Exists(selected.ManifestPath) ? selected.ManifestPath :
            File.Exists(Path.Combine(selected.StagingDirectory, "package-manifest.json"))
                ? Path.Combine(selected.StagingDirectory, "package-manifest.json") : "";
        if (path.Length == 0)
        {
            MessageBox.Show("Для выбранного кандидата манифест ещё не создан.", "Каталог обновлений", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void VerifyBackupCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (DriverBackupGrid.SelectedItem is not DriverBackupRecord selected) return;
        try
        {
            var issues = await _driverCatalog.VerifyBackupAsync(selected.DirectoryPath);
            MessageBox.Show(issues.Count == 0
                ? $"Проверка завершена. SHA-256 совпадает для всех {selected.PackageCount} пакетов."
                : "Обнаружены проблемы:\n" + string.Join("\n", issues.Take(30)),
                "Проверка резервной копии", MessageBoxButton.OK,
                issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Проверка резервной копии", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void SearchInternetCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning) return;
        if (_devices.Count == 0)
        {
            var scanned = await _inventory.ScanAsync();
            ApplyDevices(scanned);
        }

        var eligible = _devices.Where(d => d.HardwareIds.Length > 0)
            .OrderByDescending(IsProblemDevice)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (eligible.Length == 0)
        {
            MessageBox.Show("Не найдены устройства с аппаратными идентификаторами для поиска.",
                "Интернет-каталог драйверов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _scanRunning = true;
        try
        {
            UpdatesCountText.Text = $"Поиск официальных страниц драйверов для {eligible.Length} устройств...";
            var results = await _internetCatalog.SearchAsync(eligible);
            if (results.Count == 0)
            {
                UpdatesCountText.Text = "Официальные страницы по заданным Hardware ID не найдены.";
                AppendEvent("Интернет-поиск драйверов: совпадений на сайтах производителей нет.");
                return;
            }

            var menu = string.Join(Environment.NewLine, results.Take(30).Select((item, index) =>
                $"{index + 1}. {item.DeviceName} — {item.Title} ({new Uri(item.Url).Host})"));
            var answer = Microsoft.VisualBasic.Interaction.InputBox(
                $"Найдено страниц: {results.Count}. Выберите номер для открытия.\n" +
                "Это результаты поиска, а не подтверждение совместимости пакета.\n\n" + menu,
                "Результаты интернет-каталога", "1");
            if (!int.TryParse(answer, out var number) || number < 1 || number > Math.Min(results.Count, 30))
                return;

            var selected = results[number - 1];
            var selectedDevice = eligible.FirstOrDefault(device =>
                string.Equals(device.Name, selected.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                device.HardwareIds.Concat(device.CompatibleIds).Contains(selected.HardwareId, StringComparer.OrdinalIgnoreCase));
            if (selectedDevice is null)
            {
                MessageBox.Show("Не удалось связать страницу каталога с устройством из текущей инвентаризации.",
                    "Интернет-каталог драйверов", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(selected.Url) { UseShellExecute = true });
            UpdatesCountText.Text = $"Страница производителя открыта: {selected.DeviceName}. Поиск прямых ссылок на пакеты...";
            AppendEvent($"Открыта страница драйвера: {selected.DeviceName}; {selected.HardwareId}.");

            IReadOnlyList<OfficialDriverPackageCandidate> candidates;
            try { candidates = await _packageCandidates.DiscoverAsync(selected); }
            catch (Exception discoveryError) when (discoveryError is HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                UpdatesCountText.Text = "Страница открыта. Автоматически извлечь ссылки на пакеты не удалось.";
                AppendEvent("Извлечение прямых ссылок недоступно: " + discoveryError.Message);
                return;
            }

            if (candidates.Count == 0)
            {
                UpdatesCountText.Text = "На странице не найдены прямые ссылки на установочные пакеты. Используйте сайт производителя.";
                AppendEvent("Прямые ссылки на пакеты на странице не обнаружены.");
                return;
            }

            var catalogRecords = new List<DriverUpdateRecord>();
            foreach (var item in candidates)
                catalogRecords.Add(await _driverCatalog.UpsertCandidateAsync(selectedDevice, item.Provider,
                    item.Title, item.Url, item.SourcePage));
            await RefreshDriverCatalogAsync();
            var packageMenu = string.Join(Environment.NewLine, candidates.Select((item, index) =>
                $"{index + 1}. {item.Title} [{item.FileType}] — {new Uri(item.Url).Host}"));
            var packageAnswer = Microsoft.VisualBasic.Interaction.InputBox(
                "Найдены прямые ссылки на файлы пакетов. Выберите номер для загрузки:" +
                "\n\n" + packageMenu +
                "\n\nСсылки извлечены со страницы производителя, но совместимость пакета ещё не подтверждена.",
                "Пакеты драйверов", "1");
            if (!int.TryParse(packageAnswer, out var packageNumber) ||
                packageNumber < 1 || packageNumber > candidates.Count) return;

            var package = candidates[packageNumber - 1];
            var candidateRecord = catalogRecords[packageNumber - 1];
            var targetDevice = selectedDevice;
            if (targetDevice is null)
            {
                MessageBox.Show("Устройство, для которого выполнялся поиск, больше не присутствует в текущем списке.",
                    "Пакет драйвера", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var folder = Microsoft.VisualBasic.Interaction.InputBox(
                "Укажите корневую папку для хранения пакетов и манифестов:",
                "Загрузка и анализ пакета", downloads);
            if (string.IsNullOrWhiteSpace(folder)) return;

            UpdatesCountText.Text = "Загрузка, SHA-256, подготовка пакета и анализ INF...";
            await _driverCatalog.SetStateAsync(candidateRecord.Id, DriverCandidateState.Downloading);
            var intake = await _packageIntake.AcquireAndAnalyzeAsync(package, targetDevice, folder);
            await _driverCatalog.UpdateStageAsync(candidateRecord.Id, intake);
            await RefreshDriverCatalogAsync();
            var matchSummary = intake.Matches.Count == 0
                ? "Совпадения INF: не обнаружены."
                : string.Join(Environment.NewLine, intake.Matches.Take(12).Select(match =>
                    $"{match.Package.DriverVersion} | {match.Verdict} | подпись CAT: {(match.SignatureValid ? "подтверждена" : "не подтверждена")}"));
            UpdatesCountText.Text = $"Пакет подготовлен: {Path.GetFileName(intake.PackagePath)}; найдено INF-сопоставлений: {intake.Matches.Count}.";
            AppendEvent($"Пакет подготовлен: {package.Title}; SHA-256 {intake.Sha256}; INF-сопоставлений {intake.Matches.Count}.");
            MessageBox.Show(
                $"Файл: {intake.PackagePath}\nРазмер: {intake.SizeBytes:N0} байт\nSHA-256: {intake.Sha256}\n\n{matchSummary}\n\nСледующий шаг: {intake.NextAction}\n\nМанифест: {intake.ManifestPath}\n\nПакет не установлен.",
                "Пакет драйвера подготовлен", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            UpdatesCountText.Text = "Интернет-поиск не завершён.";
            MessageBox.Show(ex.Message, "Интернет-каталог драйверов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _scanRunning = false; }
    }

    private async Task LaunchDriverUpdatesAsync()
    {
        if (_scanRunning) return;
        _scanRunning = true;
        try
        {
            ShowPanel("drivers");
            UpdatesCountText.Text = "Подготовка проверки актуальности драйверов...";
            if (_devices.Count == 0)
            {
                var devices = await _inventory.ScanAsync();
                ApplyDevices(devices);
            }
            UpdatesCountText.Text = $"Устройств найдено: {_devices.Count}. Запуск проверки версий драйверов...";
            if (MessageBox.Show(
                "Будет загружен официальный Snappy Driver Installer Origin (SDIO), архив будет проверен по SHA-256. SDIO обновит индексы и сравнит установленные драйверы с доступными совместимыми пакетами. Автоматическая установка отключена: вы сможете просмотреть найденные варианты и подтвердить только нужные обновления вручную.",
                "Поиск актуальных драйверов", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
            UpdatesCountText.Text = "Загрузка индексов и поиск совместимых драйверов...";
            var result = await new SdioEngineService().DownloadAndLaunchAsync();
            UpdatesCountText.Text = result;
            StatusText.Text = result;
            AppendEvent("Выполнен поиск актуальных драйверов через проверенный движок SDIO.");
        }
        catch (Exception ex)
        {
            UpdatesCountText.Text = "Поиск драйверов не завершён.";
            MessageBox.Show(ex.Message, "DjShrek-Draiver", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _scanRunning = false; }
    }

    private async void DetectOem()
    {
        if (_oemDetectRunning) return;
        _oemDetectRunning = true;
        OemDetectText.Text = "Получение модели, ProductNumber и BIOS-идентификаторов...";
        try
        {
            _oemIdentity = await _oemIdentityService.GetIdentityAsync();
            var identity = _oemIdentity;
            _detectedOem = _oemCatalog.Detect(
                string.Join(" ", identity.Manufacturer, identity.Model, identity.ProductName,
                    identity.ProductNumber, identity.BoardManufacturer, identity.BoardProduct,
                    identity.BiosManufacturer));
            var vendor = string.IsNullOrWhiteSpace(identity.Vendor) ? "Не определён" : identity.Vendor;
            var model = string.IsNullOrWhiteSpace(identity.Model) ? identity.ProductName : identity.Model;
            var productNumber = string.IsNullOrWhiteSpace(identity.ProductNumber)
                ? "не предоставлен BIOS/SMBIOS" : identity.ProductNumber;
            OemDetectText.Text =
                $"Производитель: {vendor}\nМодель: {model}\nProductNumber: {productNumber}\n" +
                $"Плата: {identity.BoardManufacturer} {identity.BoardProduct}\nBIOS: {identity.BiosVersion}";
            OemIdentityText.Text = string.IsNullOrWhiteSpace(identity.SearchModel)
                ? "Идентификаторы модели отсутствуют. Используйте серийный номер/Service Tag на сайте производителя."
                : "Ключ модели для каталога: " + identity.SearchModel;
            AppendEvent($"OEM-идентификация завершена: {vendor}; модель {model}; ProductNumber {productNumber}.");
        }
        catch (Exception ex)
        {
            _detectedOem = _oemCatalog.Detect(ComputerValue.Text ?? "");
            OemDetectText.Text = _detectedOem is null
                ? "Не удалось получить OEM-данные. Выберите каталог производителя вручную."
                : $"Производитель: {_detectedOem.Name}. Детальная модель недоступна.";
            OemIdentityText.Text = "Расширенные BIOS-идентификаторы недоступны: " + ex.Message;
        }
        finally { _oemDetectRunning = false; }
    }

    private void OpenDetectedOem_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedOem is null) { OpenOemCatalog_Click(sender, e); return; }
        try
        {
            var url = _oemIdentity is not null && !string.IsNullOrWhiteSpace(_oemIdentity.OfficialSupportUrl)
                ? _oemIdentity.OfficialSupportUrl : _detectedOem.Url;
            Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Каталог драйверов", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenModelCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_oemIdentity is null)
        {
            MessageBox.Show("Сначала дождитесь завершения определения модели компьютера.",
                "Каталоги по модели", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = _oemCatalogAdapter.BuildTargets(_oemIdentity, _devices);
        if (targets.Count == 0)
        {
            MessageBox.Show("Для обнаруженного оборудования пока нет подходящих ссылок. Откройте общий каталог производителя.",
                "Каталоги по модели", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu = string.Join(Environment.NewLine, targets.Select((item, index) =>
            $"{index + 1}. {item.Name} — {item.MatchKey}: {item.Query}"));
        var answer = Microsoft.VisualBasic.Interaction.InputBox(
            "Выберите каталог для обнаруженной модели/устройства:\n" + menu,
            "Каталоги драйверов по модели", "1");
        if (!int.TryParse(answer, out var number) || number < 1 || number > targets.Count) return;

        try
        {
            var selected = targets[number - 1];
            Process.Start(new ProcessStartInfo(selected.Url) { UseShellExecute = true });
            AppendEvent("Открыт каталог драйверов: " + selected.Name + "; " + selected.MatchKey);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Каталоги по модели", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
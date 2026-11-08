using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using DjShrek.Draiver.App.Services;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunUiSmokeTestAsync();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private async Task RunUiSmokeTestAsync()
    {
        var checks = new List<string>();
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var logRoot = string.IsNullOrWhiteSpace(runnerTemp) ? Path.GetTempPath() : runnerTemp;
        var logPath = Path.Combine(logRoot, "DjShrek-Draiver-self-test.log");
        try
        {
            var window = new MainWindow();
            var requiredPanels = new[]
            {
                "DashboardPanel", "DevicesPanel", "DriversPanel",
                "RegistryPanel", "CleanupPanel", "SettingsPanel"
            };
            foreach (var name in requiredPanels)
            {
                if (window.FindName(name) is null)
                    throw new InvalidOperationException("Missing application screen: " + name);
            }
            checks.Add("PASS: six WPF screens loaded.");

            // Pure matching checks: exact hardware/compatible IDs match; substrings never do.
            var matcherDevices = new[]
            {
                new DeviceInfo
                {
                    Name = "Test GPU",
                    HardwareIds = new[] { "PCI\\VEN_10DE&DEV_1234" },
                    CompatibleIds = new[] { "PCI\\VEN_10DE&CC_030000" }
                }
            };
            if (!ReferenceEquals(DriverCompatibilityMatcher.FindExactMatch(matcherDevices, "PCI\\VEN_10DE&DEV_1234"), matcherDevices[0]))
                throw new InvalidOperationException("Exact hardware ID was not matched.");
            if (!ReferenceEquals(DriverCompatibilityMatcher.FindExactMatch(matcherDevices, "pci\\ven_10de&cc_030000"), matcherDevices[0]))
                throw new InvalidOperationException("Case-insensitive compatible ID was not matched.");
            if (DriverCompatibilityMatcher.FindExactMatch(matcherDevices, "PCI\\VEN_10DE") is not null)
                throw new InvalidOperationException("Partial hardware ID was incorrectly accepted.");
            if (DriverCompatibilityMatcher.FindExactMatch(matcherDevices, "") is not null)
                throw new InvalidOperationException("Empty hardware ID was incorrectly accepted.");
            checks.Add("PASS: exact Hardware ID matcher accepts full IDs and rejects partial/empty IDs.");            var ambiguousDevices = new[]
            {
                matcherDevices[0],
                new DeviceInfo
                {
                    Name = "Second GPU",
                    HardwareIds = new[] { "PCI\\VEN_10DE&DEV_5678" },
                    CompatibleIds = new[] { "PCI\\VEN_10DE&CC_030000" }
                }
            };
            if (DriverCompatibilityMatcher.FindExactMatch(ambiguousDevices, "PCI\\VEN_10DE&CC_030000") is not null)
                throw new InvalidOperationException("A shared compatible ID was incorrectly assigned to one of multiple devices.");
            if (!ReferenceEquals(DriverCompatibilityMatcher.FindExactMatch(ambiguousDevices, "PCI\\VEN_10DE&DEV_5678"), ambiguousDevices[1]))
                throw new InvalidOperationException("A unique exact hardware ID was not matched when another device shared only a compatible ID.");
            checks.Add("PASS: ambiguous shared compatible IDs are rejected while unique hardware IDs remain matchable.");

            if (!InternetDriverCatalogService.IsOfficialVendorUrl("https://download.nvidia.com/driver.exe") ||
                !InternetDriverCatalogService.IsOfficialVendorUrl("https://support.lenovo.com/driver") ||
                InternetDriverCatalogService.IsOfficialVendorUrl("http://download.nvidia.com/driver.exe") ||
                InternetDriverCatalogService.IsOfficialVendorUrl("https://nvidia.com.attacker.example/driver") ||
                InternetDriverCatalogService.IsOfficialVendorUrl("https://attacker-intel.com/driver") ||
                InternetDriverCatalogService.IsOfficialVendorUrl("javascript:alert(1)"))
                throw new InvalidOperationException("Official vendor URL validation accepted an unsafe URL or rejected a valid vendor URL.");
            checks.Add("PASS: official vendor URL allowlist accepts legitimate subdomains and rejects lookalike, HTTP and script URLs.");
            var vendorDevice = new DeviceInfo { Manufacturer = "HP" };
            if (InternetDriverCatalogService.GetVendorDomain(vendorDevice, "USB\\\\VID_1234&PID_5678") != "hp.com")
                throw new InvalidOperationException("Short OEM name HP was not mapped to its official domain.");
            vendorDevice = new DeviceInfo { Manufacturer = "Micro-Star International" };
            if (InternetDriverCatalogService.GetVendorDomain(vendorDevice, "PCI\\\\VEN_1234&DEV_5678") != "msi.com")
                throw new InvalidOperationException("Micro-Star manufacturer name was not mapped to MSI.");
            vendorDevice = new DeviceInfo { Manufacturer = "Desktop Chipset HPQ-123" };
            if (InternetDriverCatalogService.GetVendorDomain(vendorDevice, "PCI\\\\VEN_1234&DEV_5678") is not null)
                throw new InvalidOperationException("Incidental HP substring incorrectly selected HP support.");
            vendorDevice = new DeviceInfo { Manufacturer = "Unknown" };
            if (InternetDriverCatalogService.GetVendorDomain(vendorDevice, "PCI\\\\VEN_10DE&DEV_1234") != "nvidia.com")
                throw new InvalidOperationException("PCI vendor ID did not take priority for NVIDIA.");
            checks.Add("PASS: OEM/component vendor routing handles HP/MSI names, rejects incidental substrings, and prioritizes PCI vendor IDs.");


            var matchedCandidate = new DriverUpdateCandidate
            {
                Compatibility = DriverUpdateCandidate.HardwareIdSearchNotice,
                SourceUrl = "https://support.lenovo.com/driver"
            };
            if (!DjShrek.Draiver.App.MainWindow.IsCandidateOpenable(matchedCandidate))
                throw new InvalidOperationException("A matched Hardware ID search candidate cannot open its official manufacturer page.");
            var unmatchedCandidate = new DriverUpdateCandidate
            {
                Compatibility = DriverUpdateCandidate.UnmatchedDeviceNotice,
                SourceUrl = "https://support.lenovo.com/driver"
            };
            if (DjShrek.Draiver.App.MainWindow.IsCandidateOpenable(unmatchedCandidate))
                throw new InvalidOperationException("An unmatched device was incorrectly marked as a matched search candidate.");
            checks.Add("PASS: UI action is enabled for a matched Hardware ID and official URL, and blocked for an unmatched device.");
            
            // OEM detection must identify the system builder, not a component vendor.
            var oemCatalog = new OemCatalogService();
            if (oemCatalog.Detect("Dell Inc. XPS 15")?.Name != "Dell")
                throw new InvalidOperationException("Dell system manufacturer was not detected.");
            if (oemCatalog.Detect("Hewlett-Packard HP Inc.")?.Name != "HP")
                throw new InvalidOperationException("Hewlett-Packard system manufacturer was not detected.");
            if (oemCatalog.Detect("Lenovo ThinkPad")?.Name != "Lenovo")
                throw new InvalidOperationException("Lenovo system manufacturer was not detected.");
            if (oemCatalog.Detect("Intel Corporation") is not null ||
                oemCatalog.Detect("NVIDIA Corporation") is not null ||
                oemCatalog.Detect("Advanced Micro Devices") is not null)
                throw new InvalidOperationException("A CPU/GPU component vendor was incorrectly classified as the PC OEM.");
            if (oemCatalog.Detect("Desktop Chipset HPQ-123") is not null)
                throw new InvalidOperationException("An incidental HP substring was incorrectly classified as HP.");
            checks.Add("PASS: OEM detection identifies PC manufacturers and rejects CPU/GPU vendors and incidental substrings.");

            if (ThemeManager.Themes.Count != 12)
                throw new InvalidOperationException($"Expected 12 themes, got {ThemeManager.Themes.Count}.");

            var themeSelector = (ComboBox?)window.FindName("ThemeSelector")
                ?? throw new InvalidOperationException("Theme selector is missing.");
            var settingsSelector = (ComboBox?)window.FindName("SettingsThemeSelector")
                ?? throw new InvalidOperationException("Settings theme selector is missing.");

            foreach (var selector in new[] { themeSelector, settingsSelector })
            {
                if (selector.Items.Count != 12)
                    throw new InvalidOperationException($"Theme combo contains {selector.Items.Count} entries.");
                if (selector.DisplayMemberPath != "Name" || selector.SelectedValuePath != "Id")
                    throw new InvalidOperationException("Theme selector must display Name and select by Id.");
            }

            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme);
                var brush = Application.Current.Resources["AccentBrush"] as SolidColorBrush
                    ?? throw new InvalidOperationException("AccentBrush is missing.");
                var expected = (Color)ColorConverter.ConvertFromString(theme.Accent);
                if (brush.Color != expected)
                    throw new InvalidOperationException("Theme did not apply: " + theme.Name);
                themeSelector.SelectedValue = theme.Id;
                settingsSelector.SelectedValue = theme.Id;
            }

            var heroDeviceCount = window.FindName("HeroDeviceCountText") as TextBlock;
            var heroIssueCount = window.FindName("HeroIssueCountText") as TextBlock;
            var heroScanStatus = window.FindName("HeroScanStatusText") as TextBlock;
            if (heroDeviceCount is null || heroIssueCount is null || heroScanStatus is null)
                throw new InvalidOperationException("Live hardware status panel is incomplete.");
            if (Application.Current.Resources["PanelCornerRadius"] is not CornerRadius)
                throw new InvalidOperationException("Theme-specific panel geometry resource is missing.");
            checks.Add("PASS: all twelve named themes applied to dynamic WPF resources.");

            // Read-only Windows integration checks: no registry edits, file deletes, or driver installs.
            var inventory = new DeviceInventoryService();
            var metrics = await inventory.GetSystemMetricsAsync();
            if (metrics.MemoryTotalGb <= 0 || string.IsNullOrWhiteSpace(metrics.Os))
                throw new InvalidOperationException("System telemetry did not return valid RAM/OS data.");
            checks.Add("PASS: Windows system telemetry queried (CPU/RAM/disk/hardware/uptime).");

            var devices = await inventory.ScanAsync();
            if (devices.Count == 0)
                throw new InvalidOperationException("PnP scan returned zero devices on the Windows runner.");
            checks.Add($"PASS: PnP inventory queried ({devices.Count} devices).");

            var registryService = new RegistryMaintenanceService();
            var registry = registryService.Scan();
            checks.Add($"PASS: Run/RunOnce registry diagnostics queried ({registry.Count} findings).");

            // Exercise backup-before-repair against one uniquely named disposable HKCU Run value.
            var testValueName = "DjShrekSelfTest-" + Guid.NewGuid().ToString("N");
            var testValueData = Path.Combine(Path.GetTempPath(), "DjShrekSelfTest-Missing.exe");
            try
            {
                using (var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                    runKey!.SetValue(testValueName, testValueData, RegistryValueKind.String);
                var finding = registryService.Scan().SingleOrDefault(x => x.Hive == "HKCU" &&
                    x.ValueName == testValueName && x.Repairable)
                    ?? throw new InvalidOperationException("Disposable missing startup value was not detected as repairable.");
                var backupPath = registryService.Repair(finding);
                if (!File.Exists(backupPath))
                    throw new InvalidOperationException("Registry repair did not create its .reg backup.");
                using var verifyKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
                if (verifyKey?.GetValue(testValueName) is not null)
                    throw new InvalidOperationException("Disposable startup value remains after repair.");
                checks.Add("PASS: HKCU startup repair created a .reg backup and removed only the test value.");
            }
            finally
            {
                using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                runKey?.DeleteValue(testValueName, throwOnMissingValue: false);
            }

            var cleanupService = new JunkCleanupService();
            var junk = cleanupService.Scan(minimumAgeDays: 7);
            if (junk.Any(item => string.IsNullOrWhiteSpace(item.Path)))
                throw new InvalidOperationException("Temporary file scan returned an empty path.");
            var testTempFile = Path.Combine(Path.GetTempPath(), "DjShrekSelfTest-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(testTempFile, "DjShrek cleanup integration test");
                File.SetLastWriteTimeUtc(testTempFile, DateTime.UtcNow.AddDays(-8));
                var candidate = cleanupService.Scan(minimumAgeDays: 7)
                    .SingleOrDefault(item => string.Equals(item.Path, testTempFile, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Disposable aged temp file was not found by scanner.");
                var cleanupResult = cleanupService.Clean(new[] { candidate }, minimumAgeDays: 7);
                if (cleanupResult.DeletedFiles != 1 || File.Exists(testTempFile))
                    throw new InvalidOperationException("Cleanup integration test did not delete exactly its disposable test file.");
                checks.Add("PASS: cleanup deleted exactly one generated 8-day-old temp test file.");
            }
            finally
            {
                if (File.Exists(testTempFile)) File.Delete(testTempFile);
            }

            var driverPackages = new DriverMaintenanceService().ListDriverPackages();
            if (string.IsNullOrWhiteSpace(driverPackages))
                throw new InvalidOperationException("pnputil returned an empty Driver Store listing.");
            checks.Add("PASS: Driver Store listing queried with pnputil /enum-drivers.");

            var invalidInf = Path.Combine(Path.GetTempPath(), "DjShrekSelfTest-" + Guid.NewGuid().ToString("N") + ".inf");
            try
            {
                File.WriteAllText(invalidInf, "[Version]" + Environment.NewLine + "Signature=\"$Windows NT$\"");
                var rejected = false;
                try { DriverMaintenanceService.ValidateSignedInfPackage(invalidInf); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected)
                    throw new InvalidOperationException("Driver preflight accepted an INF without a signed catalog.");
                checks.Add("PASS: driver preflight rejects an INF package without a catalog signature.");
            }
            finally
            {
                if (File.Exists(invalidInf)) File.Delete(invalidInf);
            }

            var sensors = new HardwareSensorService().ReadMetrics();
            checks.Add("PASS: hardware sensor provider queried (unavailable sensors are reported as N/A).");

            var defaultTheme = ThemeManager.Themes.First(x => x.Id == "02");
            ThemeManager.Apply(defaultTheme);
            themeSelector.SelectedValue = defaultTheme.Id;
            settingsSelector.SelectedValue = defaultTheme.Id;
            checks.Add("PASS: default theme restored after UI test.");

            File.WriteAllLines(logPath, checks);
            Debug.WriteLine(string.Join(Environment.NewLine, checks));
            window.Close();
            Environment.ExitCode = 0;
            Shutdown(0);
        }
        catch (Exception ex)
        {
            checks.Add("FAIL: " + ex);
            try { File.WriteAllLines(logPath, checks); } catch { }
            Debug.WriteLine(ex);
            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }
}
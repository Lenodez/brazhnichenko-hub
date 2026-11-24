using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LocalAIHub.Installer;

public partial class MainWindow : Window
{
    private string? _installedExe;
    private ExistingInstallInfo? _existing;
    private bool _isUpdateMode;
    private readonly bool _autoUpdateMode;
    private readonly string? _autoUpdateTargetDir;
    private readonly int _autoUpdateParentPid;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableDarkTitleBar();

        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--auto-update", StringComparison.OrdinalIgnoreCase))
                _autoUpdateMode = true;
            else if (args[i].Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                _autoUpdateTargetDir = args[++i].Trim('"');
            else if (args[i].Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int pid))
                _autoUpdateParentPid = pid;
        }

        _existing = SetupEngine.DetectExistingInstall(_autoUpdateTargetDir);
        if (_existing is not null)
        {
            InstallDirBox.Text = _existing.InstallDir;
            DataDirBox.Text = _existing.DataRoot;
            SwitchToUpdateMode();
        }
        else
        {
            InstallDirBox.Text = _autoUpdateTargetDir ?? SetupEngine.DefaultInstallDir;
            DataDirBox.Text = SetupEngine.DefaultDataRoot;
        }

        Loaded += async (_, _) =>
        {
            if (_autoUpdateMode)
            {
                await RunAutoUpdateAsync();
            }
            else
            {
                HwSummaryText.Text = await SetupEngine.DetectHardwareLineAsync();
            }
        };
    }

    private async Task RunAutoUpdateAsync()
    {
        Title = "Brazhnichenko Hub — Автообновление";
        HeaderTitleText.Text = $"Автообновление Brazhnichenko Hub v{SetupEngine.InstallerVersion}";
        UpdateModeBanner.Visibility = Visibility.Visible;
        FullInstallPanel.Visibility = Visibility.Collapsed;
        ToggleReinstallBtn.Visibility = Visibility.Collapsed;
        InstallBtn.IsEnabled = false;
        InstallBtn.Content = "Обновление...";
        InstallProgressBar.IsIndeterminate = true;
        LogBox.Clear();

        void Log(string msg) => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(msg + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        try
        {
            if (_autoUpdateParentPid > 0)
            {
                Log("Ожидание закрытия текущей версии Brazhnichenko Hub...");
                await Task.Run(() =>
                {
                    try
                    {
                        using var parent = Process.GetProcessById(_autoUpdateParentPid);
                        if (!parent.WaitForExit(4500))
                        {
                            parent.Kill(entireProcessTree: true);
                            parent.WaitForExit(2000);
                        }
                    }
                    catch { }
                });
            }

            string targetDir = !string.IsNullOrWhiteSpace(_autoUpdateTargetDir)
                ? _autoUpdateTargetDir
                : (_existing?.InstallDir ?? SetupEngine.DefaultInstallDir);
            string llmRoot = _existing?.LlmRoot ?? Path.Combine(SetupEngine.DefaultDataRoot, "LLM");
            string mediaRoot = _existing?.MediaRoot ?? Path.Combine(SetupEngine.DefaultDataRoot, "MediaAI");

            string exe = await SetupEngine.UpdateExistingAsync(targetDir, llmRoot, mediaRoot, Log);
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            InstallBtn.Content = "✓ Обновлено!";

            if (File.Exists(exe))
            {
                Log("Запуск обновлённого Brazhnichenko Hub...");
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                await Task.Delay(500);
                Close();
            }
        }
        catch (Exception ex)
        {
            InstallProgressBar.IsIndeterminate = false;
            Log($"ОШИБКА автообновления: {ex.Message}");
            InstallBtn.IsEnabled = true;
            InstallBtn.Content = "Повторить обновление";
            MessageBox.Show(ex.Message, "Ошибка автообновления Brazhnichenko Hub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SwitchToUpdateMode()
    {
        _isUpdateMode = true;
        Title = "Brazhnichenko Hub — Обновление";
        HeaderTitleText.Text = $"Обновление Brazhnichenko Hub v{SetupEngine.InstallerVersion}";
        UpdateModeBanner.Visibility = Visibility.Visible;
        FullInstallPanel.Visibility = Visibility.Collapsed;
        ToggleReinstallBtn.Visibility = Visibility.Visible;
        ToggleReinstallBtn.Content = "Изменить пути / Переустановить";
        InstallBtn.Content = "Обновить Brazhnichenko Hub";
        UpdatePathInfoText.Text = $"Папка программы: {_existing!.InstallDir}\nПапка данных и моделей: {_existing.DataRoot}";
        LogBox.Text = "Найдена установленная версия. Нажми «Обновить Brazhnichenko Hub» — обновим программу за пару секунд без потери моделей и настроек.";
    }

    private void SwitchToFullReinstallMode()
    {
        _isUpdateMode = false;
        Title = "Brazhnichenko Hub — Переустановка";
        HeaderTitleText.Text = $"Переустановка Brazhnichenko Hub v{SetupEngine.InstallerVersion}";
        UpdateModeBanner.Visibility = Visibility.Collapsed;
        FullInstallPanel.Visibility = Visibility.Visible;
        ToggleReinstallBtn.Content = "← Назад к быстрому обновлению";
        InstallBtn.Content = "Переустановить Brazhnichenko Hub";
        LogBox.Text = "Режим полной настройки. Можно изменить папки, пересоздать ярлыки или подключить MCP.";
    }

    private void ToggleReinstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        if (_isUpdateMode) SwitchToFullReinstallMode();
        else SwitchToUpdateMode();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void EnableDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        ToggleReinstallBtn.IsEnabled = false;
        LogBox.Clear();
        InstallProgressBar.IsIndeterminate = true;
        try
        {
            string exe = _isUpdateMode && _existing is not null
                ? await SetupEngine.UpdateExistingAsync(
                    _existing.InstallDir,
                    _existing.LlmRoot,
                    _existing.MediaRoot,
                    msg => Dispatcher.Invoke(() =>
                    {
                        LogBox.AppendText(msg + Environment.NewLine);
                        LogBox.ScrollToEnd();
                    }))
                : await SetupEngine.InstallAsync(
                    InstallDirBox.Text.Trim(),
                    DataDirBox.Text.Trim(),
                    ShortcutsCheck.IsChecked == true,
                    UninstallRegCheck.IsChecked == true,
                    RegisterMcpCheck.IsChecked == true,
                    SviNodeCheck.IsChecked == true,
                    msg => Dispatcher.Invoke(() =>
                    {
                        LogBox.AppendText(msg + Environment.NewLine);
                        LogBox.ScrollToEnd();
                    }));

            _installedExe = exe;
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            InstallBtn.Content = _isUpdateMode ? "✓ Обновлено!" : "✓ Установлено!";

            if (LaunchAfterCheck.IsChecked == true && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                Close();
            }
        }
        catch (Exception ex)
        {
            InstallProgressBar.IsIndeterminate = false;
            LogBox.AppendText($"ОШИБКА: {ex.Message}{Environment.NewLine}");
            InstallBtn.IsEnabled = true;
            ToggleReinstallBtn.IsEnabled = true;
            MessageBox.Show(ex.Message, _isUpdateMode ? "Ошибка обновления" : "Ошибка установки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed record ExistingInstallInfo(string InstallDir, string DataRoot, string LlmRoot, string MediaRoot);

internal static class SetupEngine
{
    public static string InstallerVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "2.2.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "LocalAIHub");

    public static string DefaultDataRoot
    {
        get
        {
            if (Directory.Exists(@"D:\LocalMediaAI")) return @"D:\LocalMediaAI";
            try
            {
                var d = new DriveInfo("D");
                if (d.IsReady && d.DriveType == DriveType.Fixed && d.AvailableFreeSpace > 30L * 1024 * 1024 * 1024)
                    return @"D:\LocalAI";
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAIHub", "Data");
        }
    }

    public static ExistingInstallInfo? DetectExistingInstall(string? overrideInstallDir = null)
    {
        string? installDir = null;

        if (!string.IsNullOrWhiteSpace(overrideInstallDir) && Directory.Exists(overrideInstallDir))
            installDir = overrideInstallDir;

        // 1. Check Windows Uninstall registry
        if (installDir is null)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAIHub");
                string? regDir = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(regDir) && File.Exists(Path.Combine(regDir, "LocalAIHub.exe")))
                    installDir = regDir;
            }
            catch { }
        }

        // 2. Check standard default install location
        if (installDir is null && File.Exists(Path.Combine(DefaultInstallDir, "LocalAIHub.exe")))
            installDir = DefaultInstallDir;

        if (installDir is null) return null;

        // Read existing config.json for exact data roots
        string cfgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAIHub", "config.json");
        string llmRoot = "";
        string mediaRoot = "";
        try
        {
            if (File.Exists(cfgPath))
            {
                var obj = JsonNode.Parse(File.ReadAllText(cfgPath, Encoding.UTF8)) as JsonObject;
                llmRoot = obj?["LlmRoot"]?.GetValue<string>() ?? "";
                mediaRoot = obj?["MediaRoot"]?.GetValue<string>() ?? "";
            }
        }
        catch { }

        string dataRoot = DefaultDataRoot;
        if (!string.IsNullOrWhiteSpace(mediaRoot))
        {
            string? parent = Path.GetDirectoryName(mediaRoot.TrimEnd('\\'));
            dataRoot = string.Equals(Path.GetFileName(mediaRoot.TrimEnd('\\')), "MediaAI", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(parent)
                ? parent
                : mediaRoot;
        }
        if (string.IsNullOrWhiteSpace(llmRoot))
            llmRoot = Path.Combine(dataRoot, "LLM");
        if (string.IsNullOrWhiteSpace(mediaRoot))
            mediaRoot = Path.Combine(dataRoot, "MediaAI");

        return new ExistingInstallInfo(installDir, dataRoot, llmRoot, mediaRoot);
    }

    public static async Task<string> DetectHardwareLineAsync()
    {
        string gpuLine = "NVIDIA GPU не найдена (будет доступен CPU-режим для LLM)";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                string outStr = (await p.StandardOutput.ReadToEndAsync()).Trim();
                await p.WaitForExitAsync();
                var parts = outStr.Split(',');
                if (parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double mib))
                    gpuLine = $"{parts[0].Trim()} ({mib / 1024.0:F0} ГБ VRAM)";
            }
        }
        catch { }

        double ramGb = QueryTotalRamGB();
        return $"GPU: {gpuLine}  •  ОЗУ: {ramGb:F0} ГБ RAM — каталог моделей автоматически подстроится под эти спеки.";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static double QueryTotalRamGB()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) ? mem.ullTotalPhys / (1024.0 * 1024 * 1024) : 16;
    }

    private static void StopRunningHubProcesses(string installDir, Action<string>? onLog)
    {
        bool killedAny = false;
        foreach (var name in new[] { "LocalAIHub", "LocalAIHub.Mcp" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? procDir = Path.GetDirectoryName(p.MainModule?.FileName);
                    if (string.IsNullOrEmpty(procDir) || string.Equals(procDir, installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                        killedAny = true;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        if (killedAny)
        {
            onLog?.Invoke("Закрыт запущенный экземпляр Local AI Hub для замены файлов...");
            Thread.Sleep(500);
        }
    }

    private static void ExtractPayload(string installDir, Action<string>? onLog)
    {
        onLog?.Invoke("Распаковка обновлённых LocalAIHub.exe, LocalAIHub.Mcp.exe и модулей...");
        using Stream zipStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LocalAIHub.Installer.payload.zip")
            ?? throw new InvalidOperationException("Встроенный архив payload.zip не найден.");
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(installDir, overwriteFiles: true);
    }

    private static void SyncSviNode(string installDir, string mediaRoot, Action<string>? onLog)
    {
        string bundledSvi = Path.Combine(installDir, "ComfyUI-LocalAIHub-SVI", "__init__.py");
        if (!File.Exists(bundledSvi)) return;
        string[] comfyCandidates = [
            Path.Combine(mediaRoot, "ComfyUI_windows_portable", "ComfyUI", "custom_nodes"),
            Path.Combine(mediaRoot, "ComfyUI", "custom_nodes")
        ];
        foreach (var cnDir in comfyCandidates)
        {
            if (Directory.Exists(cnDir))
            {
                string dst = Path.Combine(cnDir, "ComfyUI-LocalAIHub-SVI");
                Directory.CreateDirectory(dst);
                File.Copy(bundledSvi, Path.Combine(dst, "__init__.py"), overwrite: true);
                onLog?.Invoke($"Обновлён узел SVI 2.0 Pro -> {dst}");
            }
        }
    }

    public static async Task<string> UpdateExistingAsync(
        string installDir,
        string llmRoot,
        string mediaRoot,
        Action<string>? onLog)
    {
        return await Task.Run(() =>
        {
            onLog?.Invoke($"Быстрое обновление Local AI Hub в {installDir}...");
            Directory.CreateDirectory(installDir);
            StopRunningHubProcesses(installDir, onLog);
            ExtractPayload(installDir, onLog);

            // Refresh helper scripts without touching user's config.json or downloaded models
            string bundledHelper = Path.Combine(installDir, "story_media.py");
            if (File.Exists(bundledHelper) && Directory.Exists(mediaRoot))
            {
                File.Copy(bundledHelper, Path.Combine(mediaRoot, "story_media.py"), overwrite: true);
                onLog?.Invoke("Обновлён модуль story_media.py.");
            }
            string bundledCtx = Path.Combine(installDir, "media-director-context.md");
            string destCtx = Path.Combine(llmRoot, "media-director-context.md");
            if (File.Exists(bundledCtx) && Directory.Exists(llmRoot) && !File.Exists(destCtx))
                File.Copy(bundledCtx, destCtx, overwrite: false);

            SyncSviNode(installDir, mediaRoot, onLog);

            // Refresh installed version in Windows Uninstall registry if registered
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAIHub", writable: true);
                key?.SetValue("DisplayVersion", InstallerVersion);
            }
            catch { }

            onLog?.Invoke($"Готово! Local AI Hub обновлён до v{InstallerVersion}, все модели и настройки сохранены.");
            return Path.Combine(installDir, "LocalAIHub.exe");
        });
    }

    public static async Task<string> InstallAsync(
        string installDir,
        string dataDir,
        bool createShortcuts,
        bool registerUninstall,
        bool registerClaudeMcp,
        bool installSviNode,
        Action<string>? onLog)
    {
        return await Task.Run(() =>
        {
            onLog?.Invoke($"Создание папки программы: {installDir}");
            Directory.CreateDirectory(installDir);
            StopRunningHubProcesses(installDir, onLog);
            ExtractPayload(installDir, onLog);

            // Resolve LLM and Media data directories
            bool isExistingSplit = Directory.Exists(@"D:\LocalLLM") &&
                                   string.Equals(dataDir.TrimEnd('\\'), @"D:\LocalMediaAI", StringComparison.OrdinalIgnoreCase);
            string llmRoot = isExistingSplit ? @"D:\LocalLLM" : Path.Combine(dataDir, "LLM");
            string mediaRoot = isExistingSplit ? @"D:\LocalMediaAI" : Path.Combine(dataDir, "MediaAI");
            string storiesRoot = Path.Combine(mediaRoot, "Stories");

            Directory.CreateDirectory(Path.Combine(llmRoot, "models"));
            Directory.CreateDirectory(Path.Combine(llmRoot, "runtime"));
            Directory.CreateDirectory(Path.Combine(llmRoot, "logs"));
            Directory.CreateDirectory(mediaRoot);
            Directory.CreateDirectory(storiesRoot);

            // Copy media-director-context.md & story_media.py into data folders if missing
            string bundledCtx = Path.Combine(installDir, "media-director-context.md");
            string destCtx = Path.Combine(llmRoot, "media-director-context.md");
            if (File.Exists(bundledCtx) && !File.Exists(destCtx))
                File.Copy(bundledCtx, destCtx, overwrite: false);

            string bundledHelper = Path.Combine(installDir, "story_media.py");
            string destHelper = Path.Combine(mediaRoot, "story_media.py");
            if (File.Exists(bundledHelper) && !File.Exists(destHelper))
                File.Copy(bundledHelper, destHelper, overwrite: false);

            if (installSviNode)
                SyncSviNode(installDir, mediaRoot, onLog);

            // Write %LocalAppData%\LocalAIHub\config.json (preserving existing tuning if present)
            string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAIHub");
            Directory.CreateDirectory(cfgDir);
            string cfgPath = Path.Combine(cfgDir, "config.json");
            JsonObject cfgObj = File.Exists(cfgPath)
                ? (JsonNode.Parse(File.ReadAllText(cfgPath, Encoding.UTF8)) as JsonObject ?? new JsonObject())
                : new JsonObject();
            cfgObj["LlmRoot"] = llmRoot;
            cfgObj["MediaRoot"] = mediaRoot;
            cfgObj["StoriesRoot"] = storiesRoot;
            File.WriteAllText(cfgPath, cfgObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            onLog?.Invoke($"Конфигурация сохранена: {cfgPath}");

            string exePath = Path.Combine(installDir, "LocalAIHub.exe");
            string mcpPath = Path.Combine(installDir, "LocalAIHub.Mcp.exe");

            // Create Uninstall script
            string uninstCmd = Path.Combine(installDir, "Uninstall-LocalAIHub.cmd");
            File.WriteAllText(uninstCmd,
                $"@echo off\r\n" +
                $"taskkill /F /IM LocalAIHub.exe >nul 2>&1\r\n" +
                $"taskkill /F /IM LocalAIHub.Mcp.exe >nul 2>&1\r\n" +
                $"reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\LocalAIHub\" /f >nul 2>&1\r\n" +
                $"del /f /q \"%USERPROFILE%\\Desktop\\Local AI Hub.lnk\" >nul 2>&1\r\n" +
                $"del /f /q \"%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\Local AI Hub.lnk\" >nul 2>&1\r\n" +
                $"echo Local AI Hub удалён (твои скачанные модели в {dataDir} сохранены).\r\n" +
                $"start /b \"\" cmd /c \"timeout /t 2 >nul & rmdir /s /q \\\"{installDir}\\\"\"\r\n",
                Encoding.UTF8);

            if (createShortcuts)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Local AI Hub.lnk"), exePath, installDir);
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Local AI Hub.lnk"), exePath, installDir);
                onLog?.Invoke("Созданы ярлыки на Рабочем столе и в меню «Пуск».");
            }

            if (registerUninstall)
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAIHub");
                key.SetValue("DisplayName", "Brazhnichenko Hub");
                key.SetValue("DisplayVersion", InstallerVersion);
                key.SetValue("Publisher", "Brazhnichenko Hub");
                key.SetValue("DisplayIcon", exePath);
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", $"cmd.exe /c \"\"{uninstCmd}\"\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                onLog?.Invoke("Зарегистрировано в «Приложения и возможности» Windows.");
            }

            if (registerClaudeMcp && File.Exists(mcpPath))
            {
                string claudeCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
                Directory.CreateDirectory(Path.GetDirectoryName(claudeCfg)!);
                JsonObject root = File.Exists(claudeCfg)
                    ? (JsonNode.Parse(File.ReadAllText(claudeCfg, Encoding.UTF8)) as JsonObject ?? new JsonObject())
                    : new JsonObject();
                if (root["mcpServers"] is not JsonObject servers)
                {
                    servers = new JsonObject();
                    root["mcpServers"] = servers;
                }
                servers["local-ai-hub"] = new JsonObject { ["command"] = mcpPath, ["args"] = new JsonArray() };
                File.WriteAllText(claudeCfg, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                onLog?.Invoke($"MCP-сервер зарегистрирован в {claudeCfg}");
            }

            onLog?.Invoke("Готово! Установка успешно завершена.");
            return exePath;
        });
    }

    private static void CreateShortcut(string lnkPath, string targetExe, string workDir)
    {
        string ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnkPath.Replace("'", "''")}');" +
                    $"$s.TargetPath='{targetExe.Replace("'", "''")}';" +
                    $"$s.WorkingDirectory='{workDir.Replace("'", "''")}';" +
                    $"$s.IconLocation='{targetExe.Replace("'", "''")},0';" +
                    $"$s.Save()";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{ps}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
    }
}

using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAIHub.Services;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace LocalAIHub;

public partial class MainWindow : Window
{
    private readonly AiServiceManager _manager = new();
    private readonly AssistantReminderService _reminderService = new();
    private readonly DispatcherTimer _pollTimer;
    private DispatcherTimer? _toastTimer;
    private readonly Dictionary<string, ServiceCardUi> _cards = new();
    private readonly Dictionary<string, string?> _busyState = new();
    private System.Windows.Controls.ComboBox? _llmModelCombo;
    private bool _suppressLlmChange;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.NotifyIcon? _assistantTrayIcon;
    private AssistantOverlayWindow? _assistantOverlay;
    private NotesViewerWindow? _notesViewer;
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;
    private bool _pollInFlight;
    private bool _forceExit;

    private const int WM_HOTKEY = 0x0312;
    private const int ASSISTANT_HOTKEY_ID = 0x4842;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> FrozenBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastBackgroundPollUtc = DateTime.MinValue;

    private sealed class ServiceCardUi
    {
        public required Border CardBorder;
        public required Border StatusBadge;
        public required TextBlock StatusText;
        public required System.Windows.Controls.Button StartBtn;
        public required System.Windows.Controls.Button OpenBtn;
        public required System.Windows.Controls.Button StopBtn;
    }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            EnableDarkTitleBar();
            InitDesktopAssistantHotkeyHook();
        };
        BuildServiceCards();
        InitStudio();
        InitStories();
        InitCatalogAndSettings();
        InitAssistant();
        InitAutoUpdaterUi();
        InitLocalChat();
        InitLocalVoice();
        InitTelemetryAndBackup();
        InitQueueAndPresets();
        InitLogSelector();
        LoadModelsTable();
        SetChatSubMode("llm");
        SetSystemSubMode("services");
        ApplyInitialTabSelection();
        SetupTrayIcon();
        RefreshDesktopAssistantHooks();
        _reminderService.ReminderTriggered += OnAssistantReminderTriggered;
        _reminderService.Start();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Loaded += async (_, _) =>
        {
            await RefreshStatusAsync();
            _pollTimer.Start();
            _ = CheckUpdatesSilentlyOnStartupAsync();
            MaybeShowFirstStartChecklistOnLaunch();
        };
    }

    // Native Windows 11 Dark Title Bar
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void EnableDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private void SetupTrayIcon()
    {
        Icon trayIco = SystemIcons.Application;
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res?.Stream is not null)
            {
                trayIco = new Icon(res.Stream);
            }
        }
        catch { }

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Brazhnichenko Hub",
            Icon = trayIco,
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ассистент ПК (заметки / таймер / экран / сеть)", null, (_, _) => Dispatcher.Invoke(() => _ = OpenDesktopAssistantOverlayAsync()));
        menu.Items.Add("Заметки ассистента (.md)", null, (_, _) => Dispatcher.Invoke(() => OpenNotesViewer(null)));
        menu.Items.Add("Открыть Brazhnichenko Hub", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Освободить GPU (остановить всё)", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () => await StopAllAsync());
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _forceExit = true;
            Close();
        }));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void InitDesktopAssistantHotkeyHook()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(DesktopAssistantWndProc);
        RegisterAssistantHotkey(HubConfig.Current.DesktopAssistantHotkey);
    }

    private IntPtr DesktopAssistantWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == ASSISTANT_HOTKEY_ID)
        {
            handled = true;
            Dispatcher.BeginInvoke(() => _ = OpenDesktopAssistantOverlayAsync());
        }
        return IntPtr.Zero;
    }

    internal bool RefreshDesktopAssistantHooks()
    {
        var cfg = HubConfig.Current;

        // Dedicated single-click System Tray icon for Desktop Assistant
        if (cfg.DesktopAssistantEnabled)
        {
            if (_assistantTrayIcon is null)
            {
                Icon assistIco = CreateAssistantBadgeIcon();
                _assistantTrayIcon = new Forms.NotifyIcon
                {
                    Text = $"Ассистент ПК ({cfg.DesktopAssistantHotkey}) — клик для вызова",
                    Icon = assistIco,
                    Visible = true
                };
                var amenu = new Forms.ContextMenuStrip();
                amenu.Items.Add("Открыть Ассистента ПК", null, (_, _) => Dispatcher.Invoke(() => _ = OpenDesktopAssistantOverlayAsync()));
                amenu.Items.Add("Заметки ассистента (.md)", null, (_, _) => Dispatcher.Invoke(() => OpenNotesViewer(null)));
                amenu.Items.Add("Открыть Brazhnichenko Hub", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
                amenu.Items.Add("Освободить GPU", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await StopAllAsync()));
                amenu.Items.Add(new Forms.ToolStripSeparator());
                amenu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() =>
                {
                    _forceExit = true;
                    Close();
                }));
                _assistantTrayIcon.ContextMenuStrip = amenu;
                _assistantTrayIcon.MouseClick += (_, args) =>
                {
                    if (args.Button == Forms.MouseButtons.Left)
                        Dispatcher.Invoke(() => _ = OpenDesktopAssistantOverlayAsync());
                };
            }
            else
            {
                _assistantTrayIcon.Text = $"Ассистент ПК ({cfg.DesktopAssistantHotkey}) — клик для вызова";
                _assistantTrayIcon.Visible = true;
            }
        }
        else if (_assistantTrayIcon is not null)
        {
            _assistantTrayIcon.Visible = false;
            _assistantTrayIcon.Dispose();
            _assistantTrayIcon = null;
        }

        return RegisterAssistantHotkey(cfg.DesktopAssistantHotkey);
    }

    private static Icon CreateAssistantBadgeIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new SolidBrush(System.Drawing.Color.FromArgb(255, 18, 18, 21));
                using var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 68, 68, 78), 1.8f);
                using var innerRing = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 228, 228, 232), 1.5f);
                using var coreDot = new SolidBrush(System.Drawing.Color.FromArgb(255, 255, 82, 27));
                g.FillEllipse(bg, 2, 2, 28, 28);
                g.DrawEllipse(ring, 2, 2, 28, 28);
                g.DrawEllipse(innerRing, 8, 8, 16, 16);
                g.FillEllipse(coreDot, 12.5f, 12.5f, 7f, 7f);
            }
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = System.Drawing.Icon.FromHandle(hIcon);
                return (System.Drawing.Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return SystemIcons.Information;
        }
    }

    private bool RegisterAssistantHotkey(string? spec)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return true;

        if (_hotkeyRegistered)
        {
            UnregisterHotKey(hwnd, ASSISTANT_HOTKEY_ID);
            _hotkeyRegistered = false;
        }

        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("None", StringComparison.OrdinalIgnoreCase))
            return true;

        uint mods = MOD_NOREPEAT;
        uint vk = 0;
        switch (spec.Trim())
        {
            case "Alt+Space":
                mods |= MOD_ALT;
                vk = 0x20; // VK_SPACE
                break;
            case "Ctrl+Shift+Space":
                mods |= MOD_CONTROL | MOD_SHIFT;
                vk = 0x20;
                break;
            case "Alt+Q":
                mods |= MOD_ALT;
                vk = (uint)'Q';
                break;
            default:
                return true;
        }

        _hotkeyRegistered = RegisterHotKey(hwnd, ASSISTANT_HOTKEY_ID, mods, vk);
        return _hotkeyRegistered;
    }

    private void UnregisterAssistantHotkey()
    {
        if (!_hotkeyRegistered) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                UnregisterHotKey(hwnd, ASSISTANT_HOTKEY_ID);
        }
        catch { }
        _hotkeyRegistered = false;
    }

    internal async void OpenDesktopAssistantBtn_Click(object sender, RoutedEventArgs e) =>
        await OpenDesktopAssistantOverlayAsync();

    internal void OpenNotesViewerBtn_Click(object sender, RoutedEventArgs e) =>
        OpenNotesViewer(null);

    internal void OpenFirstStartChecklistBtn_Click(object sender, RoutedEventArgs e) =>
        OpenFirstStartChecklist();

    private void MaybeShowFirstStartChecklistOnLaunch()
    {
        var cfg = HubConfig.Current;
        if (!cfg.ShowFirstStartGuideOnLaunch && cfg.FirstRunCompleted)
            return;

        bool coreMissing = !RuntimeInstaller.IsLlamaInstalled()
            || _manager.GetAvailableLlmModels().Count == 0
            || !LocalVoiceService.GetStatus().WhisperReady;

        if (!cfg.FirstRunCompleted || coreMissing)
        {
            OpenFirstStartChecklist();
        }
    }

    internal void OpenFirstStartChecklist()
    {
        try
        {
            var win = new FirstStartChecklistWindow(
                _manager,
                () => _detectedHardware,
                async () => await InstallRuntimeWithUiAsync("llama"),
                () =>
                {
                    MainTabs.SelectedItem = CatalogTab;
                    SetCatalogView("text");
                },
                async () => await InstallWhisperWithUiAsync(),
                async () => await InstallPiperWithUiAsync(),
                async () => await InstallRuntimeWithUiAsync("comfyui"));
            win.Owner = this;
            win.ShowDialog();
            RefreshRuntimeStatusUi();
            RefreshVoiceStatusUi();
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Не удалось открыть Мастер старта: {ex.Message}";
        }
    }

    internal void OpenNotesViewer(string? selectFilePath = null)
    {
        try
        {
            if (_notesViewer is null || !_notesViewer.IsLoaded)
            {
                _notesViewer = new NotesViewerWindow();
                _notesViewer.Closed += (_, _) => _notesViewer = null;
                _notesViewer.Show();
            }
            else
            {
                if (_notesViewer.WindowState == WindowState.Minimized)
                    _notesViewer.WindowState = WindowState.Normal;
                _notesViewer.Activate();
            }

            if (!string.IsNullOrWhiteSpace(selectFilePath))
                _notesViewer.SelectNoteByPath(selectFilePath);
            else
                _notesViewer.ReloadNotes();
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Не удалось открыть Заметки: {ex.Message}";
            ShowToast("Не удалось открыть Заметки", "warn");
        }
    }

    private void OnAssistantReminderTriggered(AssistantReminderItem item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            try
            {
                var icon = _assistantTrayIcon ?? _trayIcon;
                icon?.ShowBalloonTip(9000, "Напоминание — Brazhnichenko Hub", item.Text, Forms.ToolTipIcon.Info);
            }
            catch { }
            StatusFooterText.Text = $"Напоминание ({item.FireAtLocal:HH:mm}): {item.Text}";
            ShowToast($"Напоминание: {item.Text}", "info");
        });
    }

    internal async Task OpenDesktopAssistantOverlayAsync()
    {
        try
        {
            if (_assistantOverlay is null || !_assistantOverlay.IsLoaded)
            {
                _assistantOverlay = new AssistantOverlayWindow(
                    _manager,
                    _voiceService,
                    _reminderService,
                    path => Dispatcher.Invoke(() => OpenNotesViewer(path)));
                _assistantOverlay.Closed += (_, _) => _assistantOverlay = null;
            }
            await _assistantOverlay.OpenAndActivateAsync();
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка запуска Ассистента ПК: {ex.Message}";
            ShowToast("Не удалось открыть Ассистента ПК", "warn");
        }
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
        _ = RefreshStatusAsync();
    }

    internal static void TrimProcessMemory(bool clearThumbCache = false)
    {
        if (clearThumbCache)
            ClearGalleryThumbCache();
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            GC.WaitForPendingFinalizers();
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(proc.Handle);
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopGalleryVideo();
        if (!_forceExit && MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            TrimProcessMemory(clearThumbCache: true);
            return;
        }
        _pollTimer.Stop();
        _toastTimer?.Stop();
        _genClockTimer?.Stop();
        _genCts?.Cancel();
        _catalogDlCts?.Cancel();
        _ = HubAssistantService.StopAssistantServerAsync();
        UnregisterAssistantHotkey();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(DesktopAssistantWndProc);
            _hwndSource = null;
        }
        _reminderService.Dispose();
        if (_notesViewer is not null)
        {
            try { _notesViewer.Close(); } catch { }
            _notesViewer = null;
        }
        if (_assistantOverlay is not null)
        {
            try { _assistantOverlay.Close(); } catch { }
            _assistantOverlay = null;
        }
        if (_assistantTrayIcon is not null)
        {
            _assistantTrayIcon.Visible = false;
            _assistantTrayIcon.Dispose();
            _assistantTrayIcon = null;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        System.Windows.Application.Current?.Shutdown();
    }

    private void BuildServiceCards()
    {
        ServicesGrid.Children.Clear();
        _cards.Clear();

        foreach (var svc in _manager.Services)
        {
            var cardBorder = new Border
            {
                Background = BrushFromHex("#121215"),
                BorderBrush = BrushFromHex("#28282E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(14),
                Margin = new Thickness(5)
            };

            var root = new StackPanel();

            // Top row: Title + status badge
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var statusText = new TextBlock
            {
                Text = "Проверка...",
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex("#A1A1AA")
            };
            var statusBadge = new Border
            {
                Background = BrushFromHex("#19191E"),
                BorderBrush = BrushFromHex("#2A2A32"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Top,
                Child = statusText
            };
            DockPanel.SetDock(statusBadge, Dock.Right);
            top.Children.Add(statusBadge);

            string iconGeomKey = svc.Id switch
            {
                "qwen" => "IconChat",
                "claude" => "IconTerminal",
                "comfyui" => "IconImage",
                "swarmui" => "IconLayers",
                _ => "IconSliders"
            };

            var iconPath = new System.Windows.Shapes.Path
            {
                Style = TryFindResource("UiIcon16") as Style,
                Data = TryFindResource(iconGeomKey) as Geometry,
                Stroke = BrushFromHex("#E4E4E8"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconBadge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(4),
                Background = BrushFromHex("#18181D"),
                BorderBrush = BrushFromHex("#2C2C34"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 11, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = iconPath
            };
            DockPanel.SetDock(iconBadge, Dock.Left);
            top.Children.Add(iconBadge);

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = svc.Name,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex("#F4F4F6")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = svc.Description,
                FontSize = 11.5,
                Foreground = BrushFromHex("#A1A1AA"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 8, 0)
            });
            top.Children.Add(titleStack);
            root.Children.Add(top);

            // Meta row: port & folder
            var meta = new TextBlock
            {
                Text = $"ПОРТ :{svc.Port}  •  {svc.Folder}",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Foreground = BrushFromHex("#71717A"),
                Margin = new Thickness(0, 6, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(meta);

            // LLM Model selector right on the Qwen card
            if (svc.Id == "qwen")
            {
                var modelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var lbl = new TextBlock
                {
                    Text = "ВЕСА:",
                    FontSize = 10.5,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = BrushFromHex("#71717A"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                DockPanel.SetDock(lbl, Dock.Left);
                modelRow.Children.Add(lbl);

                _llmModelCombo = new System.Windows.Controls.ComboBox
                {
                    DisplayMemberPath = "DisplayName",
                    SelectedValuePath = "FileName",
                    FontSize = 12,
                    Padding = new Thickness(6, 4, 6, 4)
                };
                _llmModelCombo.SelectionChanged += async (_, _) =>
                {
                    if (_suppressLlmChange) return;
                    if (_llmModelCombo.SelectedValue is string chosen && !string.IsNullOrWhiteSpace(chosen))
                    {
                        await ApplySelectedLlmWeightsAsync(chosen);
                    }
                };
                modelRow.Children.Add(_llmModelCombo);
                root.Children.Add(modelRow);
                RefreshLlmSelector();
            }

            // Buttons
            var actions = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            var startBtn = new System.Windows.Controls.Button
            {
                Content = "Запустить (авто-GPU)",
                Style = (Style)FindResource("PrimaryButton"),
                Margin = new Thickness(0, 0, 7, 6)
            };
            startBtn.Click += async (_, _) => await RunServiceActionAsync(svc.Id, "switch");

            var openBtn = new System.Windows.Controls.Button
            {
                Content = "Открыть UI",
                Style = (Style)FindResource("SuccessButton"),
                Margin = new Thickness(0, 0, 7, 6),
                Visibility = Visibility.Collapsed
            };
            openBtn.Click += (_, _) => AiServiceManager.OpenExternal(svc.Url);

            var stopBtn = new System.Windows.Controls.Button
            {
                Content = "Остановить",
                Style = (Style)FindResource("DangerButton"),
                Margin = new Thickness(0, 0, 7, 6),
                Visibility = Visibility.Collapsed
            };
            stopBtn.Click += async (_, _) => await RunServiceActionAsync(svc.Id, "stop");

            var folderBtn = new System.Windows.Controls.Button
            {
                Content = "Папка",
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 7, 6)
            };
            folderBtn.Click += (_, _) => AiServiceManager.OpenExternal(svc.Folder);

            actions.Children.Add(startBtn);
            actions.Children.Add(openBtn);
            actions.Children.Add(stopBtn);
            if (!string.IsNullOrEmpty(svc.OutputFolder))
            {
                var galleryBtn = new System.Windows.Controls.Button
                {
                    Content = "Галерея",
                    Style = (Style)FindResource("DarkButton"),
                    Margin = new Thickness(0, 0, 7, 6)
                };
                galleryBtn.Click += (_, _) =>
                {
                    System.IO.Directory.CreateDirectory(svc.OutputFolder);
                    AiServiceManager.OpenExternal(svc.OutputFolder);
                };
                actions.Children.Add(galleryBtn);
            }
            actions.Children.Add(folderBtn);

            var logBtn = new System.Windows.Controls.Button
            {
                Content = "Логи",
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            logBtn.Click += (_, _) => OpenLogsFor(svc.Id);
            actions.Children.Add(logBtn);
            root.Children.Add(actions);

            cardBorder.Child = root;
            ServicesGrid.Children.Add(cardBorder);

            _cards[svc.Id] = new ServiceCardUi
            {
                CardBorder = cardBorder,
                StatusBadge = statusBadge,
                StatusText = statusText,
                StartBtn = startBtn,
                OpenBtn = openBtn,
                StopBtn = stopBtn
            };
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_pollInFlight) return;

        bool uiHidden = !IsVisible || WindowState == WindowState.Minimized;
        if (uiHidden)
        {
            // When minimized or in tray, skip UI/nvidia-smi polling every 2.5s.
            // Only check LLM idle auto-unload every 30s if enabled.
            if (HubConfig.Current.LlmIdleUnloadMinutes <= 0 ||
                (DateTime.UtcNow - _lastBackgroundPollUtc).TotalSeconds < 30)
            {
                return;
            }
            _lastBackgroundPollUtc = DateTime.UtcNow;
            _pollInFlight = true;
            try
            {
                var qwenSvc = _manager.GetService("qwen");
                if (await _manager.IsRunningAsync(qwenSvc))
                {
                    var statuses = new Dictionary<string, bool> { ["qwen"] = true };
                    var m = await _manager.GetSystemMetricsAsync();
                    await CheckLlmIdleAutoUnloadAsync(statuses, m);
                }
            }
            catch { }
            finally { _pollInFlight = false; }
            return;
        }

        _pollInFlight = true;
        try
        {
            var metricsTask = _manager.GetSystemMetricsAsync();
            var statusesTask = _manager.GetAllStatusesAsync();
            await Task.WhenAll(metricsTask, statusesTask);

            var m = metricsTask.Result;
            var statuses = statusesTask.Result;

            if (m.Gpu is not null)
            {
                double usedG = m.Gpu.VramUsedMiB / 1024.0;
                double totalG = m.Gpu.VramTotalMiB / 1024.0;
                double pct = totalG > 0 ? Math.Min(100, usedG / totalG * 100.0) : 0;
                GpuNameText.Text = m.Gpu.Name;
                GpuExtraText.Text = $"{m.Gpu.UtilPct}% • {Math.Round(m.Gpu.TempC)}°C";
                VramText.Text = $"{usedG:F1} / {totalG:F1} ГБ ({pct:0}%)";
                VramBar.Value = pct;
                VramBar.Foreground = pct > 85 ? BrushFromHex("#EF4444") : pct > 60 ? BrushFromHex("#F59E0B") : BrushFromHex("#FF521B");
                GenGpuLiveText.Text = $"{usedG:F1}/{totalG:F1} ГБ • {m.Gpu.UtilPct}% • {Math.Round(m.Gpu.TempC)}°C";
            }

            double ramPct = m.RamTotalGB > 0 ? Math.Min(100, m.RamUsedGB / m.RamTotalGB * 100.0) : 0;
            RamText.Text = $"{m.RamUsedGB:F1} / {m.RamTotalGB:F1} ГБ";
            RamPctText.Text = $"{ramPct:0}%";
            RamBar.Value = ramPct;
            double? vramPctVal = m.Gpu is not null && m.Gpu.VramTotalMiB > 0
                ? Math.Min(100, (double)m.Gpu.VramUsedMiB / m.Gpu.VramTotalMiB * 100.0)
                : null;
            UpdateTelemetrySparklines(vramPctVal, m.Gpu?.TempC, ramPct);
            _ = CheckLlmIdleAutoUnloadAsync(statuses, m);
            DiskText.Text = $"{m.DiskFreeDGB:F1} ГБ";
            try
            {
                var drv = new System.IO.DriveInfo(HubConfig.DataDriveLetter);
                if (drv.IsReady && drv.TotalSize > 0)
                {
                    double usedPct = Math.Clamp((1.0 - (double)drv.AvailableFreeSpace / drv.TotalSize) * 100.0, 0.0, 100.0);
                    DiskBar.Value = usedPct;
                    DiskBar.Foreground = m.DiskFreeDGB < 15 ? BrushFromHex("#EF4444")
                                       : m.DiskFreeDGB < 35 ? BrushFromHex("#F59E0B")
                                       : BrushFromHex("#D4D4D8");
                }
            }
            catch { }
            UpdateCatalogDetectedHardware(m);

            bool llamaReady = RuntimeInstaller.IsLlamaInstalled();
            bool comfyReady = RuntimeInstaller.IsComfyInstalled();
            bool hasLlmModels = _manager.GetAvailableLlmModels().Count > 0;

            foreach (var svc in _manager.Services)
            {
                bool running = statuses.GetValueOrDefault(svc.Id);
                string? busy = _busyState.GetValueOrDefault(svc.Id);
                var ui = _cards[svc.Id];
                bool runtimeReady = svc.Id is "qwen" or "claude" ? llamaReady : comfyReady;

                if (busy is not null)
                {
                    ui.StatusText.Text = busy;
                    ui.StatusText.Foreground = BrushFromHex("#F59E0B");
                    ui.StatusBadge.Background = BrushFromHex("#241D0E");
                    ui.CardBorder.BorderBrush = BrushFromHex("#6B5218");
                    ui.StartBtn.IsEnabled = false;
                    ui.StopBtn.IsEnabled = false;
                }
                else if (running)
                {
                    ui.StatusText.Text = "● РАБОТАЕТ";
                    ui.StatusText.Foreground = BrushFromHex("#34D399");
                    ui.StatusBadge.Background = BrushFromHex("#11241C");
                    ui.CardBorder.BorderBrush = BrushFromHex("#265944");
                    ui.StartBtn.Visibility = Visibility.Collapsed;
                    ui.OpenBtn.Visibility = svc.Id == "claude" ? Visibility.Collapsed : Visibility.Visible;
                    ui.StopBtn.Visibility = Visibility.Visible;
                    ui.StopBtn.IsEnabled = true;
                }
                else if (!runtimeReady)
                {
                    ui.StatusText.Text = "▲ НУЖЕН ДВИЖОК";
                    ui.StatusText.Foreground = BrushFromHex("#F59E0B");
                    ui.StatusBadge.Background = BrushFromHex("#241D0E");
                    ui.CardBorder.BorderBrush = BrushFromHex("#6B5218");
                    ui.StartBtn.Content = svc.Id == "comfyui" ? "Установить ComfyUI" : "Установить llama.cpp";
                    ui.StartBtn.Style = (Style)FindResource("SuccessButton");
                    ui.StartBtn.Visibility = Visibility.Visible;
                    ui.StartBtn.IsEnabled = !_catalogDownloading;
                    ui.OpenBtn.Visibility = Visibility.Collapsed;
                    ui.StopBtn.Visibility = Visibility.Collapsed;
                }
                else if (svc.Id == "qwen" && !hasLlmModels)
                {
                    ui.StatusText.Text = "○ НЕТ ВЕСОВ";
                    ui.StatusText.Foreground = BrushFromHex("#E4E4E8");
                    ui.StatusBadge.Background = BrushFromHex("#19191E");
                    ui.CardBorder.BorderBrush = BrushFromHex("#383842");
                    ui.StartBtn.Content = "Выбрать модель →";
                    ui.StartBtn.Style = (Style)FindResource("PrimaryButton");
                    ui.StartBtn.Visibility = Visibility.Visible;
                    ui.StartBtn.IsEnabled = true;
                    ui.OpenBtn.Visibility = Visibility.Collapsed;
                    ui.StopBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ui.StatusText.Text = "○ ОСТАНОВЛЕН";
                    ui.StatusText.Foreground = BrushFromHex("#71717A");
                    ui.StatusBadge.Background = BrushFromHex("#18181C");
                    ui.CardBorder.BorderBrush = BrushFromHex("#28282E");
                    ui.StartBtn.Content = "Запустить (авто-GPU)";
                    ui.StartBtn.Style = (Style)FindResource("PrimaryButton");
                    ui.StartBtn.Visibility = Visibility.Visible;
                    ui.StartBtn.IsEnabled = true;
                    ui.OpenBtn.Visibility = Visibility.Collapsed;
                    ui.StopBtn.Visibility = Visibility.Collapsed;
                }
            }

            // Mirror main LLM status right inside the «Чат» tab
            bool llmRunning = statuses.GetValueOrDefault("qwen");
            string? llmBusy = _busyState.GetValueOrDefault("qwen");
            if (ChatQuickLlmStatusText is not null)
            {
                if (llmBusy is not null)
                {
                    ChatQuickLlmStatusText.Text = $"LLM: {llmBusy}";
                    ChatQuickLlmStatusText.Foreground = BrushFromHex("#F59E0B");
                    ChatLlmStatusBadgeText.Text = llmBusy;
                    ChatLlmStatusBadgeText.Foreground = BrushFromHex("#F59E0B");
                    ChatLlmStatusBadge.Background = BrushFromHex("#241D0E");
                    ChatLlmStartBtn.IsEnabled = false;
                }
                else if (llmRunning)
                {
                    ChatQuickLlmStatusText.Text = "● LLM активна (:18080)";
                    ChatQuickLlmStatusText.Foreground = BrushFromHex("#34D399");
                    ChatLlmStatusBadgeText.Text = "● :18080";
                    ChatLlmStatusBadgeText.Foreground = BrushFromHex("#34D399");
                    ChatLlmStatusBadge.Background = BrushFromHex("#11241C");
                    ChatLlmStartBtn.Visibility = Visibility.Collapsed;
                    ChatLlmOpenUiBtn.Visibility = Visibility.Visible;
                    ChatLlmStopBtn.Visibility = Visibility.Visible;
                }
                else if (!llamaReady)
                {
                    ChatQuickLlmStatusText.Text = "▲ Движок llama.cpp не установлен";
                    ChatQuickLlmStatusText.Foreground = BrushFromHex("#F59E0B");
                    ChatLlmStatusBadgeText.Text = "▲ НУЖЕН ДВИЖОК";
                    ChatLlmStatusBadgeText.Foreground = BrushFromHex("#F59E0B");
                    ChatLlmStatusBadge.Background = BrushFromHex("#241D0E");
                    ChatLlmStartBtn.Content = "Установить llama.cpp";
                    ChatLlmStartBtn.Style = (Style)FindResource("SuccessButton");
                    ChatLlmStartBtn.Visibility = Visibility.Visible;
                    ChatLlmStartBtn.IsEnabled = !_catalogDownloading;
                    ChatLlmOpenUiBtn.Visibility = Visibility.Collapsed;
                    ChatLlmStopBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ChatQuickLlmStatusText.Text = hasLlmModels ? "○ LLM остановлена" : "○ Нет скачанных LLM";
                    ChatQuickLlmStatusText.Foreground = BrushFromHex("#71717A");
                    ChatLlmStatusBadgeText.Text = hasLlmModels ? "○ СТОП" : "○ НЕТ ВЕСОВ";
                    ChatLlmStatusBadgeText.Foreground = BrushFromHex("#A1A1AA");
                    ChatLlmStatusBadge.Background = BrushFromHex("#18181C");
                    ChatLlmStartBtn.Content = hasLlmModels ? "Запустить LLM" : "Выбрать модель →";
                    ChatLlmStartBtn.Style = (Style)FindResource("PrimaryButton");
                    ChatLlmStartBtn.Visibility = Visibility.Visible;
                    ChatLlmStartBtn.IsEnabled = true;
                    ChatLlmOpenUiBtn.Visibility = Visibility.Collapsed;
                    ChatLlmStopBtn.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка опроса: {ex.Message}";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private async Task RunServiceActionAsync(string serviceId, string action)
    {
        var svc = _manager.GetService(serviceId);

        if (action != "stop")
        {
            bool isComfy = serviceId == "comfyui";
            bool runtimeReady = isComfy ? RuntimeInstaller.IsComfyInstalled() : RuntimeInstaller.IsLlamaInstalled();
            if (!runtimeReady)
            {
                string rtName = isComfy ? "ComfyUI Portable (NVIDIA, ~2.0 ГБ)" : "llama.cpp (NVIDIA CUDA, ~620 МБ)";
                var ans = HubDialogWindow.Show(
                    $"Для запуска «{svc.Name}» нужен движок {rtName}.\n\nСкачать и установить его автоматически прямо сейчас?",
                    "Требуется движок (Runtime)",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (ans == MessageBoxResult.Yes)
                {
                    bool installed = await InstallRuntimeWithUiAsync(isComfy ? "comfyui" : "llama");
                    if (!installed) return;
                    // If weights are already downloaded (e.g. user downloaded LLM first), proceed to start automatically!
                    if (isComfy || _manager.GetAvailableLlmModels().Count > 0)
                    {
                        await RunServiceActionAsync(serviceId, "switch");
                    }
                }
                return;
            }

            if (serviceId == "qwen" && _manager.GetAvailableLlmModels().Count == 0)
            {
                MainTabs.SelectedItem = CatalogTab;
                SetCatalogView("text");
                StatusFooterText.Text = "Выбери и скачай текстовую модель (LLM) в Каталоге, затем вернись и нажми «Запустить».";
                return;
            }
        }

        _busyState[serviceId] = action == "stop" ? "Останавливается..." : "Запускается...";
        await RefreshStatusAsync();

        try
        {
            if (action == "stop")
            {
                StatusFooterText.Text = $"Остановка {svc.Name}...";
                await _manager.StopServiceAsync(serviceId);
                StatusFooterText.Text = $"{svc.Name} остановлен.";
            }
            else
            {
                await _manager.SwitchAndStartAsync(serviceId, msg => Dispatcher.Invoke(() => StatusFooterText.Text = msg));
                if (serviceId != "claude")
                {
                    AiServiceManager.OpenExternal(svc.Url);
                }
            }
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка ({svc.Name}): {ex.Message}";
            HubDialogWindow.Show(ex.Message, $"Ошибка — {svc.Name}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busyState[serviceId] = null;
            await RefreshStatusAsync();
        }
    }

    private async void StopAllBtn_Click(object sender, RoutedEventArgs e) => await StopAllAsync();

    private async Task StopAllAsync()
    {
        StopAllBtn.IsEnabled = false;
        StatusFooterText.Text = "Остановка всех GPU-сервисов...";
        try
        {
            await _manager.StopAllGpuAsync();
            TrimProcessMemory(clearThumbCache: true);
            StatusFooterText.Text = "Все GPU-сервисы остановлены, кэш памяти очищен.";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка остановки: {ex.Message}";
        }
        finally
        {
            StopAllBtn.IsEnabled = true;
            await RefreshStatusAsync();
        }
    }

    private sealed record LogSourceOption(string Id, string Name);

    private void InitLogSelector()
    {
        var items = _manager.Services
            .Select(s => new LogSourceOption(s.Id, s.Name))
            .ToList();
        items.Add(new LogSourceOption("crash", "Краш-логи приложения (crash.log)"));
        LogServiceCombo.DisplayMemberPath = "Name";
        LogServiceCombo.SelectedValuePath = "Id";
        LogServiceCombo.ItemsSource = items;
        LogServiceCombo.SelectedIndex = 0;
    }

    private void OpenLogsFor(string serviceId)
    {
        LogServiceCombo.SelectedValue = serviceId;
        MainTabs.SelectedItem = SystemTab;
        SetSystemSubMode("logs");
        RefreshSelectedLog();
    }

    private void SetChatSubMode(string mode)
    {
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");
        ChatModeAssistantBtn.Style = mode == "assistant" ? primary : dark;
        ChatModeLlmBtn.Style = mode == "llm" ? primary : dark;
        ChatAssistantView.Visibility = mode == "assistant" ? Visibility.Visible : Visibility.Collapsed;
        ChatLlmView.Visibility = mode == "llm" ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ChatModeAssistantBtn_Click(object sender, RoutedEventArgs e) => SetChatSubMode("assistant");
    internal void ChatModeLlmBtn_Click(object sender, RoutedEventArgs e) => SetChatSubMode("llm");

    internal async void ChatLlmModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLlmChange) return;
        if (ChatLlmModelCombo.SelectedValue is string chosen && !string.IsNullOrWhiteSpace(chosen))
        {
            await ApplySelectedLlmWeightsAsync(chosen);
        }
    }

    private async Task ApplySelectedLlmWeightsAsync(string chosen)
    {
        string prev = _manager.GetActiveLlmFileName();
        _manager.SetActiveLlmFileName(chosen);
        RefreshLlmSelector();
        UpdateLocalChatVisionBadge();

        if (!string.Equals(prev, chosen, StringComparison.OrdinalIgnoreCase) &&
            _busyState.GetValueOrDefault("qwen") is null &&
            !_localChatBusy &&
            await _manager.IsRunningAsync(_manager.GetService("qwen")))
        {
            _busyState["qwen"] = "Смена весов...";
            await RefreshStatusAsync();
            try
            {
                StatusFooterText.Text = $"Переключение активной LLM на {chosen}...";
                ShowToast($"Переключаю LLM на {AiServiceManager.FriendlyLlmName(chosen)}...", "info");
                await _manager.StopServiceAsync("qwen");
                await _manager.SwitchAndStartAsync("qwen", msg => Dispatcher.Invoke(() => StatusFooterText.Text = msg));
                StatusFooterText.Text = $"Активная LLM переключена на {chosen}.";
                ShowToast($"Активна LLM: {AiServiceManager.FriendlyLlmName(chosen)}", "success");
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = $"Ошибка переключения LLM: {ex.Message}";
                ShowToast("Не удалось перезапустить LLM с новыми весами", "warn");
            }
            finally
            {
                _busyState["qwen"] = null;
                await RefreshStatusAsync();
            }
        }
        else
        {
            StatusFooterText.Text = $"Выбрана модель LLM: {chosen} (запустится автоматически при первом запросе).";
        }
    }

    internal async void ChatLlmStartBtn_Click(object sender, RoutedEventArgs e) => await RunServiceActionAsync("qwen", "switch");
    internal async void ChatLlmStopBtn_Click(object sender, RoutedEventArgs e) => await RunServiceActionAsync("qwen", "stop");
    internal void ChatLlmOpenUiBtn_Click(object sender, RoutedEventArgs e) => AiServiceManager.OpenExternal(_manager.GetService("qwen").Url);
    internal async void ChatLlmClaudeBtn_Click(object sender, RoutedEventArgs e) => await RunServiceActionAsync("claude", "switch");
    internal void ChatJumpCatalogTextBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = CatalogTab;
        SetCatalogView("text");
    }

    private void SetSystemSubMode(string mode)
    {
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");
        SysTabServicesBtn.Style = mode == "services" ? primary : dark;
        SysTabSettingsBtn.Style = mode == "settings" ? primary : dark;
        SysTabLogsBtn.Style = mode == "logs" ? primary : dark;
        SysTabGuideBtn.Style = mode == "guide" ? primary : dark;

        SysServicesView.Visibility = mode == "services" ? Visibility.Visible : Visibility.Collapsed;
        SysSettingsView.Visibility = mode == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SysLogsView.Visibility = mode == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SysGuideView.Visibility = mode == "guide" ? Visibility.Visible : Visibility.Collapsed;

        if (mode == "settings") RefreshRuntimeStatusUi();
        else if (mode == "logs") RefreshSelectedLog();
    }

    internal void SysTabServicesBtn_Click(object sender, RoutedEventArgs e) => SetSystemSubMode("services");
    internal void SysTabSettingsBtn_Click(object sender, RoutedEventArgs e) => SetSystemSubMode("settings");
    internal void SysTabLogsBtn_Click(object sender, RoutedEventArgs e) => SetSystemSubMode("logs");
    internal void SysTabGuideBtn_Click(object sender, RoutedEventArgs e) => SetSystemSubMode("guide");

    internal void LogServiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSelectedLog();
    internal void RefreshLogBtn_Click(object sender, RoutedEventArgs e) => RefreshSelectedLog();

    private void RefreshSelectedLog()
    {
        if (LogServiceCombo.SelectedValue is string id)
        {
            LogsTextBox.Text = id == "crash"
                ? ReadCrashLogTail(150)
                : _manager.ReadLogsTail(id);
            LogsTextBox.ScrollToEnd();
        }
    }

    private static string ReadCrashLogTail(int maxLines = 150)
    {
        const string noCrashesMsg = "Краш-логов нет — приложение работает стабильно.";
        try
        {
            string path = App.CrashLogPath;
            if (!System.IO.File.Exists(path))
                return noCrashesMsg;

            var tail = new Queue<string>(maxLines + 1);
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (tail.Count >= maxLines)
                    tail.Dequeue();
                tail.Enqueue(line);
            }

            if (tail.Count == 0)
                return noCrashesMsg;

            string text = string.Join(Environment.NewLine, tail).Trim();
            return string.IsNullOrWhiteSpace(text) ? noCrashesMsg : text;
        }
        catch (Exception ex)
        {
            return $"Не удалось прочитать crash.log: {ex.Message}";
        }
    }

    internal async void CopyDiagnosticReportBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Brazhnichenko Hub — Diagnostic Report ===");
            sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            sb.AppendLine($"App Version: v{AppUpdateService.CurrentVersion}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");

            try
            {
                var metricsTask = _manager.GetSystemMetricsAsync();
                var statusesTask = _manager.GetAllStatusesAsync();
                await Task.WhenAll(metricsTask, statusesTask);
                var m = metricsTask.Result;
                var statuses = statusesTask.Result;

                if (m.Gpu is not null)
                {
                    double usedG = m.Gpu.VramUsedMiB / 1024.0;
                    double totalG = m.Gpu.VramTotalMiB / 1024.0;
                    sb.AppendLine($"GPU: {m.Gpu.Name} | VRAM: {usedG:F1} / {totalG:F1} GB | Util: {m.Gpu.UtilPct}% | Temp: {Math.Round(m.Gpu.TempC)}C");
                }
                else
                {
                    sb.AppendLine($"GPU: {GpuNameText?.Text ?? "N/A"} | VRAM: {VramText?.Text ?? "N/A"}");
                }
                sb.AppendLine($"RAM: {m.RamUsedGB:F1} / {m.RamTotalGB:F1} GB | Free Disk: {m.DiskFreeDGB:F1} GB");

                sb.AppendLine();
                sb.AppendLine("--- Services & Ports ---");
                foreach (var svc in _manager.Services)
                {
                    bool running = statuses.GetValueOrDefault(svc.Id);
                    sb.AppendLine($"- {svc.Name} (id={svc.Id}, port=:{svc.Port}): {(running ? "RUNNING" : "STOPPED")}");
                }
                sb.AppendLine($"- Assistant port: :{HubConfig.Current.AssistantPort}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Hardware/Services probe error: {ex.Message}");
                sb.AppendLine($"GPU UI: {GpuNameText?.Text} ({VramText?.Text}, {GpuExtraText?.Text})");
                sb.AppendLine($"RAM UI: {RamText?.Text} ({RamPctText?.Text}) | Disk: {DiskText?.Text}");
            }

            if (LogServiceCombo.SelectedValue is string selectedId && selectedId != "crash")
            {
                sb.AppendLine();
                sb.AppendLine($"--- Recent Service Log ({selectedId}) ---");
                string svcLog = _manager.ReadLogsTail(selectedId);
                var lines = svcLog.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                sb.AppendLine(string.Join(Environment.NewLine, lines.TakeLast(40)));
            }

            sb.AppendLine();
            sb.AppendLine($"--- Crash Log ({App.CrashLogPath}) ---");
            sb.AppendLine(ReadCrashLogTail(100));

            System.Windows.Clipboard.SetText(sb.ToString());
            ShowToast("Диагностический отчёт скопирован", "success");
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось скопировать отчёт: {ex.Message}", "warn");
        }
    }

    private void RefreshLlmSelector()
    {
        _suppressLlmChange = true;
        try
        {
            var models = _manager.GetAvailableLlmModels();
            System.Collections.IEnumerable items = models.Count == 0
                ? new[] { new LlmModelOption("", "Нет скачанных LLM — установите в «Модели и GPU»", "") }
                : models;
            string active = _manager.GetActiveLlmFileName();

            foreach (var combo in new[] { _llmModelCombo, ChatLlmModelCombo })
            {
                if (combo is null) continue;
                combo.ItemsSource = items;
                if (models.Count == 0) combo.SelectedIndex = 0;
                else combo.SelectedValue = active;
            }

            // Populate synchronized Director LLM selectors across Photo & Video tabs
            var directorChoices = new List<LlmModelOption> { new("", "Авто (активная LLM)", "") };
            directorChoices.AddRange(models);
            string savedDirModel = HubConfig.Current.DirectorLlmFile?.Trim() ?? "";
            if (!string.IsNullOrEmpty(savedDirModel) && !models.Any(m => m.FileName.Equals(savedDirModel, StringComparison.OrdinalIgnoreCase)))
                savedDirModel = "";

            foreach (var dCombo in new[] { PhotoSingleDirectorModelCombo, PhotoStoryDirectorModelCombo, VideoSingleDirectorModelCombo, VideoStoryDirectorModelCombo })
            {
                if (dCombo is null) continue;
                dCombo.DisplayMemberPath = "DisplayName";
                dCombo.SelectedValuePath = "FileName";
                dCombo.ItemsSource = directorChoices;
                dCombo.SelectedValue = savedDirModel;
                if (dCombo.SelectedIndex < 0) dCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressLlmChange = false;
        }
    }

    internal void DirectorModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLlmChange || !IsLoaded) return;
        if (sender is System.Windows.Controls.ComboBox src && src.SelectedValue is string chosen)
        {
            HubConfig.Current.DirectorLlmFile = chosen;
            HubConfig.Save();
            _suppressLlmChange = true;
            try
            {
                foreach (var dCombo in new[] { PhotoSingleDirectorModelCombo, PhotoStoryDirectorModelCombo, VideoSingleDirectorModelCombo, VideoStoryDirectorModelCombo })
                {
                    if (dCombo is not null && !ReferenceEquals(dCombo, src))
                        dCombo.SelectedValue = chosen;
                }
            }
            finally
            {
                _suppressLlmChange = false;
            }
            string label = string.IsNullOrEmpty(chosen) ? "Авто (активная LLM)" : chosen;
            StatusFooterText.Text = $"Модель Режиссёра выбрана: {label}";
        }
    }

    private void ApplyInitialTabSelection()
    {
        bool hasAnyModels = _manager.ScanInstalledModels().Count > 0;
        if (!HubConfig.Current.FirstRunCompleted || !hasAnyModels)
        {
            MainTabs.SelectedItem = CatalogTab;
            StatusFooterText.Text = hasAnyModels
                ? "Добро пожаловать! Открыт раздел «Модели и GPU» с рекомендациями под твой ПК."
                : "На ПК пока нет локальных моделей — выбери рекомендованные модели в «Модели и GPU» или спроси AI-Помощника во вкладке «Чат».";

            if (!HubConfig.Current.FirstRunCompleted && hasAnyModels)
            {
                HubConfig.Current.FirstRunCompleted = true;
                HubConfig.Save();
            }
        }
    }

    private void LoadModelsTable()
    {
        var models = _manager.ScanInstalledModels();
        ModelsGrid.ItemsSource = models;
        double totalGb = models.Sum(m => m.SizeGB);
        ModelsSummaryText.Text = $"Найдено файлов: {models.Count} • Общий объём: {totalGb:F1} ГБ";
        ModelCountBadge.Text = $"{models.Count} моделей ({totalGb:F1} ГБ)";
        RefreshLlmSelector();
        PopulateDesktopAssistantModelCombo();
        RefreshStudioEngineStatuses();
        RefreshAssistantStatusUi();
    }

    internal void RefreshModelsBtn_Click(object sender, RoutedEventArgs e) => LoadModelsTable();

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, MainTabs)) return;

        // Stop gallery video playback and release heavy gallery previews whenever leaving the Gallery tab
        if (!ReferenceEquals(MainTabs.SelectedItem, GalleryTab))
            ReleaseGalleryUiMemory();

        // Auto-sync models installed externally (e.g. via MCP from ChatGPT/Claude/Antigravity)
        if (ReferenceEquals(MainTabs.SelectedItem, PhotoTab) || ReferenceEquals(MainTabs.SelectedItem, VideoTab))
        {
            RefreshStudioEngineStatuses();
        }
        else if (ReferenceEquals(MainTabs.SelectedItem, GalleryTab))
        {
            _ = RefreshGalleryAsync(preserveSelection: true);
        }
        else if (ReferenceEquals(MainTabs.SelectedItem, CatalogTab) && !_catalogDownloading)
        {
            RenderCatalogRecommendations();
            LoadModelsTable();
        }
        else if (ReferenceEquals(MainTabs.SelectedItem, ChatTab))
        {
            RefreshAssistantStatusUi();
        }
        else if (ReferenceEquals(MainTabs.SelectedItem, SystemTab))
        {
            RefreshRuntimeStatusUi();
        }
    }

    internal void OpenSelectedModelFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsGrid.SelectedItem is not ModelEntry entry || string.IsNullOrWhiteSpace(entry.FullPath))
        {
            StatusFooterText.Text = "Сначала выбери модель в таблице.";
            return;
        }
        string? dir = System.IO.Path.GetDirectoryName(entry.FullPath);
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            AiServiceManager.OpenExternal(dir);
    }

    internal async void DeleteSelectedModelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsGrid.SelectedItem is not ModelEntry entry || string.IsNullOrWhiteSpace(entry.FullPath))
        {
            StatusFooterText.Text = "Сначала выбери файл модели в таблице.";
            return;
        }

        var confirm = HubDialogWindow.Show(
            $"Удалить файл модели с диска?\n\n{entry.Name}\nРазмер: {entry.SizeGB:F2} ГБ\nПуть: {entry.FullPath}",
            "Удаление файла модели",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            if (System.IO.File.Exists(entry.FullPath))
                System.IO.File.Delete(entry.FullPath);

            StatusFooterText.Text = $"Удалён файл: {entry.Name} (освобождено {entry.SizeGB:F2} ГБ).";
            LoadModelsTable();
            RenderCatalogRecommendations();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            HubDialogWindow.Show(
                $"Не удалось удалить файл (возможно, модель сейчас используется — сначала нажми «Освободить GPU»):\n\n{ex.Message}",
                "Ошибка удаления",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && GalleryLightboxOverlay?.Visibility == Visibility.Visible)
        {
            GalleryLightboxOverlay.Visibility = Visibility.Collapsed;
            GalleryLightboxImage.Source = null;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    internal void ShowToast(string message, string kind = "info")
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        void Apply()
        {
            if (ToastBanner is null || ToastText is null) return;

            var (bgHex, borderHex, fgHex) = kind switch
            {
                "success" => ("#121B17", "#34D399", "#ECFDF5"),
                "warn" => ("#1F1910", "#F59E0B", "#FEF3C7"),
                _ => ("#141418", "#FF521B", "#F4F4F6")
            };

            ToastBanner.Background = BrushFromHex(bgHex);
            ToastBanner.BorderBrush = BrushFromHex(borderHex);
            ToastText.Foreground = BrushFromHex(fgHex);
            ToastText.Text = message;
            ToastBanner.Visibility = Visibility.Visible;

            if (_toastTimer is null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
                _toastTimer.Tick += (_, _) =>
                {
                    _toastTimer.Stop();
                    if (ToastBanner is not null)
                        ToastBanner.Visibility = Visibility.Collapsed;
                };
            }
            else
            {
                _toastTimer.Stop();
            }
            _toastTimer.Start();
        }

        if (Dispatcher.CheckAccess())
            Apply();
        else
            Dispatcher.Invoke(Apply);
    }

    private static MediaBrush BrushFromHex(string hex) =>
        FrozenBrushCache.GetOrAdd(hex, static h =>
        {
            var b = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(h));
            b.Freeze();
            return b;
        });
}

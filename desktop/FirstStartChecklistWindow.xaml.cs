using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LocalAIHub.Services;
using MediaColor = System.Windows.Media.Color;

namespace LocalAIHub;

public partial class FirstStartChecklistWindow : Window
{
    private readonly AiServiceManager _manager;
    private readonly Func<Task> _installLlama;
    private readonly Action _openCatalogLlm;
    private readonly Func<Task> _installWhisper;
    private readonly Func<Task> _installPiper;
    private readonly Func<Task> _installComfy;
    private readonly Func<HardwareProfile?> _getHardware;
    private bool _suppressCheckEvent;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public FirstStartChecklistWindow(
        AiServiceManager manager,
        Func<HardwareProfile?> getHardware,
        Func<Task> installLlama,
        Action openCatalogLlm,
        Func<Task> installWhisper,
        Func<Task> installPiper,
        Func<Task> installComfy)
    {
        InitializeComponent();
        _manager = manager;
        _getHardware = getHardware;
        _installLlama = installLlama;
        _openCatalogLlm = openCatalogLlm;
        _installWhisper = installWhisper;
        _installPiper = installPiper;
        _installComfy = installComfy;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        };

        _suppressCheckEvent = true;
        ShowOnStartupCheck.IsChecked = HubConfig.Current.ShowFirstStartGuideOnLaunch;
        _suppressCheckEvent = false;

        RefreshChecklist();
    }

    public void RefreshChecklist()
    {
        ChecklistPanel.Children.Clear();

        var rt = RuntimeInstaller.GetStatus();
        var llmModels = _manager.GetAvailableLlmModels();
        var voice = LocalVoiceService.GetStatus();
        var sherpa = SherpaDesktopSttEngine.GetStatus();
        bool anySttReady = sherpa.AnyReady || voice.WhisperReady;
        var hw = _getHardware();
        bool isNvidia = RuntimeInstaller.IsNvidiaGpu(hw);

        if (hw is not null)
        {
            HwLineText.Text = $"Оборудование: {hw.OverallTierSummaryRu}";
        }

        int coreDone = (rt.LlamaInstalled ? 1 : 0) + (llmModels.Count > 0 ? 1 : 0) + (anySttReady ? 1 : 0);
        int totalDone = coreDone + (voice.PiperReady ? 1 : 0) + (rt.ComfyInstalled ? 1 : 0);
        ReadinessSummaryBadge.Text = coreDone == 3
            ? $"✓ БАЗА ГОТОВА ({totalDone}/5)"
            : $"БАЗА: {coreDone}/3 · ВСЕГО {totalDone}/5";
        ReadinessSummaryBadge.Foreground = new SolidColorBrush(
            coreDone == 3 ? MediaColor.FromRgb(52, 211, 153) : MediaColor.FromRgb(255, 82, 27));

        AddModuleCard(
            stepTag: "БАЗА · 1",
            title: isNvidia ? "Движок llama.cpp (NVIDIA CUDA)" : "Движок llama.cpp (Vulkan GPU)",
            purpose: "Необходим для локального чата, ассистента Alt+Space и умного оформления заметок. Подбирает CUDA или Vulkan автоматически.",
            installed: rt.LlamaInstalled,
            installedDetail: rt.LlamaInstalled ? $"Установлен: {rt.LlamaExePath}" : "Не установлен (~50–620 МБ)",
            required: true,
            actionLabel: "Установить llama.cpp",
            onAction: async btn =>
            {
                btn.IsEnabled = false;
                LiveStatusText.Text = "Запуск установки llama.cpp...";
                await _installLlama();
                RefreshChecklist();
            });

        AddModuleCard(
            stepTag: "БАЗА · 2",
            title: "Языковая модель LLM (например, Qwen 3 4B)",
            purpose: "Мозг чата и персонального ассистента: отвечает на вопросы, ищет в сети, переводит и структурирует заметки (.md). Для ноутбука оптимальна 4B (2.3 ГБ).",
            installed: llmModels.Count > 0,
            installedDetail: llmModels.Count > 0
                ? $"Найдено моделей: {llmModels.Count} ({llmModels[0].DisplayName})"
                : "Модели .gguf не скачаны — открой каталог и скачай рекомендованную под твой GPU",
            required: true,
            actionLabel: "Открыть каталог LLM",
            onAction: _ =>
            {
                _openCatalogLlm();
                Close();
                return Task.CompletedTask;
            });

        AddModuleCard(
            stepTag: "БАЗА · 3",
            title: "Офлайн-голос GigaAM v2 RU (Sherpa-ONNX, как на Android)",
            purpose: "Мгновенная офлайн-диктовка (~0.2 с) с авто-стопом по паузе для кнопки «Голос» в ассистенте Alt+Space.",
            installed: anySttReady,
            installedDetail: sherpa.AnyReady
                ? $"Установлен: {sherpa.ActiveModelLabel} — мгновенная диктовка готова"
                : (voice.WhisperReady ? "Установлен Whisper STT (можно также добавить GigaAM v2 в Настройках)" : "Не установлен — установи GigaAM v2 (~230 МБ) для мгновенной диктовки"),
            required: true,
            actionLabel: "Установить GigaAM v2 (~230 МБ)",
            onAction: async btn =>
            {
                btn.IsEnabled = false;
                btn.Content = "Установка GigaAM v2...";
                LiveStatusText.Text = "Скачивание Sherpa-ONNX + GigaAM v2 Russian (~230 МБ)...";
                await _installWhisper();
                RefreshChecklist();
                LiveStatusText.Text = "Готово! Статусы обновлены.";
            });

        AddModuleCard(
            stepTag: "ОПЦИОНАЛЬНО",
            title: "Офлайн-озвучка ответов (Piper HD TTS, ~80 МБ)",
            purpose: "Натуральный русский нейро-голос для озвучки ответов ассистента и озвучки видео-историй. Без него работает базовый Windows TTS.",
            installed: voice.PiperReady,
            installedDetail: voice.PiperReady ? "Установлен (русский нейро-голос готов)" : "Не установлен (опционально)",
            required: false,
            actionLabel: "Установить Piper HD (~80 МБ)",
            onAction: async btn =>
            {
                btn.IsEnabled = false;
                btn.Content = "Установка Piper...";
                LiveStatusText.Text = "Установка Piper HD TTS (~80 МБ)...";
                await _installPiper();
                RefreshChecklist();
                LiveStatusText.Text = "Готово! Статусы обновлены.";
            });

        AddModuleCard(
            stepTag: "ОПЦИОНАЛЬНО",
            title: "Студия фото и видео (ComfyUI, ~2 ГБ)",
            purpose: isNvidia
                ? "Нужен только если планируешь генерировать фото и видео (FLUX 3, Z-Image, Wan 2.2, LTX-2.5)."
                : "Нужен только для генерации фото/видео (рассчитан на NVIDIA CUDA; на Intel/AMD ноутбуке для ассистента и чата НЕ требуется).",
            installed: rt.ComfyInstalled,
            installedDetail: rt.ComfyInstalled ? $"Установлен: {rt.ComfyRootDir}" : "Не установлен (опционально)",
            required: false,
            actionLabel: "Установить ComfyUI",
            onAction: async btn =>
            {
                btn.IsEnabled = false;
                LiveStatusText.Text = "Запуск установки ComfyUI...";
                await _installComfy();
                RefreshChecklist();
            });
    }

    private void AddModuleCard(
        string stepTag,
        string title,
        string purpose,
        bool installed,
        string installedDetail,
        bool required,
        string actionLabel,
        Func<System.Windows.Controls.Button, Task> onAction)
    {
        var borderColor = installed
            ? MediaColor.FromRgb(38, 89, 68)
            : (required ? MediaColor.FromRgb(120, 62, 34) : MediaColor.FromRgb(42, 42, 48));

        var card = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(18, 18, 21)),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var dock = new DockPanel();

        var rightCol = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0)
        };
        DockPanel.SetDock(rightCol, Dock.Right);

        var statusBadge = new Border
        {
            Background = new SolidColorBrush(installed
                ? MediaColor.FromRgb(15, 36, 27)
                : (required ? MediaColor.FromRgb(41, 24, 14) : MediaColor.FromRgb(26, 26, 30))),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = installed ? "✓ УСТАНОВЛЕНО" : (required ? "○ НУЖНО ДЛЯ СТАРТА" : "○ ОПЦИОНАЛЬНО"),
                FontSize = 10,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(installed
                    ? MediaColor.FromRgb(52, 211, 153)
                    : (required ? MediaColor.FromRgb(255, 82, 27) : MediaColor.FromRgb(161, 161, 170)))
            }
        };
        rightCol.Children.Add(statusBadge);

        if (!installed)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = actionLabel,
                Style = (Style)FindResource(required ? "SuccessButton" : "DarkButton"),
                Padding = new Thickness(11, 5, 11, 5),
                FontSize = 11.5,
                Margin = new Thickness(0, 7, 0, 0)
            };
            btn.Click += async (_, _) => await onAction(btn);
            rightCol.Children.Add(btn);
        }

        dock.Children.Add(rightCol);

        var leftCol = new StackPanel();
        var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        titleRow.Children.Add(new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(28, 28, 34)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = stepTag,
                FontSize = 9.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(MediaColor.FromRgb(180, 180, 188))
            }
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 246)),
            VerticalAlignment = VerticalAlignment.Center
        });
        leftCol.Children.Add(titleRow);

        leftCol.Children.Add(new TextBlock
        {
            Text = purpose,
            FontSize = 12,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(180, 180, 188)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 4)
        });
        leftCol.Children.Add(new TextBlock
        {
            Text = installedDetail,
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = new SolidColorBrush(installed ? MediaColor.FromRgb(52, 211, 153) : MediaColor.FromRgb(126, 126, 135)),
            TextWrapping = TextWrapping.Wrap
        });

        dock.Children.Add(leftCol);
        card.Child = dock;
        ChecklistPanel.Children.Add(card);
    }

    private void ShowOnStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckEvent) return;
        HubConfig.Current.ShowFirstStartGuideOnLaunch = ShowOnStartupCheck.IsChecked == true;
        HubConfig.Save();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshChecklist();
        LiveStatusText.Text = "Статусы модулей обновлены.";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        HubConfig.Current.FirstRunCompleted = true;
        HubConfig.Save();
        Close();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

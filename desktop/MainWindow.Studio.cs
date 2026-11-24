using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private string _photoSubMode = "single"; // single | story | upscale
    private string _videoSubMode = "single"; // single | story

    private string? _studioPhotoRefPath;
    private string? _studioPhotoMaskPath;
    private string? _studioVideoStartPath;
    private string? _studioVideoEndPath;
    private string? _studioUpscaleSourcePath;

    private string? _photoLastResultPath;
    private string? _photoCompareBeforePath;
    private string? _videoLastResultPath;
    private long _photoLastSeed;
    private long _videoLastSeed;

    private bool _suppressPresetSelection;
    private readonly List<(string Path, long Seed, string Meta)> _photoBatchResults = [];
    private int _photoBatchSelectedIndex = -1;

    private List<QueuedGenerationJob> _queueJobs = [];
    private bool _queueRunning;
    private bool _queuePauseRequested;
    private bool _queueListExpanded;

    private static string StudioFramesDir => Path.Combine(ComfyStoryClient.StoriesRoot, "studio_frames");
    private static string StudioOutputDir => Path.Combine(HubConfig.ComfyOutputDir, "studio");

    private void InitStudio()
    {
        InitStudioPromptChips();
        RefreshStudioEngineStatuses();
        RefreshCustomLoras();
        SetPhotoSubMode("single");
        SetVideoSubMode("single");
    }

    private void RefreshCustomLoras()
    {
        if (PhotoExtraLoraCombo is null || VideoExtraLoraHighCombo is null || VideoExtraLoraLowCombo is null) return;
        var loras = ComfyStoryClient.ListCustomLoras();

        static void Populate(System.Windows.Controls.ComboBox combo, List<string> files)
        {
            string prev = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "— Без доп. LoRA —", Tag = "" });
            int selectIdx = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                combo.Items.Add(new ComboBoxItem { Content = f, Tag = f });
                if (f.Equals(prev, StringComparison.OrdinalIgnoreCase))
                    selectIdx = i + 1;
            }
            combo.SelectedIndex = selectIdx;
        }

        Populate(PhotoExtraLoraCombo, loras);
        Populate(VideoExtraLoraHighCombo, loras);
        Populate(VideoExtraLoraLowCombo, loras);
    }

    internal void RefreshLorasBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshCustomLoras();
        int cnt = ComfyStoryClient.ListCustomLoras().Count;
        StatusFooterText.Text = cnt > 0
            ? $"Обновлён список доп. LoRA: найдено {cnt} файл(а/ов) в ComfyUI\\models\\loras."
            : "В папке ComfyUI\\models\\loras пока нет пользовательских LoRA — нажми «Папка» и скопируй туда .safetensors.";
    }

    internal void OpenLorasFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(HubConfig.ComfyModelsDir, "loras");
        Directory.CreateDirectory(dir);
        AiServiceManager.OpenExternal(dir);
    }

    internal void LoraSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PhotoExtraLoraValText is not null && PhotoExtraLoraSlider is not null)
            PhotoExtraLoraValText.Text = PhotoExtraLoraSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (VideoExtraLoraHighValText is not null && VideoExtraLoraHighSlider is not null)
            VideoExtraLoraHighValText.Text = VideoExtraLoraHighSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (VideoExtraLoraLowValText is not null && VideoExtraLoraLowSlider is not null)
            VideoExtraLoraLowValText.Text = VideoExtraLoraLowSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private (string? lora, double strength) GetSelectedPhotoExtraLora()
    {
        string? lora = (PhotoExtraLoraCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
        double str = PhotoExtraLoraSlider?.Value ?? 0.75;
        return (string.IsNullOrWhiteSpace(lora) ? null : lora, str);
    }

    private (string? highLora, double highStr, string? lowLora, double lowStr) GetSelectedVideoExtraLoras()
    {
        string? high = (VideoExtraLoraHighCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
        string? low = (VideoExtraLoraLowCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
        double hStr = VideoExtraLoraHighSlider?.Value ?? 0.75;
        double lStr = VideoExtraLoraLowSlider?.Value ?? 0.75;
        return (
            string.IsNullOrWhiteSpace(high) ? null : high,
            hStr,
            string.IsNullOrWhiteSpace(low) ? null : low,
            lStr
        );
    }

    private void InitStudioPromptChips()
    {
        var photoChips = new (string Label, string Snippet)[]
        {
            ("Фотореализм 35mm", "shot on 35mm Kodak Portra 400 film, photorealistic, natural textures, ultra detailed"),
            ("Кино ARRI Alexa", "cinematic film still shot on ARRI Alexa, anamorphic lens, rich filmic color grading"),
            ("Золотой час", "warm golden hour sunlight, soft rim light, long gentle shadows"),
            ("Неон / Ночь", "vibrant cyan and magenta neon reflections on wet surfaces, moody night atmosphere"),
            ("Боке 85mm", "85mm portrait lens, shallow depth of field, creamy cinematic bokeh"),
            ("3D-мультфильм", "stylized 3D animated film still, expressive character, soft global illumination, clean textures"),
        };
        PopulateChips(PhotoChipsPanel, PhotoPromptBox, photoChips);

        var videoChips = new (string Label, string Snippet)[]
        {
            ("Плавный наезд", "smooth slow dolly push-in camera movement"),
            ("Полёт вокруг", "slow orbital camera shot moving smoothly around the subject"),
            ("Лёгкий ветер", "gentle natural breeze moving hair and fabric, subtle realistic motion"),
            ("Звук природы (LTX)", "natural ambient sound of soft wind, rustling leaves, and calm atmosphere"),
            ("Звук дождя (LTX)", "synchronized realistic sound of gentle rain and distant rolling waves"),
        };
        PopulateChips(VideoChipsPanel, VideoPromptBox, videoChips);
    }

    private void PopulateChips(System.Windows.Controls.Panel panel, System.Windows.Controls.TextBox targetBox, (string Label, string Snippet)[] chips)
    {
        panel.Children.Clear();
        foreach (var (label, snippet) in chips)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = "+ " + label,
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 6, 5),
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 11.5
            };
            btn.Click += (_, _) =>
            {
                string cur = targetBox.Text.Trim();
                targetBox.Text = string.IsNullOrEmpty(cur) ? snippet : cur.TrimEnd(',', '.') + ", " + snippet;
                StatusFooterText.Text = $"Добавлен тег: {label}";
            };
            panel.Children.Add(btn);
        }
    }

    internal void JumpPhotoBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = PhotoTab;
        SetPhotoSubMode("single");
    }

    internal void JumpVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = VideoTab;
        SetVideoSubMode("single");
    }

    internal void PhotoModeSingleBtn_Click(object sender, RoutedEventArgs e) => SetPhotoSubMode("single");
    internal void PhotoModeStoryBtn_Click(object sender, RoutedEventArgs e) => SetPhotoSubMode("story");
    internal void PhotoModeUpscaleBtn_Click(object sender, RoutedEventArgs e) => SetPhotoSubMode("upscale");

    internal void VideoModeSingleBtn_Click(object sender, RoutedEventArgs e) => SetVideoSubMode("single");
    internal void VideoModeStoryBtn_Click(object sender, RoutedEventArgs e) => SetVideoSubMode("story");

    private void SetPhotoSubMode(string mode)
    {
        _photoSubMode = mode;
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");

        PhotoModeSingleBtn.Style = mode == "single" ? primary : dark;
        PhotoModeStoryBtn.Style = mode == "story" ? primary : dark;
        PhotoModeUpscaleBtn.Style = mode == "upscale" ? primary : dark;

        PhotoSingleGrid.Visibility = mode is "single" or "upscale" ? Visibility.Visible : Visibility.Collapsed;
        PhotoStoryView.Visibility = mode == "story" ? Visibility.Visible : Visibility.Collapsed;

        StudioPhotoOptions.Visibility = mode == "single" ? Visibility.Visible : Visibility.Collapsed;
        StudioUpscaleOptions.Visibility = mode == "upscale" ? Visibility.Visible : Visibility.Collapsed;
        PhotoPromptCard.Visibility = mode == "single" ? Visibility.Visible : Visibility.Collapsed;

        PhotoGenerateBtn.Content = mode == "upscale" ? "Увеличить резкость ×4" : "Сгенерировать фото";
        if (PhotoEnqueueBtn is not null)
            PhotoEnqueueBtn.Visibility = mode == "single" ? Visibility.Visible : Visibility.Collapsed;
        if (PhotoBatchStripPanel is not null)
            PhotoBatchStripPanel.Visibility = mode == "single" && _photoBatchResults.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PhotoUseLastSeedBtn.Visibility = mode == "single" && _photoLastSeed > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStudioMissingBanner();
    }

    private void SetVideoSubMode(string mode)
    {
        _videoSubMode = mode;
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");

        VideoModeSingleBtn.Style = mode == "single" ? primary : dark;
        VideoModeStoryBtn.Style = mode == "story" ? primary : dark;

        VideoSingleGrid.Visibility = mode == "single" ? Visibility.Visible : Visibility.Collapsed;
        VideoStoryView.Visibility = mode == "story" ? Visibility.Visible : Visibility.Collapsed;
        VideoUseLastSeedBtn.Visibility = mode == "single" && _videoLastSeed > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStudioMissingBanner();
    }

    private void RefreshStudioEngineStatuses()
    {
        static void AnnotateCombo(System.Windows.Controls.ComboBox combo)
        {
            int firstInstalled = -1;
            int selectedIdx = combo.SelectedIndex;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is not ComboBoxItem item) continue;
                string tag = item.Tag as string ?? "";
                if (string.IsNullOrEmpty(tag)) continue;

                string raw = (item.Content?.ToString() ?? "")
                    .Replace("✓ ", "")
                    .Replace("✕ ", "")
                    .Replace(" [не скачана]", "");

                bool installed = ModelCatalog.IsMediaEngineInstalled(tag);
                item.Content = installed ? $"✓ {raw}" : $"✕ {raw} [не скачана]";
                item.Foreground = BrushFromHex(installed ? "#F4F4F6" : "#71717A");
                if (installed && firstInstalled < 0) firstInstalled = i;
            }

            if (selectedIdx >= 0 && selectedIdx < combo.Items.Count &&
                combo.Items[selectedIdx] is ComboBoxItem cur && cur.Tag is string curTag &&
                !ModelCatalog.IsMediaEngineInstalled(curTag) && firstInstalled >= 0)
            {
                combo.SelectedIndex = firstInstalled;
            }
        }

        AnnotateCombo(StudioPhotoEngineCombo);
        AnnotateCombo(StudioVideoEngineCombo);
        AnnotateCombo(PhotoStoryEngineCombo);
        AnnotateCombo(VideoStoryEngineCombo);
        RefreshCustomLoras();
        UpdateStudioMissingBanner();
    }

    internal void StudioEngineCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateStudioMissingBanner();
    }

    private string GetCurrentPhotoEngineTag() =>
        _photoSubMode == "upscale"
            ? "upscale"
            : (StudioPhotoEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zimage";

    private string GetCurrentVideoEngineTag() =>
        (StudioVideoEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wan14b_fp8";

    private void UpdateStudioMissingBanner()
    {
        bool comfyOk = RuntimeInstaller.IsComfyInstalled();

        if (PhotoMissingBanner is not null)
        {
            string pTag = GetCurrentPhotoEngineTag();
            bool pModelOk = ModelCatalog.IsMediaEngineInstalled(pTag);
            if (!comfyOk)
            {
                PhotoMissingBanner.Visibility = Visibility.Visible;
                PhotoMissingText.Text = "⚠ Для генерации фото нужен движок ComfyUI Portable (~2.0 ГБ) и фото-модели.";
                PhotoMissingActionBtn.Content = "Установить в «Модели и GPU» →";
            }
            else if (!pModelOk)
            {
                PhotoMissingBanner.Visibility = Visibility.Visible;
                PhotoMissingText.Text = "⚠ Выбранная модель фото ещё не скачана на диск. Скачай её в «Модели и GPU → 2. Фото».";
                PhotoMissingActionBtn.Content = "Скачать модель →";
            }
            else
            {
                PhotoMissingBanner.Visibility = Visibility.Collapsed;
            }
        }

        if (VideoMissingBanner is not null)
        {
            string vTag = GetCurrentVideoEngineTag();
            bool vModelOk = ModelCatalog.IsMediaEngineInstalled(vTag);
            if (!comfyOk)
            {
                VideoMissingBanner.Visibility = Visibility.Visible;
                VideoMissingText.Text = "⚠ Для генерации видео нужен движок ComfyUI Portable (~2.0 ГБ) и видео-модели.";
                VideoMissingActionBtn.Content = "Установить в «Модели и GPU» →";
            }
            else if (!vModelOk)
            {
                VideoMissingBanner.Visibility = Visibility.Visible;
                VideoMissingText.Text = "⚠ Выбранная видео-модель ещё не скачана на диск. Скачай её в «Модели и GPU → 3. Видео».";
                VideoMissingActionBtn.Content = "Скачать модель →";
            }
            else
            {
                VideoMissingBanner.Visibility = Visibility.Collapsed;
            }
        }
    }

    internal void PhotoMissingActionBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = CatalogTab;
        SetCatalogView("photo");
    }

    internal void VideoMissingActionBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = CatalogTab;
        SetCatalogView("video");
    }

    private readonly List<(string Role, string Content)> _singleVideoDirectorChat = [];

    private void RefreshStudioControlsEnabled()
    {
        PhotoGenerateBtn.IsEnabled = !_storyBusy;
        VideoGenerateBtn.IsEnabled = !_storyBusy;
        PhotoEnhanceDirectorBtn.IsEnabled = !_storyBusy && !_directorBusy;
        VideoEnhanceDirectorBtn.IsEnabled = !_storyBusy && !_directorBusy;
        if (VideoSingleDiscussBtn is not null) VideoSingleDiscussBtn.IsEnabled = !_storyBusy && !_directorBusy;
        PhotoDirectorBuildBtn.IsEnabled = !_storyBusy && !_directorBusy;
        VideoDirectorBuildBtn.IsEnabled = !_storyBusy && !_directorBusy;
        if (QueueStartBtn is not null) QueueStartBtn.IsEnabled = !_storyBusy && !_queueRunning;
    }

    // ---- Built-in AI Director for Single Photo & Single Video ----

    internal async void PhotoEnhanceDirectorBtn_Click(object sender, RoutedEventArgs e)
    {
        string idea = PhotoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Напиши короткую идею (можно по-русски) в поле промпта.";
            return;
        }
        PhotoEnhanceDirectorBtn.IsEnabled = false;
        PhotoGenerateBtn.IsEnabled = false;
        try
        {
            bool hasRef = !string.IsNullOrWhiteSpace(_studioPhotoRefPath);
            string enhanced = await _manager.EnhancePromptWithQwenAsync(
                idea,
                "photo",
                msg => Dispatcher.Invoke(() => StatusFooterText.Text = msg),
                hasInputImage: hasRef);
            if (!string.IsNullOrWhiteSpace(enhanced))
            {
                PhotoPromptBox.Text = enhanced;
                PromptHistoryService.Record("photo", enhanced);
                StatusFooterText.Text = hasRef
                    ? "Готово! Режиссёр собрал промпт правки по референсу — можно жать «Сгенерировать фото»."
                    : "Готово! Режиссёр улучшил промпт с оптикой и светом — можно жать «Сгенерировать фото».";
                ShowToast("Промпт фото улучшен AI-Режиссёром", "success");
            }
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = ex.Message;
            HubDialogWindow.Show(ex.Message, "AI-Режиссёр промпта", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            PhotoEnhanceDirectorBtn.IsEnabled = !_storyBusy && !_directorBusy;
            PhotoGenerateBtn.IsEnabled = !_storyBusy;
        }
    }

    internal async void VideoEnhanceDirectorBtn_Click(object sender, RoutedEventArgs e) =>
        await RunSingleVideoEnhanceAsync(includeDiscussion: _singleVideoDirectorChat.Count > 0);

    internal void SingleVideoDirectorHideChatBtn_Click(object sender, RoutedEventArgs e) =>
        SingleVideoDirectorChatPanel.Visibility = Visibility.Collapsed;

    internal void SingleVideoDirectorClearChatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_directorBusy) return;
        _singleVideoDirectorChat.Clear();
        SingleVideoDirectorMessagesPanel.Children.Clear();
        StatusFooterText.Text = "Диалог с Режиссёром одиночного видео очищен.";
    }

    internal async void VideoSingleDiscussBtn_Click(object sender, RoutedEventArgs e)
    {
        string idea = VideoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Сначала напиши короткую идею сцены в поле «Промпт видео».";
            return;
        }
        SingleVideoDirectorChatPanel.Visibility = Visibility.Visible;
        if (_singleVideoDirectorChat.Count > 0)
        {
            SingleVideoDirectorReplyBox.Focus();
            return;
        }
        SingleVideoDirectorMessagesPanel.Children.Clear();
        await RunSingleVideoDiscussionAsync($"Вот моя идея сцены: {idea}. Оцени и предложи конкретное движение камеры/действие (и задай 1–2 уточняющих вопроса, если нужно).");
    }

    internal async void SingleVideoDirectorSendBtn_Click(object sender, RoutedEventArgs e)
    {
        string msg = SingleVideoDirectorReplyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(msg)) return;
        SingleVideoDirectorReplyBox.Clear();
        await RunSingleVideoDiscussionAsync(msg);
    }

    internal async void SingleVideoDirectorReplyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            string msg = SingleVideoDirectorReplyBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                SingleVideoDirectorReplyBox.Clear();
                await RunSingleVideoDiscussionAsync(msg);
            }
        }
    }

    internal async void SingleVideoDirectorApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        string pending = SingleVideoDirectorReplyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(pending))
        {
            SingleVideoDirectorReplyBox.Clear();
            _singleVideoDirectorChat.Add(("user", pending));
            AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "user", pending);
        }
        await RunSingleVideoEnhanceAsync(includeDiscussion: true);
    }

    private async Task RunSingleVideoDiscussionAsync(string userMsg)
    {
        if (_storyBusy || _directorBusy) return;
        _directorBusy = true;
        SetDirectorButtonsEnabled(false);
        VideoGenerateBtn.IsEnabled = false;
        SingleVideoDirectorChatPanel.Visibility = Visibility.Visible;

        AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "user", userMsg);
        var wait = AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "status", "Режиссёр обдумывает сцену...");
        try
        {
            string engine = GetCurrentVideoEngineTag();
            string targetMode = engine == "ltx25" ? "video_ltx" : "video_wan";
            int dur = int.TryParse((StudioVideoDurCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 4;
            bool hasStart = !string.IsNullOrWhiteSpace(_studioVideoStartPath);
            bool hasEnd = !string.IsNullOrWhiteSpace(_studioVideoEndPath);

            string reply = await _manager.DiscussSinglePromptWithDirectorAsync(
                targetMode,
                VideoPromptBox.Text.Trim(),
                hasStart,
                hasEnd,
                dur,
                _singleVideoDirectorChat,
                userMsg,
                msg => Dispatcher.Invoke(() => StatusFooterText.Text = msg));

            SingleVideoDirectorMessagesPanel.Children.Remove(wait);
            _singleVideoDirectorChat.Add(("user", userMsg));
            _singleVideoDirectorChat.Add(("assistant", reply));
            AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "assistant", reply);
            StatusFooterText.Text = "Режиссёр ответил — напиши уточнение или жми «Собрать в промпт».";
            SingleVideoDirectorReplyBox.Focus();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            SingleVideoDirectorMessagesPanel.Children.Remove(wait);
            AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "error", ex.Message);
            StatusFooterText.Text = $"Ошибка Режиссёра: {ex.Message}";
        }
        finally
        {
            _directorBusy = false;
            SetDirectorButtonsEnabled(!_storyBusy);
            VideoGenerateBtn.IsEnabled = !_storyBusy;
        }
    }

    private async Task RunSingleVideoEnhanceAsync(bool includeDiscussion)
    {
        if (_storyBusy || _directorBusy) return;
        string idea = VideoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Напиши короткую идею сцены (можно по-русски) в поле промпта.";
            return;
        }
        string engine = GetCurrentVideoEngineTag();
        string targetMode = engine == "ltx25" ? "video_ltx" : "video_wan";
        bool hasStart = !string.IsNullOrWhiteSpace(_studioVideoStartPath);
        bool hasEnd = !string.IsNullOrWhiteSpace(_studioVideoEndPath);

        _directorBusy = true;
        SetDirectorButtonsEnabled(false);
        VideoGenerateBtn.IsEnabled = false;
        Border? wait = SingleVideoDirectorChatPanel.Visibility == Visibility.Visible
            ? AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "status", "Собираю финальный кинематографический промпт...")
            : null;
        try
        {
            string enhanced = await _manager.EnhancePromptWithQwenAsync(
                idea,
                targetMode,
                msg => Dispatcher.Invoke(() => StatusFooterText.Text = msg),
                hasInputImage: hasStart,
                hasLastImage: hasEnd,
                discussionHistory: includeDiscussion ? _singleVideoDirectorChat : null);

            if (wait is not null) SingleVideoDirectorMessagesPanel.Children.Remove(wait);

            if (!string.IsNullOrWhiteSpace(enhanced))
            {
                VideoPromptBox.Text = enhanced;
                PromptHistoryService.Record("video", enhanced);
                string modeTag = engine == "ltx25"
                    ? (hasStart ? "LTX-2.5 I2V + звук" : "LTX-2.5 T2V + звук")
                    : (hasStart && hasEnd ? "Wan 2.2 First→Last" : hasStart ? "Wan 2.2 I2V (фокус на движении и камере)" : "Wan 2.2 T2V");
                StatusFooterText.Text = $"Готово! Режиссёр собрал промпт ({modeTag}).";
                ShowToast("Промпт видео улучшен AI-Режиссёром", "success");
                if (SingleVideoDirectorChatPanel.Visibility == Visibility.Visible)
                {
                    AppendDirectorBubble(SingleVideoDirectorMessagesPanel, SingleVideoDirectorChatScroll, "assistant",
                        $"✓ **Готово ({modeTag})!** Новый промпт вставлен в поле выше. Если захочешь подправить движение камеры, свет или темп — просто напиши здесь и снова нажми **«Собрать в промпт»**.");
                }
            }
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            if (wait is not null) SingleVideoDirectorMessagesPanel.Children.Remove(wait);
            StatusFooterText.Text = ex.Message;
            HubDialogWindow.Show(ex.Message, "AI-Режиссёр видео-промпта", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _directorBusy = false;
            SetDirectorButtonsEnabled(!_storyBusy);
            VideoGenerateBtn.IsEnabled = !_storyBusy;
        }
    }

    // ---- Prompt History, Favorites & Copy Prompt ----

    internal void PhotoHistoryBtn_Click(object sender, RoutedEventArgs e) =>
        ShowPromptHistoryMenu(PhotoHistoryBtn, PhotoPromptBox, "photo");

    internal void VideoHistoryBtn_Click(object sender, RoutedEventArgs e) =>
        ShowPromptHistoryMenu(VideoHistoryBtn, VideoPromptBox, "video");

    internal void PhotoCopyPromptBtn_Click(object sender, RoutedEventArgs e) =>
        CopyPromptToClipboard(PhotoPromptBox.Text);

    internal void VideoCopyPromptBtn_Click(object sender, RoutedEventArgs e) =>
        CopyPromptToClipboard(VideoPromptBox.Text);

    private void CopyPromptToClipboard(string text)
    {
        string clean = (text ?? "").Trim();
        if (string.IsNullOrEmpty(clean))
        {
            ShowToast("Поле промпта пустое", "warn");
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(clean);
            StatusFooterText.Text = "Промпт скопирован в буфер обмена.";
            ShowToast("Промпт скопирован в буфер", "success");
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Не удалось скопировать промпт: {ex.Message}";
            ShowToast("Не удалось скопировать промпт", "warn");
        }
    }

    private void ShowPromptHistoryMenu(System.Windows.Controls.Button anchorBtn, System.Windows.Controls.TextBox targetBox, string kind)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = anchorBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = BrushFromHex("#141418"),
            BorderBrush = BrushFromHex("#30303A"),
            Foreground = BrushFromHex("#F4F4F6"),
            MaxWidth = 520
        };

        string currentPrompt = targetBox.Text.Trim();
        if (string.IsNullOrEmpty(currentPrompt))
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "★ Сохранить текущий промпт в избранное (поле пустое)",
                IsEnabled = false,
                Foreground = BrushFromHex("#71717A")
            });
        }
        else
        {
            bool curFav = PromptHistoryService.IsFavorite(kind, currentPrompt);
            var saveCurItem = new System.Windows.Controls.MenuItem
            {
                Header = curFav
                    ? "★ Убрать текущий промпт из избранного"
                    : "★ Сохранить текущий промпт в избранное",
                Foreground = BrushFromHex("#F59E0B"),
                FontWeight = FontWeights.SemiBold
            };
            saveCurItem.Click += (_, _) =>
            {
                bool nowFav = PromptHistoryService.ToggleFavorite(kind, currentPrompt);
                ShowToast(nowFav ? "Промпт сохранён в избранное" : "Промпт убран из избранного", nowFav ? "success" : "info");
            };
            menu.Items.Add(saveCurItem);
        }

        menu.Items.Add(new Separator());

        var items = PromptHistoryService.Load(kind);
        if (items.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "История пока пуста — сгенерируй или сохрани промпт",
                IsEnabled = false,
                Foreground = BrushFromHex("#71717A")
            });
        }
        else
        {
            bool addedFavHeader = false;
            bool addedRecentHeader = false;

            foreach (var entry in items)
            {
                if (entry.IsFavorite && !addedFavHeader)
                {
                    addedFavHeader = true;
                    menu.Items.Add(new System.Windows.Controls.MenuItem
                    {
                        Header = "★ ИЗБРАННОЕ",
                        IsEnabled = false,
                        FontSize = 10,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushFromHex("#F59E0B")
                    });
                }
                else if (!entry.IsFavorite && !addedRecentHeader)
                {
                    if (addedFavHeader) menu.Items.Add(new Separator());
                    addedRecentHeader = true;
                    menu.Items.Add(new System.Windows.Controls.MenuItem
                    {
                        Header = "НЕДАВНИЕ",
                        IsEnabled = false,
                        FontSize = 10,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushFromHex("#71717A")
                    });
                }

                string capturedText = entry.Text;
                bool isFav = entry.IsFavorite;

                var rowGrid = new Grid { Width = 420 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleTb = new TextBlock
                {
                    Text = TruncatePromptPreview(capturedText, 72),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = BrushFromHex(isFav ? "#FEF3C7" : "#E4E4E8"),
                    FontSize = 12
                };
                Grid.SetColumn(titleTb, 0);
                rowGrid.Children.Add(titleTb);

                var actions = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var mi = new System.Windows.Controls.MenuItem
                {
                    Header = rowGrid,
                    ToolTip = capturedText
                };

                var favBtn = new System.Windows.Controls.Button
                {
                    Content = isFav ? "★" : "☆",
                    ToolTip = isFav ? "Убрать из избранного" : "Добавить в избранное",
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = BrushFromHex(isFav ? "#F59E0B" : "#71717A"),
                    Padding = new Thickness(5, 1, 5, 1),
                    FontSize = 12,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                favBtn.Click += (_, ev) =>
                {
                    ev.Handled = true;
                    bool nowFav = PromptHistoryService.ToggleFavorite(kind, capturedText);
                    favBtn.Content = nowFav ? "★" : "☆";
                    favBtn.Foreground = BrushFromHex(nowFav ? "#F59E0B" : "#71717A");
                    favBtn.ToolTip = nowFav ? "Убрать из избранного" : "Добавить в избранное";
                    titleTb.Foreground = BrushFromHex(nowFav ? "#FEF3C7" : "#E4E4E8");
                    ShowToast(nowFav ? "Добавлено в избранное" : "Убрано из избранного", nowFav ? "success" : "info");
                };
                actions.Children.Add(favBtn);

                var delBtn = new System.Windows.Controls.Button
                {
                    Content = "✕",
                    ToolTip = "Удалить из истории",
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = BrushFromHex("#71717A"),
                    Padding = new Thickness(5, 1, 4, 1),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false
                };
                delBtn.Click += (_, ev) =>
                {
                    ev.Handled = true;
                    PromptHistoryService.Remove(kind, capturedText);
                    menu.Items.Remove(mi);
                    ShowToast("Промпт удалён из истории", "info");
                };
                actions.Children.Add(delBtn);

                Grid.SetColumn(actions, 1);
                rowGrid.Children.Add(actions);

                mi.Click += (_, _) =>
                {
                    targetBox.Text = capturedText;
                    targetBox.Focus();
                    targetBox.CaretIndex = capturedText.Length;
                    StatusFooterText.Text = "Промпт вставлен из истории.";
                    ShowToast("Промпт вставлен из истории", "info");
                };

                menu.Items.Add(mi);
            }
        }

        anchorBtn.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static string TruncatePromptPreview(string text, int maxLen)
    {
        string singleLine = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (singleLine.Contains("  ", StringComparison.Ordinal))
            singleLine = singleLine.Replace("  ", " ", StringComparison.Ordinal);
        return singleLine.Length <= maxLen ? singleLine : singleLine[..(maxLen - 1)] + "…";
    }

    // ---- Drag & Drop Image Support ----

    private static bool TryExtractDroppedImagePath(System.Windows.IDataObject? data, out string? filePath)
    {
        filePath = null;
        if (data is null || !data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return false;
        if (data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return false;

        foreach (string f in files)
        {
            if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) continue;
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
            {
                filePath = f;
                return true;
            }
        }
        return false;
    }

    internal void ImageDrop_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = !_storyBusy && TryExtractDroppedImagePath(e.Data, out _)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    internal void PhotoRefDrop_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (_storyBusy) return;
        if (!TryExtractDroppedImagePath(e.Data, out string? file) || file is null)
        {
            ShowToast("Поддерживаются изображения PNG, JPG, WEBP, BMP", "warn");
            return;
        }
        SetStudioPhotoRefImage(file, "Перетащено");
        ShowToast($"Референс добавлен: {Path.GetFileName(file)}", "success");
    }

    internal void UpscaleDrop_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (_storyBusy) return;
        if (!TryExtractDroppedImagePath(e.Data, out string? file) || file is null)
        {
            ShowToast("Поддерживаются изображения PNG, JPG, WEBP, BMP", "warn");
            return;
        }
        SetStudioUpscaleSource(file);
        ShowToast($"Фото для апскейла загружено: {Path.GetFileName(file)}", "success");
    }

    internal void VideoStartDrop_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (_storyBusy) return;
        if (!TryExtractDroppedImagePath(e.Data, out string? file) || file is null)
        {
            ShowToast("Поддерживаются изображения PNG, JPG, WEBP, BMP", "warn");
            return;
        }
        SetStudioVideoStartImage(file);
        ShowToast($"Стартовый кадр (I2V) добавлен: {Path.GetFileName(file)}", "success");
    }

    // ---- Reference Thumbnail Helpers ----

    private static BitmapImage? LoadThumbnailNoLock(string? path, int decodePixelWidth = 100)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodePixelWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void UpdateStudioRefThumb(System.Windows.Controls.Image? img, Border? border, string? path)
    {
        if (img is null || border is null) return;
        var bmp = LoadThumbnailNoLock(path, 100);
        img.Source = bmp;
        border.Visibility = bmp is not null ? Visibility.Visible : Visibility.Collapsed;
        border.ToolTip = bmp is not null && !string.IsNullOrEmpty(path)
            ? $"Открыть оригинал: {Path.GetFileName(path)}"
            : null;
    }

    internal void StudioPhotoRefThumb_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(_studioPhotoRefPath) && File.Exists(_studioPhotoRefPath))
            AiServiceManager.OpenExternal(_studioPhotoRefPath);
    }

    internal void StudioVideoStartThumb_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(_studioVideoStartPath) && File.Exists(_studioVideoStartPath))
            AiServiceManager.OpenExternal(_studioVideoStartPath);
    }

    internal void StudioVideoEndThumb_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(_studioVideoEndPath) && File.Exists(_studioVideoEndPath))
            AiServiceManager.OpenExternal(_studioVideoEndPath);
    }

    // ---- File Pickers & Clipboard Image Paste ----

    private bool TryGetImageFromClipboard(out string? filePath)
    {
        filePath = null;
        try
        {
            Directory.CreateDirectory(StudioFramesDir);

            // 1. File copied in Windows Explorer (Ctrl+C on an image file)
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                foreach (string? f in files)
                {
                    if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
                    {
                        filePath = f;
                        return true;
                    }
                    if (ext is ".bmp" or ".gif" or ".tif" or ".tiff")
                    {
                        string dst = Path.Combine(StudioFramesDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                        using var img = System.Drawing.Image.FromFile(f);
                        img.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
                        filePath = dst;
                        return true;
                    }
                }
            }

            // 2. Direct bitmap in clipboard (Win+Shift+S, PrintScreen, browser "Copy image", Telegram, Photoshop)
            // Prefer System.Windows.Forms.Clipboard.GetImage() first because it preserves alpha/RGB reliably from browsers
            if (System.Windows.Forms.Clipboard.ContainsImage())
            {
                using var winImg = System.Windows.Forms.Clipboard.GetImage();
                if (winImg is not null)
                {
                    string dst = Path.Combine(StudioFramesDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                    winImg.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
                    filePath = dst;
                    return true;
                }
            }

            // 3. WPF BitmapSource fallback
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bmp = System.Windows.Clipboard.GetImage();
                if (bmp is not null)
                {
                    string dst = Path.Combine(StudioFramesDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(dst);
                    encoder.Save(fs);
                    filePath = dst;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Не удалось прочитать картинку из буфера: {ex.Message}";
            return false;
        }

        StatusFooterText.Text = "В буфере обмена нет картинки — скопируй фото (Win+Shift+S, браузер или Ctrl+C по файлу) и повтори.";
        return false;
    }

    private static bool ClipboardHasImage()
    {
        try
        {
            if (System.Windows.Forms.Clipboard.ContainsImage() || System.Windows.Clipboard.ContainsImage())
                return true;
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                foreach (string? f in System.Windows.Clipboard.GetFileDropList())
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp") return true;
                }
            }
        }
        catch { }
        return false;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (_storyBusy) return;

        // If focus is in a text box and clipboard contains text, let normal text paste work
        bool inTextControl = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;
        if (inTextControl && System.Windows.Clipboard.ContainsText() && !ClipboardHasImage())
            return;

        if (!ClipboardHasImage()) return;

        bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ReferenceEquals(MainTabs.SelectedItem, PhotoTab))
        {
            if (_photoSubMode == "single")
            {
                e.Handled = true;
                StudioPastePhotoRefBtn_Click(this, new RoutedEventArgs());
            }
            else if (_photoSubMode == "upscale")
            {
                e.Handled = true;
                StudioPasteUpscaleBtn_Click(this, new RoutedEventArgs());
            }
        }
        else if (ReferenceEquals(MainTabs.SelectedItem, VideoTab))
        {
            if (_videoSubMode == "single")
            {
                e.Handled = true;
                if (shiftHeld && !string.IsNullOrEmpty(_studioVideoStartPath))
                    StudioPasteVideoEndBtn_Click(this, new RoutedEventArgs());
                else
                    StudioPasteVideoStartBtn_Click(this, new RoutedEventArgs());
            }
            else if (_videoSubMode == "story")
            {
                e.Handled = true;
                PasteStarterPhotoFromClipboard();
            }
        }
    }

    private void SetStudioPhotoRefImage(string file, string sourceLabel = "Референс")
    {
        _studioPhotoRefPath = file;
        ClearStudioPhotoMask();
        string dims = TryReadImageDimensions(file, out int w, out int h) ? $" ({w}×{h})" : "";
        StudioPhotoRefLabel.Text = $"{sourceLabel}: {Path.GetFileName(file)}{dims}";
        StudioClearPhotoRefBtn.Visibility = Visibility.Visible;
        if (StudioPhotoMaskBtn is not null) StudioPhotoMaskBtn.Visibility = Visibility.Visible;
        UpdateStudioRefThumb(StudioPhotoRefThumbImage, StudioPhotoRefThumbBorder, file);

        string curEngine = (StudioPhotoEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zimage";
        if (curEngine == "zimage")
        {
            SelectComboByTag(StudioPhotoEngineCombo, "flux3");
            StatusFooterText.Text = $"{sourceLabel} вставлен — модель переключена на FLUX 3 Klein (поддерживает правку по фото).";
        }
        else
        {
            StatusFooterText.Text = $"{sourceLabel} загружен: {Path.GetFileName(file)}{dims}";
        }
    }

    private void ClearStudioPhotoMask()
    {
        _studioPhotoMaskPath = null;
        if (StudioPhotoMaskBtnText is not null) StudioPhotoMaskBtnText.Text = "Маска";
        if (StudioPhotoMaskBtn is not null && TryFindResource("DarkButton") is Style ds)
            StudioPhotoMaskBtn.Style = ds;
        if (StudioClearPhotoMaskBtn is not null) StudioClearPhotoMaskBtn.Visibility = Visibility.Collapsed;
    }

    internal void StudioPhotoMaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy || string.IsNullOrWhiteSpace(_studioPhotoRefPath) || !File.Exists(_studioPhotoRefPath)) return;
        var editor = new MaskEditorWindow(_studioPhotoRefPath, _studioPhotoMaskPath) { Owner = this };
        if (editor.ShowDialog() == true && !string.IsNullOrWhiteSpace(editor.SavedMaskPath))
        {
            _studioPhotoMaskPath = editor.SavedMaskPath;
            if (StudioPhotoMaskBtnText is not null) StudioPhotoMaskBtnText.Text = "Маска ✓";
            if (StudioPhotoMaskBtn is not null && TryFindResource("PrimaryButton") is Style ps)
                StudioPhotoMaskBtn.Style = ps;
            if (StudioClearPhotoMaskBtn is not null) StudioClearPhotoMaskBtn.Visibility = Visibility.Visible;
            ShowToast("Маска Inpainting применена — изменятся только закрашенные области", "success");
        }
    }

    internal void StudioClearPhotoMaskBtn_Click(object sender, RoutedEventArgs e)
    {
        ClearStudioPhotoMask();
        ShowToast("Маска Inpainting сброшена", "info");
    }

    internal void PhotoCompareBeforeAfterBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_photoCompareBeforePath) || !File.Exists(_photoCompareBeforePath) ||
            string.IsNullOrWhiteSpace(_photoLastResultPath) || !File.Exists(_photoLastResultPath))
        {
            ShowToast("Нет пары До / После для сравнения", "info");
            return;
        }
        string title = _photoSubMode == "upscale" ? "Апскейл ×4 — До / После" : "Правка фото — До / После";
        var win = new BeforeAfterWindow(_photoCompareBeforePath, _photoLastResultPath, title) { Owner = this };
        win.ShowDialog();
    }

    internal void PhotoBatchGridBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_photoBatchResults.Count <= 1) return;
        var items = _photoBatchResults.Select(x => (x.Path, x.Seed)).ToList();
        var win = new BatchCompareWindow(items, Math.Max(0, _photoBatchSelectedIndex)) { Owner = this };
        if (win.ShowDialog() == true && win.SelectedIndex is int picked && picked >= 0 && picked < _photoBatchResults.Count)
        {
            var item = _photoBatchResults[picked];
            _photoLastSeed = item.Seed;
            ShowPhotoPreview(item.Path, item.Path, item.Meta);
            RenderPhotoBatchStrip(picked);
            ShowToast($"Выбран вариант #{picked + 1} (Seed: {item.Seed})", "success");
        }
    }

    internal void StudioPickPhotoRefBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        SetStudioPhotoRefImage(file);
    }

    internal void StudioPastePhotoRefBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        if (!TryGetImageFromClipboard(out string? file) || file is null) return;
        SetStudioPhotoRefImage(file, "Из буфера");
        ShowToast($"Референс вставлен из буфера: {Path.GetFileName(file)}", "success");
    }

    internal void StudioClearPhotoRefBtn_Click(object sender, RoutedEventArgs e)
    {
        _studioPhotoRefPath = null;
        ClearStudioPhotoMask();
        StudioPhotoRefLabel.Text = "Без референса (перетащи фото сюда, Ctrl+V или выбери файл)";
        StudioClearPhotoRefBtn.Visibility = Visibility.Collapsed;
        if (StudioPhotoMaskBtn is not null) StudioPhotoMaskBtn.Visibility = Visibility.Collapsed;
        UpdateStudioRefThumb(StudioPhotoRefThumbImage, StudioPhotoRefThumbBorder, null);
    }

    internal void StudioPickVideoStartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        SetStudioVideoStartImage(file);
    }

    internal void StudioPasteVideoStartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        if (!TryGetImageFromClipboard(out string? file) || file is null) return;
        SetStudioVideoStartImage(file, fromClipboard: true);
        ShowToast($"Стартовый кадр вставлен из буфера: {Path.GetFileName(file)}", "success");
    }

    private void SetStudioVideoStartImage(string file, bool fromClipboard = false)
    {
        _studioVideoStartPath = file;
        string dims = TryReadImageDimensions(file, out int w, out int h) ? $" ({w}×{h})" : "";
        StudioVideoStartLabel.Text = (fromClipboard ? "Старт из буфера (I2V): " : "Старт (I2V): ") + Path.GetFileName(file) + dims;
        StudioClearVideoStartBtn.Visibility = Visibility.Visible;
        UpdateStudioRefThumb(StudioVideoStartThumbImage, StudioVideoStartThumbBorder, file);
        StatusFooterText.Text = $"Стартовое фото I2V {(fromClipboard ? "вставлено из буфера" : "выбрано")}: {Path.GetFileName(file)}{dims}";
    }

    internal void StudioClearVideoStartBtn_Click(object sender, RoutedEventArgs e)
    {
        _studioVideoStartPath = null;
        StudioVideoStartLabel.Text = "Без фото (генерация по тексту T2V)";
        StudioClearVideoStartBtn.Visibility = Visibility.Collapsed;
        UpdateStudioRefThumb(StudioVideoStartThumbImage, StudioVideoStartThumbBorder, null);
    }

    internal void StudioPickVideoEndBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        SetStudioVideoEndImage(file);
    }

    internal void StudioPasteVideoEndBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        if (!TryGetImageFromClipboard(out string? file) || file is null) return;
        SetStudioVideoEndImage(file, fromClipboard: true);
        ShowToast($"Конечный кадр вставлен из буфера: {Path.GetFileName(file)}", "success");
    }

    private void SetStudioVideoEndImage(string file, bool fromClipboard = false)
    {
        _studioVideoEndPath = file;
        string dims = TryReadImageDimensions(file, out int w, out int h) ? $" ({w}×{h})" : "";
        StudioVideoEndLabel.Text = (fromClipboard ? "Финал из буфера (First→Last): " : "Финал (First→Last): ") + Path.GetFileName(file) + dims;
        StudioClearVideoEndBtn.Visibility = Visibility.Visible;
        UpdateStudioRefThumb(StudioVideoEndThumbImage, StudioVideoEndThumbBorder, file);
        StatusFooterText.Text = $"Конечный кадр First→Last {(fromClipboard ? "вставлен из буфера" : "выбран")}: {Path.GetFileName(file)}{dims}";
    }

    internal void StudioClearVideoEndBtn_Click(object sender, RoutedEventArgs e)
    {
        _studioVideoEndPath = null;
        StudioVideoEndLabel.Text = "Не задан (свободный финал)";
        StudioClearVideoEndBtn.Visibility = Visibility.Collapsed;
        UpdateStudioRefThumb(StudioVideoEndThumbImage, StudioVideoEndThumbBorder, null);
    }

    internal void StudioPickUpscaleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        SetStudioUpscaleSource(file);
    }

    internal void StudioPasteUpscaleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        if (!TryGetImageFromClipboard(out string? file) || file is null) return;
        SetStudioUpscaleSource(file);
        StatusFooterText.Text = $"Фото для Апскейла ×4 вставлено из буфера: {Path.GetFileName(file)}";
        ShowToast($"Фото для апскейла вставлено из буфера: {Path.GetFileName(file)}", "success");
    }

    private void SetStudioUpscaleSource(string file)
    {
        _studioUpscaleSourcePath = file;
        ClearPhotoBatchStrip();
        string dims = TryReadImageDimensions(file, out int w, out int h) ? $" ({w}×{h} → {w * 4}×{h * 4})" : "";
        StudioUpscaleSourceLabel.Text = $"{Path.GetFileName(file)}{dims}";
        ShowPhotoPreview(file, file, $"Исходник для апскейла: {Path.GetFileName(file)}{dims}", isCompletedOutput: false);
    }

    private static bool TryReadImageDimensions(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch { return false; }
    }

    internal void PhotoUseLastSeedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_photoLastSeed <= 0) return;
        StudioPhotoSeedBox.Text = _photoLastSeed.ToString();
        StatusFooterText.Text = $"Установлен Seed фото: {_photoLastSeed}";
    }

    internal void VideoUseLastSeedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_videoLastSeed <= 0) return;
        StudioVideoSeedBox.Text = _videoLastSeed.ToString();
        StatusFooterText.Text = $"Установлен Seed видео: {_videoLastSeed}";
    }

    private void ShowPhotoPreview(string previewImagePath, string resultPath, string meta, bool isCompletedOutput = true)
    {
        var bmp = LoadBitmapNoLock(previewImagePath);
        if (bmp is not null)
        {
            PhotoPreviewImage.Source = bmp;
            PhotoPreviewImage.Visibility = Visibility.Visible;
            PhotoPreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        PhotoResultMetaText.Text = meta;

        if (isCompletedOutput)
        {
            _photoLastResultPath = resultPath;
            PhotoOpenResultBtn.Visibility = Visibility.Visible;
            PhotoSendToUpscaleBtn.Visibility = Visibility.Visible;
            PhotoSendToVideoBtn.Visibility = Visibility.Visible;
            PhotoSendToStoryBtn.Visibility = Visibility.Visible;
        }
    }

    private void ShowVideoPreview(string previewImagePath, string resultPath, string meta)
    {
        var bmp = LoadBitmapNoLock(previewImagePath);
        if (bmp is not null)
        {
            VideoPreviewImage.Source = bmp;
            VideoPreviewImage.Visibility = Visibility.Visible;
            VideoPreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        VideoResultMetaText.Text = meta;
        _videoLastResultPath = resultPath;
        VideoOpenResultBtn.Visibility = Visibility.Visible;
    }

    private static long? ParseSeedBox(System.Windows.Controls.TextBox box) =>
        long.TryParse(box.Text.Trim(), out long s) && s > 0 ? s : null;

    // ---- Preflight Helper ----

    private async Task<bool> EnsureMediaReadyPreflightAsync(string engineTag)
    {
        if (!RuntimeInstaller.IsComfyInstalled())
        {
            var ans = HubDialogWindow.Show(
                "Для генерации фото, видео и апскейла нужен движок ComfyUI Portable (~2.0 ГБ), который пока не установлен.\n\n" +
                "Установить ComfyUI автоматически прямо сейчас?",
                "Требуется движок ComfyUI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes)
            {
                await InstallRuntimeWithUiAsync("comfyui");
                UpdateStudioMissingBanner();
            }
            return false;
        }

        if (!ModelCatalog.IsMediaEngineInstalled(engineTag))
        {
            string cat = ModelCatalog.EngineTagToCatalogCategory(engineTag);
            string catTitle = cat == "video" ? "3. Видео" : "2. Фото";
            var ans = HubDialogWindow.Show(
                $"Выбранная модель ещё не скачана на диск.\n\nОткрыть «Модели и GPU → {catTitle}», чтобы скачать её?",
                "Модель не установлена",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (ans == MessageBoxResult.Yes)
            {
                MainTabs.SelectedItem = CatalogTab;
                SetCatalogView(cat);
            }
            return false;
        }

        return true;
    }

    // ---- Generate Photo / Upscale ----

    internal async void PhotoGenerateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        if (!await EnsureMediaReadyPreflightAsync(GetCurrentPhotoEngineTag())) return;

        if (_photoSubMode == "upscale")
        {
            await RunStudioUpscaleAsync();
            return;
        }

        string prompt = PhotoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusFooterText.Text = "Напиши промпт в поле выше (или нажми «Режиссёр»).";
            return;
        }

        PromptHistoryService.Record("photo", prompt);
        await RunStudioPhotoAsync(prompt);
    }

    internal async void VideoGenerateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        if (!await EnsureMediaReadyPreflightAsync(GetCurrentVideoEngineTag())) return;

        string prompt = VideoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusFooterText.Text = "Напиши промпт видео в поле выше (или нажми «Режиссёр»).";
            return;
        }

        PromptHistoryService.Record("video", prompt);
        await RunStudioVideoAsync(prompt);
    }

    private async Task RunStudioPhotoAsync(string prompt)
    {
        string engine = (StudioPhotoEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zimage";
        var (w, h) = ParseSizeTag(StudioPhotoSizeCombo, 1024, 1024);
        long? fixedSeed = ParseSeedBox(StudioPhotoSeedBox);
        int batchCount = GetSelectedPhotoBatchCount();

        string? refPath = !string.IsNullOrEmpty(_studioPhotoRefPath) && File.Exists(_studioPhotoRefPath) ? _studioPhotoRefPath : null;
        if (refPath is not null && engine == "zimage")
        {
            engine = "flux3";
            SelectComboByTag(StudioPhotoEngineCombo, "flux3");
        }

        ClearPhotoBatchStrip();

        _storyBusy = true;
        RefreshStudioControlsEnabled();
        string hudTitle = batchCount > 1 ? $"Фото ({w}×{h}) • ×{batchCount}" : $"Фото ({w}×{h})";
        var ct = BeginGenerationHud(hudTitle);
        bool ok = false;
        string summary = "Готово";
        try
        {
            await EnsureComfyReadyForStoryAsync();
            ct.ThrowIfCancellationRequested();

            var (extraLora, extraLoraStr) = GetSelectedPhotoExtraLora();
            string engLabel = engine switch { "zimage" => "Z-Image Turbo", "qwen21" => "Qwen-Image 2.1", _ => "FLUX 3 Klein" };
            var usedSeeds = new HashSet<long>();
            string lastOutFile = "";
            long lastSeed = -1;

            for (int i = 0; i < batchCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                long? iterSeed = fixedSeed.HasValue
                    ? fixedSeed.Value + i
                    : (batchCount > 1 ? NextDistinctRandomSeed(usedSeeds) : null);

                int varNum = i + 1;
                string batchPrefix = batchCount > 1 ? $"[{varNum}/{batchCount}] " : "";
                if (batchCount > 1)
                    Dispatcher.Invoke(() => GenTitleText.Text = $"Фото ({w}×{h}) • вариант {varNum}/{batchCount}");

                var (outFile, usedSeed) = await _storyClient.GeneratePhotoStageAsync(
                    engine, prompt, w, h, refPath, null, "studio/photo",
                    msg => Dispatcher.Invoke(() =>
                    {
                        StatusFooterText.Text = batchPrefix + msg;
                        GenStageText.Text = batchPrefix + msg;
                    }),
                    info =>
                    {
                        if (batchCount > 1)
                            info = info with { Title = $"Фото ({w}×{h}) • {varNum}/{batchCount}" };
                        UpdateGenerationHud(info);
                    },
                    ct,
                    iterSeed,
                    extraLora,
                    extraLoraStr);

                usedSeeds.Add(usedSeed);

                // Apply feathered Inpainting mask composite if user painted a mask over reference
                if (!string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath) &&
                    !string.IsNullOrWhiteSpace(_studioPhotoMaskPath) && File.Exists(_studioPhotoMaskPath))
                {
                    outFile = MaskEditorWindow.ApplyInpaintMaskComposite(refPath, outFile, _studioPhotoMaskPath);
                }

                SavePhotoMetadata(outFile, prompt, engine, w, h, usedSeed, extraLora, extraLoraStr, refPath);

                lastOutFile = outFile;
                lastSeed = usedSeed;
                _photoCompareBeforePath = (!string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath)) ? refPath : null;
                if (PhotoCompareBeforeAfterBtn is not null)
                    PhotoCompareBeforeAfterBtn.Visibility = _photoCompareBeforePath is not null ? Visibility.Visible : Visibility.Collapsed;

                string metaLine = batchCount > 1
                    ? $"Вариант {varNum}/{batchCount} • {engLabel} • {w}×{h} • Seed: {usedSeed}\n{Path.GetFileName(outFile)}"
                    : $"{engLabel} • {w}×{h} • Seed: {usedSeed}\n{Path.GetFileName(outFile)}";

                _photoBatchResults.Add((outFile, usedSeed, metaLine));
                _photoLastSeed = usedSeed;
                PhotoUseLastSeedBtn.Visibility = Visibility.Visible;
                ShowPhotoPreview(outFile, outFile, metaLine);

                if (batchCount > 1 && _photoBatchResults.Count > 1)
                    RenderPhotoBatchStrip(_photoBatchResults.Count - 1);
            }

            if (batchCount > 1)
            {
                summary = $"Готово {batchCount} фото! Последнее: {Path.GetFileName(lastOutFile)}";
                StatusFooterText.Text = $"Сгенерировано {batchCount} вариаций фото (последний Seed: {lastSeed})";
                ShowToast($"Готово {batchCount} вариаций фото!", "success");
            }
            else
            {
                summary = $"Фото готово! {Path.GetFileName(lastOutFile)}";
                StatusFooterText.Text = $"Фото готово! {Path.GetFileName(lastOutFile)} (Seed: {lastSeed})";
                ShowToast($"Фото готово! {Path.GetFileName(lastOutFile)}", "success");
            }
            ok = true;
        }
        catch (OperationCanceledException)
        {
            summary = _photoBatchResults.Count > 0
                ? $"Отменено (успело сгенерироваться: {_photoBatchResults.Count})"
                : "Отменено пользователем";
            StatusFooterText.Text = _photoBatchResults.Count > 0
                ? $"Генерация фото отменена (сохранено {_photoBatchResults.Count})."
                : "Генерация фото отменена.";
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            StatusFooterText.Text = $"Ошибка: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Ошибка генерации фото", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndGenerationHud(ok, summary);
            _storyBusy = false;
            RefreshStudioControlsEnabled();
        }
    }

    private async Task RunStudioVideoAsync(string prompt)
    {
        string engine = GetCurrentVideoEngineTag();
        var (w, h) = ParseSizeTag(StudioVideoSizeCombo, 832, 480);
        int dur = int.TryParse((StudioVideoDurCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 4;
        long? fixedSeed = ParseSeedBox(StudioVideoSeedBox);

        string? startImg = !string.IsNullOrEmpty(_studioVideoStartPath) && File.Exists(_studioVideoStartPath) ? _studioVideoStartPath : null;
        string? endImg = !string.IsNullOrEmpty(_studioVideoEndPath) && File.Exists(_studioVideoEndPath) ? _studioVideoEndPath : null;

        _storyBusy = true;
        RefreshStudioControlsEnabled();
        var ct = BeginGenerationHud($"Видео ({w}×{h}, {dur}с)");
        bool ok = false;
        string summary = "Готово";
        try
        {
            await EnsureComfyReadyForStoryAsync();
            ct.ThrowIfCancellationRequested();

            string refMode = (startImg is not null && endImg is not null) ? "prev_to_anchor" : "prev_only";
            var (loraHigh, loraHighStr, loraLow, loraLowStr) = GetSelectedVideoExtraLoras();
            var (mp4Path, previewPath, _, usedSeed) = await _storyClient.GenerateVideoStageAsync(
                engine, prompt, w, h, dur, startImg, endImg, refMode, "studio/video", StudioFramesDir,
                msg => Dispatcher.Invoke(() =>
                {
                    StatusFooterText.Text = msg;
                    GenStageText.Text = msg;
                }),
                UpdateGenerationHud,
                ct,
                fixedSeed,
                null,
                null,
                null,
                loraHigh,
                loraHighStr,
                loraLow,
                loraLowStr);

            SaveVideoMetadata(mp4Path, prompt, engine, w, h, dur, usedSeed, loraHigh, loraHighStr, loraLow, loraLowStr, startImg, endImg);

            _videoLastSeed = usedSeed;
            VideoUseLastSeedBtn.Visibility = Visibility.Visible;
            string engLabel = engine switch
            {
                "ltx25" => "LTX-2.5 (Видео + Звук)",
                "wan14b_fp8" => "Wan 2.2 14B FP8",
                "wan14b_q6" => "Wan 2.2 14B Q6",
                _ => "Wan 2.2 14B Q4"
            };
            string modeTag = startImg is not null ? (endImg is not null ? "First→Last" : "I2V") : "T2V";
            ShowVideoPreview(previewPath, mp4Path, $"{engLabel} ({modeTag}) • {w}×{h} • {dur}с • Seed: {usedSeed}\n{Path.GetFileName(mp4Path)}");
            summary = $"Видео готово! {Path.GetFileName(mp4Path)}";
            StatusFooterText.Text = $"Видео готово! {Path.GetFileName(mp4Path)} (Seed: {usedSeed})";
            ShowToast($"Видео готово! {Path.GetFileName(mp4Path)}", "success");
            ok = true;
        }
        catch (OperationCanceledException)
        {
            summary = "Отменено пользователем";
            StatusFooterText.Text = "Генерация видео отменена.";
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            StatusFooterText.Text = $"Ошибка: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Ошибка генерации видео", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndGenerationHud(ok, summary);
            _storyBusy = false;
            RefreshStudioControlsEnabled();
        }
    }

    private async Task RunStudioUpscaleAsync()
    {
        if (string.IsNullOrEmpty(_studioUpscaleSourcePath) || !File.Exists(_studioUpscaleSourcePath))
        {
            StatusFooterText.Text = "Сначала выбери фото с диска для апскейла.";
            return;
        }

        ClearPhotoBatchStrip();
        _storyBusy = true;
        RefreshStudioControlsEnabled();
        var ct = BeginGenerationHud("Апскейл ×4 (4x-UltraSharp)");
        bool ok = false;
        string summary = "Готово";
        try
        {
            await EnsureComfyReadyForStoryAsync();
            ct.ThrowIfCancellationRequested();

            string outFile = await _storyClient.UpscaleImage4xAsync(
                _studioUpscaleSourcePath, "studio/upscale",
                msg => Dispatcher.Invoke(() =>
                {
                    StatusFooterText.Text = msg;
                    GenStageText.Text = msg;
                }),
                UpdateGenerationHud,
                ct);

            bool hasDims = TryReadImageDimensions(outFile, out int w, out int h);
            string dims = hasDims ? $" ({w}×{h})" : "";
            GenerationMetadataService.Save(outFile, new GenerationMetadata
            {
                Kind = "photo",
                Prompt = "Upscale ×4 (4x-UltraSharp)",
                Engine = "upscale",
                Size = hasDims ? $"{w}x{h}" : "",
                Seed = -1,
                RefImagePath = _studioUpscaleSourcePath,
                CreatedUtc = DateTime.UtcNow
            });

            _photoCompareBeforePath = _studioUpscaleSourcePath;
            if (PhotoCompareBeforeAfterBtn is not null)
                PhotoCompareBeforeAfterBtn.Visibility = File.Exists(_photoCompareBeforePath) ? Visibility.Visible : Visibility.Collapsed;

            ShowPhotoPreview(outFile, outFile, $"Апскейл ×4 (4x-UltraSharp){dims}\n{Path.GetFileName(outFile)}");
            summary = $"Апскейл ×4 готов! {Path.GetFileName(outFile)}{dims}";
            StatusFooterText.Text = summary;
            ShowToast(summary, "success");
            ok = true;
        }
        catch (OperationCanceledException)
        {
            summary = "Отменено пользователем";
            StatusFooterText.Text = "Апскейл отменён.";
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            StatusFooterText.Text = $"Ошибка апскейла: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Ошибка апскейла ×4", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndGenerationHud(ok, summary);
            _storyBusy = false;
            RefreshStudioControlsEnabled();
        }
    }

    // ---- Result Preview & Seamless Cross-Tab Handoff ----

    internal void PhotoPreviewBorder_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_photoLastResultPath) && File.Exists(_photoLastResultPath))
            AiServiceManager.OpenExternal(_photoLastResultPath);
    }

    internal void PhotoOpenResultBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_photoLastResultPath) && File.Exists(_photoLastResultPath))
            AiServiceManager.OpenExternal(_photoLastResultPath);
    }

    internal void VideoPreviewBorder_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_videoLastResultPath) && File.Exists(_videoLastResultPath))
            AiServiceManager.OpenExternal(_videoLastResultPath);
    }

    internal void VideoOpenResultBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_videoLastResultPath) && File.Exists(_videoLastResultPath))
            AiServiceManager.OpenExternal(_videoLastResultPath);
    }

    internal void StudioOpenOutputFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(StudioOutputDir);
        AiServiceManager.OpenExternal(StudioOutputDir);
    }

    internal void StudioSendToUpscaleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_photoLastResultPath) || !File.Exists(_photoLastResultPath)) return;
        SetStudioUpscaleSource(_photoLastResultPath);
        SetPhotoSubMode("upscale");
        StatusFooterText.Text = "Фото передано в «Апскейл ×4» — жми «Увеличить резкость ×4»!";
    }

    internal void StudioSendToVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_photoLastResultPath) || !File.Exists(_photoLastResultPath)) return;
        SetStudioVideoStartImage(_photoLastResultPath);
        MainTabs.SelectedItem = VideoTab;
        SetVideoSubMode("single");
        StatusFooterText.Text = "Фото установлено как стартовый кадр (I2V) во вкладке «Видео»!";
    }

    internal void StudioSendToStoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_photoLastResultPath) || !File.Exists(_photoLastResultPath)) return;
        _videoProject.StarterImagePath = _photoLastResultPath;
        _videoProject.UseStarterFrame = true;
        SaveVideoStoryState();
        RenderVideoStages();
        MainTabs.SelectedItem = VideoTab;
        SetVideoSubMode("story");
        StatusFooterText.Text = $"Фото передано в «Видео → Видео-история» как Шаг 0 (Якорь): {Path.GetFileName(_photoLastResultPath)}";
    }

    // ---- Photo Batch Variations & Metadata Sidecar Helpers ----

    private int GetSelectedPhotoBatchCount()
    {
        string tag = (StudioPhotoBatchCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "1";
        return int.TryParse(tag, out int n) && n is 1 or 2 or 4 ? n : 1;
    }

    private static long NextDistinctRandomSeed(HashSet<long> used)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            long candidate = Random.Shared.NextInt64(1, 9_999_999_999_999);
            if (used.Add(candidate))
                return candidate;
        }
        long fallback = DateTime.UtcNow.Ticks & 0x7FFF_FFFF_FFFF;
        used.Add(fallback);
        return fallback;
    }

    private void ClearPhotoBatchStrip()
    {
        _photoBatchResults.Clear();
        _photoBatchSelectedIndex = -1;
        if (PhotoBatchStripPanel is not null)
        {
            PhotoBatchStripPanel.Children.Clear();
            PhotoBatchStripPanel.Visibility = Visibility.Collapsed;
        }
        if (PhotoBatchGridBtn is not null)
            PhotoBatchGridBtn.Visibility = Visibility.Collapsed;
    }

    private void RenderPhotoBatchStrip(int selectedIndex)
    {
        if (PhotoBatchStripPanel is null) return;
        PhotoBatchStripPanel.Children.Clear();

        if (_photoBatchResults.Count <= 1)
        {
            PhotoBatchStripPanel.Visibility = Visibility.Collapsed;
            if (PhotoBatchGridBtn is not null) PhotoBatchGridBtn.Visibility = Visibility.Collapsed;
            _photoBatchSelectedIndex = _photoBatchResults.Count == 1 ? 0 : -1;
            return;
        }

        _photoBatchSelectedIndex = Math.Clamp(selectedIndex, 0, _photoBatchResults.Count - 1);
        PhotoBatchStripPanel.Visibility = _photoSubMode == "single" ? Visibility.Visible : Visibility.Collapsed;
        if (PhotoBatchGridBtn is not null)
            PhotoBatchGridBtn.Visibility = _photoSubMode == "single" ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _photoBatchResults.Count; i++)
        {
            int capturedIdx = i;
            var item = _photoBatchResults[i];
            bool isSelected = i == _photoBatchSelectedIndex;

            var grid = new Grid();
            var thumbBmp = LoadThumbnailNoLock(item.Path, 140);
            if (thumbBmp is not null)
            {
                grid.Children.Add(new System.Windows.Controls.Image
                {
                    Source = thumbBmp,
                    Stretch = System.Windows.Media.Stretch.UniformToFill
                });
            }

            var numBadge = new Border
            {
                Background = BrushFromHex(isSelected ? "#E6FF521B" : "#D909090B"),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3)
            };
            numBadge.Child = new TextBlock
            {
                Text = $"#{i + 1}",
                FontSize = 9.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex("#FFFFFF")
            };
            grid.Children.Add(numBadge);

            var border = new Border
            {
                Width = 68,
                Height = 68,
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Background = BrushFromHex("#070708"),
                BorderBrush = BrushFromHex(isSelected ? "#FF521B" : "#28282E"),
                BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = capturedIdx,
                ToolTip = $"Вариант #{i + 1} • Seed: {item.Seed}\n{Path.GetFileName(item.Path)}",
                Child = grid
            };
            border.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                SelectPhotoBatchItem(capturedIdx);
            };

            PhotoBatchStripPanel.Children.Add(border);
        }
    }

    private void SelectPhotoBatchItem(int index)
    {
        if (index < 0 || index >= _photoBatchResults.Count) return;
        _photoBatchSelectedIndex = index;
        var item = _photoBatchResults[index];

        _photoLastSeed = item.Seed;
        PhotoUseLastSeedBtn.Visibility = _photoLastSeed > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPhotoPreview(item.Path, item.Path, item.Meta, isCompletedOutput: true);

        if (PhotoBatchStripPanel is not null)
        {
            foreach (UIElement child in PhotoBatchStripPanel.Children)
            {
                if (child is Border b && b.Tag is int idx)
                {
                    bool sel = idx == index;
                    b.BorderBrush = BrushFromHex(sel ? "#FF521B" : "#28282E");
                    b.BorderThickness = new Thickness(sel ? 1.5 : 1);
                    if (b.Child is Grid g && g.Children.Count > 0 && g.Children[^1] is Border badge)
                        badge.Background = BrushFromHex(sel ? "#E6FF521B" : "#D909090B");
                }
            }
        }
    }

    private static void SavePhotoMetadata(
        string outPath,
        string prompt,
        string engine,
        int width,
        int height,
        long seed,
        string? loraHigh,
        double loraHighStrength,
        string? refImagePath)
    {
        GenerationMetadataService.Save(outPath, new GenerationMetadata
        {
            Kind = "photo",
            Prompt = prompt,
            Engine = engine,
            Size = $"{width}x{height}",
            Seed = seed,
            DurationSec = 0,
            LoraHigh = loraHigh,
            LoraHighStrength = loraHighStrength,
            RefImagePath = refImagePath,
            CreatedUtc = DateTime.UtcNow
        });
    }

    private static void SaveVideoMetadata(
        string outPath,
        string prompt,
        string engine,
        int width,
        int height,
        int durationSec,
        long seed,
        string? loraHigh,
        double loraHighStrength,
        string? loraLow,
        double loraLowStrength,
        string? startImagePath,
        string? endImagePath)
    {
        GenerationMetadataService.Save(outPath, new GenerationMetadata
        {
            Kind = "video",
            Prompt = prompt,
            Engine = engine,
            Size = $"{width}x{height}",
            Seed = seed,
            DurationSec = durationSec,
            LoraHigh = loraHigh,
            LoraHighStrength = loraHighStrength,
            LoraLow = loraLow,
            LoraLowStrength = loraLowStrength,
            RefImagePath = startImagePath,
            EndImagePath = endImagePath,
            CreatedUtc = DateTime.UtcNow
        });
    }

    // ---- Studio Presets & Queue Initialization ----

    private void InitQueueAndPresets()
    {
        RefreshStudioPresets("photo");
        RefreshStudioPresets("video");

        _queueJobs = GenerationQueueService.LoadAll();
        bool recoveredRunning = false;
        foreach (var job in _queueJobs)
        {
            if (string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                job.Status = "pending";
                recoveredRunning = true;
            }
        }
        if (recoveredRunning)
            GenerationQueueService.SaveAll(_queueJobs);

        RefreshQueueUi();
    }

    private void RefreshStudioPresets(string kind, string? selectName = null)
    {
        bool isVideo = string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase);
        var combo = isVideo ? VideoPresetCombo : PhotoPresetCombo;
        if (combo is null) return;

        var presets = StudioPresetService.Load(isVideo ? "video" : "photo");
        _suppressPresetSelection = true;
        try
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem
            {
                Content = "— Выбери пресет —",
                Tag = null,
                Foreground = BrushFromHex("#71717A")
            });

            int selectIdx = 0;
            for (int i = 0; i < presets.Count; i++)
            {
                var p = presets[i];
                combo.Items.Add(new ComboBoxItem
                {
                    Content = p.IsBuiltIn ? p.Name : $"★ {p.Name}",
                    Tag = p,
                    Foreground = BrushFromHex(p.IsBuiltIn ? "#E4E4E8" : "#F59E0B")
                });
                if (!string.IsNullOrWhiteSpace(selectName) &&
                    string.Equals(p.Name, selectName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    selectIdx = i + 1;
                }
            }
            combo.SelectedIndex = selectIdx;
        }
        finally
        {
            _suppressPresetSelection = false;
        }
    }

    private void SelectLoraComboByTag(System.Windows.Controls.ComboBox combo, string? loraName)
    {
        if (combo is null || combo.Items.Count == 0) return;

        if (string.IsNullOrWhiteSpace(loraName))
        {
            combo.SelectedIndex = 0;
            return;
        }

        static bool TrySelect(System.Windows.Controls.ComboBox cb, string name)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if (cb.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    cb.SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        if (TrySelect(combo, loraName))
            return;

        RefreshCustomLoras();
        if (!TrySelect(combo, loraName))
            combo.SelectedIndex = 0;
    }

    private void ApplyStudioPreset(StudioPreset preset)
    {
        bool isVideo = string.Equals(preset.Kind, "video", StringComparison.OrdinalIgnoreCase);
        if (isVideo)
        {
            if (!string.IsNullOrWhiteSpace(preset.Engine))
                SelectComboByTag(StudioVideoEngineCombo, preset.Engine);
            if (!string.IsNullOrWhiteSpace(preset.Size))
                SelectComboByTag(StudioVideoSizeCombo, preset.Size);
            if (preset.DurationSec > 0)
                SelectComboByTag(StudioVideoDurCombo, preset.DurationSec.ToString());

            SelectLoraComboByTag(VideoExtraLoraHighCombo, preset.LoraHigh);
            if (VideoExtraLoraHighSlider is not null)
            {
                double hStr = preset.LoraHighStrength > 0 ? preset.LoraHighStrength : 0.75;
                VideoExtraLoraHighSlider.Value = Math.Clamp(hStr, VideoExtraLoraHighSlider.Minimum, VideoExtraLoraHighSlider.Maximum);
            }

            SelectLoraComboByTag(VideoExtraLoraLowCombo, preset.LoraLow);
            if (VideoExtraLoraLowSlider is not null)
            {
                double lStr = preset.LoraLowStrength > 0 ? preset.LoraLowStrength : 0.75;
                VideoExtraLoraLowSlider.Value = Math.Clamp(lStr, VideoExtraLoraLowSlider.Minimum, VideoExtraLoraLowSlider.Maximum);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(preset.Engine))
                SelectComboByTag(StudioPhotoEngineCombo, preset.Engine);
            if (!string.IsNullOrWhiteSpace(preset.Size))
                SelectComboByTag(StudioPhotoSizeCombo, preset.Size);

            SelectLoraComboByTag(PhotoExtraLoraCombo, preset.LoraHigh);
            if (PhotoExtraLoraSlider is not null)
            {
                double str = preset.LoraHighStrength > 0 ? preset.LoraHighStrength : 0.75;
                PhotoExtraLoraSlider.Value = Math.Clamp(str, PhotoExtraLoraSlider.Minimum, PhotoExtraLoraSlider.Maximum);
            }
        }

        UpdateStudioMissingBanner();
        StatusFooterText.Text = $"Применён пресет: {preset.Name}";
        ShowToast($"Пресет применён: {preset.Name}", "success");
    }

    internal void PhotoPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetSelection || !IsLoaded) return;
        if ((PhotoPresetCombo?.SelectedItem as ComboBoxItem)?.Tag is StudioPreset preset)
            ApplyStudioPreset(preset);
    }

    internal void PhotoPresetApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((PhotoPresetCombo?.SelectedItem as ComboBoxItem)?.Tag is StudioPreset preset)
            ApplyStudioPreset(preset);
        else
            ShowToast("Сначала выбери пресет из списка", "warn");
    }

    internal void PhotoPresetSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string? name = ShowDarkInputDialog("Сохранить пресет фото", "Название пресета:");
        if (string.IsNullOrWhiteSpace(name)) return;

        string cleanName = name.Trim();
        var (lora, loraStr) = GetSelectedPhotoExtraLora();
        string sizeTag = (StudioPhotoSizeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1024x1024";

        var preset = new StudioPreset
        {
            Name = cleanName,
            Kind = "photo",
            Engine = GetCurrentPhotoEngineTag(),
            Size = sizeTag,
            DurationSec = 0,
            LoraHigh = lora,
            LoraHighStrength = loraStr,
            IsBuiltIn = false
        };

        StudioPresetService.SaveCustom(preset);
        RefreshStudioPresets("photo", cleanName);
        StatusFooterText.Text = $"Пресет фото «{cleanName}» сохранён.";
        ShowToast($"Пресет «{cleanName}» сохранён", "success");
    }

    internal void PhotoPresetDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((PhotoPresetCombo?.SelectedItem as ComboBoxItem)?.Tag is not StudioPreset preset)
        {
            ShowToast("Выбери пользовательский пресет для удаления", "warn");
            return;
        }
        if (preset.IsBuiltIn)
        {
            ShowToast("Встроенные пресеты нельзя удалить", "warn");
            return;
        }

        StudioPresetService.DeleteCustom("photo", preset.Name);
        RefreshStudioPresets("photo");
        StatusFooterText.Text = $"Пресет фото «{preset.Name}» удалён.";
        ShowToast($"Пресет «{preset.Name}» удалён", "info");
    }

    internal void VideoPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetSelection || !IsLoaded) return;
        if ((VideoPresetCombo?.SelectedItem as ComboBoxItem)?.Tag is StudioPreset preset)
            ApplyStudioPreset(preset);
    }

    internal void VideoPresetApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((VideoPresetCombo?.SelectedItem as ComboBoxItem)?.Tag is StudioPreset preset)
            ApplyStudioPreset(preset);
        else
            ShowToast("Сначала выбери пресет из списка", "warn");
    }

    internal void VideoPresetSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string? name = ShowDarkInputDialog("Сохранить пресет видео", "Название пресета:");
        if (string.IsNullOrWhiteSpace(name)) return;

        string cleanName = name.Trim();
        var (highLora, highStr, lowLora, lowStr) = GetSelectedVideoExtraLoras();
        string sizeTag = (StudioVideoSizeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "832x480";
        int dur = int.TryParse((StudioVideoDurCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 4;

        var preset = new StudioPreset
        {
            Name = cleanName,
            Kind = "video",
            Engine = GetCurrentVideoEngineTag(),
            Size = sizeTag,
            DurationSec = dur,
            LoraHigh = highLora,
            LoraHighStrength = highStr,
            LoraLow = lowLora,
            LoraLowStrength = lowStr,
            IsBuiltIn = false
        };

        StudioPresetService.SaveCustom(preset);
        RefreshStudioPresets("video", cleanName);
        StatusFooterText.Text = $"Пресет видео «{cleanName}» сохранён.";
        ShowToast($"Пресет «{cleanName}» сохранён", "success");
    }

    internal void VideoPresetDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((VideoPresetCombo?.SelectedItem as ComboBoxItem)?.Tag is not StudioPreset preset)
        {
            ShowToast("Выбери пользовательский пресет для удаления", "warn");
            return;
        }
        if (preset.IsBuiltIn)
        {
            ShowToast("Встроенные пресеты нельзя удалить", "warn");
            return;
        }

        StudioPresetService.DeleteCustom("video", preset.Name);
        RefreshStudioPresets("video");
        StatusFooterText.Text = $"Пресет видео «{preset.Name}» удалён.";
        ShowToast($"Пресет «{preset.Name}» удалён", "info");
    }

    private string? ShowDarkInputDialog(string title, string label, string defaultValue = "")
    {
        var dlg = new Window
        {
            Title = title,
            Width = 370,
            Height = 158,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = BrushFromHex("#121215"),
            Foreground = BrushFromHex("#F4F4F6"),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        };

        dlg.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            }
            catch { }
        };

        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        root.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = BrushFromHex("#A1A1AA"),
            Margin = new Thickness(0, 0, 0, 7)
        });

        var inputBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BrushFromHex("#09090B"),
            Foreground = BrushFromHex("#F4F4F6"),
            CaretBrush = BrushFromHex("#FF521B"),
            BorderBrush = BrushFromHex("#2E2E36"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 0, 9, 0),
            FontSize = 13
        };
        root.Children.Add(inputBox);

        var btnRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Отмена",
            IsCancel = true,
            MinWidth = 80,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Style = TryFindResource("DarkButton") as Style
        };
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "Сохранить",
            IsDefault = true,
            MinWidth = 92,
            Padding = new Thickness(14, 6, 14, 6),
            Style = TryFindResource("PrimaryButton") as Style
        };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inputBox.Text))
            {
                inputBox.BorderBrush = BrushFromHex("#E74C3C");
                inputBox.Focus();
                return;
            }
            dlg.DialogResult = true;
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);

        dlg.Content = root;
        dlg.Loaded += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        return dlg.ShowDialog() == true ? inputBox.Text.Trim() : null;
    }

    // ---- Background Generation Queue ----

    internal void PhotoEnqueueBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_photoSubMode != "single")
        {
            ShowToast("В очередь можно добавлять одиночное фото", "warn");
            return;
        }

        string prompt = PhotoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusFooterText.Text = "Напиши промпт фото перед добавлением в очередь.";
            ShowToast("Поле промпта фото пустое", "warn");
            return;
        }

        string engine = GetCurrentPhotoEngineTag();
        var (w, h) = ParseSizeTag(StudioPhotoSizeCombo, 1024, 1024);
        int batchCount = GetSelectedPhotoBatchCount();
        long seed = ParseSeedBox(StudioPhotoSeedBox) ?? -1;
        string? refPath = !string.IsNullOrEmpty(_studioPhotoRefPath) && File.Exists(_studioPhotoRefPath) ? _studioPhotoRefPath : null;
        if (refPath is not null && engine == "zimage")
            engine = "flux3";
        var (extraLora, extraLoraStr) = GetSelectedPhotoExtraLora();

        var job = new QueuedGenerationJob
        {
            Kind = "photo",
            Title = TruncatePromptPreview(prompt, 52),
            Prompt = prompt,
            Engine = engine,
            Width = w,
            Height = h,
            DurationSec = 0,
            Seed = seed,
            BatchCount = batchCount,
            RefImagePath = refPath,
            LoraHigh = extraLora,
            LoraHighStrength = extraLoraStr,
            Status = "pending",
            CreatedUtc = DateTime.UtcNow
        };

        PromptHistoryService.Record("photo", prompt);
        GenerationQueueService.Enqueue(job);
        _queueJobs.Add(job);
        RefreshQueueUi();
        StatusFooterText.Text = $"Задача фото добавлена в очередь ({_queueJobs.Count} в списке).";
        ShowToast("Задача добавлена в очередь", "success");
    }

    internal void VideoEnqueueBtn_Click(object sender, RoutedEventArgs e)
    {
        string prompt = VideoPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusFooterText.Text = "Напиши промпт видео перед добавлением в очередь.";
            ShowToast("Поле промпта видео пустое", "warn");
            return;
        }

        string engine = GetCurrentVideoEngineTag();
        var (w, h) = ParseSizeTag(StudioVideoSizeCombo, 832, 480);
        int dur = int.TryParse((StudioVideoDurCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 4;
        long seed = ParseSeedBox(StudioVideoSeedBox) ?? -1;
        string? startImg = !string.IsNullOrEmpty(_studioVideoStartPath) && File.Exists(_studioVideoStartPath) ? _studioVideoStartPath : null;
        string? endImg = !string.IsNullOrEmpty(_studioVideoEndPath) && File.Exists(_studioVideoEndPath) ? _studioVideoEndPath : null;
        var (loraHigh, loraHighStr, loraLow, loraLowStr) = GetSelectedVideoExtraLoras();

        var job = new QueuedGenerationJob
        {
            Kind = "video",
            Title = TruncatePromptPreview(prompt, 52),
            Prompt = prompt,
            Engine = engine,
            Width = w,
            Height = h,
            DurationSec = dur,
            Seed = seed,
            BatchCount = 1,
            StartImagePath = startImg,
            EndImagePath = endImg,
            LoraHigh = loraHigh,
            LoraHighStrength = loraHighStr,
            LoraLow = loraLow,
            LoraLowStrength = loraLowStr,
            Status = "pending",
            CreatedUtc = DateTime.UtcNow
        };

        PromptHistoryService.Record("video", prompt);
        GenerationQueueService.Enqueue(job);
        _queueJobs.Add(job);
        RefreshQueueUi();
        StatusFooterText.Text = $"Видео добавлено в очередь ({_queueJobs.Count} в списке).";
        ShowToast("Видео добавлено в очередь", "success");
    }

    private async void QueueStartBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync();
    }

    private void QueuePauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_queueRunning) return;
        _queuePauseRequested = true;
        RefreshQueueUi();
        StatusFooterText.Text = "Очередь встанет на паузу после завершения текущей задачи.";
        ShowToast("Очередь встанет на паузу после текущей задачи", "info");
    }

    private void QueueToggleListBtn_Click(object sender, RoutedEventArgs e)
    {
        _queueListExpanded = !_queueListExpanded;
        RefreshQueueUi();
    }

    private void QueueClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_queueJobs.Count == 0) return;

        int finishedCount = _queueJobs.Count(IsQueueJobFinished);
        if (_queueRunning)
        {
            if (finishedCount > 0)
            {
                GenerationQueueService.ClearFinished();
                _queueJobs.RemoveAll(IsQueueJobFinished);
                RefreshQueueUi();
                ShowToast("Завершённые задачи очищены", "info");
            }
            else
            {
                ShowToast("Поставь очередь на паузу или дождись завершения задачи", "warn");
            }
            return;
        }

        if (finishedCount > 0 && finishedCount < _queueJobs.Count)
        {
            GenerationQueueService.ClearFinished();
            _queueJobs.RemoveAll(IsQueueJobFinished);
            RefreshQueueUi();
            ShowToast("Завершённые задачи очищены", "info");
        }
        else
        {
            GenerationQueueService.ClearAll();
            _queueJobs.Clear();
            _queueListExpanded = false;
            RefreshQueueUi();
            ShowToast("Очередь очищена", "info");
        }
    }

    private static bool IsQueueJobFinished(QueuedGenerationJob job) =>
        string.Equals(job.Status, "done", StringComparison.OrdinalIgnoreCase)
        || string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private void RefreshQueueUi()
    {
        if (QueueBarBorder is null || QueueSummaryText is null || QueueItemsPanel is null) return;

        if (_queueJobs.Count == 0)
        {
            QueueBarBorder.Visibility = Visibility.Collapsed;
            QueueListScroll.Visibility = Visibility.Collapsed;
            QueueItemsPanel.Children.Clear();
            return;
        }

        QueueBarBorder.Visibility = Visibility.Visible;

        int pending = _queueJobs.Count(j => string.Equals(j.Status, "pending", StringComparison.OrdinalIgnoreCase));
        int running = _queueJobs.Count(j => string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase));
        int done = _queueJobs.Count(j => string.Equals(j.Status, "done", StringComparison.OrdinalIgnoreCase));
        int failed = _queueJobs.Count(j => string.Equals(j.Status, "failed", StringComparison.OrdinalIgnoreCase));

        string errSuffix = failed > 0 ? $" • ошибок: {failed}" : "";
        if (_queueRunning)
        {
            QueueSummaryText.Text = _queuePauseRequested
                ? $"⏸ Очередь (пауза после текущей): в работе {running} • ожидают {pending} • готово {done}{errSuffix}"
                : $"▶ Очередь выполняется: в работе {running} • ожидают {pending} • готово {done}{errSuffix}";
        }
        else
        {
            QueueSummaryText.Text = $"Очередь: {_queueJobs.Count} • ожидают {pending} • готово {done}{errSuffix}";
        }

        QueueStartBtn.Visibility = !_queueRunning && pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueStartBtn.IsEnabled = !_storyBusy && !_queueRunning;

        QueuePauseBtn.Visibility = _queueRunning ? Visibility.Visible : Visibility.Collapsed;
        QueuePauseBtn.IsEnabled = _queueRunning && !_queuePauseRequested;
        QueuePauseBtn.Content = _queuePauseRequested ? "Пауза..." : "⏸ Пауза";

        QueueToggleListBtn.Content = _queueListExpanded
            ? $"Список ▴ ({_queueJobs.Count})"
            : $"Список ▾ ({_queueJobs.Count})";

        QueueListScroll.Visibility = _queueListExpanded ? Visibility.Visible : Visibility.Collapsed;

        QueueItemsPanel.Children.Clear();
        if (_queueListExpanded)
        {
            foreach (var job in _queueJobs)
                QueueItemsPanel.Children.Add(BuildQueueItemRow(job));
        }
    }

    private Border BuildQueueItemRow(QueuedGenerationJob job)
    {
        string status = (job.Status ?? "pending").ToLowerInvariant();
        bool isRunning = status == "running";

        var rowBorder = new Border
        {
            Background = BrushFromHex(isRunning ? "#191512" : "#101013"),
            BorderBrush = BrushFromHex(isRunning ? "#FF521B" : "#26262E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 0, 4)
        };

        var dock = new DockPanel();

        var rightControls = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(rightControls, Dock.Right);

        var (badgeText, badgeBg, badgeFg) = status switch
        {
            "running" => ("● В РАБОТЕ", "#26140F", "#FF521B"),
            "done" => ("✓ ГОТОВО", "#11241C", "#34D399"),
            "failed" => ("✕ ОШИБКА", "#261416", "#F87171"),
            "cancelled" => ("⊘ ОТМЕНЕНО", "#241D0E", "#F59E0B"),
            _ => ("○ ОЖИДАЕТ", "#19191E", "#A1A1AA")
        };

        var statusBadge = new Border
        {
            Background = BrushFromHex(badgeBg),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = !string.IsNullOrWhiteSpace(job.Error) ? job.Error : null,
            Child = new TextBlock
            {
                Text = badgeText,
                FontSize = 10,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex(badgeFg)
            }
        };
        rightControls.Children.Add(statusBadge);

        if (status == "done" && !string.IsNullOrEmpty(job.OutputPath) && File.Exists(job.OutputPath))
        {
            string outPath = job.OutputPath;
            var openBtn = new System.Windows.Controls.Button
            {
                Content = "↗",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(7, 2, 7, 2),
                FontSize = 11,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = $"Открыть: {Path.GetFileName(outPath)}"
            };
            openBtn.Click += (_, _) => AiServiceManager.OpenExternal(outPath);
            rightControls.Children.Add(openBtn);
        }
        else if (status is "failed" or "cancelled")
        {
            var retryBtn = new System.Windows.Controls.Button
            {
                Content = "↻",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(7, 2, 7, 2),
                FontSize = 11,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Вернуть задачу в ожидание"
            };
            retryBtn.Click += (_, _) =>
            {
                job.Status = "pending";
                job.Error = null;
                GenerationQueueService.SaveAll(_queueJobs);
                RefreshQueueUi();
            };
            rightControls.Children.Add(retryBtn);
        }

        string jobId = job.Id;
        var removeBtn = new System.Windows.Controls.Button
        {
            Content = "×",
            Style = (Style)FindResource("DarkButton"),
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 11,
            IsEnabled = !isRunning,
            ToolTip = isRunning ? "Нельзя удалить выполняющуюся задачу" : "Удалить из очереди"
        };
        removeBtn.Click += (_, _) =>
        {
            _queueJobs.RemoveAll(j => string.Equals(j.Id, jobId, StringComparison.OrdinalIgnoreCase));
            GenerationQueueService.Remove(jobId);
            RefreshQueueUi();
        };
        rightControls.Children.Add(removeBtn);

        dock.Children.Add(rightControls);

        string icon = job.Kind == "photo" ? "[ФОТО]" : "[ВИДЕО]";
        string spec = job.Kind == "photo"
            ? $"{job.Engine} • {job.Width}×{job.Height}" + (job.BatchCount > 1 ? $" • ×{job.BatchCount}" : "")
            : $"{job.Engine} • {job.Width}×{job.Height} • {job.DurationSec}с";

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text = $"{icon} {job.Title}",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#F4F4F6"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = job.Prompt
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(job.Error) ? spec : $"{spec} • {job.Error}",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = BrushFromHex(status == "failed" ? "#F87171" : "#71717A"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        dock.Children.Add(infoStack);
        rowBorder.Child = dock;
        return rowBorder;
    }

    private async Task RunQueueAsync()
    {
        if (_queueRunning || _storyBusy) return;
        if (!_queueJobs.Any(j => string.Equals(j.Status, "pending", StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("В очереди нет ожидающих задач", "info");
            return;
        }

        if (!RuntimeInstaller.IsComfyInstalled())
        {
            await EnsureMediaReadyPreflightAsync("flux3");
            return;
        }

        _queueRunning = true;
        _queuePauseRequested = false;
        _storyBusy = true;
        RefreshStudioControlsEnabled();
        RefreshQueueUi();

        int completedCount = 0;
        int failedCount = 0;
        bool wasCancelled = false;

        try
        {
            await EnsureComfyReadyForStoryAsync();

            while (!_queuePauseRequested)
            {
                var job = _queueJobs.FirstOrDefault(j => string.Equals(j.Status, "pending", StringComparison.OrdinalIgnoreCase));
                if (job is null) break;

                if (!ModelCatalog.IsMediaEngineInstalled(job.Engine))
                {
                    job.Status = "failed";
                    job.Error = $"Модель «{job.Engine}» не скачана на диск";
                    failedCount++;
                    GenerationQueueService.SaveAll(_queueJobs);
                    RefreshQueueUi();
                    continue;
                }

                job.Status = "running";
                job.Error = null;
                GenerationQueueService.SaveAll(_queueJobs);
                RefreshQueueUi();

                string hudTitle = job.Kind == "photo"
                    ? $"Очередь: Фото ({job.Width}×{job.Height})"
                    : $"Очередь: Видео ({job.Width}×{job.Height}, {job.DurationSec}с)";
                var ct = BeginGenerationHud(hudTitle);

                try
                {
                    if (job.Kind == "photo")
                        await ExecuteQueuedPhotoJobAsync(job, ct);
                    else
                        await ExecuteQueuedVideoJobAsync(job, ct);

                    job.Status = "done";
                    completedCount++;
                    GenerationQueueService.SaveAll(_queueJobs);
                    EndGenerationHud(true, $"Очередь: готово — {job.Title}");
                    RefreshQueueUi();
                }
                catch (OperationCanceledException)
                {
                    job.Status = "cancelled";
                    job.Error = "Отменено пользователем";
                    GenerationQueueService.SaveAll(_queueJobs);
                    EndGenerationHud(false, "Очередь остановлена пользователем");
                    wasCancelled = true;
                    RefreshQueueUi();
                    break;
                }
                catch (Exception ex)
                {
                    job.Status = "failed";
                    job.Error = ex.Message;
                    failedCount++;
                    GenerationQueueService.SaveAll(_queueJobs);
                    EndGenerationHud(false, $"Ошибка задачи: {ex.Message}");
                    RefreshQueueUi();
                }
            }
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка запуска очереди: {ex.Message}";
            ShowToast($"Ошибка запуска очереди: {ex.Message}", "warn");
        }
        finally
        {
            bool paused = _queuePauseRequested;
            _queueRunning = false;
            _queuePauseRequested = false;
            _storyBusy = false;
            RefreshStudioControlsEnabled();
            RefreshQueueUi();

            if (wasCancelled)
            {
                StatusFooterText.Text = $"Очередь остановлена пользователем (готово: {completedCount}).";
                ShowToast($"Очередь остановлена (готово: {completedCount})", "warn");
            }
            else if (paused)
            {
                StatusFooterText.Text = $"Очередь на паузе (готово: {completedCount}).";
                ShowToast($"Очередь на паузе (готово: {completedCount})", "info");
            }
            else if (completedCount > 0 && failedCount == 0)
            {
                StatusFooterText.Text = $"Очередь завершена! Выполнено задач: {completedCount}.";
                ShowToast($"Очередь завершена! Готово задач: {completedCount}", "success");
            }
            else if (completedCount > 0 && failedCount > 0)
            {
                StatusFooterText.Text = $"Очередь завершена: готово {completedCount}, ошибок {failedCount}.";
                ShowToast($"Очередь завершена: готово {completedCount}, ошибок {failedCount}", "warn");
            }
            else if (failedCount > 0)
            {
                StatusFooterText.Text = $"Очередь завершена с ошибками ({failedCount}).";
                ShowToast($"Очередь завершена с ошибками ({failedCount})", "warn");
            }
        }
    }

    private async Task ExecuteQueuedPhotoJobAsync(QueuedGenerationJob job, CancellationToken ct)
    {
        int batchCount = Math.Max(1, job.BatchCount);
        string? refPath = !string.IsNullOrEmpty(job.RefImagePath) && File.Exists(job.RefImagePath) ? job.RefImagePath : null;
        string engine = job.Engine;
        if (refPath is not null && engine == "zimage")
            engine = "flux3";

        long? baseSeed = job.Seed > 0 ? job.Seed : null;
        var usedSeeds = new HashSet<long>();
        string engLabel = engine switch { "zimage" => "Z-Image Turbo", "qwen21" => "Qwen-Image 2.1", _ => "FLUX 3 Klein" };
        string lastOutFile = "";

        ClearPhotoBatchStrip();

        for (int i = 0; i < batchCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            long? iterSeed = baseSeed.HasValue
                ? baseSeed.Value + i
                : (batchCount > 1 ? NextDistinctRandomSeed(usedSeeds) : null);

            int varNum = i + 1;
            string batchPrefix = batchCount > 1 ? $"[{varNum}/{batchCount}] " : "";

            var (outFile, usedSeed) = await _storyClient.GeneratePhotoStageAsync(
                engine, job.Prompt, job.Width, job.Height, refPath, null, "studio/photo",
                msg => Dispatcher.Invoke(() =>
                {
                    StatusFooterText.Text = "Очередь: " + batchPrefix + msg;
                    GenStageText.Text = batchPrefix + msg;
                }),
                info =>
                {
                    string t = batchCount > 1
                        ? $"Очередь: Фото ({job.Width}×{job.Height}) • {varNum}/{batchCount}"
                        : $"Очередь: Фото ({job.Width}×{job.Height})";
                    UpdateGenerationHud(info with { Title = t });
                },
                ct,
                iterSeed,
                job.LoraHigh,
                job.LoraHighStrength);

            usedSeeds.Add(usedSeed);
            SavePhotoMetadata(outFile, job.Prompt, engine, job.Width, job.Height, usedSeed, job.LoraHigh, job.LoraHighStrength, refPath);

            lastOutFile = outFile;
            string metaLine = batchCount > 1
                ? $"Вариант {varNum}/{batchCount} • {engLabel} • {job.Width}×{job.Height} • Seed: {usedSeed}\n{Path.GetFileName(outFile)}"
                : $"{engLabel} • {job.Width}×{job.Height} • Seed: {usedSeed}\n{Path.GetFileName(outFile)}";

            _photoBatchResults.Add((outFile, usedSeed, metaLine));
            _photoLastSeed = usedSeed;
            PhotoUseLastSeedBtn.Visibility = Visibility.Visible;
            ShowPhotoPreview(outFile, outFile, metaLine);

            if (batchCount > 1 && _photoBatchResults.Count > 1)
                RenderPhotoBatchStrip(_photoBatchResults.Count - 1);
        }

        job.OutputPath = lastOutFile;
    }

    private async Task ExecuteQueuedVideoJobAsync(QueuedGenerationJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? startImg = !string.IsNullOrEmpty(job.StartImagePath) && File.Exists(job.StartImagePath) ? job.StartImagePath : null;
        string? endImg = !string.IsNullOrEmpty(job.EndImagePath) && File.Exists(job.EndImagePath) ? job.EndImagePath : null;
        string refMode = (startImg is not null && endImg is not null) ? "prev_to_anchor" : "prev_only";
        long? fixedSeed = job.Seed > 0 ? job.Seed : null;

        var (mp4Path, previewPath, _, usedSeed) = await _storyClient.GenerateVideoStageAsync(
            job.Engine, job.Prompt, job.Width, job.Height, job.DurationSec, startImg, endImg, refMode, "studio/video", StudioFramesDir,
            msg => Dispatcher.Invoke(() =>
            {
                StatusFooterText.Text = "Очередь: " + msg;
                GenStageText.Text = msg;
            }),
            info => UpdateGenerationHud(info with { Title = $"Очередь: Видео ({job.Width}×{job.Height}, {job.DurationSec}с)" }),
            ct,
            fixedSeed,
            null,
            null,
            null,
            job.LoraHigh,
            job.LoraHighStrength,
            job.LoraLow,
            job.LoraLowStrength);

        SaveVideoMetadata(
            mp4Path, job.Prompt, job.Engine, job.Width, job.Height, job.DurationSec, usedSeed,
            job.LoraHigh, job.LoraHighStrength, job.LoraLow, job.LoraLowStrength, startImg, endImg);

        _videoLastSeed = usedSeed;
        VideoUseLastSeedBtn.Visibility = Visibility.Visible;
        string engLabel = job.Engine switch
        {
            "ltx25" => "LTX-2.5 (Видео + Звук)",
            "wan14b_fp8" => "Wan 2.2 14B FP8",
            "wan14b_q6" => "Wan 2.2 14B Q6",
            _ => "Wan 2.2 14B Q4"
        };
        string modeTag = startImg is not null ? (endImg is not null ? "First→Last" : "I2V") : "T2V";
        ShowVideoPreview(previewPath, mp4Path, $"{engLabel} ({modeTag}) • {job.Width}×{job.Height} • {job.DurationSec}с • Seed: {usedSeed}\n{Path.GetFileName(mp4Path)}");

        job.OutputPath = mp4Path;
    }
}

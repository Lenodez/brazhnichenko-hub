using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    // ==================== QWEN SCENARIO DIRECTOR ====================

    internal void CopyStackContextBtn_Click(object sender, RoutedEventArgs e)
    {
        string ctx = _manager.LoadMediaDirectorContext();
        System.Windows.Clipboard.SetText(ctx);
        StatusFooterText.Text = "Системный контекст всех установленных моделей и режимов скопирован в буфер обмена!";
    }

    internal void OpenStackContextFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AiServiceManager.MediaDirectorContextPath))
            AiServiceManager.OpenExternal(AiServiceManager.MediaDirectorContextPath);
    }

    private readonly List<(string Role, string Content)> _photoDirectorChat = [];
    private readonly List<(string Role, string Content)> _videoDirectorChat = [];
    private bool _directorBusy;

    private async Task<string> EnsureQwenReadyForDirectorAsync()
    {
        string url = await _manager.EnsureDirectorLlmRunningAsync(msg => Dispatcher.Invoke(() => StatusFooterText.Text = msg));
        await RefreshStatusAsync();
        return url;
    }

    private string? SerializeCurrentPhotoStorySummary()
    {
        if (_photoStages.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var st in _photoStages)
            sb.AppendLine($"[Этап {st.Index}] (anchor={st.UseAnchorRef}, prev={st.UsePrevRef}): {st.Prompt}");
        return sb.ToString();
    }

    private string? SerializeCurrentVideoStorySummary()
    {
        if (_videoStages.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(_videoProject.StarterPrompt))
            sb.AppendLine($"[Шаг 0 — Стартовый кадр ({_videoProject.StarterEngine})]: {_videoProject.StarterPrompt}");
        if (!string.IsNullOrWhiteSpace(_videoProject.NegativePrompt))
            sb.AppendLine($"[Negative]: {_videoProject.NegativePrompt}");
        foreach (var st in _videoStages)
        {
            sb.AppendLine($"[Этап {st.Index}] (ref_mode={st.RefMode}): {st.Prompt}");
            if (!string.IsNullOrWhiteSpace(st.BridgePrompt))
                sb.AppendLine($"  Bridge (FLUX 3): {st.BridgePrompt}");
        }
        return sb.ToString();
    }

    private Border AppendDirectorBubble(System.Windows.Controls.StackPanel panel, ScrollViewer scroll, string role, string message)
    {
        bool isUser = role == "user";
        bool isStatus = role == "status";
        bool isError = role == "error";

        Border border;
        if (isUser)
        {
            border = new Border
            {
                Background = BrushFromHex("#1A1A20"),
                BorderBrush = BrushFromHex("#30303A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(60, 0, 0, 7),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                ContextMenu = MarkdownUiRenderer.CreateCopyContextMenu(message),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = BrushFromHex("#F4F4F6"),
                    FontSize = 12.5,
                    LineHeight = 18,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }
        else if (isStatus)
        {
            border = new Border
            {
                Background = BrushFromHex("#121215"),
                BorderBrush = BrushFromHex("#26262E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 60, 7),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = BrushFromHex("#A1A1AA"),
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }
        else
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = isError ? "ОШИБКА РЕЖИССЁРА" : "РЕЖИССЁР",
                FontSize = 10,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex(isError ? "#F87171" : "#FF521B"),
                Margin = new Thickness(0, 0, 0, 3)
            });
            stack.Children.Add(MarkdownUiRenderer.Build(message, fontSize: 12.5, foregroundHex: isError ? "#FECACA" : "#E4E4E8"));

            border = new Border
            {
                Background = BrushFromHex(isError ? "#241214" : "#111114"),
                BorderBrush = BrushFromHex(isError ? "#69282E" : "#26262E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(11, 7, 11, 7),
                Margin = new Thickness(0, 0, 36, 7),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                ContextMenu = MarkdownUiRenderer.CreateCopyContextMenu(message),
                Child = stack
            };
        }

        panel.Children.Add(border);
        scroll.ScrollToEnd();
        return border;
    }

    private void SetDirectorButtonsEnabled(bool enabled)
    {
        PhotoDirectorDiscussBtn.IsEnabled = enabled;
        PhotoDirectorBuildBtn.IsEnabled = enabled;
        PhotoDirectorSendReplyBtn.IsEnabled = enabled;
        PhotoDirectorReviseBtn.IsEnabled = enabled;
        VideoDirectorDiscussBtn.IsEnabled = enabled;
        VideoDirectorBuildBtn.IsEnabled = enabled;
        VideoDirectorSendReplyBtn.IsEnabled = enabled;
        VideoDirectorReviseBtn.IsEnabled = enabled;
        if (VideoSingleDiscussBtn is not null) VideoSingleDiscussBtn.IsEnabled = enabled;
        if (VideoEnhanceDirectorBtn is not null) VideoEnhanceDirectorBtn.IsEnabled = enabled;
        if (SingleVideoDirectorSendBtn is not null) SingleVideoDirectorSendBtn.IsEnabled = enabled;
        if (SingleVideoDirectorApplyBtn is not null) SingleVideoDirectorApplyBtn.IsEnabled = enabled;
    }

    // ---- Photo Story Interactive Director ----

    internal void PhotoDirectorHideChatBtn_Click(object sender, RoutedEventArgs e) =>
        PhotoDirectorChatPanel.Visibility = Visibility.Collapsed;

    internal void PhotoDirectorClearChatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_directorBusy) return;
        _photoDirectorChat.Clear();
        PhotoDirectorMessagesPanel.Children.Clear();
        StatusFooterText.Text = "Диалог с Режиссёром фото-истории очищен.";
    }

    internal async void PhotoDirectorDiscussBtn_Click(object sender, RoutedEventArgs e)
    {
        string idea = PhotoDirectorIdeaBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Сначала напиши короткую идею фото-истории.";
            return;
        }
        PhotoDirectorChatPanel.Visibility = Visibility.Visible;
        if (_photoDirectorChat.Count > 0)
        {
            PhotoDirectorReplyBox.Focus();
            return;
        }
        PhotoDirectorMessagesPanel.Children.Clear();
        await RunPhotoDirectorDiscussionAsync($"Вот моя идея: {idea}. Задай 2 коротких уточняющих вопроса, чтобы сделать сценарий чётким и конкретным.", includeCurrentScenario: false);
    }

    internal async void PhotoDirectorSendReplyBtn_Click(object sender, RoutedEventArgs e)
    {
        string msg = PhotoDirectorReplyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(msg)) return;
        PhotoDirectorReplyBox.Clear();
        await RunPhotoDirectorDiscussionAsync(msg, includeCurrentScenario: _lastScenario?.Kind == "photo_story");
    }

    internal async void PhotoDirectorReplyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            string msg = PhotoDirectorReplyBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                PhotoDirectorReplyBox.Clear();
                await RunPhotoDirectorDiscussionAsync(msg, includeCurrentScenario: _lastScenario?.Kind == "photo_story");
            }
        }
    }

    private async Task RunPhotoDirectorDiscussionAsync(string userMsg, bool includeCurrentScenario)
    {
        if (_storyBusy || _directorBusy) return;
        _directorBusy = true;
        SetDirectorButtonsEnabled(false);
        PhotoDirectorChatPanel.Visibility = Visibility.Visible;
        AppendDirectorBubble(PhotoDirectorMessagesPanel, PhotoDirectorChatScroll, "user", userMsg);
        var wait = AppendDirectorBubble(PhotoDirectorMessagesPanel, PhotoDirectorChatScroll, "status", "Режиссёр обдумывает ответ...");
        StatusFooterText.Text = "Режиссёр обдумывает ответ...";
        try
        {
            string endpointUrl = await EnsureQwenReadyForDirectorAsync();
            int count = int.TryParse((PhotoDirectorCountCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int c) ? c : 3;
            string reply = await _manager.DiscussStoryWithDirectorAsync(
                "photo_story",
                PhotoDirectorIdeaBox.Text.Trim(),
                count,
                includeCurrentScenario ? SerializeCurrentPhotoStorySummary() : null,
                _photoDirectorChat,
                userMsg,
                endpointUrl);

            PhotoDirectorMessagesPanel.Children.Remove(wait);
            _photoDirectorChat.Add(("user", userMsg));
            _photoDirectorChat.Add(("assistant", reply));
            AppendDirectorBubble(PhotoDirectorMessagesPanel, PhotoDirectorChatScroll, "assistant", reply);
            StatusFooterText.Text = "Режиссёр ответил — напиши ответ/правку или жми «Применить в этапы».";
            PhotoDirectorReplyBox.Focus();
        }
        catch (Exception ex)
        {
            PhotoDirectorMessagesPanel.Children.Remove(wait);
            AppendDirectorBubble(PhotoDirectorMessagesPanel, PhotoDirectorChatScroll, "error", ex.Message);
            StatusFooterText.Text = $"Ошибка Режиссёра: {ex.Message}";
        }
        finally
        {
            _directorBusy = false;
            SetDirectorButtonsEnabled(!_storyBusy);
        }
    }

    internal async void PhotoDirectorBuildBtn_Click(object sender, RoutedEventArgs e) =>
        await RunPhotoDirectorBuildAsync(isRevision: false);

    internal async void PhotoDirectorReviseBtn_Click(object sender, RoutedEventArgs e) =>
        await RunPhotoDirectorBuildAsync(isRevision: true);

    private async Task RunPhotoDirectorBuildAsync(bool isRevision)
    {
        if (_storyBusy || _directorBusy) return;
        string idea = PhotoDirectorIdeaBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Опиши идею фото-истории на русском в поле Режиссёра выше.";
            return;
        }
        string orientHint = (PhotoDirectorOrientCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        int count = int.TryParse((PhotoDirectorCountCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int c) ? c : 3;

        string? revisionText = null;
        if (isRevision)
        {
            string typed = PhotoDirectorReplyBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(typed))
            {
                revisionText = typed;
                PhotoDirectorReplyBox.Clear();
                _photoDirectorChat.Add(("user", typed));
                AppendDirectorBubble(PhotoDirectorMessagesPanel, PhotoDirectorChatScroll, "user", typed);
            }
        }

        _directorBusy = true;
        SetDirectorButtonsEnabled(false);
        StatusFooterText.Text = "Подготовка LLM для создания сценария фото-истории...";
        try
        {
            string endpointUrl = await EnsureQwenReadyForDirectorAsync();
            StatusFooterText.Text = isRevision
                ? "Режиссёр обновляет этапы фото-истории с учётом правок..."
                : $"Режиссёр пишет фото-историю ({count} кадров) с единым персонажем...";

            var sc = await _manager.GenerateStoryScenarioWithQwenAsync(
                idea,
                "photo_story",
                count,
                false,
                orientHint,
                _photoDirectorChat,
                isRevision ? SerializeCurrentPhotoStorySummary() : null,
                revisionText,
                endpointUrl);
            _lastScenario = sc;

            SelectComboByTag(PhotoStoryEngineCombo, sc.Engine);
            SelectComboByTag(PhotoStorySizeCombo, sc.Size);
            _photoStages = sc.Stages.Select((s, i) => new PhotoStoryStage
            {
                Index = i + 1,
                Prompt = s.Prompt,
                UseAnchorRef = i > 0 && s.UseAnchorRef,
                UsePrevRef = i > 0 && s.UsePrevRef
            }).ToList();
            SavePhotoStoryState();
            RenderPhotoStages();

            PhotoDirectorChatPanel.Visibility = Visibility.Visible;
            string summaryNotes = string.Join("\n", sc.Stages.Select(s => $"- **Этап {s.Index}:** {(string.IsNullOrWhiteSpace(s.NoteRu) ? "готово" : s.NoteRu)}"));
            string doneMsg = $"✓ Сценарий **«{sc.TitleRu}»** загружен ({_photoStages.Count} кад.):\n{summaryNotes}\n\nЕсли хочешь изменить конкретный кадр — напиши ниже и жми **«Применить в этапы»**.";
            _photoDirectorChat.Add(("assistant", doneMsg));
            AppendDirectorBubble(PhotoDirectorMessagesPanel, PhotoDirectorChatScroll, "assistant", doneMsg);
            StatusFooterText.Text = $"✓ Сценарий «{sc.TitleRu}» загружен ({_photoStages.Count} кадров)!";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка Режиссёра: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "AI-Режиссёр фото-истории", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _directorBusy = false;
            SetDirectorButtonsEnabled(!_storyBusy);
        }
    }

    // ---- Video Story Interactive Director ----

    internal void VideoDirectorHideChatBtn_Click(object sender, RoutedEventArgs e) =>
        VideoDirectorChatPanel.Visibility = Visibility.Collapsed;

    internal void VideoDirectorClearChatBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_directorBusy) return;
        _videoDirectorChat.Clear();
        VideoDirectorMessagesPanel.Children.Clear();
        StatusFooterText.Text = "Диалог с Режиссёром видео-истории очищен.";
    }

    internal async void VideoDirectorDiscussBtn_Click(object sender, RoutedEventArgs e)
    {
        string idea = VideoDirectorIdeaBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Сначала напиши короткую идею ролика.";
            return;
        }
        VideoDirectorChatPanel.Visibility = Visibility.Visible;
        if (_videoDirectorChat.Count > 0)
        {
            VideoDirectorReplyBox.Focus();
            return;
        }
        VideoDirectorMessagesPanel.Children.Clear();
        await RunVideoDirectorDiscussionAsync($"Вот моя идея ролика: {idea}. Предложи острый каркас по сценам и задай 2 коротких уточняющих вопроса, чтобы сценарий не был размытым.", includeCurrentScenario: false);
    }

    internal async void VideoDirectorSendReplyBtn_Click(object sender, RoutedEventArgs e)
    {
        string msg = VideoDirectorReplyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(msg)) return;
        VideoDirectorReplyBox.Clear();
        await RunVideoDirectorDiscussionAsync(msg, includeCurrentScenario: _lastScenario is not null && _lastScenario.Kind != "photo_story");
    }

    internal async void VideoDirectorReplyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            string msg = VideoDirectorReplyBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                VideoDirectorReplyBox.Clear();
                await RunVideoDirectorDiscussionAsync(msg, includeCurrentScenario: _lastScenario is not null && _lastScenario.Kind != "photo_story");
            }
        }
    }

    private async Task RunVideoDirectorDiscussionAsync(string userMsg, bool includeCurrentScenario)
    {
        if (_storyBusy || _directorBusy) return;
        _directorBusy = true;
        SetDirectorButtonsEnabled(false);
        VideoDirectorChatPanel.Visibility = Visibility.Visible;
        AppendDirectorBubble(VideoDirectorMessagesPanel, VideoDirectorChatScroll, "user", userMsg);
        var wait = AppendDirectorBubble(VideoDirectorMessagesPanel, VideoDirectorChatScroll, "status", "Режиссёр обдумывает ответ...");
        StatusFooterText.Text = "Режиссёр обдумывает ответ...";
        try
        {
            string endpointUrl = await EnsureQwenReadyForDirectorAsync();
            string selectedEngine = (VideoStoryEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wan14b_fp8";
            string targetKind = selectedEngine == "ltx25" ? "video_ltx" : "video_wan";
            int count = int.TryParse((VideoDirectorCountCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int c) ? c : 3;

            string reply = await _manager.DiscussStoryWithDirectorAsync(
                targetKind,
                VideoDirectorIdeaBox.Text.Trim(),
                count,
                includeCurrentScenario ? SerializeCurrentVideoStorySummary() : null,
                _videoDirectorChat,
                userMsg,
                endpointUrl);

            VideoDirectorMessagesPanel.Children.Remove(wait);
            _videoDirectorChat.Add(("user", userMsg));
            _videoDirectorChat.Add(("assistant", reply));
            AppendDirectorBubble(VideoDirectorMessagesPanel, VideoDirectorChatScroll, "assistant", reply);
            StatusFooterText.Text = "Режиссёр ответил — напиши ответ/правку или жми «Обновить этапы».";
            VideoDirectorReplyBox.Focus();
        }
        catch (Exception ex)
        {
            VideoDirectorMessagesPanel.Children.Remove(wait);
            AppendDirectorBubble(VideoDirectorMessagesPanel, VideoDirectorChatScroll, "error", ex.Message);
            StatusFooterText.Text = $"Ошибка Режиссёра: {ex.Message}";
        }
        finally
        {
            _directorBusy = false;
            SetDirectorButtonsEnabled(!_storyBusy);
        }
    }

    internal async void VideoDirectorBuildBtn_Click(object sender, RoutedEventArgs e) =>
        await RunVideoDirectorBuildAsync(isRevision: false);

    internal async void VideoDirectorReviseBtn_Click(object sender, RoutedEventArgs e) =>
        await RunVideoDirectorBuildAsync(isRevision: true);

    private async Task RunVideoDirectorBuildAsync(bool isRevision)
    {
        if (_storyBusy || _directorBusy) return;
        string idea = VideoDirectorIdeaBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            StatusFooterText.Text = "Опиши сюжет ролика на русском в поле Режиссёра выше.";
            return;
        }
        string selectedEngine = (VideoStoryEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wan14b_fp8";
        string targetKind = selectedEngine == "ltx25" ? "video_ltx" : "video_wan";
        string orientHint = (VideoDirectorOrientCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        int count = int.TryParse((VideoDirectorCountCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int c) ? c : 3;
        bool loop = VideoDirectorLoopCheck.IsChecked == true;

        string? revisionText = null;
        if (isRevision)
        {
            string typed = VideoDirectorReplyBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(typed))
            {
                revisionText = typed;
                VideoDirectorReplyBox.Clear();
                _videoDirectorChat.Add(("user", typed));
                AppendDirectorBubble(VideoDirectorMessagesPanel, VideoDirectorChatScroll, "user", typed);
            }
        }

        _directorBusy = true;
        SetDirectorButtonsEnabled(false);
        StatusFooterText.Text = "Подготовка LLM для создания сценария видео-истории...";
        try
        {
            string endpointUrl = await EnsureQwenReadyForDirectorAsync();
            StatusFooterText.Text = isRevision
                ? "Режиссёр переписывает этапы видео-истории с учётом твоих правок..."
                : $"Режиссёр расписывает Шаг 0 и {count} сцен видео...";

            var sc = await _manager.GenerateStoryScenarioWithQwenAsync(
                idea,
                targetKind,
                count,
                loop,
                orientHint,
                _videoDirectorChat,
                isRevision ? SerializeCurrentVideoStorySummary() : null,
                revisionText,
                endpointUrl);
            _lastScenario = sc;

            SelectComboByTag(VideoStoryEngineCombo, sc.Engine);
            SelectComboByTag(VideoStorySizeCombo, sc.Size);
            SelectComboByTag(VideoStoryDurCombo, sc.DurationSec.ToString());
            _videoProject = new VideoStoryProject
            {
                UseStarterFrame = sc.UseStarterFrame,
                StarterEngine = sc.StarterEngine,
                StarterPrompt = sc.StarterPrompt,
                StarterImagePath = null,
                NegativePrompt = sc.NegativePrompt,
                Stages = sc.Stages.Select((s, i) => new VideoStoryStage
                {
                    Index = i + 1,
                    Prompt = s.Prompt,
                    BridgePrompt = i == 0 ? "" : s.BridgePrompt,
                    RefMode = i == 0 ? "svi_pro" : s.RefMode
                }).ToList()
            };
            SaveVideoStoryState();
            RenderVideoStages();

            VideoDirectorChatPanel.Visibility = Visibility.Visible;
            string summaryNotes = string.Join("\n", sc.Stages.Select(s => $"- **Этап {s.Index}:** {(string.IsNullOrWhiteSpace(s.NoteRu) ? "готово" : s.NoteRu)}"));
            string doneMsg = $"✓ Сценарий **«{sc.TitleRu}»** загружен ({_videoStages.Count} сц.):\n{summaryNotes}\n\nХочешь поменять какую-то сцену? Напиши правку ниже и жми **«Обновить этапы»**.";
            _videoDirectorChat.Add(("assistant", doneMsg));
            AppendDirectorBubble(VideoDirectorMessagesPanel, VideoDirectorChatScroll, "assistant", doneMsg);
            StatusFooterText.Text = $"✓ Сценарий «{sc.TitleRu}» загружен ({_videoStages.Count} сцен)!";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка Режиссёра: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "AI-Режиссёр видео-истории", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _directorBusy = false;
            SetDirectorButtonsEnabled(!_storyBusy);
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private const int LocalChatContextMessages = 16;

    private static readonly HttpClient LocalChatHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private List<ChatSession> _chatSessions = [];
    private ChatSession _activeChatSession = ChatSessionService.CreateNew();
    private bool _suppressSessionSelection;
    private bool _suppressPersonaTempSync;
    private bool _localChatSidebarVisible = true;
    private string? _localChatPendingImagePath;
    private string? _lastFailedVisionPrompt;
    private string? _lastFailedVisionImagePath;

    private CancellationTokenSource? _localChatCts;
    private bool _localChatBusy;
    private bool _localChatInitialized;

    private sealed class ThinkTagStreamSplitter
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";

        private bool _inThinkTag;
        private string _pending = "";

        public void Feed(string chunk, StringBuilder reasoningBuf, StringBuilder answerBuf)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            _pending += chunk;

            while (_pending.Length > 0)
            {
                if (!_inThinkTag)
                {
                    int openIdx = _pending.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                    if (openIdx >= 0)
                    {
                        if (openIdx > 0)
                            answerBuf.Append(_pending.AsSpan(0, openIdx));
                        _pending = _pending[(openIdx + OpenTag.Length)..];
                        _inThinkTag = true;
                        continue;
                    }

                    int keep = MatchTrailingPrefixLength(_pending, OpenTag);
                    int flushLen = _pending.Length - keep;
                    if (flushLen > 0)
                    {
                        answerBuf.Append(_pending.AsSpan(0, flushLen));
                        _pending = _pending[flushLen..];
                    }
                    break;
                }
                else
                {
                    int closeIdx = _pending.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0)
                    {
                        if (closeIdx > 0)
                            reasoningBuf.Append(_pending.AsSpan(0, closeIdx));
                        _pending = _pending[(closeIdx + CloseTag.Length)..];
                        _inThinkTag = false;
                        continue;
                    }

                    int keep = MatchTrailingPrefixLength(_pending, CloseTag);
                    int flushLen = _pending.Length - keep;
                    if (flushLen > 0)
                    {
                        reasoningBuf.Append(_pending.AsSpan(0, flushLen));
                        _pending = _pending[flushLen..];
                    }
                    break;
                }
            }
        }

        public void Flush(StringBuilder reasoningBuf, StringBuilder answerBuf)
        {
            if (_pending.Length == 0) return;
            if (_inThinkTag)
                reasoningBuf.Append(_pending);
            else
                answerBuf.Append(_pending);
            _pending = "";
        }

        private static int MatchTrailingPrefixLength(string text, string tag)
        {
            int maxLen = Math.Min(text.Length, tag.Length - 1);
            for (int len = maxLen; len >= 1; len--)
            {
                if (text.AsSpan(text.Length - len).Equals(tag.AsSpan(0, len), StringComparison.OrdinalIgnoreCase))
                    return len;
            }
            return 0;
        }
    }

    private void InitLocalChat()
    {
        if (LocalChatMessagesPanel is null) return;

        if (!_localChatInitialized)
        {
            _localChatInitialized = true;
            Closed += (_, _) =>
            {
                try { _localChatCts?.Cancel(); } catch { }
            };

            if (LocalChatPersonaCombo is not null)
            {
                LocalChatPersonaCombo.Items.Clear();
                foreach (var p in ChatSessionService.BuiltInPersonas)
                {
                    LocalChatPersonaCombo.Items.Add(new ComboBoxItem
                    {
                        Content = p.Name,
                        Tag = p.Id,
                        ToolTip = p.Description
                    });
                }
            }
        }

        _chatSessions = ChatSessionService.LoadAll();
        if (_chatSessions.Count == 0)
            _chatSessions.Add(ChatSessionService.CreateNew());
        _activeChatSession = _chatSessions[0];

        if (LocalChatStatsText is not null)
            LocalChatStatsText.Text = "Готов к чату • Enter — отправить • Ctrl+V — прикрепить фото";

        if (LocalChatStopBtn is not null)
            LocalChatStopBtn.Visibility = Visibility.Collapsed;

        SyncPersonaAndTempControls();
        RenderLocalChatSessionsList();
        RenderLocalChatHistory();
        UpdateLocalChatVisionBadge();
    }

    private void UpdateLocalChatVisionBadge()
    {
        if (LocalChatVisionBadgeText is null || LocalChatVisionBadge is null) return;
        string? activeModel = _manager.GetActiveLlmFileName();
        string? mmproj = _manager.FindMatchingMmprojForModel(activeModel);
        if (!string.IsNullOrWhiteSpace(mmproj))
        {
            LocalChatVisionBadgeText.Text = "VISION";
            LocalChatVisionBadgeText.Foreground = BrushFromHex("#34D399");
            LocalChatVisionBadge.Background = BrushFromHex("#11241C");
            LocalChatVisionBadge.BorderBrush = BrushFromHex("#265944");
            LocalChatVisionBadge.ToolTip = $"Найден файл зрения: {Path.GetFileName(mmproj)} — модель понимает картинки.";
        }
        else
        {
            LocalChatVisionBadgeText.Text = "TEXT ONLY";
            LocalChatVisionBadgeText.Foreground = BrushFromHex("#71717A");
            LocalChatVisionBadge.Background = BrushFromHex("#151519");
            LocalChatVisionBadge.BorderBrush = BrushFromHex("#26262E");
            LocalChatVisionBadge.ToolTip = "Для анализа картинок скачай mmproj-*.gguf для выбранной модели в «Модели и память → Поиск HF»";
        }
    }

    private void SyncPersonaAndTempControls()
    {
        _suppressPersonaTempSync = true;
        try
        {
            if (LocalChatPersonaCombo is not null)
                SelectComboByTag(LocalChatPersonaCombo, _activeChatSession.PersonaId);
            if (LocalChatTempSlider is not null)
                LocalChatTempSlider.Value = Math.Clamp(_activeChatSession.Temperature, 0.0, 1.5);
            if (LocalChatTempValText is not null)
                LocalChatTempValText.Text = _activeChatSession.Temperature.ToString("0.00", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressPersonaTempSync = false;
        }
    }

    private void RenderLocalChatSessionsList()
    {
        if (LocalChatSessionsList is null) return;
        _suppressSessionSelection = true;
        try
        {
            string query = LocalChatSearchBox?.Text ?? "";
            var filtered = ChatSessionService.FilterSessions(_chatSessions, query);
            LocalChatSessionsList.Items.Clear();

            ListBoxItem? selectedItem = null;
            foreach (var s in filtered)
            {
                var panel = new StackPanel { Margin = new Thickness(1, 2, 1, 2) };
                panel.Children.Add(new TextBlock
                {
                    Text = s.Title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFromHex("#F4F4F6"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var persona = ChatSessionService.GetPersona(s.PersonaId);
                string meta = $"{persona.Name} • {s.Messages.Count}";
                panel.Children.Add(new TextBlock
                {
                    Text = meta,
                    FontSize = 10.5,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = BrushFromHex("#71717A"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });

                var lbi = new ListBoxItem
                {
                    Content = panel,
                    Tag = s.Id,
                    Padding = new Thickness(8, 6, 8, 6)
                };
                LocalChatSessionsList.Items.Add(lbi);
                if (s.Id == _activeChatSession.Id)
                    selectedItem = lbi;
            }

            if (selectedItem is not null)
                LocalChatSessionsList.SelectedItem = selectedItem;
        }
        finally
        {
            _suppressSessionSelection = false;
        }
    }

    internal void LocalChatToggleSidebarBtn_Click(object sender, RoutedEventArgs e)
    {
        _localChatSidebarVisible = !_localChatSidebarVisible;
        if (LocalChatSidebarBorder is not null)
            LocalChatSidebarBorder.Visibility = _localChatSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (LocalChatSidebarCol is not null)
            LocalChatSidebarCol.Width = _localChatSidebarVisible ? new GridLength(228) : new GridLength(0);
    }

    internal void LocalChatNewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_localChatBusy) return;
        var session = ChatSessionService.CreateNew(_activeChatSession.PersonaId);
        _chatSessions.Insert(0, session);
        _activeChatSession = session;
        ChatSessionService.SaveAll(_chatSessions);
        ClearPendingLocalChatImage();
        SyncPersonaAndTempControls();
        RenderLocalChatSessionsList();
        RenderLocalChatHistory();
        LocalChatInputBox?.Focus();
        ShowToast("Создан новый диалог", "info");
    }

    internal void LocalChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RenderLocalChatSessionsList();
    }

    internal void LocalChatSessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSessionSelection || _localChatBusy) return;
        if (LocalChatSessionsList?.SelectedItem is not ListBoxItem lbi || lbi.Tag is not string id) return;
        var found = _chatSessions.FirstOrDefault(s => s.Id == id);
        if (found is null || found.Id == _activeChatSession.Id) return;

        _activeChatSession = found;
        ClearPendingLocalChatImage();
        SyncPersonaAndTempControls();
        RenderLocalChatHistory();
    }

    internal void LocalChatRenameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_localChatBusy) return;
        string? newName = ShowQuickInputDialog("Переименовать диалог", "Новое название диалога:", _activeChatSession.Title);
        if (string.IsNullOrWhiteSpace(newName)) return;
        _activeChatSession.Title = newName.Trim();
        _activeChatSession.UpdatedAtUtc = DateTime.UtcNow;
        ChatSessionService.SaveAll(_chatSessions);
        RenderLocalChatSessionsList();
        ShowToast("Диалог переименован", "success");
    }

    internal void LocalChatDeleteSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_localChatBusy) return;
        var confirm = HubDialogWindow.Show(
            $"Удалить диалог «{_activeChatSession.Title}» ({_activeChatSession.Messages.Count} сообщений)?",
            "Удаление диалога",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _chatSessions.RemoveAll(s => s.Id == _activeChatSession.Id);
        if (_chatSessions.Count == 0)
            _chatSessions.Add(ChatSessionService.CreateNew());
        _activeChatSession = _chatSessions[0];
        ChatSessionService.SaveAll(_chatSessions);
        SyncPersonaAndTempControls();
        RenderLocalChatSessionsList();
        RenderLocalChatHistory();
        ShowToast("Диалог удалён", "info");
    }

    internal void LocalChatPersonaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPersonaTempSync || !IsLoaded) return;
        if (LocalChatPersonaCombo?.SelectedItem is not ComboBoxItem item || item.Tag is not string personaId) return;
        var p = ChatSessionService.GetPersona(personaId);
        _activeChatSession.PersonaId = p.Id;
        _activeChatSession.Temperature = p.DefaultTemperature;
        SyncPersonaAndTempControls();
        ChatSessionService.SaveAll(_chatSessions);
        RenderLocalChatSessionsList();
        ShowToast($"Персона: {p.Name}", "info");
    }

    internal void LocalChatTempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LocalChatTempValText is not null)
            LocalChatTempValText.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
        if (_suppressPersonaTempSync || !IsLoaded) return;
        _activeChatSession.Temperature = Math.Round(e.NewValue, 2);
        ChatSessionService.SaveAll(_chatSessions);
    }

    // ==================== VISION IMAGE ATTACHMENT ====================

    internal void LocalChatAttachImgBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_localChatBusy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Прикрепить изображение для Vision LLM",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Все файлы|*.*"
        };
        if (dlg.ShowDialog() == true)
            SetPendingLocalChatImage(dlg.FileName);
    }

    internal void LocalChatClearImgBtn_Click(object sender, RoutedEventArgs e) =>
        ClearPendingLocalChatImage();

    private static bool IsChatImageFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
    }

    internal void LocalChat_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_localChatBusy) return;
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files &&
            files.Length > 0 && IsChatImageFile(files[0]))
        {
            SetPendingLocalChatImage(files[0]);
            e.Handled = true;
        }
    }

    private void SetPendingLocalChatImage(string path)
    {
        if (!File.Exists(path)) return;
        _localChatPendingImagePath = path;
        if (LocalChatPendingImgBorder is not null)
            LocalChatPendingImgBorder.Visibility = Visibility.Visible;
        if (LocalChatPendingImgName is not null)
            LocalChatPendingImgName.Text = Path.GetFileName(path);
        if (LocalChatPendingImg is not null)
            LocalChatPendingImg.Source = LoadBitmapNoLock(path);

        UpdateLocalChatVisionBadge();
        if (string.IsNullOrWhiteSpace(_manager.FindMatchingMmprojForModel(_manager.GetActiveLlmFileName())))
            ShowToast("Фото прикреплено (для Vision нужен файл mmproj-*.gguf рядом с моделью)", "info");
        else
            ShowToast($"Vision: прикреплено {Path.GetFileName(path)}", "success");
    }

    private void ClearPendingLocalChatImage()
    {
        _localChatPendingImagePath = null;
        if (LocalChatPendingImgBorder is not null)
            LocalChatPendingImgBorder.Visibility = Visibility.Collapsed;
        if (LocalChatPendingImg is not null)
            LocalChatPendingImg.Source = null;
    }

    private void RenderLocalChatHistory()
    {
        if (LocalChatMessagesPanel is null) return;
        LocalChatMessagesPanel.Children.Clear();

        if (_activeChatSession.Messages.Count == 0)
        {
            var persona = ChatSessionService.GetPersona(_activeChatSession.PersonaId);
            AppendLocalChatStaticAssistantBubble(
                $"**Чат 2.0 готов** (персона: **{persona.Name}**, темп. `{_activeChatSession.Temperature:0.00}`).\n\n" +
                $"• Напиши сообщение, продиктуй голосом (кнопка микрофона) или перетащи картинку / нажми `Ctrl+V` для Vision.\n" +
                $"• Выбранная локальная LLM запустится автоматически, если сервер остановлен.",
                null,
                "Локальная LLM",
                showCopyButton: false);
            return;
        }

        string modelLabel = GetActiveLocalLlmDisplayName();
        foreach (var entry in _activeChatSession.Messages)
        {
            if (entry.Role == "user")
                AppendLocalChatUserBubble(entry.Content, entry.ImagePath);
            else if (entry.Role == "assistant")
                AppendLocalChatStaticAssistantBubble(entry.Content, entry.Reasoning, modelLabel, showCopyButton: true);
        }

        ScrollLocalChatToEnd(force: true);
    }

    private string GetActiveLocalLlmDisplayName()
    {
        try
        {
            if (ChatLlmModelCombo?.SelectedItem is LlmModelOption opt && !string.IsNullOrWhiteSpace(opt.DisplayName))
                return opt.DisplayName;
        }
        catch { }

        string? active = _manager.GetActiveLlmFileName();
        return string.IsNullOrWhiteSpace(active) ? "Локальная LLM" : Path.GetFileNameWithoutExtension(active);
    }

    internal async void LocalChatSendBtn_Click(object sender, RoutedEventArgs e)
    {
        await SendLocalChatMessageAsync(LocalChatInputBox?.Text ?? "");
    }

    internal void LocalChatStopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_localChatBusy) return;
        try { _localChatCts?.Cancel(); } catch { }
        if (LocalChatStatsText is not null)
            LocalChatStatsText.Text = "Остановка генерации...";
    }

    internal async void LocalChatInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (System.Windows.Clipboard.ContainsImage() && TryGetImageFromClipboard(out string? clipFile) && clipFile is not null)
            {
                e.Handled = true;
                SetPendingLocalChatImage(clipFile);
                return;
            }
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendLocalChatMessageAsync(LocalChatInputBox?.Text ?? "");
        }
    }

    internal void LocalChatClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_localChatBusy)
        {
            try { _localChatCts?.Cancel(); } catch { }
            return;
        }

        _activeChatSession.Messages.Clear();
        _activeChatSession.UpdatedAtUtc = DateTime.UtcNow;
        ChatSessionService.SaveAll(_chatSessions);
        ClearPendingLocalChatImage();
        RenderLocalChatSessionsList();
        RenderLocalChatHistory();
        if (LocalChatStatsText is not null)
            LocalChatStatsText.Text = "Готов к чату";
        ShowToast("Сообщения текущего диалога очищены", "info");
    }

    private async Task SendLocalChatMessageAsync(string rawText)
    {
        string text = rawText.Trim();
        string? attachedImage = _localChatPendingImagePath;
        if ((string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachedImage)) || _localChatBusy) return;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(attachedImage))
            text = "Опиши подробно, что изображено на этой картинке.";

        LocalChatInputBox?.Clear();
        ClearPendingLocalChatImage();

        if (_activeChatSession.Messages.Count == 0 && _activeChatSession.Title == "Новый чат")
            _activeChatSession.Title = ChatSessionService.AutoTitleFromPrompt(text);

        AppendLocalChatUserBubble(text, attachedImage);
        ScrollLocalChatToEnd(force: true);

        _localChatBusy = true;
        MarkLlmActivity();
        _localChatCts?.Dispose();
        _localChatCts = new CancellationTokenSource();
        var ct = _localChatCts.Token;

        SetLocalChatStreamingUi(isStreaming: true);

        Border? statusBubble = null;
        Border? assistantBubble = null;

        var reasoningBuf = new StringBuilder();
        var answerBuf = new StringBuilder();
        var thinkSplitter = new ThinkTagStreamSplitter();
        var genSw = new Stopwatch();
        var uiThrottleSw = Stopwatch.StartNew();

        int streamedTokens = 0;
        double? serverTokPerSec = null;
        int? serverTokens = null;
        bool hasStartedThinking = false;
        bool hasCollapsedThinkingOnAnswer = false;
        bool wasStopped = false;

        string modelLabel = GetActiveLocalLlmDisplayName();

        Border? thinkContainer = null;
        System.Windows.Controls.Expander? thinkExpander = null;
        TextBlock? thinkTextBlock = null;
        System.Windows.Controls.ContentControl? answerHost = null;
        TextBlock? streamingAnswerTb = null;
        System.Windows.Controls.Button? copyBtn = null;
        TextBlock? bubbleMetaText = null;

        void FlushUiSnapshot(bool forceScroll = false)
        {
            string reasoningNow = reasoningBuf.ToString().Trim();
            string answerNow = answerBuf.ToString().TrimStart('\r', '\n');

            if (reasoningNow.Length > 0 && thinkContainer is not null && thinkExpander is not null && thinkTextBlock is not null)
            {
                if (!hasStartedThinking)
                {
                    hasStartedThinking = true;
                    thinkContainer.Visibility = Visibility.Visible;
                    thinkExpander.IsExpanded = true;
                }
                thinkTextBlock.Text = reasoningNow;
            }

            if (answerNow.Length > 0)
            {
                if (hasStartedThinking && !hasCollapsedThinkingOnAnswer && thinkExpander is not null)
                {
                    hasCollapsedThinkingOnAnswer = true;
                    thinkExpander.IsExpanded = false;
                }

                if (streamingAnswerTb is not null)
                    streamingAnswerTb.Text = answerNow + " ▌";
            }

            if (streamedTokens > 0 && LocalChatStatsText is not null)
            {
                double secs = Math.Max(0.05, genSw.Elapsed.TotalSeconds);
                double tps = streamedTokens / secs;
                LocalChatStatsText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F1} tok/s • {1} токенов",
                    tps,
                    streamedTokens);
            }

            ScrollLocalChatToEnd(force: forceScroll);
        }

        try
        {
            string? webContextBlock = null;
            if (LocalChatWebSearchCheck?.IsChecked == true && !string.IsNullOrWhiteSpace(text))
            {
                statusBubble = AppendLocalChatStatusBubble("Ищу актуальную информацию в интернете...");
                ScrollLocalChatToEnd(force: true);
                if (LocalChatStatsText is not null)
                    LocalChatStatsText.Text = "Поиск в интернете...";

                try
                {
                    var searchRes = await DesktopWebSearchService.SearchAsync(text, ct);
                    if (searchRes.Results.Count > 0)
                    {
                        webContextBlock = searchRes.BuildPromptContext();
                        if (LocalChatStatsText is not null)
                            LocalChatStatsText.Text = $"Найдено источников: {searchRes.Results.Count} ({searchRes.Provider})";
                    }
                }
                catch { /* fallback silently to local knowledge */ }

                if (statusBubble is not null)
                {
                    LocalChatMessagesPanel?.Children.Remove(statusBubble);
                    statusBubble = null;
                }
            }

            var qwenSvc = _manager.GetService("qwen");
            if (!await _manager.IsRunningAsync(qwenSvc))
            {
                statusBubble = AppendLocalChatStatusBubble("Запускаю выбранную LLM...");
                ScrollLocalChatToEnd(force: true);
                if (LocalChatStatsText is not null)
                    LocalChatStatsText.Text = "Запускаю выбранную LLM...";

                await _manager.SwitchAndStartAsync("qwen", msg =>
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusFooterText.Text = msg;
                            if (statusBubble?.Child is TextBlock tb) tb.Text = msg;
                            if (LocalChatStatsText is not null) LocalChatStatsText.Text = msg;
                        });
                    }
                    catch { }
                });

                ct.ThrowIfCancellationRequested();
                await RefreshStatusAsync();
                modelLabel = GetActiveLocalLlmDisplayName();
                UpdateLocalChatVisionBadge();

                if (statusBubble is not null)
                {
                    LocalChatMessagesPanel?.Children.Remove(statusBubble);
                    statusBubble = null;
                }
            }

            var apiMessages = BuildLocalChatApiMessages(text, attachedImage, webContextBlock);
            var cfg = HubConfig.Current;
            int maxTokens = Math.Max(
                2048,
                cfg.LlmReasoningBudget > 0 ? cfg.LlmReasoningBudget + 2048 : 4096);

            var body = new
            {
                messages = apiMessages,
                stream = true,
                temperature = Math.Clamp(_activeChatSession.Temperature, 0.0, 2.0),
                top_p = cfg.LlmTopP,
                presence_penalty = cfg.LlmPresencePenalty,
                max_tokens = maxTokens
            };

            string endpoint = $"http://127.0.0.1:{cfg.QwenPort}/v1/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            (assistantBubble, thinkContainer, thinkExpander, thinkTextBlock, answerHost, streamingAnswerTb, copyBtn, bubbleMetaText) =
                CreateStreamingAssistantBubble(modelLabel);
            LocalChatMessagesPanel?.Children.Add(assistantBubble);
            ScrollLocalChatToEnd(force: true);

            if (LocalChatStatsText is not null)
                LocalChatStatsText.Text = "Ожидание первых токенов...";

            using var resp = await LocalChatHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {TrimErrorSnippet(errBody)}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = line.AsSpan(5).Trim().ToString();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("timings", out var timingsEl) && timingsEl.ValueKind == JsonValueKind.Object)
                    {
                        if (timingsEl.TryGetProperty("predicted_per_second", out var ppsEl) && ppsEl.ValueKind == JsonValueKind.Number)
                            serverTokPerSec = ppsEl.GetDouble();
                        if (timingsEl.TryGetProperty("predicted_n", out var pnEl) && pnEl.ValueKind == JsonValueKind.Number)
                            serverTokens = pnEl.GetInt32();
                    }
                    else if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number)
                            serverTokens = ctEl.GetInt32();
                    }

                    if (!root.TryGetProperty("choices", out var choicesEl) ||
                        choicesEl.ValueKind != JsonValueKind.Array ||
                        choicesEl.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var choice0 = choicesEl[0];
                    if (!choice0.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Object)
                        continue;

                    bool gotTokenChunk = false;

                    string? reasoningDelta = null;
                    if (deltaEl.TryGetProperty("reasoning_content", out var rcEl) && rcEl.ValueKind == JsonValueKind.String)
                        reasoningDelta = rcEl.GetString();
                    else if (deltaEl.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                        reasoningDelta = rEl.GetString();

                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        if (!genSw.IsRunning) genSw.Start();
                        reasoningBuf.Append(reasoningDelta);
                        gotTokenChunk = true;
                    }

                    if (deltaEl.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                    {
                        string? contentDelta = cEl.GetString();
                        if (!string.IsNullOrEmpty(contentDelta))
                        {
                            if (!genSw.IsRunning) genSw.Start();
                            thinkSplitter.Feed(contentDelta, reasoningBuf, answerBuf);
                            gotTokenChunk = true;
                        }
                    }

                    if (gotTokenChunk)
                    {
                        streamedTokens++;
                        if (uiThrottleSw.ElapsedMilliseconds >= 50)
                        {
                            uiThrottleSw.Restart();
                            FlushUiSnapshot();
                        }
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException)
        {
            wasStopped = true;
        }
        catch (Exception ex)
        {
            if (statusBubble is not null)
            {
                LocalChatMessagesPanel?.Children.Remove(statusBubble);
                statusBubble = null;
            }
            if (assistantBubble is not null && answerBuf.Length == 0 && reasoningBuf.Length == 0)
            {
                LocalChatMessagesPanel?.Children.Remove(assistantBubble);
                assistantBubble = null;
            }

            string errText = ex.Message ?? "";
            bool isVisionError = !string.IsNullOrWhiteSpace(attachedImage) &&
                (errText.Contains("image input is not supported", StringComparison.OrdinalIgnoreCase) ||
                 errText.Contains("mmproj", StringComparison.OrdinalIgnoreCase));

            if (isVisionError)
            {
                _lastFailedVisionPrompt = text;
                _lastFailedVisionImagePath = attachedImage;
                AppendLocalChatVisionGuidanceBubble(errText);
                if (LocalChatStatsText is not null)
                    LocalChatStatsText.Text = "Нужен Vision-модуль (mmproj) или перезапуск LLM";
            }
            else
            {
                AppendLocalChatStaticAssistantBubble(
                    $"Не удалось получить ответ от локальной LLM: {errText}\n\nПроверь, установлена ли модель в «Модели и GPU → Текстовые (LLM)».",
                    null,
                    "Ошибка LLM",
                    showCopyButton: true);

                if (LocalChatStatsText is not null)
                    LocalChatStatsText.Text = "Ошибка подключения к LLM";
            }
            StatusFooterText.Text = $"Локальная LLM: {errText}";
            ScrollLocalChatToEnd(force: true);
        }
        finally
        {
            if (statusBubble is not null)
            {
                LocalChatMessagesPanel?.Children.Remove(statusBubble);
                statusBubble = null;
            }

            thinkSplitter.Flush(reasoningBuf, answerBuf);
            genSw.Stop();

            string finalReasoning = reasoningBuf.ToString().Trim();
            string finalAnswer = answerBuf.ToString().Trim();

            if (assistantBubble is not null && answerHost is not null)
            {
                if (finalAnswer.Length == 0 && finalReasoning.Length == 0)
                {
                    if (wasStopped)
                    {
                        LocalChatMessagesPanel?.Children.Remove(assistantBubble);
                        if (LocalChatStatsText is not null)
                            LocalChatStatsText.Text = "Остановлено";
                    }
                    else
                    {
                        answerHost.Content = new TextBlock
                        {
                            Text = "(Модель вернула пустой ответ)",
                            Foreground = BrushFromHex("#94A0B8"),
                            FontSize = 12,
                            FontStyle = FontStyles.Italic,
                            TextWrapping = TextWrapping.Wrap
                        };
                        if (LocalChatStatsText is not null)
                            LocalChatStatsText.Text = "Готов к чату";
                    }
                }
                else
                {
                    int finalTokenCount = serverTokens ?? streamedTokens;
                    double finalTps = serverTokPerSec
                        ?? (finalTokenCount > 0 ? finalTokenCount / Math.Max(0.05, genSw.Elapsed.TotalSeconds) : 0);
                    MarkLlmActivity(finalTps);

                    string statsSummary = finalTokenCount > 0
                        ? string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}{1:F1} tok/s • {2} токенов",
                            wasStopped ? "Остановлено • " : "",
                            finalTps,
                            finalTokenCount)
                        : (wasStopped ? "Остановлено" : "Готово");

                    if (LocalChatStatsText is not null)
                        LocalChatStatsText.Text = statsSummary;

                    if (bubbleMetaText is not null && finalTokenCount > 0)
                    {
                        bubbleMetaText.Text = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:F1} tok/s • {1} ток.",
                            finalTps,
                            finalTokenCount);
                    }

                    if (finalReasoning.Length > 0 && thinkContainer is not null && thinkExpander is not null && thinkTextBlock is not null)
                    {
                        thinkContainer.Visibility = Visibility.Visible;
                        thinkTextBlock.Text = finalReasoning;
                        thinkExpander.IsExpanded = finalAnswer.Length == 0;
                    }

                    if (finalAnswer.Length > 0)
                    {
                        answerHost.Content = MarkdownUiRenderer.Build(finalAnswer, fontSize: 13.5, foregroundHex: "#E4E4E8");
                    }
                    else
                    {
                        answerHost.Content = new TextBlock
                        {
                            Text = wasStopped
                                ? "(Генерация остановлена во время размышлений)"
                                : "(Модель завершила ответ только в блоке размышлений)",
                            Foreground = BrushFromHex("#71717A"),
                            FontSize = 12,
                            FontStyle = FontStyles.Italic,
                            TextWrapping = TextWrapping.Wrap
                        };
                    }

                    string textToCopy = finalAnswer.Length > 0 ? finalAnswer : finalReasoning;
                    assistantBubble.ContextMenu = MarkdownUiRenderer.CreateCopyContextMenu(textToCopy);

                    if (copyBtn is not null)
                    {
                        copyBtn.Visibility = Visibility.Visible;
                        copyBtn.Click += (_, _) => CopyLocalChatText(textToCopy);
                    }

                    _activeChatSession.Messages.Add(new ChatMessageRecord
                    {
                        Role = "user",
                        Content = text,
                        ImagePath = attachedImage,
                        TimestampUtc = DateTime.UtcNow
                    });
                    _activeChatSession.Messages.Add(new ChatMessageRecord
                    {
                        Role = "assistant",
                        Content = finalAnswer,
                        Reasoning = finalReasoning.Length > 0 ? finalReasoning : null,
                        TimestampUtc = DateTime.UtcNow
                    });
                    _activeChatSession.UpdatedAtUtc = DateTime.UtcNow;
                    ChatSessionService.SaveAll(_chatSessions);
                    RenderLocalChatSessionsList();
                    ScrollLocalChatToEnd(force: false);
                }
            }

            _localChatBusy = false;
            SetLocalChatStreamingUi(isStreaming: false);
        }
    }

    private List<object> BuildLocalChatApiMessages(string currentUserMessage, string? currentImagePath, string? webContextBlock = null)
    {
        var messages = new List<object>();

        var persona = ChatSessionService.GetPersona(_activeChatSession.PersonaId);
        string sysPrompt = persona.SystemPrompt ?? "";
        if (!string.IsNullOrWhiteSpace(webContextBlock))
        {
            sysPrompt = (string.IsNullOrWhiteSpace(sysPrompt) ? "" : sysPrompt + "\n\n") + webContextBlock;
        }
        if (!string.IsNullOrWhiteSpace(sysPrompt))
            messages.Add(new { role = "system", content = sysPrompt });

        var recent = _activeChatSession.Messages
            .TakeLast(LocalChatContextMessages - 1)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .ToList();

        while (recent.Count > 0 && recent[0].Role != "user")
            recent.RemoveAt(0);
        while (recent.Count > 0 && recent[^1].Role == "user")
            recent.RemoveAt(recent.Count - 1);

        foreach (var item in recent)
            messages.Add(new { role = item.Role, content = item.Content });

        string? dataUrl = ChatSessionService.BuildImageDataUrl(currentImagePath);
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            object[] parts =
            [
                new { type = "text", text = currentUserMessage },
                new { type = "image_url", image_url = new { url = dataUrl } }
            ];
            messages.Add(new { role = "user", content = parts });
        }
        else
        {
            messages.Add(new { role = "user", content = currentUserMessage });
        }

        return messages;
    }

    private void SetLocalChatStreamingUi(bool isStreaming)
    {
        if (LocalChatSendBtn is not null)
            LocalChatSendBtn.IsEnabled = !isStreaming;
        if (LocalChatStopBtn is not null)
        {
            LocalChatStopBtn.Visibility = isStreaming ? Visibility.Visible : Visibility.Collapsed;
            LocalChatStopBtn.IsEnabled = isStreaming;
        }
        if (LocalChatClearBtn is not null)
            LocalChatClearBtn.IsEnabled = !isStreaming;
        if (LocalChatNewBtn is not null)
            LocalChatNewBtn.IsEnabled = !isStreaming;
        if (LocalChatAttachImgBtn is not null)
            LocalChatAttachImgBtn.IsEnabled = !isStreaming;
    }

    private void ScrollLocalChatToEnd(bool force)
    {
        if (LocalChatScroll is null) return;
        if (force || LocalChatScroll.ScrollableHeight - LocalChatScroll.VerticalOffset <= 80)
            LocalChatScroll.ScrollToEnd();
    }

    private void CopyLocalChatText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowToast("Ответ скопирован", "success");
        }
        catch { }
    }

    private static string TrimErrorSnippet(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Пустой ответ сервера";
        string clean = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length > 260 ? clean[..260] + "…" : clean;
    }

    private void AppendLocalChatUserBubble(string text, string? imagePath = null)
    {
        if (LocalChatMessagesPanel is null) return;

        var contentStack = new StackPanel();
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            var imgBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
                BorderBrush = BrushFromHex("#3F3F48"),
                BorderThickness = new Thickness(1),
                MaxWidth = 220,
                MaxHeight = 160,
                Margin = new Thickness(0, 0, 0, 7),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new System.Windows.Controls.Image
                {
                    Source = LoadBitmapNoLock(imagePath),
                    Stretch = System.Windows.Media.Stretch.Uniform
                }
            };
            string capPath = imagePath;
            imgBorder.MouseLeftButtonUp += (_, _) => AiServiceManager.OpenExternal(capPath);
            contentStack.Children.Add(imgBorder);
        }

        contentStack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = BrushFromHex("#F4F4F6"),
            FontSize = 13.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        });

        var border = new Border
        {
            Background = BrushFromHex("#19191F"),
            BorderBrush = BrushFromHex("#2E2E38"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 9, 13, 9),
            Margin = new Thickness(90, 0, 0, 12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            ContextMenu = MarkdownUiRenderer.CreateCopyContextMenu(text),
            Child = contentStack
        };

        LocalChatMessagesPanel.Children.Add(border);
    }

    private Border AppendLocalChatStatusBubble(string message)
    {
        var border = new Border
        {
            Background = BrushFromHex("#121215"),
            BorderBrush = BrushFromHex("#26262E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 90, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = message,
                Foreground = BrushFromHex("#A1A1AA"),
                FontSize = 11.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        LocalChatMessagesPanel?.Children.Add(border);
        return border;
    }

    private (
        Border Bubble,
        Border ThinkContainer,
        System.Windows.Controls.Expander ThinkExpander,
        TextBlock ThinkTextBlock,
        System.Windows.Controls.ContentControl AnswerHost,
        TextBlock StreamingAnswerTb,
        System.Windows.Controls.Button CopyBtn,
        TextBlock MetaText) CreateStreamingAssistantBubble(string modelLabel)
    {
        var stack = new StackPanel();

        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var rightHeader = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(rightHeader, Dock.Right);

        var metaText = new TextBlock
        {
            Text = "",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = BrushFromHex("#71717A"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        rightHeader.Children.Add(metaText);

        var copyBtn = new System.Windows.Controls.Button
        {
            Content = "Копировать",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            Visibility = Visibility.Collapsed
        };
        if (TryFindResource("GhostButton") is Style ghostStyle)
            copyBtn.Style = ghostStyle;
        rightHeader.Children.Add(copyBtn);

        headerDock.Children.Add(rightHeader);
        headerDock.Children.Add(new TextBlock
        {
            Text = modelLabel.ToUpperInvariant(),
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#A1A1AA"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(headerDock);

        var thinkTextBlock = new TextBlock
        {
            Text = "",
            Foreground = BrushFromHex("#90909B"),
            FontSize = 12,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 5, 4, 2)
        };

        var thinkExpander = new System.Windows.Controls.Expander
        {
            Header = new TextBlock
            {
                Text = "РАЗМЫШЛЕНИЯ · THINK",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = BrushFromHex("#71717A"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold
            },
            IsExpanded = false,
            Foreground = BrushFromHex("#71717A"),
            Content = thinkTextBlock
        };

        var thinkContainer = new Border
        {
            Background = BrushFromHex("#0F0F12"),
            BorderBrush = BrushFromHex("#2E2E38"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = thinkExpander
        };
        stack.Children.Add(thinkContainer);

        var streamingAnswerTb = new TextBlock
        {
            Text = "▌",
            Foreground = BrushFromHex("#E4E4E8"),
            FontSize = 13.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap
        };

        var answerHost = new System.Windows.Controls.ContentControl
        {
            Content = streamingAnswerTb
        };
        stack.Children.Add(answerHost);

        var bubble = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = BrushFromHex("#1E1E24"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 6, 4, 12),
            Margin = new Thickness(0, 0, 24, 12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Child = stack
        };

        return (bubble, thinkContainer, thinkExpander, thinkTextBlock, answerHost, streamingAnswerTb, copyBtn, metaText);
    }

    private void AppendLocalChatVisionGuidanceBubble(string rawError)
    {
        if (LocalChatMessagesPanel is null) return;
        string activeModel = _manager.GetActiveLlmFileName() ?? "";
        string? existingMmproj = _manager.FindMatchingMmprojForModel(activeModel);
        bool isQwen36Hauhau = activeModel.Contains("Qwen3.6-35B", StringComparison.OrdinalIgnoreCase);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Vision (анализ картинок) не активирован для текущей LLM",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#F59E0B"),
            Margin = new Thickness(0, 0, 0, 5)
        });

        string explanation = !string.IsNullOrWhiteSpace(existingMmproj)
            ? $"Файл зрения `{Path.GetFileName(existingMmproj)}` **уже скачан**, но сервер `llama-server` был запущен раньше без флага `--mmproj`.\n\nНажми **«Перезапустить LLM с Vision»** ниже — и модель сразу начнёт видеть картинки!"
            : isQwen36Hauhau
                ? "Текущая модель **Qwen 3.6 35B HauhauCS** поддерживает картинки (Vision), но к ней ещё не скачан дополнительный файл зрения **`mmproj` (0.84 ГБ)**.\n\nСкачай его одной кнопкой ниже, затем перезапусти LLM — и можешь кидать любые фото и скриншоты в чат!"
                : $"Модель `{Path.GetFileNameWithoutExtension(activeModel)}` сейчас работает только с текстом (нет подходящего файла зрения `mmproj-*.gguf`).\n\nЧтобы анализировать картинки в Чате 2.0:\n• Установи готовую Vision-модель **Qwen 2.5 VL 7B Vision (5.16 ГБ)** или **Gemma 3 12B Vision (7.59 ГБ)** (скачиваются сразу с `mmproj`),\n• Либо докачай `mmproj` для **Qwen 3.6 35B (0.84 ГБ)**.";

        stack.Children.Add(MarkdownUiRenderer.Build(explanation, fontSize: 12.5, foregroundHex: "#EDF2FA"));

        var btnWrap = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        void AddActionBtn(string label, string styleKey, Action onClick)
        {
            var b = new System.Windows.Controls.Button
            {
                Content = label,
                Padding = new Thickness(11, 5, 11, 5),
                FontSize = 12,
                Margin = new Thickness(0, 0, 7, 5)
            };
            if (TryFindResource(styleKey) is Style st) b.Style = st;
            b.Click += (_, _) => onClick();
            btnWrap.Children.Add(b);
        }

        async Task InstallVisionModelInPlaceAsync(string modelId, string displayLabel)
        {
            var spec = ModelCatalog.FindById(modelId);
            if (spec is null) return;
            if (_catalogDownloading)
            {
                ShowToast("Уже идёт другая загрузка — дождись завершения внизу окна", "info");
                return;
            }

            var statusBubble = AppendLocalChatStatusBubble($"Скачиваю {displayLabel} ({spec.TotalSizeGB:0.##} ГБ)... Прогресс отображается на зелёной полосе внизу.");
            ScrollLocalChatToEnd(force: true);
            ShowToast($"Начато скачивание: {displayLabel}", "info");

            await ExecuteAssistantModelInstallAsync(spec);

            if (LocalChatMessagesPanel is not null)
                LocalChatMessagesPanel.Children.Remove(statusBubble);

            UpdateLocalChatVisionBadge();
            if (ModelCatalog.CheckInstallState(spec).allInstalled)
            {
                ShowToast($"{displayLabel} установлен! Перезапускаю LLM с Vision...", "success");
                // Automatically restart llama-server with --mmproj and retry the photo message!
                if (modelId == "llm-qwen36-35b-hauhau-vision-mmproj")
                {
                    await RestartLocalLlmForVisionAsync(autoRetryLastPhoto: true);
                }
                else
                {
                    AppendLocalChatVisionReadyCard(displayLabel);
                    ScrollLocalChatToEnd(force: true);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(existingMmproj))
        {
            string retryLbl = !string.IsNullOrWhiteSpace(_lastFailedVisionImagePath)
                ? "Перезапустить LLM с Vision и повторить фото"
                : "Перезапустить LLM с Vision (--mmproj)";
            AddActionBtn(retryLbl, "SuccessButton", async () =>
            {
                await RestartLocalLlmForVisionAsync(autoRetryLastPhoto: true);
            });
        }
        else
        {
            if (isQwen36Hauhau)
            {
                AddActionBtn("Скачать Vision mmproj для Qwen 3.6 (0.84 ГБ)", "SuccessButton", async () =>
                {
                    await InstallVisionModelInPlaceAsync("llm-qwen36-35b-hauhau-vision-mmproj", "Vision mmproj для Qwen 3.6");
                });
            }
            AddActionBtn("Установить Qwen 2.5 VL 7B Vision (5.16 ГБ)", isQwen36Hauhau ? "DarkButton" : "SuccessButton", async () =>
            {
                await InstallVisionModelInPlaceAsync("llm-qwen25-vl-7b-vision", "Qwen 2.5 VL 7B Vision");
            });
            AddActionBtn("Открыть Каталог LLM", "PrimaryButton", () =>
            {
                MainTabs.SelectedItem = CatalogTab;
                SetCatalogView("text");
            });
        }

        AddActionBtn("Спросить AI-Помощника про Vision", "DarkButton", () =>
        {
            MainTabs.SelectedItem = ChatTab;
            SetChatSubMode("assistant");
            _ = SendAssistantMessageAsync("Как включить картинки (Vision / mmproj) в Чате 2.0 для моих моделей?");
        });

        stack.Children.Add(btnWrap);

        var border = new Border
        {
            Background = BrushFromHex("#1C1710"),
            BorderBrush = BrushFromHex("#6B5218"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 48, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ContextMenu = MarkdownUiRenderer.CreateCopyContextMenu(rawError),
            Child = stack
        };

        LocalChatMessagesPanel.Children.Add(border);
    }

    private async Task RestartLocalLlmForVisionAsync(bool autoRetryLastPhoto = false)
    {
        var restartBubble = AppendLocalChatStatusBubble("Перезапускаю LLM с Vision (--mmproj)...");
        ScrollLocalChatToEnd(force: true);
        try
        {
            ShowToast("Перезапускаю LLM с Vision (--mmproj)...", "info");
            if (LocalChatStatsText is not null)
                LocalChatStatsText.Text = "Перезапуск LLM с Vision (--mmproj)...";
            await _manager.StopServiceAsync("qwen");
            await _manager.SwitchAndStartAsync("qwen", msg => Dispatcher.Invoke(() =>
            {
                StatusFooterText.Text = msg;
                if (LocalChatStatsText is not null) LocalChatStatsText.Text = msg;
                if (restartBubble.Child is TextBlock tb) tb.Text = msg;
            }));
            await RefreshStatusAsync();
            UpdateLocalChatVisionBadge();
            LocalChatMessagesPanel?.Children.Remove(restartBubble);

            if (autoRetryLastPhoto &&
                !string.IsNullOrWhiteSpace(_lastFailedVisionImagePath) &&
                File.Exists(_lastFailedVisionImagePath))
            {
                string retryPrompt = _lastFailedVisionPrompt ?? "Опиши подробно, что изображено на этой картинке.";
                string retryImg = _lastFailedVisionImagePath;
                _lastFailedVisionPrompt = null;
                _lastFailedVisionImagePath = null;

                _localChatPendingImagePath = retryImg;
                ShowToast("Vision активирован! Повторяю отправку фото...", "success");
                await SendLocalChatMessageAsync(retryPrompt);
                return;
            }

            if (LocalChatStatsText is not null)
                LocalChatStatsText.Text = "Готов к чату (Vision активен)";
            ShowToast("LLM перезапущена с Vision! Можешь отправлять фото", "success");
        }
        catch (Exception ex)
        {
            LocalChatMessagesPanel?.Children.Remove(restartBubble);
            HubDialogWindow.Show(ex.Message, "Перезапуск LLM с Vision", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AppendLocalChatVisionReadyCard(string installedLabel)
    {
        if (LocalChatMessagesPanel is null) return;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"✓ {installedLabel} установлен!",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#6AE89E"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Выбери установленную Vision-модель в списке «Локальная LLM» выше и нажми кнопку ниже, чтобы запустить её с поддержкой картинок.",
            FontSize = 12,
            Foreground = BrushFromHex("#EDF2FA"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var btn = new System.Windows.Controls.Button
        {
            Content = "Перезапустить LLM с Vision и повторить фото",
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        if (TryFindResource("SuccessButton") is Style st) btn.Style = st;
        btn.Click += async (_, _) => await RestartLocalLlmForVisionAsync(autoRetryLastPhoto: true);
        stack.Children.Add(btn);

        LocalChatMessagesPanel.Children.Add(new Border
        {
            Background = BrushFromHex("#112119"),
            BorderBrush = BrushFromHex("#265944"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 48, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = stack
        });
    }

    private void AppendLocalChatStaticAssistantBubble(string answer, string? reasoning, string headerLabel, bool showCopyButton)
    {
        if (LocalChatMessagesPanel is null) return;

        bool isError = headerLabel.Contains("Ошибка", StringComparison.OrdinalIgnoreCase);
        var stack = new StackPanel();

        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        string copyPayload = !string.IsNullOrWhiteSpace(answer) ? answer : (reasoning ?? "");

        if (showCopyButton && !string.IsNullOrWhiteSpace(copyPayload))
        {
            var copyBtn = new System.Windows.Controls.Button
            {
                Content = "Копировать",
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11
            };
            if (TryFindResource("GhostButton") is Style ghostStyle)
                copyBtn.Style = ghostStyle;
            DockPanel.SetDock(copyBtn, Dock.Right);
            copyBtn.Click += (_, _) => CopyLocalChatText(copyPayload);
            headerDock.Children.Add(copyBtn);
        }

        headerDock.Children.Add(new TextBlock
        {
            Text = headerLabel.ToUpperInvariant(),
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex(isError ? "#F87171" : "#A1A1AA"),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(headerDock);

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            var thinkExpander = new System.Windows.Controls.Expander
            {
                Header = new TextBlock
                {
                    Text = "РАЗМЫШЛЕНИЯ · THINK",
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = BrushFromHex("#71717A"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold
                },
                IsExpanded = string.IsNullOrWhiteSpace(answer),
                Foreground = BrushFromHex("#71717A"),
                Content = new TextBlock
                {
                    Text = reasoning.Trim(),
                    Foreground = BrushFromHex("#90909B"),
                    FontSize = 12,
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 5, 4, 2)
                }
            };
            stack.Children.Add(new Border
            {
                Background = BrushFromHex("#0F0F12"),
                BorderBrush = BrushFromHex("#2E2E38"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 4),
                Margin = new Thickness(0, 0, 0, 8),
                Child = thinkExpander
            });
        }

        if (!string.IsNullOrWhiteSpace(answer))
        {
            stack.Children.Add(MarkdownUiRenderer.Build(
                answer,
                fontSize: 13.5,
                foregroundHex: isError ? "#FECACA" : "#E4E4E8"));
        }

        var border = new Border
        {
            Background = isError ? BrushFromHex("#241214") : System.Windows.Media.Brushes.Transparent,
            BorderBrush = BrushFromHex(isError ? "#69282E" : "#1E1E24"),
            BorderThickness = isError ? new Thickness(1) : new Thickness(0, 0, 0, 1),
            CornerRadius = isError ? new CornerRadius(4) : new CornerRadius(0),
            Padding = isError ? new Thickness(13, 9, 13, 10) : new Thickness(4, 6, 4, 12),
            Margin = new Thickness(0, 0, 24, 12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ContextMenu = !string.IsNullOrWhiteSpace(copyPayload)
                ? MarkdownUiRenderer.CreateCopyContextMenu(copyPayload)
                : null,
            Child = stack
        };

        LocalChatMessagesPanel.Children.Add(border);
    }

    private string? ShowQuickInputDialog(string title, string prompt, string initialValue)
    {
        var win = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = BrushFromHex("#121215"),
            Foreground = BrushFromHex("#F4F4F6")
        };

        var root = new Border
        {
            BorderBrush = BrushFromHex("#30303A"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = BrushFromHex("#F4F4F6"), Margin = new Thickness(0, 0, 0, 8) });
        sp.Children.Add(new TextBlock { Text = prompt, FontSize = 12, Foreground = BrushFromHex("#A1A1AA"), Margin = new Thickness(0, 0, 0, 6) });
        var tb = new System.Windows.Controls.TextBox { Text = initialValue, FontSize = 13, Padding = new Thickness(8, 6, 8, 6) };
        sp.Children.Add(tb);

        string? result = null;
        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Отмена", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        if (TryFindResource("DarkButton") is Style ds) cancelBtn.Style = ds;
        cancelBtn.Click += (_, _) => win.DialogResult = false;

        var okBtn = new System.Windows.Controls.Button { Content = "Сохранить", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        if (TryFindResource("PrimaryButton") is Style ps) okBtn.Style = ps;
        okBtn.Click += (_, _) => { result = tb.Text; win.DialogResult = true; };

        btns.Children.Add(cancelBtn);
        btns.Children.Add(okBtn);
        sp.Children.Add(btns);
        root.Child = sp;
        win.Content = root;
        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        win.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) win.DialogResult = false; };
        return win.ShowDialog() == true ? result : null;
    }
}

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAIHub.Services;
using Forms = System.Windows.Forms;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace LocalAIHub;

/// <summary>
/// Read-Only safe Desktop System Assistant overlay (Hotkey / System Tray).
/// Can see the current screen (Vision), listen via local Whisper STT, search the web, translate, and speak replies.
/// Intentionally has ZERO file-write, shell-exec, or system-modification tools.
/// </summary>
public partial class AssistantOverlayWindow : Window
{
    private static readonly HttpClient StreamHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly AiServiceManager _manager;
    private readonly LocalVoiceService _voice;
    private readonly AssistantReminderService _reminders;
    private readonly Action<string?> _openNotesViewer;
    private readonly List<(string Role, string Content)> _history = [];
    private CancellationTokenSource? _cts;
    private string? _capturedScreenBase64;
    private bool _recording;
    private bool _busy;

    public AssistantOverlayWindow(
        AiServiceManager manager,
        LocalVoiceService voice,
        AssistantReminderService reminders,
        Action<string?> openNotesViewer)
    {
        InitializeComponent();
        _manager = manager;
        _voice = voice;
        _reminders = reminders;
        _openNotesViewer = openNotesViewer;
        ShowWelcomeHint();
    }

    public async Task OpenAndActivateAsync()
    {
        var cfg = HubConfig.Current;
        WebSearchCheck.IsChecked = cfg.DesktopAssistantAutoWebSearch;
        AutoTtsCheck.IsChecked = cfg.DesktopAssistantAutoTts;

        string targetModel = !string.IsNullOrWhiteSpace(cfg.DesktopAssistantModel)
            ? cfg.DesktopAssistantModel
            : _manager.GetActiveLlmFileName();
        bool hasVision = _manager.FindMatchingMmprojForModel(targetModel) is not null;
        ModelBadgeText.Text = string.IsNullOrWhiteSpace(targetModel)
            ? "Нет модели"
            : $"{AiServiceManager.FriendlyLlmName(targetModel)}{(hasVision ? " · Vision" : "")}";

        if (cfg.DesktopAssistantAutoCaptureScreen)
        {
            bool wasVisible = IsVisible;
            if (wasVisible) Hide();
            await Task.Delay(wasVisible ? 120 : 30);
            CaptureActiveScreen(hasVision);
        }

        PositionNearTopCenter();
        Show();
        Activate();
        Focus();

        if (string.Equals(cfg.DesktopAssistantInputMode, "voice", StringComparison.OrdinalIgnoreCase)
            && LocalVoiceService.GetStatus().WhisperReady
            && !_recording)
        {
            ToggleVoiceCapture();
        }
        else
        {
            InputBox.Focus();
        }
    }

    private void PositionNearTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2.0;
        Top = area.Top + Math.Max(36, (area.Height - Height) * 0.22);
    }

    private void CaptureActiveScreen(bool modelHasVision)
    {
        try
        {
            var cursor = Forms.Cursor.Position;
            var screen = Forms.Screen.FromPoint(cursor);
            var bounds = screen.Bounds;

            using var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            // Downscale to max 1280px for fast Vision inference
            int maxDim = 1280;
            Bitmap toEncode = bmp;
            bool disposeScaled = false;
            if (Math.Max(bmp.Width, bmp.Height) > maxDim)
            {
                double r = maxDim / (double)Math.Max(bmp.Width, bmp.Height);
                toEncode = new Bitmap(bmp, Math.Max(1, (int)(bmp.Width * r)), Math.Max(1, (int)(bmp.Height * r)));
                disposeScaled = true;
            }

            byte[] jpegBytes;
            using (var ms = new MemoryStream())
            {
                var jpgCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (jpgCodec is not null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 84L);
                    toEncode.Save(ms, jpgCodec, ep);
                }
                else
                {
                    toEncode.Save(ms, ImageFormat.Jpeg);
                }
                jpegBytes = ms.ToArray();
            }
            if (disposeScaled) toEncode.Dispose();

            _capturedScreenBase64 = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);

            var preview = new BitmapImage();
            using (var msPrev = new MemoryStream(jpegBytes))
            {
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.StreamSource = msPrev;
                preview.EndInit();
                preview.Freeze();
            }
            ScreenPreviewImg.Source = preview;
            ScreenStatusTitle.Text = modelHasVision
                ? $"Скриншот экрана прикреплён ({bounds.Width}×{bounds.Height} • Vision ✓)"
                : $"Скриншот снят ({bounds.Width}×{bounds.Height}) — выбранная модель без Vision";
            ScreenStatusSub.Text = modelHasVision
                ? "Ассистент видит содержимое экрана (только чтение)"
                : "Выбери Vision-модель (с mmproj) в Настройках → Системный ассистент, чтобы он видел экран";
            ScreenStripBorder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось снять экран: " + ex.Message;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideOverlay();
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => HideOverlay();

    private void HideOverlay()
    {
        if (_recording)
        {
            _voice.CancelRecording();
            _recording = false;
            MicBtnText.Text = "Голос";
        }
        // Release screenshot base64 & preview bitmap while hidden
        _capturedScreenBase64 = null;
        ScreenPreviewImg.Source = null;
        ScreenStripBorder.Visibility = Visibility.Collapsed;
        Hide();
    }

    private async void CaptureScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        string targetModel = !string.IsNullOrWhiteSpace(HubConfig.Current.DesktopAssistantModel)
            ? HubConfig.Current.DesktopAssistantModel
            : _manager.GetActiveLlmFileName();
        bool hasVision = _manager.FindMatchingMmprojForModel(targetModel) is not null;
        Hide();
        await Task.Delay(140);
        CaptureActiveScreen(hasVision);
        Show();
        Activate();
        InputBox.Focus();
    }

    private void RemoveScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        _capturedScreenBase64 = null;
        ScreenStripBorder.Visibility = Visibility.Collapsed;
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        MessagesPanel.Children.Clear();
        ShowWelcomeHint();
    }

    private void OpenNotesBtn_Click(object sender, RoutedEventArgs e)
    {
        _openNotesViewer(null);
    }

    private void QuickNoteChip_Click(object sender, RoutedEventArgs e)
    {
        string cur = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(cur))
        {
            InputBox.Text = "Сделай заметку: ";
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }
        else
        {
            if (!cur.StartsWith("Сделай заметку", StringComparison.OrdinalIgnoreCase))
                InputBox.Text = "Сделай заметку: " + cur;
            _ = SendAsync();
        }
    }

    private void QuickReminderChip_Click(object sender, RoutedEventArgs e)
    {
        string cur = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(cur))
        {
            InputBox.Text = "Напомни через 15 минут: ";
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }
        else
        {
            if (!cur.StartsWith("Напомни", StringComparison.OrdinalIgnoreCase))
                InputBox.Text = "Напомни " + cur;
            _ = SendAsync();
        }
    }

    private void ShowWelcomeHint()
    {
        MessagesPanel.Children.Clear();
        MessagesPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(17, 17, 20)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(38, 38, 45)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock
            {
                Text = "Локальный ассистент ПК (безопасный режим): диктуй «Сделай заметку ...» (.md в Документы\\Notes), «Напомни через 20 минут ...», «Найди в интернете ...» или спрашивай про текущий экран. Без доступа к системе и сторонним файлам.",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)),
                TextWrapping = TextWrapping.Wrap
            }
        });
    }

    private void QuickChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string prompt)
        {
            string cur = InputBox.Text.Trim();
            InputBox.Text = string.IsNullOrEmpty(cur) ? prompt.TrimEnd(':') : $"{prompt}\n{cur}";
            _ = SendAsync();
        }
    }

    private void QuickWebChip_Click(object sender, RoutedEventArgs e)
    {
        WebSearchCheck.IsChecked = true;
        if (!string.IsNullOrWhiteSpace(InputBox.Text))
            _ = SendAsync();
        else
            InputBox.Focus();
    }

    private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void MicBtn_Click(object sender, RoutedEventArgs e) => ToggleVoiceCapture();

    private async void ToggleVoiceCapture()
    {
        if (_busy) return;
        if (!_recording)
        {
            var st = LocalVoiceService.GetStatus();
            if (!st.WhisperReady)
            {
                StatusText.Text = "Установи Whisper в «Система → Настройки → Компоненты и голос»";
                return;
            }
            try
            {
                _voice.StartRecording();
                _recording = true;
                MicBtnText.Text = "Стоп…";
                StatusText.Text = "Идёт запись с микрофона — нажми «Стоп…» для распознавания";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка микрофона: " + ex.Message;
            }
        }
        else
        {
            _recording = false;
            MicBtnText.Text = "Голос";
            StatusText.Text = "Распознаю речь локально (Whisper)...";
            try
            {
                string wav = _voice.StopRecording();
                string text = await _voice.TranscribeWavAsync(wav, "ru");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    InputBox.Text = string.IsNullOrWhiteSpace(InputBox.Text) ? text : $"{InputBox.Text.Trim()} {text}";
                    StatusText.Text = "Распознано локально — отправляю...";
                    await SendAsync();
                }
                else
                {
                    StatusText.Text = "Речь не распознана — попробуй ещё раз";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка Whisper: " + ex.Message;
            }
        }
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void StopBtn_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task SendAsync()
    {
        if (_busy) return;
        string userText = InputBox.Text.Trim();
        string? screenB64 = _capturedScreenBase64;
        if (string.IsNullOrEmpty(userText) && string.IsNullOrEmpty(screenB64)) return;
        if (string.IsNullOrEmpty(userText)) userText = "Опиши кратко, что сейчас на экране.";

        InputBox.Text = "";
        _busy = true;
        SendBtn.Visibility = Visibility.Collapsed;
        StopBtn.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        if (_history.Count == 0) MessagesPanel.Children.Clear();
        AddUserBubble(userText, !string.IsNullOrEmpty(screenB64));

        var (asstContainer, bodyHost, metaText, srcHost) = AddAssistantBubblePlaceholder();
        MessagesScroll.ScrollToEnd();

        try
        {
            var intent = AssistantActionRouter.RouteUserInput(userText);

            // ---- SAFE ACTION 1: LOCAL REMINDER / TIMER (Zero-latency, no GPU required!) ----
            if (intent.Kind == AssistantIntentKind.SetReminder && intent.ReminderFireAt.HasValue)
            {
                var item = _reminders.AddReminder(intent.ExtractedPayload, intent.ReminderFireAt.Value);
                RenderReminderSavedCard(asstContainer, bodyHost, metaText, item);
                StatusText.Text = $"Напоминание установлено на {item.FireAtLocal:HH:mm}";
                _history.Add(("user", userText));
                _history.Add(("assistant", $"Напоминание установлено на {item.FireAtLocal:dd.MM HH:mm}: {item.Text}"));
                if (AutoTtsCheck.IsChecked == true)
                    _ = SpeakReplyAsync($"Хорошо, напомню в {item.FireAtLocal:HH:mm}: {item.Text}");
                return;
            }

            // ---- SAFE ACTION 2: MARKDOWN NOTE (Documents\Notes\*.md) ----
            if (intent.Kind == AssistantIntentKind.SaveNote)
            {
                StatusText.Text = "Оформление заметки...";
                metaText.Text = "Сохранение в Notes...";
                var structured = await StructureNoteWithRunningLlmOrFallbackAsync(intent.ExtractedPayload, _cts.Token);
                var saved = AssistantNotesService.SaveNote(structured.Title, structured.BodyMarkdown, structured.Tags);
                RenderNoteSavedCard(asstContainer, bodyHost, metaText, saved, structured.UsedLlm);
                StatusText.Text = $"Заметка сохранена: {saved.FileName}";
                _history.Add(("user", userText));
                _history.Add(("assistant", $"Заметка «{saved.Title}» сохранена в {saved.FilePath}"));
                if (AutoTtsCheck.IsChecked == true)
                    _ = SpeakReplyAsync($"Заметка {saved.Title} сохранена.");
                return;
            }

            // ---- SAFE ACTION 3 & 4: WEB SEARCH + READ-ONLY VISION / CHAT ----
            bool useWeb = WebSearchCheck.IsChecked == true || intent.Kind == AssistantIntentKind.WebSearch;
            string searchQuery = intent.Kind == AssistantIntentKind.WebSearch && !string.IsNullOrWhiteSpace(intent.ExtractedPayload)
                ? intent.ExtractedPayload
                : userText;

            DesktopSearchResponse? searchResp = null;
            string webBlock = "";
            if (useWeb && !string.IsNullOrWhiteSpace(searchQuery))
            {
                StatusText.Text = "Поиск в интернете...";
                metaText.Text = "Поиск в интернете...";
                searchResp = await DesktopWebSearchService.SearchAsync(searchQuery, _cts.Token);
                if (searchResp.Results.Count > 0)
                {
                    webBlock = searchResp.BuildPromptContext();
                    RenderSourcesExpander(srcHost, searchResp);
                }
            }

            string preferredModel = HubConfig.Current.DesktopAssistantModel;
            var (endpointUrl, modelFile, hasVision) = await _manager.EnsureLlmModelRunningAsync(
                preferredModel,
                msg => Dispatcher.Invoke(() => { StatusText.Text = msg; metaText.Text = msg; }));

            ModelBadgeText.Text = $"{AiServiceManager.FriendlyLlmName(modelFile)}{(hasVision ? " · Vision" : "")}";

            // Safe-Tools system prompt (strictly informational, zero system-modification capability)
            string sysPrompt =
                $"Ты приватный локальный ассистент пользователя на ПК (Brazhnichenko Hub). Текущая дата: {DateTime.Now:yyyy-MM-dd (dddd)}.\n" +
                "Работаешь в безопасном режиме: ты можешь объяснять содержимое экрана, переводить текст, искать факты и отвечать на вопросы. У тебя нет доступа к терминалу, настройкам или сторонним файлам ПК.\n" +
                "Отвечай чётко, компактно, структурированно и на языке пользователя.";
            if (!string.IsNullOrEmpty(webBlock))
                sysPrompt += "\n\n" + webBlock;

            var msgs = new List<object> { new { role = "system", content = sysPrompt } };
            foreach (var h in _history.TakeLast(6))
                msgs.Add(new { role = h.Role, content = h.Content });

            if (!string.IsNullOrEmpty(screenB64) && hasVision)
            {
                msgs.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userText },
                        new { type = "image_url", image_url = new { url = screenB64 } }
                    }
                });
            }
            else
            {
                msgs.Add(new { role = "user", content = userText });
            }

            string payload = JsonSerializer.Serialize(new
            {
                messages = msgs,
                temperature = 0.6,
                stream = true
            });

            StatusText.Text = "Генерация ответа...";
            metaText.Text = "Генерация...";
            var sw = Stopwatch.StartNew();
            int tok = 0;
            var fullText = new StringBuilder();
            long lastRenderMs = 0;

            using var req = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var resp = await StreamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!_cts.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                    {
                        var delta = ch[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                        {
                            string piece = cEl.GetString() ?? "";
                            if (piece.Length > 0)
                            {
                                fullText.Append(piece);
                                tok++;
                                if (sw.ElapsedMilliseconds - lastRenderMs > 100)
                                {
                                    lastRenderMs = sw.ElapsedMilliseconds;
                                    bodyHost.Child = MarkdownUiRenderer.Build(fullText.ToString(), 13);
                                    MessagesScroll.ScrollToEnd();
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            sw.Stop();
            string finalReply = fullText.ToString().Trim();
            bodyHost.Child = MarkdownUiRenderer.Build(string.IsNullOrEmpty(finalReply) ? "(пустой ответ)" : finalReply, 13);
            double tps = tok / Math.Max(0.15, sw.Elapsed.TotalSeconds);
            metaText.Text = $"{tps:F1} tok/s · {tok} ток · {AiServiceManager.FriendlyLlmName(modelFile)}";
            StatusText.Text = "Готово • Enter — отправить • Esc — скрыть";

            AddCopyAndSpeakButtons(asstContainer, finalReply);

            _history.Add(("user", userText));
            _history.Add(("assistant", finalReply));

            // Consume captured screenshot after first message so follow-ups don't re-send stale image
            _capturedScreenBase64 = null;
            ScreenStripBorder.Visibility = Visibility.Collapsed;

            if (AutoTtsCheck.IsChecked == true && !string.IsNullOrWhiteSpace(finalReply))
                _ = SpeakReplyAsync(finalReply);
        }
        catch (OperationCanceledException)
        {
            metaText.Text = "Остановлено";
            StatusText.Text = "Генерация остановлена";
        }
        catch (Exception ex)
        {
            bodyHost.Child = new TextBlock
            {
                Text = "Ошибка: " + ex.Message,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(252, 165, 165)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5
            };
            metaText.Text = "Ошибка";
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SendBtn.Visibility = Visibility.Visible;
            StopBtn.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
            MessagesScroll.ScrollToEnd();
        }
    }

    private void AddUserBubble(string text, bool withScreen)
    {
        var sp = new StackPanel();
        if (withScreen)
        {
            sp.Children.Add(new TextBlock
            {
                Text = "[СКРИНШОТ ЭКРАНА]",
                FontSize = 10.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 82, 27)),
                Margin = new Thickness(0, 0, 0, 3)
            });
        }
        sp.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 246)),
            TextWrapping = TextWrapping.Wrap
        });
        MessagesPanel.Children.Add(new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            MaxWidth = 520,
            Background = new SolidColorBrush(MediaColor.FromRgb(26, 26, 32)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(48, 48, 58)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = sp
        });
    }

    private (StackPanel Container, Border BodyHost, TextBlock MetaText, StackPanel SourcesHost) AddAssistantBubblePlaceholder()
    {
        var stack = new StackPanel();
        var srcHost = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        var bodyHost = new Border
        {
            Child = new TextBlock { Text = "Подготовка...", FontSize = 12.5, Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)) }
        };
        var metaText = new TextBlock
        {
            Text = "...",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(113, 113, 122)),
            Margin = new Thickness(0, 6, 0, 0)
        };
        stack.Children.Add(srcHost);
        stack.Children.Add(bodyHost);
        stack.Children.Add(metaText);

        MessagesPanel.Children.Add(new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = MediaBrushes.Transparent,
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(30, 30, 36)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 6, 4, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        });

        return (stack, bodyHost, metaText, srcHost);
    }

    private static void RenderSourcesExpander(StackPanel host, DesktopSearchResponse sr)
    {
        host.Children.Clear();
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < sr.Results.Count; i++)
        {
            var r = sr.Results[i];
            var link = new Hyperlink(new Run($"[{i + 1}] {r.Title}"))
            {
                NavigateUri = Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u : null,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(228, 228, 232))
            };
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            };
            var tb = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2) };
            tb.Inlines.Add(link);
            list.Children.Add(tb);
        }
        host.Children.Add(new Expander
        {
            Header = $"ИСТОЧНИКИ ({sr.Results.Count}) · {sr.Provider.ToUpperInvariant()}",
            Style = System.Windows.Application.Current?.TryFindResource("StudioExpanderStyle") as Style,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Content = list
        });
    }

    private void AddCopyAndSpeakButtons(StackPanel container, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var copyBtn = new System.Windows.Controls.Button
        {
            Content = "Копировать",
            Style = (Style)FindResource("DarkButton"),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0)
        };
        copyBtn.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(text); StatusText.Text = "Ответ скопирован в буфер"; } catch { }
        };
        var speakBtn = new System.Windows.Controls.Button
        {
            Content = "Озвучить",
            Style = (Style)FindResource("DarkButton"),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11
        };
        speakBtn.Click += (_, _) => _ = SpeakReplyAsync(text);
        row.Children.Add(copyBtn);
        row.Children.Add(speakBtn);
        container.Children.Add(row);
    }

    private async Task<(string Title, string BodyMarkdown, string[] Tags, bool UsedLlm)> StructureNoteWithRunningLlmOrFallbackAsync(
        string payload,
        CancellationToken ct)
    {
        var fallback = AssistantActionRouter.BuildFallbackNoteStructure(payload);
        // Only invoke LLM structuring if llama-server is already warm AND payload benefits from formatting
        try
        {
            var qwenSvc = _manager.GetService("qwen");
            if (payload.Length >= 40 && await _manager.IsRunningAsync(qwenSvc))
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                string reqJson = JsonSerializer.Serialize(new
                {
                    messages = new object[]
                    {
                        new { role = "system", content = AssistantActionRouter.BuildNoteStructuringSystemPrompt() },
                        new { role = "user", content = payload }
                    },
                    temperature = 0.2,
                    top_p = 0.9,
                    presence_penalty = 0.0,
                    repeat_penalty = 1.05,
                    dry_multiplier = 0.0,
                    max_tokens = 512,
                    stream = false
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/chat/completions")
                {
                    Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
                };
                using var resp = await StreamHttp.SendAsync(req, timeoutCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
                    string? reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    var parsed = AssistantActionRouter.ParseStructuredNoteOrFallback(reply, payload);
                    return (parsed.Title, parsed.BodyMarkdown, parsed.Tags, true);
                }
            }
        }
        catch { }

        return (fallback.Title, fallback.BodyMarkdown, fallback.Tags, false);
    }

    private void RenderNoteSavedCard(StackPanel container, Border bodyHost, TextBlock metaText, AssistantNoteEntry saved, bool usedLlm)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(17, 24, 20)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(38, 89, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 10, 12, 10)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "ЗАМЕТКА СОХРАНЕНА (.MD)",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(52, 211, 153))
        });
        sp.Children.Add(new TextBlock
        {
            Text = saved.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 246)),
            Margin = new Thickness(0, 4, 0, 3),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(saved.Preview))
        {
            sp.Children.Add(new TextBlock
            {
                Text = saved.Preview,
                FontSize = 12,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(180, 180, 188)),
                TextWrapping = TextWrapping.Wrap
            });
        }
        string tagsText = saved.Tags.Length > 0 ? string.Join(" ", saved.Tags.Select(t => "#" + t)) + " · " : "";
        sp.Children.Add(new TextBlock
        {
            Text = $"{tagsText}{saved.FilePath}",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(126, 126, 135)),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        card.Child = sp;
        bodyHost.Child = card;

        metaText.Text = usedLlm
            ? $"Оформлено локальной LLM · {saved.FileName}"
            : $"Сохранено мгновенно · {saved.FileName}";

        var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var viewBtn = new System.Windows.Controls.Button
        {
            Content = "Открыть в Заметках",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 6, 0)
        };
        viewBtn.Click += (_, _) => _openNotesViewer(saved.FilePath);

        var openFileBtn = new System.Windows.Controls.Button
        {
            Content = "Открыть .md ↗",
            Style = (Style)FindResource("DarkButton"),
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 6, 0)
        };
        openFileBtn.Click += (_, _) => AiServiceManager.OpenExternal(saved.FilePath);

        btnRow.Children.Add(viewBtn);
        btnRow.Children.Add(openFileBtn);
        container.Children.Add(btnRow);
    }

    private void RenderReminderSavedCard(StackPanel container, Border bodyHost, TextBlock metaText, AssistantReminderItem item)
    {
        var delta = item.FireAtLocal - DateTime.Now;
        string eta = delta.TotalHours >= 1
            ? $"через {(int)delta.TotalHours} ч {delta.Minutes} мин"
            : delta.TotalMinutes >= 1
                ? $"через {Math.Max(1, (int)Math.Round(delta.TotalMinutes))} мин"
                : $"через {Math.Max(1, (int)Math.Round(delta.TotalSeconds))} сек";

        var card = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(26, 20, 14)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(255, 82, 27)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 10, 12, 10)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = $"НАПОМИНАНИЕ · {item.FireAtLocal:HH:mm} ({eta})",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 82, 27))
        });
        sp.Children.Add(new TextBlock
        {
            Text = item.Text,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 246)),
            Margin = new Thickness(0, 4, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"Сработает {item.FireAtLocal:dd.MM.yyyy в HH:mm} — придёт системное уведомление Windows со звуком.",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)),
            TextWrapping = TextWrapping.Wrap
        });
        card.Child = sp;
        bodyHost.Child = card;
        metaText.Text = $"Локальный таймер #{item.Id} · 0% GPU";

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Отменить напоминание",
            Style = (Style)FindResource("DarkButton"),
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        cancelBtn.Click += (_, _) =>
        {
            if (_reminders.CancelReminder(item.Id))
            {
                cancelBtn.Content = "Отменено";
                cancelBtn.IsEnabled = false;
                metaText.Text = "Напоминание отменено";
                StatusText.Text = "Напоминание отменено";
            }
        };
        container.Children.Add(cancelBtn);
    }

    private async Task SpeakReplyAsync(string raw)
    {
        try
        {
            string clean = System.Text.RegularExpressions.Regex.Replace(raw, @"```[\s\S]*?```", " блок кода ");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"[*_`#>]+", "").Trim();
            if (clean.Length > 600) clean = clean[..600];
            var st = LocalVoiceService.GetStatus();
            var voiceOpt = st.PiperReady
                ? new TtsVoiceOption { Id = "piper", DisplayName = "Piper RU", Engine = "piper", ModelPath = st.PiperModelPath }
                : new TtsVoiceOption { Id = "sapi", DisplayName = "Windows TTS", Engine = "sapi" };
            string wav = await _voice.SynthesizeToWavAsync(clean, voiceOpt);
            if (File.Exists(wav))
            {
                using var player = new System.Media.SoundPlayer(wav);
                player.Play();
            }
        }
        catch { }
    }
}

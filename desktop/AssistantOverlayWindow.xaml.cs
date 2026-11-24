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
    private readonly List<(string Role, string Content)> _history = [];
    private CancellationTokenSource? _cts;
    private string? _capturedScreenBase64;
    private bool _recording;
    private bool _busy;

    public AssistantOverlayWindow(AiServiceManager manager, LocalVoiceService voice)
    {
        InitializeComponent();
        _manager = manager;
        _voice = voice;
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

    private void ShowWelcomeHint()
    {
        MessagesPanel.Children.Clear();
        MessagesPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(20, 26, 38)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(37, 48, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock
            {
                Text = "Локальный безопасный ассистент ПК (режим «Только чтение»). Спроси про текущий экран, попроси перевести текст или найти актуальную информацию в интернете — ничего на компьютере не изменяется.",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(184, 195, 217)),
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
            bool useWeb = WebSearchCheck.IsChecked == true;
            DesktopSearchResponse? searchResp = null;
            string webBlock = "";
            if (useWeb && !string.IsNullOrWhiteSpace(userText))
            {
                StatusText.Text = "Поиск в интернете...";
                metaText.Text = "Поиск в интернете...";
                searchResp = await DesktopWebSearchService.SearchAsync(userText, _cts.Token);
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

            // Read-Only system prompt (strictly informational, zero system-modification capability)
            string sysPrompt =
                $"Ты приватный локальный ассистент пользователя на ПК (Brazhnichenko Hub). Текущая дата: {DateTime.Now:yyyy-MM-dd (dddd)}.\n" +
                "Работаешь в безопасном режиме «Только чтение»: ты можешь объяснять содержимое экрана, переводить текст, искать факты и отвечать на вопросы, но НЕ имеешь права и инструментов изменять файлы или настройки компьютера.\n" +
                "Отвечай чётко, структурированно и на языке пользователя.";
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
                Text = "[Скриншот экрана]",
                FontSize = 11,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(125, 211, 252)),
                Margin = new Thickness(0, 0, 0, 3)
            });
        }
        sp.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = MediaBrushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        MessagesPanel.Children.Add(new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            MaxWidth = 520,
            Background = new SolidColorBrush(MediaColor.FromRgb(33, 49, 84)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(58, 91, 163)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 3, 12),
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
            Child = new TextBlock { Text = "Подготовка...", FontSize = 12.5, Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 160, 184)) }
        };
        var metaText = new TextBlock
        {
            Text = "...",
            FontSize = 11,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(143, 155, 179)),
            Margin = new Thickness(0, 6, 0, 0)
        };
        stack.Children.Add(srcHost);
        stack.Children.Add(bodyHost);
        stack.Children.Add(metaText);

        MessagesPanel.Children.Add(new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            MaxWidth = 600,
            Background = new SolidColorBrush(MediaColor.FromRgb(21, 25, 35)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(34, 41, 58)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 12, 3),
            Padding = new Thickness(12, 9, 12, 8),
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
                Foreground = new SolidColorBrush(MediaColor.FromRgb(147, 197, 253))
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
            Header = $"Источники ({sr.Results.Count}) · {sr.Provider}",
            Style = System.Windows.Application.Current?.TryFindResource("StudioExpanderStyle") as Style,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(125, 211, 252)),
            FontSize = 11.5,
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

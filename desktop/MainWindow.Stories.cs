using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private readonly ComfyStoryClient _storyClient = new();
    private List<PhotoStoryStage> _photoStages = [];
    private VideoStoryProject _videoProject = new();
    private List<VideoStoryStage> _videoStages => _videoProject.Stages;
    private GeneratedStoryScenario? _lastScenario;
    private bool _storyBusy;

    private CancellationTokenSource? _genCts;
    private Stopwatch? _genStopwatch;
    private DispatcherTimer? _genClockTimer;
    private double? _lastEtaBaseSec;
    private double _lastEtaStampSec;
    private int _genHudVersion;
    private bool _genPulseBright = true;

    private static string VideoStoryFile => Path.Combine(ComfyStoryClient.StoriesRoot, "video_story.json");
    private static string VideoFramesDir => Path.Combine(ComfyStoryClient.StoriesRoot, "video_frames");

    private void InitStories()
    {
        Directory.CreateDirectory(ComfyStoryClient.StoriesRoot);
        LoadPhotoStoryState();
        LoadVideoStoryState();
        RenderPhotoStages();
        RenderVideoStages();
    }

    private void LoadVideoStoryState()
    {
        _videoProject = new VideoStoryProject();
        try
        {
            if (File.Exists(VideoStoryFile))
            {
                string raw = File.ReadAllText(VideoStoryFile).Trim();
                if (raw.StartsWith('['))
                {
                    _videoProject.Stages = JsonSerializer.Deserialize<List<VideoStoryStage>>(raw) ?? [];
                }
                else
                {
                    _videoProject = JsonSerializer.Deserialize<VideoStoryProject>(raw) ?? new VideoStoryProject();
                }
            }
        }
        catch { _videoProject = new VideoStoryProject(); }

        if (_videoProject.Stages.Count == 0)
        {
            _videoProject.Stages =
            [
                new VideoStoryStage
                {
                    Index = 1,
                    Prompt = "Smooth cinematic tracking shot of the woman walking along the sunlit street, gentle breeze moving her hair, warm golden hour lighting"
                },
                new VideoStoryStage
                {
                    Index = 2,
                    Prompt = "Continuation from the previous frame: she slows down, turns her head toward a cozy café window with a gentle smile as warm light glows from inside, smooth slow camera pan"
                }
            ];
        }
    }

    private void SaveVideoStoryState()
    {
        for (int i = 0; i < _videoStages.Count; i++) _videoStages[i].Index = i + 1;
        Directory.CreateDirectory(ComfyStoryClient.StoriesRoot);
        File.WriteAllText(VideoStoryFile, JsonSerializer.Serialize(_videoProject, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (int w, int h) ParseSizeTag(System.Windows.Controls.ComboBox combo, int defW, int defH)
    {
        string tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? $"{defW}x{defH}";
        var parts = tag.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
            ? (w, h)
            : (defW, defH);
    }

    private static BitmapImage? LoadBitmapNoLock(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 360;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private async Task EnsureComfyReadyForStoryAsync()
    {
        var comfy = _manager.GetService("comfyui");
        if (!await _manager.IsRunningAsync(comfy))
        {
            await _manager.SwitchAndStartAsync("comfyui", msg => Dispatcher.Invoke(() =>
            {
                StatusFooterText.Text = msg;
                if (GenProgressCard.Visibility == Visibility.Visible)
                    GenStageText.Text = msg;
            }));
            await RefreshStatusAsync();
        }
    }

    private static string FormatDuration(double totalSec)
    {
        int sec = Math.Max(0, (int)Math.Round(totalSec));
        int m = sec / 60;
        int s = sec % 60;
        return $"{m:00}:{s:00}";
    }

    private CancellationToken BeginGenerationHud(string title)
    {
        _genHudVersion++;
        _genCts?.Dispose();
        _genCts = new CancellationTokenSource();
        _genStopwatch = Stopwatch.StartNew();
        _lastEtaBaseSec = null;
        _lastEtaStampSec = 0;

        GenTitleText.Text = title;
        GenStageText.Text = "Подготовка к запуску...";
        GenPercentText.Text = "0%";
        GenPercentText.Foreground = BrushFromHex("#FF521B");
        GenProgressBar.IsIndeterminate = true;
        GenProgressBar.Value = 0;
        GenProgressBar.Foreground = BrushFromHex("#FF521B");
        GenStepText.Text = "—";
        GenSpeedText.Text = "замер...";
        GenElapsedText.Text = "00:00";
        GenEtaText.Text = "расчёт...";
        CancelGenerationBtn.Visibility = Visibility.Visible;
        CancelGenerationBtn.IsEnabled = true;
        GenPulseDot.Fill = BrushFromHex("#FF521B");
        GenPulseDot.Opacity = 1.0;
        GenProgressCard.BorderBrush = BrushFromHex("#3F3F48");
        GenProgressCard.Visibility = Visibility.Visible;

        _pollTimer.Interval = TimeSpan.FromSeconds(1.2);
        _genClockTimer ??= CreateGenClockTimer();
        _genClockTimer.Start();
        return _genCts.Token;
    }

    private DispatcherTimer CreateGenClockTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (_genStopwatch is null || !_genStopwatch.IsRunning) return;
            double elapsed = _genStopwatch.Elapsed.TotalSeconds;
            GenElapsedText.Text = FormatDuration(elapsed);
            _genPulseBright = !_genPulseBright;
            GenPulseDot.Opacity = _genPulseBright ? 1.0 : 0.35;

            if (_lastEtaBaseSec.HasValue)
            {
                double remain = Math.Max(1, _lastEtaBaseSec.Value - (elapsed - _lastEtaStampSec));
                GenEtaText.Text = "~" + FormatDuration(remain);
            }
        };
        return t;
    }

    private void UpdateGenerationHud(GenerationProgressInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            GenTitleText.Text = info.Title;
            GenStageText.Text = info.StageLabel;
            GenProgressBar.IsIndeterminate = info.IsIndeterminate;
            GenProgressBar.Value = Math.Clamp(info.Percent, 0, 100);
            GenPercentText.Text = $"{info.Percent:0}%";
            GenStepText.Text = info.TotalSteps > 0 ? $"{info.CurrentStep} / {info.TotalSteps}" : "—";

            if (info.SecondsPerStep is double sps && sps > 0)
            {
                double ips = 1.0 / sps;
                GenSpeedText.Text = sps >= 1.0
                    ? $"{sps:F1} с/шаг ({ips:F2} шаг/с)"
                    : $"{ips:F2} шаг/с ({sps:F2} с/шаг)";
            }

            if (_genStopwatch is not null)
                GenElapsedText.Text = FormatDuration(_genStopwatch.Elapsed.TotalSeconds);

            if (info.EtaSeconds is double eta)
            {
                _lastEtaBaseSec = eta;
                _lastEtaStampSec = _genStopwatch?.Elapsed.TotalSeconds ?? info.ElapsedSeconds;
                GenEtaText.Text = eta <= 0.5 ? "почти готово" : "~" + FormatDuration(eta);
            }

            StatusFooterText.Text = $"{info.Title} • {info.Percent:0}% — {info.StageLabel}";
        });
    }

    private async void EndGenerationHud(bool success, string summary)
    {
        int myVer = _genHudVersion;
        _genClockTimer?.Stop();
        _genStopwatch?.Stop();
        _pollTimer.Interval = TimeSpan.FromSeconds(2.5);
        CancelGenerationBtn.Visibility = Visibility.Collapsed;
        GenPulseDot.Opacity = 1.0;

        if (success)
        {
            GenProgressBar.IsIndeterminate = false;
            GenProgressBar.Value = 100;
            GenProgressBar.Foreground = BrushFromHex("#34D399");
            GenPercentText.Text = "100%";
            GenPercentText.Foreground = BrushFromHex("#34D399");
            GenPulseDot.Fill = BrushFromHex("#34D399");
            GenProgressCard.BorderBrush = BrushFromHex("#265944");
            GenStageText.Text = summary;
            GenEtaText.Text = "00:00";
            if (_genStopwatch is not null)
                GenElapsedText.Text = FormatDuration(_genStopwatch.Elapsed.TotalSeconds);

            await Task.Delay(7000);
            if (_genHudVersion == myVer && !_storyBusy)
                GenProgressCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            GenProgressBar.IsIndeterminate = false;
            GenPulseDot.Fill = BrushFromHex("#EF4444");
            GenProgressCard.BorderBrush = BrushFromHex("#69282E");
            GenStageText.Text = summary;
            await Task.Delay(3000);
            if (_genHudVersion == myVer && !_storyBusy)
                GenProgressCard.Visibility = Visibility.Collapsed;
        }
    }

    private async void CancelGenerationBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelGenerationBtn.IsEnabled = false;
        GenStageText.Text = "Отмена генерации в ComfyUI...";
        StatusFooterText.Text = "Отправлена команда остановки в ComfyUI...";
        try
        {
            _genCts?.Cancel();
            await _storyClient.InterruptAsync();
        }
        catch { }
    }

    // ==================== VIDEO STORY ====================

    private Border BuildVideoStarterCard()
    {
        bool hasImg = !string.IsNullOrEmpty(_videoProject.StarterImagePath) && File.Exists(_videoProject.StarterImagePath);
        bool active = _videoProject.UseStarterFrame && hasImg;

        var card = new Border
        {
            Background = BrushFromHex("#121215"),
            BorderBrush = active ? BrushFromHex("#FF521B") : BrushFromHex("#28282E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };

        // Header
        var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        hdr.Children.Add(new TextBlock
        {
            Text = "ШАГ 0 — СТАРТОВЫЙ КАДР / ЯКОРЬ ПЕРСОНАЖА",
            FontSize = 11.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#FF521B")
        });

        var useChk = new System.Windows.Controls.CheckBox
        {
            Content = "Передать этот кадр в Этап 1 (I2V)",
            IsChecked = _videoProject.UseStarterFrame,
            Foreground = BrushFromHex("#D4D4D8"),
            FontSize = 11.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        useChk.Checked += (_, _) => { _videoProject.UseStarterFrame = true; SaveVideoStoryState(); RenderVideoStages(); };
        useChk.Unchecked += (_, _) => { _videoProject.UseStarterFrame = false; SaveVideoStoryState(); RenderVideoStages(); };
        hdr.Children.Add(useChk);
        left.Children.Add(hdr);

        // Prompt box
        var promptBox = new System.Windows.Controls.TextBox
        {
            Text = _videoProject.StarterPrompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 56,
            Background = BrushFromHex("#09090B"),
            Foreground = BrushFromHex("#F4F4F6"),
            BorderBrush = BrushFromHex("#28282E"),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 8)
        };
        promptBox.TextChanged += (_, _) => { _videoProject.StarterPrompt = promptBox.Text; SaveVideoStoryState(); };
        left.Children.Add(promptBox);

        // Controls row
        var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        var engineCombo = new System.Windows.Controls.ComboBox
        {
            Width = 178,
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        engineCombo.Items.Add(new ComboBoxItem { Content = "FLUX 3 Klein (16 шагов)", Tag = "flux3" });
        engineCombo.Items.Add(new ComboBoxItem { Content = "Z-Image Turbo (8 шагов)", Tag = "zimage" });
        engineCombo.Items.Add(new ComboBoxItem { Content = "Qwen-Image 2.1 (8 шагов)", Tag = "qwen21" });
        engineCombo.SelectedIndex = _videoProject.StarterEngine switch { "zimage" => 1, "qwen21" => 2, _ => 0 };
        engineCombo.SelectionChanged += (_, _) =>
        {
            _videoProject.StarterEngine = (engineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "flux3";
            SaveVideoStoryState();
        };
        controls.Children.Add(engineCombo);

        var genBtn = new System.Windows.Controls.Button
        {
            Content = hasImg ? "Перегенерировать кадр" : "Сгенерировать кадр",
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(0, 0, 7, 0),
            IsEnabled = !_storyBusy
        };
        genBtn.Click += async (_, _) => await GenerateVideoStarterFrameAsync();
        controls.Children.Add(genBtn);

        var pickBtn = new System.Windows.Controls.Button
        {
            Content = "Выбрать фото с диска",
            Style = (Style)FindResource("DarkButton"),
            Margin = new Thickness(0, 0, 7, 0),
            IsEnabled = !_storyBusy
        };
        pickBtn.Click += (_, _) => PickStarterPhotoFromDisk();
        controls.Children.Add(pickBtn);

        var pasteStarterBtn = new System.Windows.Controls.Button
        {
            Content = "Из буфера",
            Style = (Style)FindResource("DarkButton"),
            Margin = new Thickness(0, 0, 7, 0),
            IsEnabled = !_storyBusy,
            ToolTip = "Вставить стартовый кадр Шага 0 из буфера обмена (также работает Ctrl+V)"
        };
        pasteStarterBtn.Click += (_, _) => PasteStarterPhotoFromClipboard();
        controls.Children.Add(pasteStarterBtn);

        if (hasImg)
        {
            var clrBtn = new System.Windows.Controls.Button
            {
                Content = "Очистить",
                Style = (Style)FindResource("DarkButton"),
                IsEnabled = !_storyBusy
            };
            clrBtn.Click += (_, _) =>
            {
                _videoProject.StarterImagePath = null;
                _videoProject.UseStarterFrame = false;
                SaveVideoStoryState();
                RenderVideoStages();
            };
            controls.Children.Add(clrBtn);
        }

        left.Children.Add(controls);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right preview
        var previewBorder = new Border
        {
            Background = BrushFromHex("#070708"),
            BorderBrush = BrushFromHex("#28282E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Height = 124,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var bmp = LoadBitmapNoLock(_videoProject.StarterImagePath);
        if (bmp is not null)
        {
            previewBorder.Child = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Uniform };
            previewBorder.MouseLeftButtonUp += (_, _) => AiServiceManager.OpenExternal(_videoProject.StarterImagePath!);
            previewBorder.ToolTip = "Нажми, чтобы открыть стартовый кадр";
        }
        else
        {
            previewBorder.Child = new TextBlock
            {
                Text = "Нажми, чтобы\nвыбрать фото\nс диска",
                TextAlignment = TextAlignment.Center,
                Foreground = BrushFromHex("#71717A"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            previewBorder.MouseLeftButtonUp += (_, _) => PickStarterPhotoFromDisk();
            previewBorder.ToolTip = "Выбрать готовое фото с диска";
        }
        Grid.SetColumn(previewBorder, 1);
        grid.Children.Add(previewBorder);

        card.Child = grid;
        return card;
    }

    private async Task GenerateVideoStarterFrameAsync()
    {
        if (_storyBusy) return;
        if (string.IsNullOrWhiteSpace(_videoProject.StarterPrompt))
        {
            StatusFooterText.Text = "Опиши персонажа / стартовый кадр в поле Шага 0.";
            return;
        }

        var (w, h) = ParseSizeTag(VideoStorySizeCombo, 832, 480);
        _storyBusy = true;
        RenderPhotoStages();
        RenderVideoStages();
        var ct = BeginGenerationHud($"Шаг 0 • Стартовый кадр ({w}×{h})");
        bool ok = false;
        string hudDone = "Отменено";
        try
        {
            await EnsureComfyReadyForStoryAsync();
            var (outPath, _) = await _storyClient.GeneratePhotoStageAsync(
                _videoProject.StarterEngine,
                _videoProject.StarterPrompt.Trim(),
                w,
                h,
                null,
                null,
                "stories/video_starter",
                msg => Dispatcher.Invoke(() => StatusFooterText.Text = $"Шаг 0 (Стартовый кадр): {msg}"),
                UpdateGenerationHud,
                ct);

            _videoProject.StarterImagePath = outPath;
            _videoProject.UseStarterFrame = true;
            SaveVideoStoryState();
            ok = true;
            hudDone = $"Стартовый кадр готов ({w}×{h})";
            StatusFooterText.Text = $"Стартовый кадр готов ({w}×{h})! Теперь нажми «Сгенерировать клип» на Этапе 1.";
        }
        catch (OperationCanceledException)
        {
            hudDone = "Генерация отменена пользователем.";
            StatusFooterText.Text = "Шаг 0: генерация отменена.";
        }
        catch (Exception ex)
        {
            hudDone = $"Ошибка: {ex.Message}";
            StatusFooterText.Text = $"Ошибка генерации стартового кадра: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Ошибка Шага 0", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _storyBusy = false;
            EndGenerationHud(ok, hudDone);
            RenderPhotoStages();
            RenderVideoStages();
        }
    }

    private void RenderVideoStages()
    {
        VideoStagesPanel.Children.Clear();
        VideoStagesPanel.Children.Add(BuildVideoStarterCard());

        for (int i = 0; i < _videoStages.Count; i++)
        {
            int idx = i;
            var st = _videoStages[i];
            bool isFirst = idx == 0;
            bool hasStep0Img = !string.IsNullOrEmpty(_videoProject.StarterImagePath) && File.Exists(_videoProject.StarterImagePath);
            bool usesStarter = isFirst && string.IsNullOrEmpty(st.CustomStartImagePath) &&
                               _videoProject.UseStarterFrame &&
                               hasStep0Img;
            string refMode = string.IsNullOrWhiteSpace(st.RefMode) || st.RefMode == "prev_and_anchor" ? "svi_pro" : st.RefMode;

            var card = new Border
            {
                Background = BrushFromHex("#121215"),
                BorderBrush = !string.IsNullOrEmpty(st.OutputVideoPath) && File.Exists(st.OutputVideoPath)
                    ? BrushFromHex("#265944")
                    : BrushFromHex("#28282E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });

            var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };

            // Header line with reference info
            var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            hdr.Children.Add(new TextBlock
            {
                Text = isFirst ? "ЭТАП 01 — СТАРТ ВИДЕО-ИСТОРИИ" : $"ЭТАП {idx + 1:00} — ПРОДОЛЖЕНИЕ ЦЕПОЧКИ",
                FontSize = 11.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = isFirst ? BrushFromHex("#F4F4F6") : BrushFromHex("#D4D4D8")
            });

            string refBadgeText;
            if (!string.IsNullOrEmpty(st.CustomStartImagePath) && File.Exists(st.CustomStartImagePath))
                refBadgeText = hasStep0Img && !isFirst && refMode == "svi_pro"
                    ? $"SVI Pro: {Path.GetFileName(st.CustomStartImagePath)} + Шаг 0"
                    : hasStep0Img && !isFirst && refMode == "flux3_bridge"
                        ? $"FLUX 3 мост: Шаг 0 + {Path.GetFileName(st.CustomStartImagePath)}"
                        : $"Стартовое фото: {Path.GetFileName(st.CustomStartImagePath)}";
            else if (usesStarter)
                refBadgeText = "Старт: кадр из Шага 0 (I2V)";
            else if (isFirst)
                refBadgeText = "Режим: с текста (T2V)";
            else if (refMode == "anchor_only" && hasStep0Img)
                refBadgeText = "Старт: кадр из Шага 0 (новая сцена)";
            else if (refMode == "prev_to_anchor" && hasStep0Img)
                refBadgeText = $"Цикл: последний кадр Этапа {idx} → Кадр Шага 0";
            else if (refMode == "flux3_bridge" && hasStep0Img)
                refBadgeText = $"Мост FLUX 3: Шаг 0 + Этап {idx} → Видео";
            else if (refMode == "svi_pro" && hasStep0Img)
                refBadgeText = $"SVI 2.0 Pro: Последний кадр Этапа {idx} + Якорь Шага 0";
            else if (refMode == "prev_only")
                refBadgeText = $"Только последний кадр Этапа {idx} (I2V)";
            else
                refBadgeText = $"Последний кадр Этапа {idx} (задай Шаг 0 для якоря)";

            var refBadge = new Border
            {
                Background = BrushFromHex("#19191E"),
                BorderBrush = BrushFromHex("#2C2C34"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Child = new TextBlock { Text = refBadgeText, FontSize = 10.5, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = BrushFromHex("#A1A1AA") }
            };
            hdr.Children.Add(refBadge);
            left.Children.Add(hdr);

            // Reference mode selector for Stage 2+
            if (!isFirst)
            {
                var refRow = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
                var refLbl = new TextBlock
                {
                    Text = "Референсы:",
                    FontSize = 11.5,
                    Foreground = BrushFromHex("#A1A1AA"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                DockPanel.SetDock(refLbl, Dock.Left);
                refRow.Children.Add(refLbl);

                var refCombo = new System.Windows.Controls.ComboBox
                {
                    FontSize = 11.5,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Width = 390,
                    IsEnabled = !_storyBusy
                };
                refCombo.Items.Add(new ComboBoxItem { Content = "SVI 2.0 Pro: Последний кадр + Якорь Шага 0 (бесшовно)", Tag = "svi_pro" });
                refCombo.Items.Add(new ComboBoxItem { Content = "Мост FLUX 3 (Шаг 0 + Последний кадр) → Видео", Tag = "flux3_bridge" });
                refCombo.Items.Add(new ComboBoxItem { Content = "Только последний кадр (чистое продолжение I2V)", Tag = "prev_only" });
                refCombo.Items.Add(new ComboBoxItem { Content = "Последний кадр → Кадр Шага 0 (замкнуть цикл)", Tag = "prev_to_anchor" });
                refCombo.Items.Add(new ComboBoxItem { Content = "Начать заново с кадра Шага 0 (новая сцена)", Tag = "anchor_only" });
                refCombo.SelectedIndex = refMode switch
                {
                    "flux3_bridge" => 1,
                    "prev_only" => 2,
                    "prev_to_anchor" => 3,
                    "anchor_only" => 4,
                    _ => 0
                };
                refCombo.SelectionChanged += (_, _) =>
                {
                    st.RefMode = (refCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "svi_pro";
                    SaveVideoStoryState();
                    RenderVideoStages();
                };
                refRow.Children.Add(refCombo);
                left.Children.Add(refRow);

                if (refMode == "flux3_bridge")
                {
                    left.Children.Add(new TextBlock
                    {
                        Text = "Промпт моста FLUX 3 (стартовый фото-кадр: 1-й реф = Шаг 0, 2-й реф = последний кадр; пусто = авто):",
                        FontSize = 11,
                        Foreground = BrushFromHex("#A1A1AA"),
                        Margin = new Thickness(0, 0, 0, 4)
                    });
                    var bridgeBox = new System.Windows.Controls.TextBox
                    {
                        Text = st.BridgePrompt,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Height = 48,
                        Background = BrushFromHex("#0D0D10"),
                        Foreground = BrushFromHex("#E4E4E8"),
                        BorderBrush = BrushFromHex("#3A3A44"),
                        Padding = new Thickness(8, 5, 8, 5),
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 7)
                    };
                    bridgeBox.TextChanged += (_, _) => { st.BridgePrompt = bridgeBox.Text; SaveVideoStoryState(); };
                    left.Children.Add(bridgeBox);
                }
            }

            // Prompt TextBox
            var promptBox = new System.Windows.Controls.TextBox
            {
                Text = st.Prompt,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 64,
                Background = BrushFromHex("#09090B"),
                Foreground = BrushFromHex("#F4F4F6"),
                BorderBrush = BrushFromHex("#28282E"),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 8)
            };
            promptBox.TextChanged += (_, _) => { st.Prompt = promptBox.Text; SaveVideoStoryState(); };
            left.Children.Add(promptBox);

            // Buttons
            var btns = new WrapPanel();
            var genBtn = new System.Windows.Controls.Button
            {
                Content = string.IsNullOrEmpty(st.OutputVideoPath) ? "Сгенерировать клип" : "Перегенерировать",
                Style = (Style)FindResource("PrimaryButton"),
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = !_storyBusy
            };
            genBtn.Click += async (_, _) => await GenerateVideoStageClickAsync(idx);
            btns.Children.Add(genBtn);

            if (!string.IsNullOrEmpty(st.OutputVideoPath) && File.Exists(st.OutputVideoPath))
            {
                var playBtn = new System.Windows.Controls.Button
                {
                    Content = "▶ Смотреть",
                    Style = (Style)FindResource("SuccessButton"),
                    Margin = new Thickness(0, 0, 7, 0)
                };
                playBtn.Click += (_, _) => AiServiceManager.OpenExternal(st.OutputVideoPath!);
                btns.Children.Add(playBtn);
            }

            var pickImgBtn = new System.Windows.Controls.Button
            {
                Content = string.IsNullOrEmpty(st.CustomStartImagePath) ? "Выбрать фото с диска" : "Сбросить фото",
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = !_storyBusy
            };
            pickImgBtn.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(st.CustomStartImagePath))
                {
                    st.CustomStartImagePath = null;
                    SaveVideoStoryState();
                    RenderVideoStages();
                }
                else
                {
                    PickCustomPhotoForVideoStage(st, idx);
                }
            };
            btns.Children.Add(pickImgBtn);

            var pasteImgBtn = new System.Windows.Controls.Button
            {
                Content = "Из буфера",
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = !_storyBusy,
                ToolTip = "Вставить стартовый кадр для этого этапа из буфера обмена"
            };
            pasteImgBtn.Click += (_, _) =>
            {
                if (!TryGetImageFromClipboard(out string? file) || file is null) return;
                st.CustomStartImagePath = file;
                SaveVideoStoryState();
                RenderVideoStages();
                StatusFooterText.Text = $"Фото из буфера установлено для Этапа {idx + 1}.";
            };
            btns.Children.Add(pasteImgBtn);

            if (_videoStages.Count > 1)
            {
                var delBtn = new System.Windows.Controls.Button
                {
                    Content = "Удалить",
                    Style = (Style)FindResource("DangerButton"),
                    IsEnabled = !_storyBusy
                };
                delBtn.Click += (_, _) =>
                {
                    _videoStages.RemoveAt(idx);
                    SaveVideoStoryState();
                    RenderVideoStages();
                };
                btns.Children.Add(delBtn);
            }

            left.Children.Add(btns);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // Right preview box
            var previewBorder = new Border
            {
                Background = BrushFromHex("#070708"),
                BorderBrush = BrushFromHex("#28282E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Height = 132,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var bmp = LoadBitmapNoLock(st.PreviewImagePath ?? st.CustomStartImagePath);
            if (bmp is not null)
            {
                previewBorder.Child = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Uniform };
                if (!string.IsNullOrEmpty(st.OutputVideoPath) && File.Exists(st.OutputVideoPath))
                {
                    previewBorder.MouseLeftButtonUp += (_, _) => AiServiceManager.OpenExternal(st.OutputVideoPath!);
                    previewBorder.ToolTip = "Нажми, чтобы воспроизвести клип";
                }
                else if (!string.IsNullOrEmpty(st.CustomStartImagePath) && File.Exists(st.CustomStartImagePath))
                {
                    previewBorder.MouseLeftButtonUp += (_, _) => AiServiceManager.OpenExternal(st.CustomStartImagePath!);
                    previewBorder.ToolTip = "Нажми, чтобы открыть выбранное фото";
                }
            }
            else
            {
                previewBorder.Child = new TextBlock
                {
                    Text = "Нажми, чтобы\nвыбрать фото\nс диска",
                    TextAlignment = TextAlignment.Center,
                    Foreground = BrushFromHex("#71717A"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                previewBorder.MouseLeftButtonUp += (_, _) => PickCustomPhotoForVideoStage(st, idx);
                previewBorder.ToolTip = "Выбрать готовое фото с диска для этого этапа";
            }
            Grid.SetColumn(previewBorder, 1);
            grid.Children.Add(previewBorder);

            card.Child = grid;
            VideoStagesPanel.Children.Add(card);
        }

        MergeVideoStoryBtn.IsEnabled = !_storyBusy && _videoStages.Count(s => !string.IsNullOrEmpty(s.OutputVideoPath) && File.Exists(s.OutputVideoPath)) >= 1;
        RunVideoPipelineBtn.IsEnabled = !_storyBusy && _videoStages.Count >= 1;
        RefreshStudioControlsEnabled();
    }

    private async Task GenerateVideoStageClickAsync(int idx)
    {
        if (_storyBusy) return;
        var st = _videoStages[idx];
        if (string.IsNullOrWhiteSpace(st.Prompt))
        {
            StatusFooterText.Text = "Напиши промпт для видео-этапа.";
            return;
        }

        string refMode = string.IsNullOrWhiteSpace(st.RefMode) || st.RefMode == "prev_and_anchor" ? "svi_pro" : st.RefMode;
        string? step0Anchor = (!string.IsNullOrEmpty(_videoProject.StarterImagePath) && File.Exists(_videoProject.StarterImagePath))
            ? _videoProject.StarterImagePath
            : null;

        string? startImage = null;
        string? anchorImage = null;
        string? prevVideoPath = null;

        if (!string.IsNullOrEmpty(st.CustomStartImagePath) && File.Exists(st.CustomStartImagePath))
        {
            startImage = st.CustomStartImagePath;
            if (idx > 0 && refMode is "svi_pro" or "flux3_bridge" or "prev_to_anchor")
                anchorImage = step0Anchor;
        }
        else if (idx == 0 && _videoProject.UseStarterFrame && step0Anchor is not null)
        {
            startImage = step0Anchor;
        }
        else if (idx > 0)
        {
            if (refMode == "anchor_only")
            {
                if (step0Anchor is null)
                {
                    HubDialogWindow.Show("Для режима «Начать с кадра Шага 0» сначала сгенерируй или выбери фото в Шаге 0.", "Видео-история", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                startImage = step0Anchor;
            }
            else
            {
                var prev = _videoStages[idx - 1];
                if (string.IsNullOrEmpty(prev.OutputVideoPath) || !File.Exists(prev.OutputVideoPath))
                {
                    HubDialogWindow.Show($"Сначала сгенерируй Этап {idx} — из него автоматически возьмётся последний кадр для плавного продолжения.", "Видео-история", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (string.IsNullOrEmpty(prev.LastFramePath) || !File.Exists(prev.LastFramePath))
                {
                    Directory.CreateDirectory(VideoFramesDir);
                    prev.LastFramePath = Path.Combine(VideoFramesDir, $"stage_{idx}_last.png");
                    await _storyClient.ExtractVideoFrameAsync(prev.OutputVideoPath, prev.LastFramePath, "last");
                }
                startImage = prev.LastFramePath;
                prevVideoPath = prev.OutputVideoPath;

                if (refMode is "svi_pro" or "flux3_bridge" or "prev_to_anchor")
                    anchorImage = step0Anchor;
            }
        }

        string engine = (VideoStoryEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wan14b_fp8";
        var (w, h) = ParseSizeTag(VideoStorySizeCombo, 832, 480);
        int durSec = int.TryParse((VideoStoryDurCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 4;

        _storyBusy = true;
        RenderPhotoStages();
        RenderVideoStages();
        var ct = BeginGenerationHud($"Видео-история • Этап {idx + 1} ({w}×{h}, {durSec}с)");
        bool ok = false;
        string hudDone = "Отменено";
        try
        {
            await EnsureComfyReadyForStoryAsync();
            var (loraHigh, loraHighStr, loraLow, loraLowStr) = GetSelectedVideoExtraLoras();
            var (videoPath, previewPath, lastFramePath, seed) = await _storyClient.GenerateVideoStageAsync(
                engine,
                st.Prompt.Trim(),
                w,
                h,
                durSec,
                startImage,
                anchorImage,
                refMode,
                $"stories/video_stage_{idx + 1}",
                VideoFramesDir,
                msg => Dispatcher.Invoke(() => StatusFooterText.Text = $"Видео-история (Этап {idx + 1}): {msg}"),
                UpdateGenerationHud,
                ct,
                null,
                st.BridgePrompt,
                prevVideoPath,
                _videoProject.NegativePrompt,
                loraHigh,
                loraHighStr,
                loraLow,
                loraLowStr);

            st.OutputVideoPath = videoPath;
            st.PreviewImagePath = previewPath;
            st.LastFramePath = lastFramePath;
            st.LastSeed = seed;
            SaveVideoStoryState();
            ok = true;
            hudDone = $"Видео-этап {idx + 1} готов!";
            StatusFooterText.Text = $"Видео-этап {idx + 1} готов! Можно смотреть или генерировать следующий этап.";
        }
        catch (OperationCanceledException)
        {
            hudDone = "Генерация отменена пользователем.";
            StatusFooterText.Text = $"Видео-этап {idx + 1}: генерация отменена.";
        }
        catch (Exception ex)
        {
            hudDone = $"Ошибка: {ex.Message}";
            StatusFooterText.Text = $"Ошибка видео-этапа {idx + 1}: {ex.Message}";
            HubDialogWindow.Show(ex.Message, $"Ошибка Видео-истории (Этап {idx + 1})", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _storyBusy = false;
            EndGenerationHud(ok, hudDone);
            RenderPhotoStages();
            RenderVideoStages();
        }
    }

    internal void AddVideoStageBtn_Click(object sender, RoutedEventArgs e)
    {
        _videoStages.Add(new VideoStoryStage
        {
            Index = _videoStages.Count + 1,
            Prompt = "Smooth continuation from the previous frame: "
        });
        SaveVideoStoryState();
        RenderVideoStages();
    }

    internal void ResetVideoStoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        _videoProject = new VideoStoryProject
        {
            UseStarterFrame = false,
            StarterPrompt = "",
            Stages = [new VideoStoryStage { Index = 1, Prompt = "" }]
        };
        SaveVideoStoryState();
        RenderVideoStages();
        StatusFooterText.Text = "Начата новая видео-история.";
    }

    internal async void RunVideoPipelineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy || _videoStages.Count == 0) return;

        // Validate prompts for all stages
        for (int i = 0; i < _videoStages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(_videoStages[i].Prompt))
            {
                HubDialogWindow.Show($"Заполни промпт для Этапа {i + 1} перед запуском пайплайна.", "Пайплайн видео-истории", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        bool needStep0 = _videoProject.UseStarterFrame &&
                         (string.IsNullOrEmpty(_videoProject.StarterImagePath) || !File.Exists(_videoProject.StarterImagePath));
        if (needStep0 && string.IsNullOrWhiteSpace(_videoProject.StarterPrompt))
        {
            HubDialogWindow.Show("Включён Шаг 0 (стартовый кадр), но фото не выбрано и промпт Шага 0 пустой. Заполни промпт Шага 0, выбери фото с диска или сними галочку.", "Пайплайн видео-истории", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // If some stages already have videos, ask whether to regenerate all or only missing ones
        int existingCount = _videoStages.Count(s => !string.IsNullOrEmpty(s.OutputVideoPath) && File.Exists(s.OutputVideoPath));
        bool skipExisting = false;
        if (existingCount > 0)
        {
            var ans = HubDialogWindow.Show(
                $"Уже готово клипов: {existingCount} из {_videoStages.Count}.\n\n" +
                "• Да — перегенерировать ВСЕ этапы заново и склеить в MP4\n" +
                "• Нет — оставить готовые, догенерировать только недостающие и склеить в MP4\n" +
                "• Отмена — не запускать",
                "Запуск пайплайна",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (ans == MessageBoxResult.Cancel) return;
            skipExisting = ans == MessageBoxResult.No;
        }

        string engine = (VideoStoryEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wan14b_fp8";
        var (w, h) = ParseSizeTag(VideoStorySizeCombo, 832, 480);
        int durSec = int.TryParse((VideoStoryDurCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int d) ? d : 4;

        _storyBusy = true;
        RenderPhotoStages();
        RenderVideoStages();

        int totalStages = _videoStages.Count;
        var ct = BeginGenerationHud($"Пайплайн ({totalStages} эт. • {w}×{h}, {durSec}с)");
        bool ok = false;
        string hudDone = "Отменено";

        try
        {
            await EnsureComfyReadyForStoryAsync();

            // Optional Step 0 generation if checked and missing
            if (needStep0)
            {
                ct.ThrowIfCancellationRequested();
                var (starterPath, _) = await _storyClient.GeneratePhotoStageAsync(
                    _videoProject.StarterEngine,
                    _videoProject.StarterPrompt.Trim(),
                    w,
                    h,
                    null,
                    null,
                    "stories/video_starter",
                    msg => Dispatcher.Invoke(() => StatusFooterText.Text = $"Пайплайн [Шаг 0]: {msg}"),
                    info => UpdateGenerationHud(info with { Title = $"Пайплайн [Шаг 0 / {totalStages}] • {info.Title}" }),
                    ct);
                _videoProject.StarterImagePath = starterPath;
                _videoProject.UseStarterFrame = true;
                SaveVideoStoryState();
                RenderVideoStages();
            }

            // Sequential generation of all stages
            for (int idx = 0; idx < _videoStages.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();
                var st = _videoStages[idx];

                if (skipExisting && !string.IsNullOrEmpty(st.OutputVideoPath) && File.Exists(st.OutputVideoPath))
                {
                    if (string.IsNullOrEmpty(st.LastFramePath) || !File.Exists(st.LastFramePath))
                    {
                        Directory.CreateDirectory(VideoFramesDir);
                        st.LastFramePath = Path.Combine(VideoFramesDir, $"stage_{idx + 1}_last.png");
                        await _storyClient.ExtractVideoFrameAsync(st.OutputVideoPath, st.LastFramePath, "last");
                        SaveVideoStoryState();
                    }
                    continue;
                }

                string refMode = string.IsNullOrWhiteSpace(st.RefMode) || st.RefMode == "prev_and_anchor" ? "svi_pro" : st.RefMode;
                string? step0Anchor = (!string.IsNullOrEmpty(_videoProject.StarterImagePath) && File.Exists(_videoProject.StarterImagePath))
                    ? _videoProject.StarterImagePath
                    : null;

                string? startImage = null;
                string? anchorImage = null;
                string? prevVideoPath = null;

                if (!string.IsNullOrEmpty(st.CustomStartImagePath) && File.Exists(st.CustomStartImagePath))
                {
                    startImage = st.CustomStartImagePath;
                    if (idx > 0 && refMode is "svi_pro" or "flux3_bridge" or "prev_to_anchor")
                        anchorImage = step0Anchor;
                }
                else if (idx == 0 && _videoProject.UseStarterFrame && step0Anchor is not null)
                {
                    startImage = step0Anchor;
                }
                else if (idx > 0)
                {
                    if (refMode == "anchor_only")
                    {
                        if (step0Anchor is null)
                            throw new InvalidOperationException($"Этап {idx + 1}: для режима «Начать с кадра Шага 0» требуется фото в Шаге 0.");
                        startImage = step0Anchor;
                    }
                    else
                    {
                        var prev = _videoStages[idx - 1];
                        if (string.IsNullOrEmpty(prev.LastFramePath) || !File.Exists(prev.LastFramePath))
                        {
                            Directory.CreateDirectory(VideoFramesDir);
                            prev.LastFramePath = Path.Combine(VideoFramesDir, $"stage_{idx}_last.png");
                            await _storyClient.ExtractVideoFrameAsync(prev.OutputVideoPath!, prev.LastFramePath, "last");
                        }
                        startImage = prev.LastFramePath;
                        prevVideoPath = prev.OutputVideoPath;
                        if (refMode is "svi_pro" or "flux3_bridge" or "prev_to_anchor")
                            anchorImage = step0Anchor;
                    }
                }

                int stageNum = idx + 1;
                var (loraHigh, loraHighStr, loraLow, loraLowStr) = GetSelectedVideoExtraLoras();
                var (videoPath, previewPath, lastFramePath, seed) = await _storyClient.GenerateVideoStageAsync(
                    engine,
                    st.Prompt.Trim(),
                    w,
                    h,
                    durSec,
                    startImage,
                    anchorImage,
                    refMode,
                    $"stories/video_stage_{stageNum}",
                    VideoFramesDir,
                    msg => Dispatcher.Invoke(() => StatusFooterText.Text = $"Пайплайн [Этап {stageNum}/{totalStages}]: {msg}"),
                    info => UpdateGenerationHud(info with { Title = $"Пайплайн [Этап {stageNum}/{totalStages}] • {info.Title}" }),
                    ct,
                    null,
                    st.BridgePrompt,
                    prevVideoPath,
                    _videoProject.NegativePrompt,
                    loraHigh,
                    loraHighStr,
                    loraLow,
                    loraLowStr);

                st.OutputVideoPath = videoPath;
                st.PreviewImagePath = previewPath;
                st.LastFramePath = lastFramePath;
                st.LastSeed = seed;
                SaveVideoStoryState();
                RenderVideoStages();
            }

            // Final step: seamlessly concatenate all stage clips into one MP4
            ct.ThrowIfCancellationRequested();
            var readyStages = _videoStages
                .Where(s => !string.IsNullOrEmpty(s.OutputVideoPath) && File.Exists(s.OutputVideoPath))
                .ToList();
            var clips = readyStages.Select(s => s.OutputVideoPath!).ToList();
            var trimFlags = readyStages.Select((s, i) =>
                i == 0 ? 0 : (s.RefMode is "prev_only" or "prev_to_anchor" ? 1 : 0)).ToList();

            if (clips.Count == 0)
                throw new InvalidOperationException("Пайплайн не создал ни одного видеоклипа.");

            UpdateGenerationHud(new GenerationProgressInfo(
                $"Пайплайн ({totalStages}/{totalStages}) • Финал",
                $"Склейка {clips.Count} клипов в единый MP4...",
                99, totalStages, totalStages, null, _genStopwatch?.Elapsed.TotalSeconds ?? 0, 2, true));

            string outFile = Path.Combine(ComfyStoryClient.StoriesRoot, $"video_story_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await _storyClient.ConcatVideosAsync(clips, outFile, trimFlags);

            ok = true;
            hudDone = $"Пайплайн завершён! Итоговый фильм: {Path.GetFileName(outFile)}";
            StatusFooterText.Text = $"Готовый фильм ({clips.Count} эт.) сохранён: {outFile}";
            AiServiceManager.OpenExternal(outFile);
        }
        catch (OperationCanceledException)
        {
            hudDone = "Пайплайн остановлен пользователем (готовые этапы сохранены).";
            StatusFooterText.Text = hudDone;
        }
        catch (Exception ex)
        {
            hudDone = $"Ошибка пайплайна: {ex.Message}";
            StatusFooterText.Text = hudDone;
            HubDialogWindow.Show(ex.Message, "Ошибка пайплайна видео-истории", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _storyBusy = false;
            EndGenerationHud(ok, hudDone);
            RenderPhotoStages();
            RenderVideoStages();
        }
    }

    internal async void MergeVideoStoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        var readyStages = _videoStages
            .Where(s => !string.IsNullOrEmpty(s.OutputVideoPath) && File.Exists(s.OutputVideoPath))
            .ToList();
        var clips = readyStages.Select(s => s.OutputVideoPath!).ToList();
        var trimFlags = readyStages.Select((s, i) =>
            i == 0 ? 0 : (s.RefMode is "prev_only" or "prev_to_anchor" ? 1 : 0)).ToList();
        if (clips.Count == 0)
        {
            StatusFooterText.Text = "Нет готовых клипов для склейки.";
            return;
        }

        _storyBusy = true;
        RenderVideoStages();
        StatusFooterText.Text = $"Склейка {clips.Count} клипов в единый MP4...";
        try
        {
            string outFile = Path.Combine(ComfyStoryClient.StoriesRoot, $"video_story_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await _storyClient.ConcatVideosAsync(clips, outFile, trimFlags);
            StatusFooterText.Text = $"Готовый фильм сохранён: {outFile}";
            AiServiceManager.OpenExternal(outFile);
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка склейки: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Ошибка склейки MP4", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _storyBusy = false;
            RenderVideoStages();
        }
    }

    private void PasteStarterPhotoFromClipboard()
    {
        if (_storyBusy) return;
        if (!TryGetImageFromClipboard(out string? file) || file is null) return;
        _videoProject.StarterImagePath = file;
        _videoProject.UseStarterFrame = true;
        SaveVideoStoryState();
        RenderVideoStages();
        StatusFooterText.Text = $"Стартовый кадр Шага 0 вставлен из буфера: {Path.GetFileName(file)}";
    }

    internal void OpenStoriesFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ComfyStoryClient.StoriesRoot);
        AiServiceManager.OpenExternal(ComfyStoryClient.StoriesRoot);
    }

    private void JumpVideoStoryBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = VideoTab;
        SetVideoSubMode("story");
    }

    private static string? OpenImagePickerDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать фото с диска",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Все файлы|*.*"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void PickStarterPhotoFromDisk()
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        _videoProject.StarterImagePath = file;
        _videoProject.UseStarterFrame = true;
        SaveVideoStoryState();
        RenderVideoStages();
        StatusFooterText.Text = $"Стартовый кадр выбран с диска: {Path.GetFileName(file)}";
    }

    private void PickCustomPhotoForVideoStage(VideoStoryStage st, int idx)
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        st.CustomStartImagePath = file;
        SaveVideoStoryState();
        RenderVideoStages();
        StatusFooterText.Text = $"Фото для Видео-этапа {idx + 1} выбрано с диска: {Path.GetFileName(file)}";
    }

    // ==================== QWEN SCENARIO DIRECTOR ====================

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

}

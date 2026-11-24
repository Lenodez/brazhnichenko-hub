using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private static string PhotoStoryFile => Path.Combine(ComfyStoryClient.StoriesRoot, "photo_story.json");

    private void LoadPhotoStoryState()
    {
        try
        {
            if (File.Exists(PhotoStoryFile))
            {
                _photoStages = JsonSerializer.Deserialize<List<PhotoStoryStage>>(File.ReadAllText(PhotoStoryFile)) ?? [];
            }
        }
        catch { _photoStages = []; }

        if (_photoStages.Count == 0)
        {
            _photoStages =
            [
                new PhotoStoryStage
                {
                    Index = 1,
                    Prompt = "Full-body cinematic 35mm photograph of a young woman with red hair wearing a beige trench coat walking down a sunny European street, warm golden hour light, sharp focus",
                    UseAnchorRef = false,
                    UsePrevRef = false
                },
                new PhotoStoryStage
                {
                    Index = 2,
                    Prompt = "The same woman from the reference image entering a cozy warm-lit coffee shop, smiling gently while holding the glass door open, cinematic depth of field",
                    UseAnchorRef = true,
                    UsePrevRef = false
                }
            ];
        }
    }

    private void SavePhotoStoryState()
    {
        for (int i = 0; i < _photoStages.Count; i++) _photoStages[i].Index = i + 1;
        Directory.CreateDirectory(ComfyStoryClient.StoriesRoot);
        File.WriteAllText(PhotoStoryFile, JsonSerializer.Serialize(_photoStages, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ==================== PHOTO STORY ====================

    private void RenderPhotoStages()
    {
        PhotoStagesPanel.Children.Clear();
        for (int i = 0; i < _photoStages.Count; i++)
        {
            int idx = i;
            var st = _photoStages[i];
            bool isFirst = idx == 0;

            var card = new Border
            {
                Background = BrushFromHex("#171B24"),
                BorderBrush = !string.IsNullOrEmpty(st.OutputImagePath) && File.Exists(st.OutputImagePath)
                    ? BrushFromHex("#256B45")
                    : BrushFromHex("#2A3245"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });

            var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };

            // Header line
            var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            hdr.Children.Add(new TextBlock
            {
                Text = isFirst ? "Этап 1 — Якорь персонажа (первый кадр)" : $"Этап {idx + 1}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = isFirst ? BrushFromHex("#6EA8FE") : BrushFromHex("#E6EBF5")
            });

            if (!isFirst)
            {
                var refPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                var anchorChk = new System.Windows.Controls.CheckBox
                {
                    Content = "Якорь (Этап 1)",
                    IsChecked = st.UseAnchorRef,
                    Foreground = BrushFromHex("#A5B1C9"),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                anchorChk.Checked += (_, _) => { st.UseAnchorRef = true; SavePhotoStoryState(); };
                anchorChk.Unchecked += (_, _) => { st.UseAnchorRef = false; SavePhotoStoryState(); };

                var prevChk = new System.Windows.Controls.CheckBox
                {
                    Content = $"Предыдущий (Этап {idx})",
                    IsChecked = st.UsePrevRef,
                    Foreground = BrushFromHex("#A5B1C9"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                prevChk.Checked += (_, _) => { st.UsePrevRef = true; SavePhotoStoryState(); };
                prevChk.Unchecked += (_, _) => { st.UsePrevRef = false; SavePhotoStoryState(); };

                refPanel.Children.Add(anchorChk);
                refPanel.Children.Add(prevChk);
                hdr.Children.Add(refPanel);
            }
            left.Children.Add(hdr);

            // Prompt TextBox
            var promptBox = new System.Windows.Controls.TextBox
            {
                Text = st.Prompt,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 64,
                Background = BrushFromHex("#0B0D12"),
                Foreground = BrushFromHex("#E6EBF5"),
                BorderBrush = BrushFromHex("#2A3245"),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 8)
            };
            promptBox.TextChanged += (_, _) => { st.Prompt = promptBox.Text; SavePhotoStoryState(); };
            left.Children.Add(promptBox);

            // Action buttons
            var btns = new WrapPanel();
            var genBtn = new System.Windows.Controls.Button
            {
                Content = string.IsNullOrEmpty(st.OutputImagePath) ? "Сгенерировать кадр" : "Перегенерировать",
                Style = (Style)FindResource("PrimaryButton"),
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = !_storyBusy
            };
            genBtn.Click += async (_, _) => await GeneratePhotoStageClickAsync(idx);
            btns.Children.Add(genBtn);

            var pickBtn = new System.Windows.Controls.Button
            {
                Content = "Выбрать фото с диска",
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = !_storyBusy
            };
            pickBtn.Click += (_, _) => PickPhotoForStage(st, isFirst ? "Якорь персонажа загружен" : $"Фото для Этапа {idx + 1} загружено");
            btns.Children.Add(pickBtn);

            var pasteBtn = new System.Windows.Controls.Button
            {
                Content = "📋 Из буфера",
                Style = (Style)FindResource("DarkButton"),
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = !_storyBusy,
                ToolTip = "Вставить картинку из буфера обмена в этот этап"
            };
            pasteBtn.Click += (_, _) =>
            {
                if (!TryGetImageFromClipboard(out string? file) || file is null) return;
                st.OutputImagePath = file;
                SavePhotoStoryState();
                RenderPhotoStages();
                StatusFooterText.Text = isFirst ? "Якорь персонажа вставлен из буфера." : $"Фото для Этапа {idx + 1} вставлено из буфера.";
            };
            btns.Children.Add(pasteBtn);

            if (!string.IsNullOrEmpty(st.OutputImagePath) && File.Exists(st.OutputImagePath))
            {
                var toVideoBtn = new System.Windows.Controls.Button
                {
                    Content = "В Видео-историю →",
                    Style = (Style)FindResource("SuccessButton"),
                    Margin = new Thickness(0, 0, 7, 0)
                };
                toVideoBtn.Click += (_, _) => SendPhotoToVideoStory(st.OutputImagePath!);
                btns.Children.Add(toVideoBtn);
            }

            if (_photoStages.Count > 1)
            {
                var delBtn = new System.Windows.Controls.Button
                {
                    Content = "Удалить",
                    Style = (Style)FindResource("DangerButton"),
                    IsEnabled = !_storyBusy
                };
                delBtn.Click += (_, _) =>
                {
                    _photoStages.RemoveAt(idx);
                    SavePhotoStoryState();
                    RenderPhotoStages();
                };
                btns.Children.Add(delBtn);
            }

            left.Children.Add(btns);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // Right preview box
            var previewBorder = new Border
            {
                Background = BrushFromHex("#0B0D12"),
                BorderBrush = BrushFromHex("#2A3245"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Height = 132,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var bmp = LoadBitmapNoLock(st.OutputImagePath);
            if (bmp is not null)
            {
                var img = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Uniform };
                previewBorder.Child = img;
                previewBorder.MouseLeftButtonUp += (_, _) => AiServiceManager.OpenExternal(st.OutputImagePath!);
                previewBorder.ToolTip = "Нажми, чтобы открыть оригинал";
            }
            else
            {
                previewBorder.Child = new TextBlock
                {
                    Text = "Нажми, чтобы\nвыбрать фото\nс диска",
                    TextAlignment = TextAlignment.Center,
                    Foreground = BrushFromHex("#6B7794"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                previewBorder.MouseLeftButtonUp += (_, _) => PickPhotoForStage(st, isFirst ? "Якорь персонажа загружен" : $"Фото для Этапа {idx + 1} загружено");
                previewBorder.ToolTip = "Выбрать готовое фото с диска";
            }
            Grid.SetColumn(previewBorder, 1);
            grid.Children.Add(previewBorder);

            card.Child = grid;
            PhotoStagesPanel.Children.Add(card);
        }
    }

    private async Task GeneratePhotoStageClickAsync(int idx)
    {
        if (_storyBusy) return;
        var st = _photoStages[idx];
        if (string.IsNullOrWhiteSpace(st.Prompt))
        {
            StatusFooterText.Text = "Напиши промпт для этапа.";
            return;
        }

        string? anchorPath = idx > 0 && st.UseAnchorRef ? _photoStages[0].OutputImagePath : null;
        string? prevPath = idx > 0 && st.UsePrevRef ? _photoStages[idx - 1].OutputImagePath : null;
        if (idx > 0 && st.UseAnchorRef && (string.IsNullOrEmpty(anchorPath) || !File.Exists(anchorPath)))
        {
            HubDialogWindow.Show("Сначала сгенерируй (или загрузи) Этап 1 — он служит якорем персонажа.", "Фото-история", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (idx > 0 && st.UsePrevRef && (string.IsNullOrEmpty(prevPath) || !File.Exists(prevPath)))
        {
            HubDialogWindow.Show($"Сначала сгенерируй Этап {idx}, чтобы использовать его как предыдущий кадр.", "Фото-история", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string engine = (PhotoStoryEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "flux3";
        var (w, h) = ParseSizeTag(PhotoStorySizeCombo, 1024, 1024);

        _storyBusy = true;
        RenderPhotoStages();
        RenderVideoStages();
        var ct = BeginGenerationHud($"Фото-история • Этап {idx + 1}");
        bool ok = false;
        string hudDone = "Отменено";
        try
        {
            await EnsureComfyReadyForStoryAsync();
            var (extraLora, extraLoraStr) = GetSelectedPhotoExtraLora();
            var (outPath, seed) = await _storyClient.GeneratePhotoStageAsync(
                engine,
                st.Prompt.Trim(),
                w,
                h,
                anchorPath,
                prevPath,
                $"stories/photo_stage_{idx + 1}",
                msg => Dispatcher.Invoke(() => StatusFooterText.Text = $"Фото-история (Этап {idx + 1}): {msg}"),
                UpdateGenerationHud,
                ct,
                null,
                extraLora,
                extraLoraStr);

            st.OutputImagePath = outPath;
            st.LastSeed = seed;
            SavePhotoStoryState();
            ok = true;
            hudDone = $"Готово: {Path.GetFileName(outPath)}";
            StatusFooterText.Text = $"Этап {idx + 1} готов: {Path.GetFileName(outPath)}";
        }
        catch (OperationCanceledException)
        {
            hudDone = "Генерация отменена пользователем.";
            StatusFooterText.Text = $"Этап {idx + 1}: генерация отменена.";
        }
        catch (Exception ex)
        {
            hudDone = $"Ошибка: {ex.Message}";
            StatusFooterText.Text = $"Ошибка этапа {idx + 1}: {ex.Message}";
            HubDialogWindow.Show(ex.Message, $"Ошибка Фото-истории (Этап {idx + 1})", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _storyBusy = false;
            EndGenerationHud(ok, hudDone);
            RenderPhotoStages();
            RenderVideoStages();
        }
    }

    internal void AddPhotoStageBtn_Click(object sender, RoutedEventArgs e)
    {
        _photoStages.Add(new PhotoStoryStage
        {
            Index = _photoStages.Count + 1,
            Prompt = "The same character from the reference image in the next scene, ",
            UseAnchorRef = true,
            UsePrevRef = false
        });
        SavePhotoStoryState();
        RenderPhotoStages();
    }

    internal void ResetPhotoStoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBusy) return;
        _photoStages = [new PhotoStoryStage { Index = 1, Prompt = "", UseAnchorRef = false, UsePrevRef = false }];
        SavePhotoStoryState();
        RenderPhotoStages();
        StatusFooterText.Text = "Начата новая фото-история.";
    }

    private void SendPhotoToVideoStory(string imagePath)
    {
        _videoProject.StarterImagePath = imagePath;
        _videoProject.UseStarterFrame = true;
        SaveVideoStoryState();
        RenderVideoStages();
        MainTabs.SelectedItem = VideoTab;
        SetVideoSubMode("story");
        StatusFooterText.Text = $"Кадр {Path.GetFileName(imagePath)} установлен в Шаге 0 как стартовый кадр для Видео-истории!";
    }

    private void JumpPhotoStoryBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = PhotoTab;
        SetPhotoSubMode("story");
    }

    private void PickPhotoForStage(PhotoStoryStage st, string statusPrefix)
    {
        if (_storyBusy) return;
        string? file = OpenImagePickerDialog();
        if (file is null) return;
        st.OutputImagePath = file;
        SavePhotoStoryState();
        RenderPhotoStages();
        StatusFooterText.Text = $"{statusPrefix}: {Path.GetFileName(file)}";
    }
}

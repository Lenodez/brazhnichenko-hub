using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private readonly HuggingFaceClient _hfClient = new();
    private readonly RuntimeInstaller _runtimeInstaller = new();
    private HardwareProfile? _detectedHardware;
    private string _activeCatalogView = "text"; // text | photo | video | wiki | hf
    private CancellationTokenSource? _catalogDlCts;
    private bool _catalogDownloading;
    private bool _suppressSimChanged;

    private void InitCatalogAndSettings()
    {
        DiskLabelText.Text = $"Свободно на диске {HubConfig.DataDriveLetter}:";
        PopulateHardwareSimulatorCombo();
        LoadGpuWikiGrid("all");
        LoadSettingsForm();
        RefreshRuntimeStatusUi();
        SetCatalogView("text");
        _ = RefreshHfAuthStatusAsync();
    }

    internal void JumpCatalogBtn_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = CatalogTab;
    private void JumpSettingsMcpBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = SystemTab;
        SetSystemSubMode("settings");
    }

    private void PopulateHardwareSimulatorCombo()
    {
        _suppressSimChanged = true;
        try
        {
            CatalogSimGpuCombo.Items.Clear();
            CatalogSimGpuCombo.Items.Add(new ComboBoxItem { Content = "★ Текущий ПК (авто)", Tag = "auto" });
            foreach (var g in HardwareAdvisor.GpuWiki)
                CatalogSimGpuCombo.Items.Add(new ComboBoxItem { Content = $"{g.ModelName} ({g.Architecture})", Tag = g.ModelName });
            CatalogSimGpuCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressSimChanged = false;
        }
    }

    private void UpdateCatalogDetectedHardware(SystemMetrics m)
    {
        double vramGb = m.Gpu is not null ? Math.Round(m.Gpu.VramTotalMiB / 1024.0, 1) : 0;
        bool firstDetection = _detectedHardware is null;
        _detectedHardware = HardwareAdvisor.BuildProfile(m.Gpu?.Name, vramGb, m.RamTotalGB, m.DiskFreeDGB);
        if (firstDetection)
        {
            RenderCatalogRecommendations();
        }
    }

    private HardwareProfile GetActiveCatalogHardware()
    {
        string gpuTag = (CatalogSimGpuCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        int simRam = int.TryParse((CatalogSimRamCombo.SelectedItem as ComboBoxItem)?.Tag as string, out int r) ? r : 0;

        if (gpuTag != "auto")
        {
            var wikiGpu = HardwareAdvisor.GpuWiki.FirstOrDefault(g => g.ModelName == gpuTag);
            if (wikiGpu is not null)
                return HardwareAdvisor.BuildSimulatedProfile(wikiGpu, simRam > 0 ? simRam : (_detectedHardware?.DetectedRamGB ?? 32));
        }

        if (_detectedHardware is not null)
        {
            return simRam > 0
                ? HardwareAdvisor.BuildProfile(_detectedHardware.DetectedGpuName, _detectedHardware.DetectedVramGB, simRam, _detectedHardware.FreeDiskGB)
                : _detectedHardware;
        }

        return HardwareAdvisor.BuildProfile("Определение GPU...", 0, simRam > 0 ? simRam : 16, 100);
    }

    internal void CatalogSim_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSimChanged || !IsLoaded) return;
        RenderCatalogRecommendations();
    }

    internal void CatTabTextBtn_Click(object sender, RoutedEventArgs e) => SetCatalogView("text");
    internal void CatTabPhotoBtn_Click(object sender, RoutedEventArgs e) => SetCatalogView("photo");
    internal void CatTabVideoBtn_Click(object sender, RoutedEventArgs e) => SetCatalogView("video");
    internal void CatTabWikiBtn_Click(object sender, RoutedEventArgs e) => SetCatalogView("wiki");
    internal void CatTabHfSearchBtn_Click(object sender, RoutedEventArgs e) => SetCatalogView("hf");
    internal void CatTabDiskBtn_Click(object sender, RoutedEventArgs e) => SetCatalogView("disk");

    private void SetCatalogView(string view)
    {
        _activeCatalogView = view;
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");

        CatTabTextBtn.Style = view == "text" ? primary : dark;
        CatTabPhotoBtn.Style = view == "photo" ? primary : dark;
        CatTabVideoBtn.Style = view == "video" ? primary : dark;
        CatTabWikiBtn.Style = view == "wiki" ? primary : dark;
        CatTabHfSearchBtn.Style = view == "hf" ? primary : dark;
        CatTabDiskBtn.Style = view == "disk" ? primary : dark;

        CatalogModelsScroll.Visibility = view is "text" or "photo" or "video" ? Visibility.Visible : Visibility.Collapsed;
        CatalogWikiView.Visibility = view == "wiki" ? Visibility.Visible : Visibility.Collapsed;
        CatalogHfSearchView.Visibility = view == "hf" ? Visibility.Visible : Visibility.Collapsed;
        CatalogDiskView.Visibility = view == "disk" ? Visibility.Visible : Visibility.Collapsed;

        if (view is "text" or "photo" or "video")
            RenderCatalogRecommendations();
        else if (view == "disk")
            LoadModelsTable();
    }

    private void RenderCatalogRecommendations()
    {
        var hw = GetActiveCatalogHardware();
        CatalogHwSummaryText.Text = hw.OverallTierSummaryRu;
        CatalogRamAdviceText.Text = $"{hw.RamTierLabel}: {hw.RamAdviceRu}";

        if (_activeCatalogView is not ("text" or "photo" or "video")) return;

        CatalogCardsPanel.Children.Clear();

        // Step 1 prerequisite banner when the engine runtime for this section is missing
        bool isTextSection = _activeCatalogView == "text";
        bool runtimeInstalled = isTextSection ? RuntimeInstaller.IsLlamaInstalled() : RuntimeInstaller.IsComfyInstalled();
        if (!runtimeInstalled)
        {
            CatalogCardsPanel.Children.Add(BuildMissingRuntimeBannerCard(isTextSection, hw));
        }

        var items = ModelCatalog.All
            .Where(m => m.Category == _activeCatalogView)
            .Select(m => (Spec: m, Fit: HardwareAdvisor.EvaluateModel(m, hw), State: ModelCatalog.CheckInstallState(m)))
            .ToList();

        var sectionDefs = new (ModelPlacementGroup Group, string Title, string Subtitle, string AccentHex, string BgHex)[]
        {
            (
                ModelPlacementGroup.FullVram,
                $"🟢 Полностью помещается в видеопамять ({hw.DetectedVramGB:F0} ГБ VRAM)",
                "Максимальная скорость и мгновенный отклик без обращения к оперативной памяти",
                "#6AE89E",
                "#11231B"
            ),
            (
                ModelPlacementGroup.VramPlusRam,
                $"🔵 Помещается в видеопамять + ОЗУ ({hw.DetectedVramGB:F0} ГБ VRAM + {hw.DetectedRamGB:F0} ГБ RAM)",
                isTextSection
                    ? "MoE-модели (35B–80B, акт. 3B) работают бодро (~20–45 tok/s) • Плотные (Dense) — медленнее (~3–8 tok/s), но с макс. качеством"
                    : "Работает через DynamicVRAM / оффлоад в ОЗУ без потери качества",
                "#7FB4FF",
                "#121C2E"
            ),
            (
                ModelPlacementGroup.TooHeavy,
                "🔴 Слишком тяжёлые для текущих спеков (уйдут в swap на SSD)",
                "Требуют больше видеопамяти или ОЗУ — для комфортной работы выбери модели из разделов выше",
                "#FF877C",
                "#261518"
            )
        };

        foreach (var sec in sectionDefs)
        {
            var groupItems = items
                .Where(x => x.Fit.Placement == sec.Group)
                .OrderBy(x => x.Fit.Level)
                .ThenByDescending(x => x.Spec.IsMoe)
                .ThenByDescending(x => x.Spec.TotalSizeGB)
                .ToList();

            if (groupItems.Count == 0) continue;

            // Section header bar
            var secHeader = new Border
            {
                Background = BrushFromHex(sec.BgHex),
                BorderBrush = BrushFromHex(sec.AccentHex),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, CatalogCardsPanel.Children.Count > 0 ? 8 : 0, 0, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{sec.Title}  •  моделей: {groupItems.Count}",
                            FontSize = 13,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = BrushFromHex(sec.AccentHex)
                        },
                        new TextBlock
                        {
                            Text = sec.Subtitle,
                            FontSize = 11,
                            Foreground = BrushFromHex("#9BA8C4"),
                            Margin = new Thickness(0, 1, 0, 0),
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            CatalogCardsPanel.Children.Add(secHeader);

            foreach (var (spec, fit, state) in groupItems)
            {
                var (badgeBg, badgeFg, borderHex) = fit.Level switch
                {
                    ModelFitLevel.Ideal => ("#143322", "#6AE89E", "#256B45"),
                    ModelFitLevel.GoodWithOffload => ("#1B2C4A", "#7FB4FF", "#35528A"),
                    ModelFitLevel.Tight => ("#332D14", "#F5CB5C", "#7D661A"),
                    _ => ("#3A1D21", "#FF877C", "#6E2E35")
                };

                var card = new Border
                {
                    Background = BrushFromHex("#171B24"),
                    BorderBrush = BrushFromHex(state.allInstalled ? "#256B45" : borderHex),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 0, 0, 9)
                };

                var root = new StackPanel();

                // Header: title + speed badge + fit badge + size
                var top = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                var rightBadges = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                DockPanel.SetDock(rightBadges, Dock.Right);

                if (!string.IsNullOrWhiteSpace(fit.EstimatedSpeedHint) && fit.EstimatedSpeedHint != "—")
                {
                    rightBadges.Children.Add(new Border
                    {
                        Background = BrushFromHex("#182334"),
                        BorderBrush = BrushFromHex("#2E4266"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8, 2.5, 8, 2.5),
                        Margin = new Thickness(0, 0, 6, 0),
                        Child = new TextBlock { Text = $"⏱ {fit.EstimatedSpeedHint}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = BrushFromHex("#CFE0FF") }
                    });
                }

                rightBadges.Children.Add(new Border
                {
                    Background = BrushFromHex(badgeBg),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = fit.BadgeText, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushFromHex(badgeFg) }
                });
                rightBadges.Children.Add(new Border
                {
                    Background = BrushFromHex("#1F2533"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 3, 8, 3),
                    Child = new TextBlock { Text = $"{spec.TotalSizeGB:0.##} ГБ", FontSize = 11.5, Foreground = BrushFromHex("#C8D4EA"), FontFamily = new System.Windows.Media.FontFamily("Consolas") }
                });
                top.Children.Add(rightBadges);

                var titleStack = new StackPanel();
                titleStack.Children.Add(new TextBlock
                {
                    Text = spec.Name,
                    FontSize = 14.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFromHex("#FFFFFF")
                });
                titleStack.Children.Add(new TextBlock
                {
                    Text = $"{spec.ShortTag}{(spec.IsGated ? "  •  Требует HF токен" : "")}",
                    FontSize = 11.5,
                    Foreground = BrushFromHex("#8C96AD"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                top.Children.Add(titleStack);
                root.Children.Add(top);

                root.Children.Add(new TextBlock
                {
                    Text = spec.SummaryRu,
                    FontSize = 12.5,
                    Foreground = BrushFromHex("#D4DCEB"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 4)
                });

                root.Children.Add(new TextBlock
                {
                    Text = $"Для твоих спеков: {fit.ReasonRu}",
                    FontSize = 11.5,
                    Foreground = BrushFromHex(badgeFg),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 9)
                });

                // Footer actions
                var bottom = new DockPanel();
                string filesInfo = state.allInstalled
                    ? $"✓ Все файлы установлены ({state.presentCount}/{state.totalCount})"
                    : state.presentCount > 0
                        ? $"Установлено {state.presentCount}/{state.totalCount} • Докачать: {state.missingGB:0.##} ГБ"
                        : $"Файлов в комплекте: {state.totalCount} • Скачать: {spec.TotalSizeGB:0.##} ГБ";

                bottom.Children.Add(new TextBlock
                {
                    Text = filesInfo,
                    FontSize = 11.5,
                    Foreground = BrushFromHex(state.allInstalled ? "#6AE89E" : "#8C96AD"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var actionsStack = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };
                DockPanel.SetDock(actionsStack, Dock.Right);

                if (spec.IsGated && spec.Files.Length > 0)
                {
                    string gatedRepo = spec.Files[0].RepoId;
                    var licenseBtn = new System.Windows.Controls.Button
                    {
                        Content = "Лицензия HF ↗",
                        Style = (Style)FindResource("DarkButton"),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 7, 0),
                        ToolTip = $"Открыть страницу согласия https://huggingface.co/{gatedRepo}"
                    };
                    licenseBtn.Click += (_, _) => AiServiceManager.OpenExternal($"https://huggingface.co/{gatedRepo}");
                    actionsStack.Children.Add(licenseBtn);
                }

                var delInfo = ModelCatalog.GetDeletableFiles(spec);
                if (delInfo.filesToDelete.Count > 0)
                {
                    var delBtn = new System.Windows.Controls.Button
                    {
                        Content = "Удалить",
                        Style = (Style)FindResource("DangerButton"),
                        IsEnabled = !_catalogDownloading,
                        Padding = new Thickness(11, 5, 11, 5),
                        Margin = new Thickness(0, 0, 7, 0)
                    };
                    delBtn.Click += async (_, _) => await DeleteCatalogBundleAsync(spec);
                    actionsStack.Children.Add(delBtn);
                }

                var btn = new System.Windows.Controls.Button
                {
                    Content = state.allInstalled
                        ? "✓ Установлено"
                        : state.presentCount > 0 ? $"Докачать ({state.missingGB:0.##} ГБ)" : $"Скачать ({spec.TotalSizeGB:0.##} ГБ)",
                    Style = (Style)FindResource(state.allInstalled ? "DarkButton" : (fit.Level is ModelFitLevel.Ideal or ModelFitLevel.GoodWithOffload ? "SuccessButton" : "PrimaryButton")),
                    IsEnabled = !state.allInstalled && !_catalogDownloading,
                    Padding = new Thickness(13, 5, 13, 5)
                };
                btn.Click += async (_, _) => await DownloadCatalogBundleAsync(spec);
                actionsStack.Children.Add(btn);
                bottom.Children.Add(actionsStack);
                root.Children.Add(bottom);

                card.Child = root;
                CatalogCardsPanel.Children.Add(card);
            }
        }
    }

    private async Task DeleteCatalogBundleAsync(CatalogModelSpec spec)
    {
        if (_catalogDownloading) return;
        var (files, reclaimGb, sharedKept) = ModelCatalog.GetDeletableFiles(spec);
        if (files.Count == 0)
        {
            StatusFooterText.Text = $"Для «{spec.Name}» нет локальных файлов для удаления.";
            return;
        }

        string sharedNote = sharedKept > 0
            ? $"\n\nПримечание: {sharedKept} общих файл(а/ов), нужных другим установленным моделям, будут сохранены."
            : "";
        var confirm = HubDialogWindow.Show(
            $"Удалить файлы модели «{spec.Name}» с диска?\n\n" +
            $"Будет удалено файлов: {files.Count} (освободится ~{reclaimGb:0.##} ГБ).{sharedNote}",
            "Удаление модели",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        int deleted = 0;
        var locked = new List<string>();
        foreach (string path in files)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (IOException)
            {
                locked.Add(Path.GetFileName(path));
            }
            catch (UnauthorizedAccessException)
            {
                locked.Add(Path.GetFileName(path));
            }
        }

        if (locked.Count > 0)
        {
            HubDialogWindow.Show(
                $"Не удалось удалить занятые файлы (сначала останови активный сервис кнопкой «Освободить GPU»):\n• {string.Join("\n• ", locked)}",
                "Файлы заняты процессом",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        StatusFooterText.Text = deleted > 0
            ? $"Удалено файлов: {deleted} из «{spec.Name}» (освобождено ~{reclaimGb:0.##} ГБ)."
            : "Файлы не были удалены.";
        LoadModelsTable();
        RenderCatalogRecommendations();
        await RefreshStatusAsync();
    }

    private Border BuildMissingRuntimeBannerCard(bool isTextSection, HardwareProfile hw)
    {
        string engineName = isTextSection ? "llama.cpp" : "ComfyUI Portable";
        string cudaHint = isTextSection
            ? (RuntimeInstaller.PreferCuda13(hw) ? "NVIDIA CUDA 13 (Blackwell / RTX 50)" : "NVIDIA CUDA 12.4")
            : (hw.MatchedWiki?.Series == "10" ? "NVIDIA Portable (CUDA 12.6)" : "NVIDIA Portable (CUDA 12.8+)");
        string sizeHint = isTextSection ? "~620 МБ" : "~2.0 ГБ";
        string purpose = isTextSection
            ? "Без движка llama.cpp скачанные текстовые веса (.gguf) не смогут запуститься."
            : "Без движка ComfyUI Portable генерация фото, видео и историй не сможет запуститься.";

        var card = new Border
        {
            Background = BrushFromHex("#241F12"),
            BorderBrush = BrushFromHex("#C79C2B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var dock = new DockPanel();
        var installBtn = new System.Windows.Controls.Button
        {
            Content = $"Установить {engineName} ({sizeHint})",
            Style = (Style)FindResource("SuccessButton"),
            IsEnabled = !_catalogDownloading,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(14, 7, 14, 7)
        };
        installBtn.Click += async (_, _) => await InstallRuntimeWithUiAsync(isTextSection ? "llama" : "comfyui");
        DockPanel.SetDock(installBtn, Dock.Right);
        dock.Children.Add(installBtn);

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = $"⚠ Шаг 1: установите движок {engineName} ({cudaHint})",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#FFD970")
        });
        textStack.Children.Add(new TextBlock
        {
            Text = $"{purpose} Нажмите кнопку справа — движок скачается и настроится автоматически.",
            FontSize = 11.5,
            Foreground = BrushFromHex("#E6DEC8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        dock.Children.Add(textStack);
        card.Child = dock;
        return card;
    }

    private async Task<bool> InstallRuntimeWithUiAsync(string runtimeId, bool holdDownloadLock = false)
    {
        if (!holdDownloadLock && _catalogDownloading) return false;

        bool acquiredLock = false;
        if (!holdDownloadLock)
        {
            _catalogDownloading = true;
            acquiredLock = true;
            _catalogDlCts?.Dispose();
            _catalogDlCts = new CancellationTokenSource();
        }

        CatalogDownloadCard.Visibility = Visibility.Visible;
        CatalogDlTitleText.Text = runtimeId == "llama"
            ? "Установка движка llama.cpp..."
            : "Установка движка ComfyUI Portable...";
        CatalogDlStatsText.Text = "Подготовка...";
        CatalogDlBar.Value = 0;
        if (acquiredLock)
        {
            RenderCatalogRecommendations();
            RefreshRuntimeStatusUi();
        }

        try
        {
            var progress = new Progress<RuntimeInstallProgress>(p => Dispatcher.Invoke(() =>
            {
                CatalogDlTitleText.Text = p.Stage;
                CatalogDlStatsText.Text = p.SpeedMBs > 0.05
                    ? $"{p.Percent:F1}% • {p.DownloadedMB:F0}/{p.TotalMB:F0} МБ • {p.SpeedMBs:F1} МБ/с"
                    : $"{p.Percent:F1}%";
                CatalogDlBar.Value = p.Percent;
                StatusFooterText.Text = $"{p.Stage} ({p.Percent:F0}%)";
            }));

            var hw = _detectedHardware ?? GetActiveCatalogHardware();
            string summary = runtimeId == "llama"
                ? await _runtimeInstaller.InstallLlamaCppAsync(hw, progress, _catalogDlCts!.Token)
                : await _runtimeInstaller.InstallComfyUiAsync(hw, progress, _catalogDlCts!.Token);

            StatusFooterText.Text = summary;
            RefreshRuntimeStatusUi();
            RefreshStudioEngineStatuses();
            _ = RefreshStatusAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusFooterText.Text = "Установка движка отменена (частичный файл сохранён для докачки).";
            return false;
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка установки движка: {ex.Message}";
            string manualUrl = runtimeId == "llama" ? RuntimeInstaller.LlamaReleasesUrl : RuntimeInstaller.ComfyReleasesUrl;
            string destHint = runtimeId == "llama"
                ? $"Распакуй оба zip-архива (llama-...-win-cuda-...-x64.zip и cudart-llama-...) в папку:\n{HubConfig.LlmRuntimeDir}"
                : $"Скачай ComfyUI_windows_portable_nvidia.7z и распакуй в папку:\n{HubConfig.MediaRoot}";

            var openSite = HubDialogWindow.Show(
                $"Не удалось скачать движок автоматически:\n{ex.Message}\n\n" +
                $"Частая причина — провайдер/DPI/SSL блокирует соединение с GitHub.\n" +
                $"Открыть официальную страницу релизов в браузере, чтобы скачать вручную?\n\n{destHint}",
                "Ошибка установки движка — скачать вручную?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (openSite == MessageBoxResult.Yes)
            {
                AiServiceManager.OpenExternal(manualUrl);
                string openDir = runtimeId == "llama" ? HubConfig.LlmRuntimeDir : HubConfig.MediaRoot;
                try { Directory.CreateDirectory(openDir); AiServiceManager.OpenExternal(openDir); } catch { }
            }
            return false;
        }
        finally
        {
            if (acquiredLock)
            {
                _catalogDownloading = false;
                CatalogDownloadCard.Visibility = Visibility.Collapsed;
                RenderCatalogRecommendations();
                RefreshRuntimeStatusUi();
                _ = RefreshStatusAsync();
            }
        }
    }

    private void HandleHfAccessRequired(string repoId, bool hasToken, int statusCode = 403)
    {
        string repoUrl = $"https://huggingface.co/{repoId}";
        bool needToken = !hasToken || statusCode == 401;

        if (needToken)
        {
            var ans = HubDialogWindow.Show(
                $"Модель {repoId} защищена лицензией Hugging Face (Gated).\n\n" +
                $"Чтобы её скачать, нужны 2 шага:\n" +
                $"1. Принять условия использования на странице:\n   {repoUrl}\n" +
                $"2. Подключить свой Hugging Face Read-токен во вкладке «Настройки и MCP».\n\n" +
                $"Открыть страницу модели в браузере и перейти к настройке токена прямо сейчас?",
                "Требуется согласие и токен Hugging Face",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (ans == MessageBoxResult.Yes)
            {
                AiServiceManager.OpenExternal(repoUrl);
                MainTabs.SelectedItem = SystemTab;
                SetSystemSubMode("settings");
                HfTokenBox.Focus();
                StatusFooterText.Text = $"Открыта лицензия {repoUrl} — прими условия и вставь HF-токен в разделе «1. Подключение к Hugging Face».";
            }
        }
        else
        {
            var ans = HubDialogWindow.Show(
                $"Токен Hugging Face подключён, но автор модели {repoId} требует предварительно принять условия использования на сайте:\n\n" +
                $"{repoUrl}\n\n" +
                $"Открыть эту страницу в браузере прямо сейчас?\n" +
                $"После нажатия «Agree and access repository» на сайте просто вернись и нажми «Скачать» ещё раз.",
                "Требуется согласие с лицензией на Hugging Face",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (ans == MessageBoxResult.Yes)
            {
                AiServiceManager.OpenExternal(repoUrl);
                StatusFooterText.Text = $"Открыта страница лицензии {repoUrl}. Прими условия на сайте и повтори скачивание.";
            }
        }
    }

    private async Task DownloadCatalogBundleAsync(CatalogModelSpec spec)
    {
        if (_catalogDownloading) return;

        // Preflight check for gated Hugging Face models BEFORE asking about runtime or starting download
        if (spec.IsGated && spec.Files.Length > 0)
        {
            string gatedRepo = spec.Files[0].RepoId;
            bool hasToken = !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken());
            if (!hasToken)
            {
                HandleHfAccessRequired(gatedRepo, hasToken: false, statusCode: 401);
                return;
            }

            StatusFooterText.Text = $"Проверка доступа к {gatedRepo} на Hugging Face...";
            bool allowed = await _hfClient.HasRepoAccessAsync(gatedRepo, spec.Files[0].RemotePath);
            if (!allowed)
            {
                HandleHfAccessRequired(gatedRepo, hasToken: true, statusCode: 403);
                return;
            }
        }

        // Proactively offer to install missing runtime together with the model!
        bool needLlama = spec.Category == "text" && !RuntimeInstaller.IsLlamaInstalled();
        bool needComfy = spec.Category is "photo" or "video" && !RuntimeInstaller.IsComfyInstalled();
        bool installRuntimeToo = false;

        if (needLlama || needComfy)
        {
            string rtName = needLlama ? "llama.cpp (NVIDIA CUDA, ~620 МБ)" : "ComfyUI Portable (NVIDIA, ~2.0 ГБ)";
            var choice = HubDialogWindow.Show(
                $"Для запуска «{spec.Name}» также нужен движок {rtName}, которого пока нет на этом ПК.\n\n" +
                $"Установить движок автоматически вместе с моделью?\n" +
                $"• Да — скачать движок + модель (сразу готово к запуску)\n" +
                $"• Нет — скачать только файлы модели",
                "Требуется движок для запуска модели",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel) return;
            installRuntimeToo = choice == MessageBoxResult.Yes;
        }

        _catalogDownloading = true;
        _catalogDlCts?.Dispose();
        _catalogDlCts = new CancellationTokenSource();

        CatalogDownloadCard.Visibility = Visibility.Visible;
        CatalogDlTitleText.Text = $"Установка: {spec.Name}";
        CatalogDlStatsText.Text = "Подготовка...";
        CatalogDlBar.Value = 0;
        RenderCatalogRecommendations();
        RefreshRuntimeStatusUi();

        try
        {
            if (installRuntimeToo)
            {
                bool ok = await InstallRuntimeWithUiAsync(needLlama ? "llama" : "comfyui", holdDownloadLock: true);
                if (!ok) return;
            }

            await _hfClient.InstallCatalogBundleAsync(spec, p => Dispatcher.Invoke(() =>
            {
                CatalogDlTitleText.Text = $"[{p.FileIndex}/{p.TotalFiles}] {p.CurrentFile} — {p.StatusText}";
                CatalogDlStatsText.Text = p.SpeedMBs > 0.05
                    ? $"{p.Percent:F1}% • {p.DownloadedMB / 1024.0:F2}/{p.TotalMB / 1024.0:F2} ГБ • {p.SpeedMBs:F1} МБ/с"
                    : $"{p.Percent:F1}% • {p.StatusText}";
                CatalogDlBar.Value = p.Percent;
                StatusFooterText.Text = $"Скачивание {p.CurrentFile}: {p.Percent:F1}% ({p.SpeedMBs:F1} МБ/с)";
            }), _catalogDlCts.Token);

            StatusFooterText.Text = $"Модель «{spec.Name}» успешно установлена и проверена по SHA-256!";
            LoadModelsTable();
            _ = RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            StatusFooterText.Text = "Скачивание остановлено (прогресс сохранён в .part — можно докачать позже).";
        }
        catch (HfAccessRequiredException hfEx)
        {
            StatusFooterText.Text = hfEx.Message;
            HandleHfAccessRequired(hfEx.RepoId, hfEx.HasToken, hfEx.StatusCode);
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка скачивания: {ex.Message}";
            string repoUrl = spec.Files.Length > 0
                ? $"https://huggingface.co/{spec.Files[0].RepoId}/tree/main"
                : "https://huggingface.co/models";
            string destFolder = spec.Category == "text" ? HubConfig.LlmModelsDir : HubConfig.ComfyModelsDir;

            var openSite = HubDialogWindow.Show(
                $"Не удалось скачать «{spec.Name}» автоматически:\n{ex.Message}\n\n" +
                $"Открыть страницу модели на Hugging Face в браузере (и целевую папку), чтобы скачать файл вручную?\n" +
                $"Папка назначения: {destFolder}",
                "Ошибка загрузки — скачать вручную?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (openSite == MessageBoxResult.Yes)
            {
                AiServiceManager.OpenExternal(repoUrl);
                try { Directory.CreateDirectory(destFolder); AiServiceManager.OpenExternal(destFolder); } catch { }
            }
        }
        finally
        {
            _catalogDownloading = false;
            CatalogDownloadCard.Visibility = Visibility.Collapsed;
            RenderCatalogRecommendations();
            RefreshRuntimeStatusUi();
            _ = RefreshStatusAsync();
        }
    }

    private void CatalogCancelDlBtn_Click(object sender, RoutedEventArgs e)
    {
        _catalogDlCts?.Cancel();
        CatalogDlStatsText.Text = "Отмена...";
    }

    // ---- GPU Wiki ----
    internal void WikiSeriesCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        string series = (WikiSeriesCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        LoadGpuWikiGrid(series);
    }

    private void LoadGpuWikiGrid(string series)
    {
        GpuWikiGrid.ItemsSource = series == "all"
            ? HardwareAdvisor.GpuWiki
            : HardwareAdvisor.GpuWiki.Where(g => g.Series == series).ToList();
    }

    // ==================== SETTINGS, HF AUTH & MCP ====================

    private void LoadSettingsForm()
    {
        var cfg = HubConfig.Current;
        CfgLlmRootBox.Text = cfg.LlmRoot;
        CfgMediaRootBox.Text = cfg.MediaRoot;
        CfgStoriesRootBox.Text = cfg.StoriesRoot;
        CfgLlmCtxBox.Text = cfg.LlmCtxSize.ToString();
        CfgLlmFitBox.Text = cfg.LlmFitTargetMiB.ToString();
        CfgLlmTempBox.Text = cfg.LlmTemperature.ToString("0.0#", CultureInfo.InvariantCulture);
        CfgLlmPresenceBox.Text = cfg.LlmPresencePenalty.ToString("0.0#", CultureInfo.InvariantCulture);
        CfgLlmDryBox.Text = cfg.LlmDryMultiplier.ToString("0.0#", CultureInfo.InvariantCulture);
        CfgLlmThinkBudgetBox.Text = cfg.LlmReasoningBudget.ToString();
        CfgWanShiftBox.Text = cfg.WanShift.ToString("0.0#", CultureInfo.InvariantCulture);
        CfgSviHighBox.Text = cfg.WanSviHighLightningStrength.ToString("0.0#", CultureInfo.InvariantCulture);
        CfgSviTailBox.Text = cfg.WanSviTailFrames.ToString();

        McpConfigJsonBox.Text = HubConfig.BuildClaudeDesktopMcpJson();
        bool mcpExists = File.Exists(HubConfig.McpExePath);
        McpStatusText.Text = mcpExists ? "● MCP готов (LocalAIHub.Mcp.exe)" : "○ LocalAIHub.Mcp.exe не найден";
        McpStatusText.Foreground = BrushFromHex(mcpExists ? "#6AE89E" : "#FFB35C");
    }

    private async Task RefreshHfAuthStatusAsync()
    {
        var info = await _hfClient.VerifyTokenAsync();
        _hfConnectedCached = info.Connected;
        _hfUserCached = info.Username;
        if (info.Connected)
        {
            HfStatusBadgeText.Text = $"● Подключён: @{info.Username}";
            HfStatusBadgeText.Foreground = BrushFromHex("#6AE89E");
        }
        else
        {
            HfStatusBadgeText.Text = $"○ {info.Error ?? "Не подключён"}";
            HfStatusBadgeText.Foreground = BrushFromHex("#8C96AD");
        }
    }

    internal async void HfConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        string tok = HfTokenBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(tok))
        {
            await RefreshHfAuthStatusAsync();
            return;
        }
        HfStatusBadgeText.Text = "Проверка токена...";
        var info = await _hfClient.VerifyTokenAsync(tok);
        if (info.Connected)
        {
            _hfConnectedCached = true;
            _hfUserCached = info.Username;
            HubConfig.SaveHuggingFaceToken(tok);
            HfTokenBox.Password = "";
            HfStatusBadgeText.Text = $"● Подключён: @{info.Username}";
            HfStatusBadgeText.Foreground = BrushFromHex("#6AE89E");
            StatusFooterText.Text = $"Hugging Face подключён как @{info.Username}!";
        }
        else
        {
            HfStatusBadgeText.Text = $"✕ {info.Error}";
            HfStatusBadgeText.Foreground = BrushFromHex("#FF877C");
            StatusFooterText.Text = $"Ошибка авторизации HF: {info.Error}";
        }
    }

    internal async void HfDisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        HubConfig.SaveHuggingFaceToken(null);
        HfTokenBox.Password = "";
        await RefreshHfAuthStatusAsync();
        StatusFooterText.Text = "Токен Hugging Face удалён.";
    }

    internal void HfOpenTokensPageBtn_Click(object sender, RoutedEventArgs e) =>
        AiServiceManager.OpenExternal("https://huggingface.co/settings/tokens");

    internal void CopyMcpJsonBtn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(McpConfigJsonBox.Text);
        StatusFooterText.Text = "Конфиг MCP скопирован! Вставь в Claude Desktop, Cursor, Antigravity или VS Code.";
    }

    private void RefreshRuntimeStatusUi()
    {
        var st = RuntimeInstaller.GetStatus();
        RuntimeLlamaStatusText.Text = st.LlamaInstalled
            ? $"● Установлен: {st.LlamaExePath}"
            : $"⚠ Не установлен — нужен для запуска текстовых LLM ({HubConfig.LlmRuntimeDir})";
        RuntimeLlamaStatusText.Foreground = BrushFromHex(st.LlamaInstalled ? "#6AE89E" : "#F5CB5C");
        InstallLlamaBtn.Content = st.LlamaInstalled ? "Обновить llama.cpp" : "Установить llama.cpp (~620 МБ)";
        InstallLlamaBtn.Style = (Style)FindResource(st.LlamaInstalled ? "DarkButton" : "SuccessButton");
        InstallLlamaBtn.IsEnabled = !_catalogDownloading;

        RuntimeComfyStatusText.Text = st.ComfyInstalled
            ? $"● Установлен: {st.ComfyRootDir}"
            : $"⚠ Не установлен — нужен для фото, видео и историй ({HubConfig.ComfyRootDir})";
        RuntimeComfyStatusText.Foreground = BrushFromHex(st.ComfyInstalled ? "#6AE89E" : "#F5CB5C");
        InstallComfyBtn.Content = st.ComfyInstalled ? "Обновить ComfyUI" : "Установить ComfyUI (~2.0 ГБ)";
        InstallComfyBtn.Style = (Style)FindResource(st.ComfyInstalled ? "DarkButton" : "SuccessButton");
        InstallComfyBtn.IsEnabled = !_catalogDownloading;
    }

    internal async void InstallLlamaBtn_Click(object sender, RoutedEventArgs e) =>
        await InstallRuntimeWithUiAsync("llama");

    internal async void InstallComfyBtn_Click(object sender, RoutedEventArgs e) =>
        await InstallRuntimeWithUiAsync("comfyui");

    internal void RegisterAntigravityMcpBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string agyCfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "config", "mcp_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(agyCfg)!);

            JsonObject root = File.Exists(agyCfg) && !string.IsNullOrWhiteSpace(File.ReadAllText(agyCfg, Encoding.UTF8))
                ? (JsonNode.Parse(File.ReadAllText(agyCfg, Encoding.UTF8)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            servers["local-ai-hub"] = new JsonObject
            {
                ["command"] = HubConfig.McpExePath,
                ["args"] = new JsonArray()
            };

            File.WriteAllText(agyCfg, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            StatusFooterText.Text = $"MCP-сервер 'local-ai-hub' добавлен в {agyCfg}!";
            HubDialogWindow.Show(
                $"MCP-сервер «local-ai-hub» успешно прописан в:\n{agyCfg}\n\nОткрой новый чат в Antigravity — появятся все инструменты управления Local AI Hub.",
                "MCP подключён в Antigravity", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HubDialogWindow.Show(ex.Message, "Ошибка подключения MCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal void RegisterClaudeMcpBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string claudeCfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(claudeCfg)!);

            JsonObject root = File.Exists(claudeCfg)
                ? (JsonNode.Parse(File.ReadAllText(claudeCfg, Encoding.UTF8)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            servers["local-ai-hub"] = new JsonObject
            {
                ["command"] = HubConfig.McpExePath,
                ["args"] = new JsonArray()
            };

            File.WriteAllText(claudeCfg, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            StatusFooterText.Text = $"MCP-сервер 'local-ai-hub' добавлен в {claudeCfg}! Перезапусти Claude Desktop.";
            HubDialogWindow.Show(
                $"MCP-сервер «local-ai-hub» успешно прописан в:\n{claudeCfg}\n\nПерезапусти Claude Desktop, чтобы появились 12 инструментов управления Local AI Hub.",
                "MCP подключён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HubDialogWindow.Show(ex.Message, "Ошибка подключения MCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal async void TestMcpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(HubConfig.McpExePath))
        {
            HubDialogWindow.Show($"Файл {HubConfig.McpExePath} не найден.", "Проверка MCP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TestMcpBtn.IsEnabled = false;
        StatusFooterText.Text = "Проверка JSON-RPC рукопожатия LocalAIHub.Mcp.exe...";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = HubConfig.McpExePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить LocalAIHub.Mcp.exe");
            await proc.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"hub-test\",\"version\":\"1.0\"}}}");
            await proc.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            proc.StandardInput.Close();

            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                using var doc = JsonDocument.Parse(lines[1]);
                int count = doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength();
                McpStatusText.Text = $"● MCP проверен: {count} инструментов";
                McpStatusText.Foreground = BrushFromHex("#6AE89E");
                StatusFooterText.Text = $"MCP-сервер отвечает корректно (JSON-RPC 2.0, {count} инструментов)!";
            }
            else
            {
                throw new InvalidOperationException("MCP не вернул ожидаемый JSON-RPC ответ.");
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = "✕ Ошибка MCP";
            McpStatusText.Foreground = BrushFromHex("#FF877C");
            StatusFooterText.Text = $"Ошибка проверки MCP: {ex.Message}";
        }
        finally
        {
            TestMcpBtn.IsEnabled = true;
        }
    }

    internal void SaveHubSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var cfg = HubConfig.Current;
        if (!string.IsNullOrWhiteSpace(CfgLlmRootBox.Text)) cfg.LlmRoot = CfgLlmRootBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(CfgMediaRootBox.Text)) cfg.MediaRoot = CfgMediaRootBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(CfgStoriesRootBox.Text)) cfg.StoriesRoot = CfgStoriesRootBox.Text.Trim();

        if (int.TryParse(CfgLlmCtxBox.Text.Trim(), out int ctx)) cfg.LlmCtxSize = Math.Max(0, ctx);
        if (int.TryParse(CfgLlmFitBox.Text.Trim(), out int fit)) cfg.LlmFitTargetMiB = Math.Max(0, fit);
        if (double.TryParse(CfgLlmTempBox.Text.Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out double temp)) cfg.LlmTemperature = Math.Clamp(temp, 0, 2);
        if (double.TryParse(CfgLlmPresenceBox.Text.Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out double pres)) cfg.LlmPresencePenalty = Math.Clamp(pres, 0, 2);
        if (double.TryParse(CfgLlmDryBox.Text.Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out double dry)) cfg.LlmDryMultiplier = Math.Clamp(dry, 0, 2);
        if (int.TryParse(CfgLlmThinkBudgetBox.Text.Trim(), out int tb)) cfg.LlmReasoningBudget = Math.Max(-1, tb);
        if (double.TryParse(CfgWanShiftBox.Text.Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out double sh)) cfg.WanShift = Math.Clamp(sh, 2, 10);
        if (double.TryParse(CfgSviHighBox.Text.Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out double sv)) cfg.WanSviHighLightningStrength = Math.Clamp(sv, 0.4, 1.2);
        if (int.TryParse(CfgSviTailBox.Text.Trim(), out int tail)) cfg.WanSviTailFrames = Math.Clamp(tail, 1, 9);

        HubConfig.Save();
        DiskLabelText.Text = $"Свободно на диске {HubConfig.DataDriveLetter}:";
        LoadModelsTable();
        RefreshRuntimeStatusUi();
        RenderCatalogRecommendations();
        _ = RefreshStatusAsync();
        StatusFooterText.Text = $"Настройки сохранены в {HubConfig.ConfigFilePath}!";
    }

    internal void ApplyQwenAntiLoopBtn_Click(object sender, RoutedEventArgs e)
    {
        var cfg = HubConfig.Current;
        cfg.LlmTemperature = 1.0;
        cfg.LlmTopP = 0.95;
        cfg.LlmTopK = 20;
        cfg.LlmMinP = 0.0;
        cfg.LlmPresencePenalty = 1.5;
        cfg.LlmRepeatPenalty = 1.0;
        cfg.LlmDryMultiplier = 0.5;
        cfg.LlmReasoningBudget = 4096;
        HubConfig.Save();
        LoadSettingsForm();
        StatusFooterText.Text = "Применён профиль Qwen Анти-луп (temp=1.0, presence=1.5, dry=0.5, think-лимит=4096). Перезапусти LLM!";
    }

    internal void OpenConfigJsonBtn_Click(object sender, RoutedEventArgs e)
    {
        HubConfig.Save();
        AiServiceManager.OpenExternal(HubConfig.ConfigFilePath);
    }
}

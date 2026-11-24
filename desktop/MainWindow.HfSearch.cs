using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    // ---- Live Hugging Face Hub Search (Smart Beginner-Friendly Mode) ----

    private List<HfRepoSearchItem> _hfSearchResults = [];
    private List<HfRepoFileItem> _hfCurrentFiles = [];
    private List<HfDisplayFileGroup> _hfDisplayedFileGroups = [];
    private string _hfSearchMode = "llm"; // llm | lora | media | all
    private string? _hfSelectedRepo;

    private sealed class HfDisplayFileGroup
    {
        public required string DisplayName { get; init; }
        public required List<HfRepoFileItem> Parts { get; init; }
        public long TotalBytes => Parts.Sum(p => p.SizeBytes);
        public double TotalGB => TotalBytes / (1024.0 * 1024.0 * 1024.0);
        public required string FitBadge { get; init; }
        public required string FitBgHex { get; init; }
        public required string FitFgHex { get; init; }
        public required bool FitsWithoutSwap { get; init; }
        public required string QuantHint { get; init; }
        public required string DetailedHint { get; init; }
        public required int SortTier { get; init; } // 0 = fits GPU, 1 = fits GPU+RAM, 2 = mmproj/aux, 3 = too heavy
    }

    private static readonly Regex SplitGgufRegex = new(
        @"^(?<prefix>.+)-(?<part>\d{5})-of-(?<total>\d{5})\.gguf$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal void HfSearchModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string mode) return;
        SetHfSearchMode(mode);
        if (!string.IsNullOrWhiteSpace(HfSearchQueryBox.Text))
            HfSearchBtn_Click(this, new RoutedEventArgs());
    }

    private void SetHfSearchMode(string mode)
    {
        _hfSearchMode = mode;
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");
        HfModeLlmBtn.Style = mode == "llm" ? primary : dark;
        HfModeLoraBtn.Style = mode == "lora" ? primary : dark;
        HfModeMediaBtn.Style = mode == "media" ? primary : dark;
        HfModeAllBtn.Style = mode == "all" ? primary : dark;

        switch (mode)
        {
            case "llm":
                SelectComboByTag(HfTargetFolderCombo, "llm_models");
                HfModeHintText.Text = "Автофильтр: скрываем Mac/MLX и сырые серверные .safetensors — оставляем готовые GGUF.";
                break;
            case "lora":
                SelectComboByTag(HfTargetFolderCombo, "loras");
                HfModeHintText.Text = "Поиск LoRA (.safetensors) для фото и видео — скачается прямо в ComfyUI\\models\\loras.";
                break;
            case "media":
                SelectComboByTag(HfTargetFolderCombo, "diffusion_models");
                HfModeHintText.Text = "Поиск фото/видео моделей (FLUX, Wan, Z-Image, LTX) — папка Diffusion / Video.";
                break;
            default:
                HfModeHintText.Text = "Показываются все репозитории и форматы Hugging Face без фильтрации.";
                break;
        }
    }

    internal void HfQuickChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        string q = btn.Tag as string ?? btn.Content?.ToString() ?? "";
        if (q.Contains("lora", StringComparison.OrdinalIgnoreCase))
            SetHfSearchMode("lora");
        else if (_hfSearchMode != "llm")
            SetHfSearchMode("llm");

        HfSearchQueryBox.Text = q;
        HfSearchBtn_Click(this, new RoutedEventArgs());
    }

    internal void HfSearchQueryBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            HfSearchBtn_Click(this, new RoutedEventArgs());
        }
    }

    internal void HfOpenRepoWebBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_hfSelectedRepo))
            AiServiceManager.OpenExternal($"https://huggingface.co/{_hfSelectedRepo}");
    }

    internal void HfOnlyFitCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(_hfSelectedRepo)) return;
        RenderHfFileList();
    }

    internal async void HfSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        string rawQuery = HfSearchQueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawQuery)) return;

        // Auto-append format keyword so HF API returns directly usable repos
        string apiQuery = rawQuery;
        if (_hfSearchMode == "llm" && !rawQuery.Contains("gguf", StringComparison.OrdinalIgnoreCase))
            apiQuery += " GGUF";
        else if (_hfSearchMode == "lora" && !rawQuery.Contains("lora", StringComparison.OrdinalIgnoreCase))
            apiQuery += " lora";

        HfSearchBtn.IsEnabled = false;
        HfOpenRepoWebBtn.Visibility = Visibility.Collapsed;
        HfSelectedFileHintBorder.Visibility = Visibility.Collapsed;
        StatusFooterText.Text = $"Поиск «{apiQuery}» на Hugging Face...";
        try
        {
            var rawResults = await _hfClient.SearchModelsAsync(apiQuery, 30);

            if (_hfSearchMode == "llm")
            {
                // Keep GGUF repos, hide Apple MLX and raw non-GGUF server checkpoints
                var ggufOnly = rawResults.Where(r =>
                    r.RepoId.Contains("gguf", StringComparison.OrdinalIgnoreCase) &&
                    !r.RepoId.Contains("-mlx", StringComparison.OrdinalIgnoreCase) &&
                    !r.RepoId.Contains("mlx-", StringComparison.OrdinalIgnoreCase)).ToList();

                _hfSearchResults = ggufOnly.Count > 0 ? ggufOnly : rawResults;
            }
            else if (_hfSearchMode == "lora")
            {
                _hfSearchResults = rawResults.Where(r =>
                    !r.RepoId.Contains("-mlx", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                _hfSearchResults = rawResults;
            }

            HfReposList.Items.Clear();
            foreach (var r in _hfSearchResults)
                HfReposList.Items.Add(BuildHfRepoListItem(r));

            HfFilesList.Items.Clear();
            _hfCurrentFiles = [];
            _hfDisplayedFileGroups = [];
            _hfSelectedRepo = null;
            HfReposHeaderText.Text = $"Шаг 1. Репозитории ({_hfSearchResults.Count}) — кликни по нужному";
            HfFilesHeaderText.Text = "Шаг 2. Выбери файл под свою видеокарту";
            StatusFooterText.Text = _hfSearchResults.Count > 0
                ? $"Найдено подходящих репозиториев: {_hfSearchResults.Count}. Кликни по строке слева."
                : "Ничего не найдено — попробуй упростить запрос или переключить режим на «Все форматы».";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка поиска HF: {ex.Message}";
        }
        finally
        {
            HfSearchBtn.IsEnabled = true;
        }
    }

    private ListBoxItem BuildHfRepoListItem(HfRepoSearchItem r)
    {
        string lower = r.RepoId.ToLowerInvariant();
        var root = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };

        root.Children.Add(new TextBlock
        {
            Text = r.RepoId,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#F4F4F6"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        void AddBadge(string txt, string bg, string fg)
        {
            meta.Children.Add(new Border
            {
                Background = BrushFromHex(bg),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 5, 2),
                Child = new TextBlock
                {
                    Text = txt,
                    FontSize = 9.5,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFromHex(fg)
                }
            });
        }

        if (lower.Contains("gguf"))
            AddBadge("GGUF ✓", "#1C1C22", "#E4E4E8");
        else if (lower.Contains("mlx"))
            AddBadge("MLX / MAC", "#261416", "#F87171");
        else if (lower.Contains("lora"))
            AddBadge("LORA", "#11241C", "#34D399");

        if (lower.Contains("abliterated") || lower.Contains("uncensored") || lower.Contains("hauhaucs") || lower.Contains("heretic"))
            AddBadge("UNCENSORED", "#24140F", "#FF521B");
        if (lower.Contains("thinking") || lower.Contains("r1"))
            AddBadge("THINK", "#19191F", "#A1A1AA");
        if (lower.Contains("a3b") || lower.Contains("moe"))
            AddBadge("MOE", "#11241C", "#34D399");
        if (r.Gated)
            AddBadge("GATED", "#241D0E", "#F59E0B");

        meta.Children.Add(new TextBlock
        {
            Text = $"↓ {r.Downloads:N0}",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = BrushFromHex("#71717A"),
            VerticalAlignment = VerticalAlignment.Center
        });

        root.Children.Add(meta);
        return new ListBoxItem { Content = root, Tag = r, Padding = new Thickness(6, 4, 6, 4) };
    }

    internal async void HfReposList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HfReposList.SelectedItem is not ListBoxItem lbi || lbi.Tag is not HfRepoSearchItem repo) return;
        _hfSelectedRepo = repo.RepoId;
        HfOpenRepoWebBtn.Visibility = Visibility.Visible;
        HfSelectedFileHintBorder.Visibility = Visibility.Collapsed;
        HfFilesHeaderText.Text = $"Загрузка файлов {_hfSelectedRepo}...";
        HfFilesList.Items.Clear();

        // Auto-select target folder based on repo name
        string lowerRepo = _hfSelectedRepo.ToLowerInvariant();
        if (lowerRepo.Contains("gguf") && !lowerRepo.Contains("wan") && !lowerRepo.Contains("flux"))
            SelectComboByTag(HfTargetFolderCombo, "llm_models");
        else if (lowerRepo.Contains("lora"))
            SelectComboByTag(HfTargetFolderCombo, "loras");

        try
        {
            _hfCurrentFiles = await _hfClient.ListModelFilesAsync(_hfSelectedRepo);
            RenderHfFileList();
        }
        catch (HfAccessRequiredException hfEx)
        {
            HfFilesHeaderText.Text = $"🔒 Закрытый репозиторий: {_hfSelectedRepo} (нужна лицензия + HF-токен)";
            StatusFooterText.Text = hfEx.Message;
            HandleHfAccessRequired(hfEx.RepoId, hfEx.HasToken, hfEx.StatusCode);
        }
        catch (Exception ex)
        {
            HfFilesHeaderText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private void RenderHfFileList()
    {
        HfFilesList.Items.Clear();
        _hfDisplayedFileGroups = [];
        HfSelectedFileHintBorder.Visibility = Visibility.Collapsed;
        HfDownloadSelectedBtn.Content = "Скачать выбранный файл";

        var hw = GetActiveCatalogHardware();
        double vramGb = Math.Round(hw.DetectedVramGB);
        double ramGb = Math.Round(hw.DetectedRamGB);
        double vramFitMax = Math.Max(4.0, hw.DetectedVramGB - 1.5);
        double ramHeadroom = Math.Max(4.0, hw.DetectedRamGB - 10.0);
        double totalNoSwapMax = vramFitMax + ramHeadroom;

        string repoLower = (_hfSelectedRepo ?? "").ToLowerInvariant();
        bool isMoeRepo = repoLower.Contains("a3b") || repoLower.Contains("moe") || repoLower.Contains("mixtral") || repoLower.Contains("80b-a3b") || repoLower.Contains("35b-a3b");

        // Filter raw file list by search mode
        IEnumerable<HfRepoFileItem> filteredRaw = _hfCurrentFiles;
        if (_hfSearchMode == "llm")
        {
            var ggufs = _hfCurrentFiles.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToList();
            if (ggufs.Count > 0) filteredRaw = ggufs;
        }
        else if (_hfSearchMode == "lora")
        {
            var safes = _hfCurrentFiles.Where(f => f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).ToList();
            if (safes.Count > 0) filteredRaw = safes;
        }

        // Group multi-part split GGUFs (e.g. *-00001-of-00002.gguf + *-00002-of-00002.gguf) into 1 clickable bundle
        var splitBuckets = new Dictionary<string, List<HfRepoFileItem>>(StringComparer.OrdinalIgnoreCase);
        var singles = new List<HfRepoFileItem>();
        foreach (var f in filteredRaw)
        {
            var m = SplitGgufRegex.Match(f.Path);
            if (m.Success)
            {
                string key = m.Groups["prefix"].Value;
                if (!splitBuckets.TryGetValue(key, out var list))
                {
                    list = [];
                    splitBuckets[key] = list;
                }
                list.Add(f);
            }
            else
            {
                singles.Add(f);
            }
        }

        var groups = new List<HfDisplayFileGroup>();
        void AddGroup(string displayName, List<HfRepoFileItem> parts)
        {
            parts = parts.OrderBy(p => p.Path).ToList();
            long bytes = parts.Sum(p => p.SizeBytes);
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            string nameLower = displayName.ToLowerInvariant();
            bool isMmproj = nameLower.Contains("mmproj");
            bool isLora = _hfSearchMode == "lora" || nameLower.Contains("lora") || (displayName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) && gb < 1.2);
            bool isRawSharded = nameLower.Contains("model-000") && displayName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);

            string quantHint = DescribeQuantization(displayName, parts.Count);
            string badge, bg, fg, detail;
            bool fits;
            int tier;

            if (isMmproj)
            {
                badge = "VISION (MMPROJ)";
                bg = "#1A1A20"; fg = "#E4E4E8"; fits = true; tier = 2;
                detail = "Дополнительный файл зрения (чтобы LLM понимала картинки). Основную модель нужно скачать отдельно.";
            }
            else if (isRawSharded)
            {
                badge = "▲ СЫРОЙ ШАРД";
                bg = "#261416"; fg = "#F87171"; fits = false; tier = 3;
                detail = "Это часть сырой серверной модели (.safetensors). Для запуска на ПК переключись слева на репозиторий с «-GGUF» в названии.";
            }
            else if (isLora)
            {
                badge = "✓ LORA";
                bg = "#11241C"; fg = "#34D399"; fits = true; tier = 0;
                detail = $"Лёгкая LoRA ({gb * 1024:F0} МБ) — сохранится в ComfyUI\\models\\loras и сразу появится в выборе Доп. LoRA в Фото/Видео.";
            }
            else if (gb <= vramFitMax)
            {
                badge = "■ В GPU";
                bg = "#11241C"; fg = "#34D399"; fits = true; tier = 0;
                detail = $"Файл ({gb:F1} ГБ) целиком поместится в {vramGb:F0} ГБ VRAM — максимальная скорость генерации.";
            }
            else if (gb <= totalNoSwapMax + (isMoeRepo ? 2.5 : 0.0))
            {
                badge = isMoeRepo ? "◧ GPU+RAM (20–35 tok/s)" : "◧ GPU+RAM (3–6 tok/s)";
                bg = "#241D0E";
                fg = "#F59E0B";
                fits = true; tier = 1;
                detail = isMoeRepo
                    ? $"Поместится в связку {vramGb:F0} ГБ GPU + {ramGb:F0} ГБ ОЗУ ({gb:F1} ГБ). MoE-архитектура (~3B активных параметров) сохранит скорость ~20–35 tok/s."
                    : $"Поместится в {vramGb:F0} ГБ GPU + {ramGb:F0} ГБ ОЗУ, но часть слоёв пойдёт через оперативку (~3–6 tok/s).";
            }
            else
            {
                badge = "□ SWAP";
                bg = "#261416"; fg = "#F87171"; fits = false; tier = 3;
                detail = $"Слишком тяжёлый файл ({gb:F1} ГБ) для {vramGb:F0} ГБ VRAM + {ramGb:F0} ГБ RAM (безопасный потолок без SSD-свопа ~{totalNoSwapMax:F0} ГБ). Выбери более компактный квант.";
            }

            if (parts.Count > 1)
                detail += $" Комплект из {parts.Count} частей — Хаб скачает все части автоматически подряд.";

            groups.Add(new HfDisplayFileGroup
            {
                DisplayName = displayName,
                Parts = parts,
                FitBadge = badge,
                FitBgHex = bg,
                FitFgHex = fg,
                FitsWithoutSwap = fits,
                QuantHint = quantHint,
                DetailedHint = detail,
                SortTier = tier
            });
        }

        foreach (var f in singles)
            AddGroup(Path.GetFileName(f.Path), [f]);

        foreach (var kv in splitBuckets)
        {
            string baseName = Path.GetFileName(kv.Key) + $" ({kv.Value.Count} части)";
            AddGroup(baseName, kv.Value);
        }

        bool onlyFit = HfOnlyFitCheck?.IsChecked == true;
        var sorted = groups
            .Where(g => !onlyFit || g.FitsWithoutSwap)
            .OrderBy(g => g.SortTier)
            .ThenByDescending(g => g.TotalBytes) // best quality within each fit tier first!
            .ToList();

        _hfDisplayedFileGroups = sorted;
        foreach (var g in sorted)
            HfFilesList.Items.Add(BuildHfFileListItem(g));

        int fitCount = groups.Count(g => g.FitsWithoutSwap && g.SortTier < 2);
        HfFilesHeaderText.Text = $"{_hfSelectedRepo} — файлов: {sorted.Count} (подходит под твой ПК: {fitCount})";

        if (sorted.Count == 0 && groups.Count > 0 && onlyFit)
        {
            HfSelectedFileHintBorder.Visibility = Visibility.Visible;
            HfSelectedFileHintText.Text = $"Все файлы в этом репозитории тяжелее безопасного потолка (~{totalNoSwapMax:F0} ГБ) для твоего ПК. Сними галочку «Только подходящие», чтобы увидеть их.";
        }
    }

    private ListBoxItem BuildHfFileListItem(HfDisplayFileGroup g)
    {
        var root = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        var top = new DockPanel();

        var rightPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(rightPanel, Dock.Right);
        rightPanel.Children.Add(new TextBlock
        {
            Text = g.TotalGB >= 1.0 ? $"{g.TotalGB:F2} ГБ" : $"{g.TotalGB * 1024:F0} МБ",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = BrushFromHex("#D4D4D8"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        rightPanel.Children.Add(new Border
        {
            Background = BrushFromHex(g.FitBgHex),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock
            {
                Text = g.FitBadge,
                FontSize = 9.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex(g.FitFgHex)
            }
        });
        top.Children.Add(rightPanel);

        top.Children.Add(new TextBlock
        {
            Text = g.DisplayName,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#F4F4F6"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(top);

        if (!string.IsNullOrWhiteSpace(g.QuantHint))
        {
            root.Children.Add(new TextBlock
            {
                Text = g.QuantHint,
                FontSize = 10.5,
                Foreground = BrushFromHex("#A1A1AA"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        return new ListBoxItem { Content = root, Tag = g, Padding = new Thickness(6, 4, 6, 4) };
    }

    private static string DescribeQuantization(string name, int partsCount)
    {
        string u = name.ToUpperInvariant();
        string q =
            u.Contains("MMPROJ") ? "Доп. файл зрения (Vision) для картинок" :
            u.Contains("Q4_K_M") || u.Contains("Q4_K_XL") || u.Contains("UD-Q4") ? "4-бит • Золотой стандарт (лучший баланс качества и веса)" :
            u.Contains("Q5_K") || u.Contains("UD-Q5") ? "5-бит • Повышенное качество, почти без потерь" :
            u.Contains("Q6_K") || u.Contains("UD-Q6") ? "6-бит • Почти эталонное качество оригинала" :
            u.Contains("Q8_0") || u.Contains("UD-Q8") ? "8-бит • Без потерь качества (очень тяжёлый)" :
            u.Contains("BF16") || u.Contains("F16") ? "16-бит • Полный неквантованный оригинал" :
            u.Contains("IQ3_XXS") ? "3-бит (сильное сжатие) • Минимум памяти, слабее чем IQ3_XS" :
            u.Contains("IQ3_XS") || u.Contains("IQ3_S") || u.Contains("IQ3_M") ? "3-бит (IQ3) • Хорошее умное сжатие для больших моделей" :
            u.Contains("Q3_K") || u.Contains("UD-Q3") ? "3-бит (Q3) • Компактный, хорошо держит смысл" :
            u.Contains("IQ2") || u.Contains("Q2_K") || u.Contains("UD-Q2") ? "2-бит • Максимальное сжатие (экономит RAM, но ниже точность)" :
            u.Contains("IQ4") ? "4-бит (IQ4) • Компактный и точный 4-битный квант" :
            u.EndsWith(".SAFETENSORS") ? "Формат SafeTensors (ComfyUI / LoRA / Diffusion)" :
            "Файл модели GGUF";
        return partsCount > 1 ? $"{q} • Комплект из {partsCount} частей (скачаются вместе)" : q;
    }

    internal void HfFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HfFilesList.SelectedItem is not ListBoxItem lbi || lbi.Tag is not HfDisplayFileGroup g)
        {
            HfSelectedFileHintBorder.Visibility = Visibility.Collapsed;
            HfDownloadSelectedBtn.Content = "Скачать выбранный файл";
            return;
        }

        HfSelectedFileHintBorder.Visibility = Visibility.Visible;
        HfSelectedFileHintBorder.BorderBrush = BrushFromHex(g.FitFgHex);
        HfSelectedFileHintText.Text = g.DetailedHint;

        string sizeTxt = g.TotalGB >= 1.0 ? $"{g.TotalGB:F1} ГБ" : $"{g.TotalGB * 1024:F0} МБ";
        HfDownloadSelectedBtn.Content = g.Parts.Count > 1
            ? $"Скачать комплект ({g.Parts.Count} ч. • {sizeTxt})"
            : $"Скачать ({sizeTxt})";
    }

    internal async void HfDownloadSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogDownloading) return;
        if (string.IsNullOrWhiteSpace(_hfSelectedRepo) ||
            HfFilesList.SelectedItem is not ListBoxItem lbi ||
            lbi.Tag is not HfDisplayFileGroup group)
        {
            StatusFooterText.Text = "Сначала выбери файл в списке справа.";
            return;
        }

        string folderTag = (HfTargetFolderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "llm_models";
        string ResolveDest(string fileName) => folderTag switch
        {
            "llm_models" => Path.Combine(HubConfig.LlmModelsDir, fileName),
            "diffusion_models" => Path.Combine(HubConfig.ComfyModelsDir, "diffusion_models", fileName),
            "loras" => Path.Combine(HubConfig.ComfyModelsDir, "loras", fileName),
            "text_encoders" => Path.Combine(HubConfig.ComfyModelsDir, "text_encoders", fileName),
            "vae" => Path.Combine(HubConfig.ComfyModelsDir, "vae", fileName),
            _ => Path.Combine(HubConfig.ComfyModelsDir, "upscale_models", fileName)
        };

        bool needLlama = folderTag == "llm_models" && !RuntimeInstaller.IsLlamaInstalled();
        bool needComfy = folderTag != "llm_models" && !RuntimeInstaller.IsComfyInstalled();
        bool installRuntimeToo = false;
        if (needLlama || needComfy)
        {
            string rtName = needLlama ? "llama.cpp (~620 МБ)" : "ComfyUI Portable (~2.0 ГБ)";
            var choice = HubDialogWindow.Show(
                $"Для запуска «{group.DisplayName}» понадобится движок {rtName}, которого пока нет на этом ПК.\n\nУстановить движок автоматически вместе с файлом модели?",
                "Требуется движок",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            installRuntimeToo = choice == MessageBoxResult.Yes;
        }

        _catalogDownloading = true;
        _catalogDlCts?.Dispose();
        _catalogDlCts = new CancellationTokenSource();
        CatalogDownloadCard.Visibility = Visibility.Visible;

        string lastDest = "";
        try
        {
            if (installRuntimeToo)
            {
                bool ok = await InstallRuntimeWithUiAsync(needLlama ? "llama" : "comfyui", holdDownloadLock: true);
                if (!ok) return;
            }

            int totalParts = group.Parts.Count;
            for (int i = 0; i < totalParts; i++)
            {
                var part = group.Parts[i];
                string fileName = Path.GetFileName(part.Path);
                string destPath = ResolveDest(fileName);
                lastDest = destPath;

                await _hfClient.DownloadSingleFileAsync(fileName, _hfSelectedRepo, part.Path, destPath, i + 1, totalParts, p => Dispatcher.Invoke(() =>
                {
                    CatalogDlTitleText.Text = totalParts > 1
                        ? $"[{i + 1}/{totalParts}] {p.CurrentFile} — {p.StatusText}"
                        : $"{p.CurrentFile} — {p.StatusText}";
                    CatalogDlStatsText.Text = $"{p.Percent:F1}% • {p.DownloadedMB / 1024.0:F2}/{p.TotalMB / 1024.0:F2} ГБ • {p.SpeedMBs:F1} МБ/с";
                    CatalogDlBar.Value = p.Percent;
                    StatusFooterText.Text = $"Скачивание {p.CurrentFile}: {p.Percent:F1}% ({p.SpeedMBs:F1} МБ/с)";
                }), _catalogDlCts.Token);
            }

            StatusFooterText.Text = totalParts > 1
                ? $"Готово! Все {totalParts} части «{group.DisplayName}» скачаны и готовы к запуску."
                : $"Готово! Файл сохранён: {lastDest}";
            LoadModelsTable();
            _ = RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            StatusFooterText.Text = "Скачивание отменено (.part сохранён для докачки).";
        }
        catch (HfAccessRequiredException hfEx)
        {
            StatusFooterText.Text = hfEx.Message;
            HandleHfAccessRequired(hfEx.RepoId, hfEx.HasToken, hfEx.StatusCode);
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка загрузки: {ex.Message}";
            string fileUrl = $"https://huggingface.co/{_hfSelectedRepo}/tree/main";
            string destDir = Path.GetDirectoryName(lastDest) ?? HubConfig.LlmModelsDir;
            var openSite = HubDialogWindow.Show(
                $"Не удалось скачать файл автоматически:\n{ex.Message}\n\n" +
                $"Открыть страницу модели на Hugging Face в браузере, чтобы скачать вручную?\n" +
                $"Сохранить в папку: {destDir}",
                "Ошибка Hugging Face — скачать вручную?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (openSite == MessageBoxResult.Yes)
            {
                AiServiceManager.OpenExternal(fileUrl);
                try { Directory.CreateDirectory(destDir); AiServiceManager.OpenExternal(destDir); } catch { }
            }
        }
        finally
        {
            _catalogDownloading = false;
            CatalogDownloadCard.Visibility = Visibility.Collapsed;
            RenderCatalogRecommendations();
            RefreshRuntimeStatusUi();
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private readonly HubAssistantService _assistant = new();
    private readonly List<(string Role, string Content)> _assistantHistory = [];
    private bool _assistantBusy;
    private bool _hfConnectedCached;
    private string? _hfUserCached;
    private TabItem? _assistantReturnTab;

    private void InitAssistant()
    {
        RefreshAssistantStatusUi();
        AddAssistantWelcomeMessage();
    }

    internal void JumpAssistantBtn_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = ChatTab;
        SetChatSubMode("assistant");
        AssistantInputBox.Focus();
    }

    private void RememberReturnTab()
    {
        if (MainTabs.SelectedItem is TabItem cur && !ReferenceEquals(cur, ChatTab))
        {
            _assistantReturnTab = cur;
            string hdr = (cur.Header?.ToString() ?? "назад").Trim();
            AssistantBackBtn.Content = $"← {hdr}";
            AssistantBackBtn.Visibility = Visibility.Visible;
        }
    }

    internal void AssistantBackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_assistantReturnTab is not null)
        {
            MainTabs.SelectedItem = _assistantReturnTab;
        }
        AssistantBackBtn.Visibility = Visibility.Collapsed;
    }

    private void AskAiGlobalBtn_Click(object sender, RoutedEventArgs e)
    {
        var origin = MainTabs.SelectedItem as TabItem;
        if (ReferenceEquals(origin, ChatTab))
        {
            SetChatSubMode("assistant");
            AssistantInputBox.Focus();
            return;
        }

        RememberReturnTab();
        MainTabs.SelectedItem = ChatTab;
        SetChatSubMode("assistant");

        string screenName = origin?.Header?.ToString() ?? "Local AI Hub";
        List<AssistantAction> quickQuestions = ReferenceEquals(origin, PhotoTab)
            ? [
                new("ask", "Объясни простыми словами настройки вкладки Фото: модели, Seed, Референс и Доп. LoRA", "Настройки Фото и LoRA"),
                new("ask", "Подобрать лучшую модель для фото под мою видеокарту", "Подобрать фото-модель"),
                new("ask", "Как работает Апскейл ×4 и Референс в FLUX 3?", "Про Апскейл и Референс")
            ]
            : ReferenceEquals(origin, VideoTab)
            ? [
                new("ask", "Объясни настройки вкладки Видео: чем отличаются Wan 2.2 и LTX-2.5, что такое I2V, First→Last, SVI 2.0 Pro, Мост FLUX 3 и зачем две LoRA High и Low?", "Настройки Видео и High/Low LoRA"),
                new("ask", "Подобрать лучшую видео-модель под мою видеокарту", "Подобрать видео-модель"),
                new("ask", "Что делают параметры Wan 2.2 Shift, SVI Lightning High и SVI Хвост?", "Про Shift и SVI-настройки")
            ]
            : ReferenceEquals(origin, SystemTab)
            ? [
                new("ask", "Объясни простыми словами все настройки тюнинга: LLM Контекст, Запас VRAM, Температура, Presence Penalty, DRY, Лимит <think>, Wan Shift, SVI Lightning High и SVI Хвост", "Объяснить весь тюнинг"),
                new("ask", "Почему Qwen ходит по кругу и как настроить Анти-луп?", "Про Qwen Анти-луп"),
                new("check_network", "", "Проверить сеть и движки")
            ]
            : [
                new("ask", "Какие модели (текст, фото, видео) лучше всего поставить под мою видеокарту?", "Что поставить на мой ПК?"),
                new("ask", "Почему не скачивается модель / ошибка SSL или 403 на Hugging Face?", "Ошибки скачивания / HF"),
                new("check_network", "", "Проверить сеть и движки")
            ];

        AppendAssistantBubble(
            $"Ты открыл Помощника из раздела «{screenName}». Напиши любой вопрос про настройки или выбери подсказку ниже (кнопка «← {screenName}» сверху вернёт обратно):",
            quickQuestions,
            "Контекстная помощь",
            null);
        AssistantInputBox.Focus();
    }

    internal async void AskAiPhotoSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        RememberReturnTab();
        MainTabs.SelectedItem = ChatTab;
        SetChatSubMode("assistant");
        await SendAssistantMessageAsync("Объясни простыми словами настройки вкладки Фото: чем отличаются Z-Image, FLUX 3 и Qwen-Image 2.1, что такое Seed (-1), Референс и как пользоваться Доп. LoRA (и какую силу ставить)?");
    }

    internal async void AskAiVideoSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        RememberReturnTab();
        MainTabs.SelectedItem = ChatTab;
        SetChatSubMode("assistant");
        await SendAssistantMessageAsync("Объясни простыми словами настройки вкладки Видео: чем отличаются Wan 2.2 FP8 / Q4 и LTX-2.5, что такое I2V, First→Last, SVI 2.0 Pro, Мост FLUX 3 и зачем отдельные Доп. LoRA High и Low?");
    }

    internal async void AskAiTuningSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        RememberReturnTab();
        MainTabs.SelectedItem = ChatTab;
        SetChatSubMode("assistant");
        await SendAssistantMessageAsync("Объясни простыми словами все параметры тюнинга в Настройках: LLM Контекст, Запас VRAM, Температура, Presence Penalty, DRY Multiplier, Лимит <think>, Wan 2.2 Shift, SVI Lightning High и SVI Хвост.");
    }

    private void RefreshAssistantStatusUi()
    {
        if (AssistantStatusSubText is null) return;
        bool modelReady = HubAssistantService.IsAssistantModelInstalled();
        bool llamaReady = RuntimeInstaller.IsLlamaInstalled();

        if (modelReady && llamaReady)
        {
            AssistantStatusSubText.Text = "● Компактный AI-Помощник готов (Qwen 3 4B • 2.33 ГБ) • Если запущена основная LLM, переиспользует её без расхода VRAM.";
            AssistantStatusSubText.Foreground = BrushFromHex("#34D399");
            InstallAssistantBtn.Visibility = Visibility.Collapsed;
        }
        else if (modelReady && !llamaReady)
        {
            AssistantStatusSubText.Text = "▲ Модель Помощника скачана, но нужен движок llama.cpp — нажми кнопку справа для установки.";
            AssistantStatusSubText.Foreground = BrushFromHex("#F59E0B");
            InstallAssistantBtn.Content = "Установить llama.cpp (~620 МБ)";
            InstallAssistantBtn.Visibility = Visibility.Visible;
            InstallAssistantBtn.IsEnabled = !_catalogDownloading;
        }
        else
        {
            AssistantStatusSubText.Text = "○ Сейчас активен быстрый офлайн RAG-режим. Установи Qwen 3 4B (2.33 ГБ) — появится живой локальный AI-чат (работает даже на 4 ГБ VRAM / CPU).";
            AssistantStatusSubText.Foreground = BrushFromHex("#A1A1AA");
            InstallAssistantBtn.Content = "Установить Помощника (2.33 ГБ)";
            InstallAssistantBtn.Visibility = Visibility.Visible;
            InstallAssistantBtn.IsEnabled = !_catalogDownloading;
        }
    }

    private void AddAssistantWelcomeMessage()
    {
        AssistantMessagesPanel.Children.Clear();
        _assistantHistory.Clear();

        var initialActions = new List<AssistantAction>
        {
            new("check_network", "", "Проверить сеть и движки")
        };
        if (!HubAssistantService.IsAssistantModelInstalled())
            initialActions.Add(new("install_model", ModelCatalog.AssistantModelId, "Установить Помощника Qwen 3 4B (2.33 ГБ)"));

        AppendAssistantBubble(
            "Привет! Я встроенный AI-Помощник **Brazhnichenko Hub** с базой знаний (RAG) по твоему железу, Вики NVIDIA (10–50 серии), всем моделям (Текст / Фото / Видео) и частым ошибкам.\n\n" +
            "Напиши обычным языком, например: «хочу поставить модель для фото», «почему не скачивается ComfyUI?» или «Qwen ходит по кругу» — я подберу варианты под твою видеокарту, запущу установку и объясню, если что-то пойдёт не так.",
            initialActions,
            "RAG-Помощник",
            null);
    }

    internal void AssistantClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_assistantBusy) return;
        AddAssistantWelcomeMessage();
    }

    internal async void InstallAssistantBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogDownloading) return;
        if (!RuntimeInstaller.IsLlamaInstalled())
        {
            bool ok = await InstallRuntimeWithUiAsync("llama");
            if (!ok)
            {
                await NotifyAssistantAboutFailureAsync("Установка движка llama.cpp", StatusFooterText.Text);
                return;
            }
        }
        if (!HubAssistantService.IsAssistantModelInstalled())
        {
            await ExecuteAssistantModelInstallAsync(ModelCatalog.AssistantSpec);
        }
        RefreshAssistantStatusUi();
    }

    internal async void AssistantNetCheckBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunAssistantNetworkCheckAsync();
    }

    internal async void AssistantQuickChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Content is string text)
        {
            await SendAssistantMessageAsync(text);
        }
    }

    internal async void AssistantInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendAssistantMessageAsync(AssistantInputBox.Text);
        }
    }

    internal async void AssistantSendBtn_Click(object sender, RoutedEventArgs e)
    {
        await SendAssistantMessageAsync(AssistantInputBox.Text);
    }

    private async Task SendAssistantMessageAsync(string rawText)
    {
        string text = rawText.Trim();
        if (string.IsNullOrWhiteSpace(text) || _assistantBusy) return;

        AssistantInputBox.Clear();
        AppendUserBubble(text);

        _assistantBusy = true;
        AssistantSendBtn.IsEnabled = false;
        AssistantNetCheckBtn.IsEnabled = false;
        var thinkingBubble = AppendStatusBubble("Анализирую базу знаний (RAG) и готовлю ответ...");

        try
        {
            // Refresh HF status quietly if unknown
            if (!_hfConnectedCached && !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken()))
            {
                var info = await _hfClient.VerifyTokenAsync();
                _hfConnectedCached = info.Connected;
                _hfUserCached = info.Username;
            }

            var hw = _detectedHardware ?? GetActiveCatalogHardware();
            var reply = await _assistant.AskAsync(
                text,
                _assistantHistory,
                hw,
                _hfConnectedCached,
                _hfUserCached,
                msg => Dispatcher.Invoke(() =>
                {
                    StatusFooterText.Text = msg;
                    if (thinkingBubble.Child is TextBlock tb) tb.Text = msg;
                }));

            AssistantMessagesPanel.Children.Remove(thinkingBubble);
            _assistantHistory.Add(("user", text));
            _assistantHistory.Add(("assistant", reply.Text));
            if (_assistantHistory.Count > 12) _assistantHistory.RemoveRange(0, _assistantHistory.Count - 12);

            AppendAssistantBubble(reply.Text, reply.Actions, reply.EngineUsed, reply.RagSources);
            StatusFooterText.Text = $"AI-Помощник ответил ({reply.EngineUsed}).";
        }
        catch (Exception ex)
        {
            AssistantMessagesPanel.Children.Remove(thinkingBubble);
            AppendAssistantBubble(
                $"Не удалось получить ответ от локальной LLM: {ex.Message}\n\nПроверь, достаточно ли свободной VRAM (можно нажать «Освободить GPU»), или воспользуйся быстрыми действиями ниже.",
                [
                    new("check_network", "", "Проверить сеть и движки"),
                    new("open_tab", "catalog_photo", "Открыть Каталог")
                ],
                "Ошибка Помощника",
                null);
        }
        finally
        {
            _assistantBusy = false;
            AssistantSendBtn.IsEnabled = true;
            AssistantNetCheckBtn.IsEnabled = true;
            RefreshAssistantStatusUi();
        }
    }

    private async Task RunAssistantNetworkCheckAsync()
    {
        if (_assistantBusy) return;
        _assistantBusy = true;
        AssistantSendBtn.IsEnabled = false;
        AssistantNetCheckBtn.IsEnabled = false;
        var waitBubble = AppendStatusBubble("Проверяю доступность GitHub, Hugging Face, токена и движков...");

        try
        {
            double freeDisk = _detectedHardware?.FreeDiskGB ?? 0;
            var res = await _assistant.RunNetworkDiagnosticsAsync(_hfClient, freeDisk);
            _hfConnectedCached = res.HfAuthOk;
            AssistantMessagesPanel.Children.Remove(waitBubble);

            var actions = new List<AssistantAction>();
            if (!res.GitHubOk)
            {
                actions.Add(new("open_url", RuntimeInstaller.ComfyReleasesUrl, "GitHub ComfyUI ↗"));
                actions.Add(new("open_url", RuntimeInstaller.LlamaReleasesUrl, "GitHub llama.cpp ↗"));
            }
            if (!res.HuggingFaceOk)
                actions.Add(new("open_url", "https://huggingface.co/models", "Открыть Hugging Face ↗"));
            if (!res.HfAuthOk)
                actions.Add(new("open_tab", "settings", "Подключить HF-токен"));
            if (!res.LlamaInstalled && res.GitHubOk)
                actions.Add(new("install_runtime", "llama", "Установить llama.cpp"));
            if (!res.ComfyInstalled && res.GitHubOk)
                actions.Add(new("install_runtime", "comfyui", "Установить ComfyUI"));

            AppendAssistantBubble(res.ToSummaryText(), actions, "Диагностика сети", ["Сеть и окружение"]);
            StatusFooterText.Text = "Диагностика сети и окружения завершена.";
        }
        finally
        {
            _assistantBusy = false;
            AssistantSendBtn.IsEnabled = true;
            AssistantNetCheckBtn.IsEnabled = true;
        }
    }

    private async Task ExecuteAssistantActionAsync(AssistantAction act)
    {
        switch (act.Type)
        {
            case "ask":
                await SendAssistantMessageAsync(act.Param);
                break;

            case "check_network":
                await RunAssistantNetworkCheckAsync();
                break;

            case "apply_antiloop":
                ApplyQwenAntiLoopBtn_Click(this, new RoutedEventArgs());
                AppendAssistantBubble(
                    "Готово! Профиль «Qwen Анти-луп» применён (temp=1.0, top_p=0.95, top_k=20, presence_penalty=1.5, dry=0.5, лимит <think>=4096, --no-reasoning-preserve).\nПерезапусти локальную LLM на вкладке «Сервисы и порты», чтобы настройки вступили в силу.",
                    [new("open_tab", "services", "Открыть Сервисы и порты")],
                    "Действие выполнено",
                    null);
                break;

            case "open_url":
                AiServiceManager.OpenExternal(act.Param);
                StatusFooterText.Text = $"Открыта ссылка: {act.Param}";
                break;

            case "open_folder":
                string dir = act.Param switch
                {
                    "llm_models" => HubConfig.LlmModelsDir,
                    "llm_runtime" => HubConfig.LlmRuntimeDir,
                    "comfy_models" => HubConfig.ComfyModelsDir,
                    _ => HubConfig.MediaRoot
                };
                try { Directory.CreateDirectory(dir); AiServiceManager.OpenExternal(dir); } catch { }
                break;

            case "open_tab":
                switch (act.Param)
                {
                    case "catalog_text": MainTabs.SelectedItem = CatalogTab; SetCatalogView("text"); break;
                    case "catalog_photo": MainTabs.SelectedItem = CatalogTab; SetCatalogView("photo"); break;
                    case "catalog_video": MainTabs.SelectedItem = CatalogTab; SetCatalogView("video"); break;
                    case "hf_search": MainTabs.SelectedItem = CatalogTab; SetCatalogView("hf"); break;
                    case "chat_llm": MainTabs.SelectedItem = ChatTab; SetChatSubMode("llm"); break;
                    case "gallery": MainTabs.SelectedItem = GalleryTab; break;
                    case "settings": MainTabs.SelectedItem = SystemTab; SetSystemSubMode("settings"); break;
                    case "services": MainTabs.SelectedItem = SystemTab; SetSystemSubMode("services"); break;
                    case "studio": MainTabs.SelectedItem = PhotoTab; SetPhotoSubMode("single"); break;
                    case "video": MainTabs.SelectedItem = VideoTab; SetVideoSubMode("single"); break;
                    case "models": MainTabs.SelectedItem = CatalogTab; SetCatalogView("disk"); break;
                }
                break;

            case "install_runtime":
                await ExecuteAssistantRuntimeInstallAsync(act.Param);
                break;

            case "install_model":
                var spec = ModelCatalog.FindById(act.Param);
                if (spec is null)
                {
                    StatusFooterText.Text = $"Модель {act.Param} не найдена в каталоге.";
                    return;
                }
                await ExecuteAssistantModelInstallAsync(spec);
                break;
        }
    }

    private async Task ExecuteAssistantRuntimeInstallAsync(string runtimeId)
    {
        if (_catalogDownloading)
        {
            StatusFooterText.Text = "Уже идёт другая загрузка — дождись завершения или нажми «Отменить».";
            return;
        }
        string label = runtimeId == "llama" ? "движок llama.cpp" : "движок ComfyUI Portable";
        var wait = AppendStatusBubble($"Запускаю установку: {label}... (прогресс отображается на зелёной полосе внизу)");

        _catalogDownloading = true;
        _catalogDlCts?.Dispose();
        _catalogDlCts = new CancellationTokenSource();
        CatalogDownloadCard.Visibility = Visibility.Visible;
        CatalogDlBar.Value = 0;
        RenderCatalogRecommendations();
        RefreshRuntimeStatusUi();
        RefreshAssistantStatusUi();

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
                ? await _runtimeInstaller.InstallLlamaCppAsync(hw, progress, _catalogDlCts.Token)
                : await _runtimeInstaller.InstallComfyUiAsync(hw, progress, _catalogDlCts.Token);

            AssistantMessagesPanel.Children.Remove(wait);
            AppendAssistantBubble(
                $"✓ Готово! {summary}\nТеперь можно скачивать и запускать модели.",
                [
                    new("open_tab", runtimeId == "llama" ? "services" : "studio", runtimeId == "llama" ? "Открыть Сервисы" : "Открыть Студию"),
                    new("open_tab", runtimeId == "llama" ? "catalog_text" : "catalog_photo", "Открыть Каталог")
                ],
                "Установка завершена",
                null);
        }
        catch (OperationCanceledException)
        {
            AssistantMessagesPanel.Children.Remove(wait);
            AppendAssistantBubble("Установка отменена пользователем (частичный файл сохранён — можно докачать позже).", [], "Отменено", null);
        }
        catch (Exception ex)
        {
            AssistantMessagesPanel.Children.Remove(wait);
            await NotifyAssistantAboutFailureAsync($"Установка {label}", ex.Message);
        }
        finally
        {
            _catalogDownloading = false;
            CatalogDownloadCard.Visibility = Visibility.Collapsed;
            RenderCatalogRecommendations();
            RefreshRuntimeStatusUi();
            RefreshStudioEngineStatuses();
            RefreshAssistantStatusUi();
            _ = RefreshStatusAsync();
        }
    }

    private async Task ExecuteAssistantModelInstallAsync(CatalogModelSpec spec)
    {
        if (_catalogDownloading)
        {
            StatusFooterText.Text = "Уже идёт другая загрузка — дождись завершения или нажми «Отменить».";
            return;
        }

        // Preflight check for gated HF models — let Assistant explain clearly in chat!
        if (spec.IsGated && spec.Files.Length > 0)
        {
            string gatedRepo = spec.Files[0].RepoId;
            bool hasToken = !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken());
            if (!hasToken)
            {
                await NotifyAssistantAboutFailureAsync(
                    $"Скачивание закрытой модели {spec.Name} ({gatedRepo})",
                    $"HTTP 401: модель {gatedRepo} закрыта лицензией (Gated) на Hugging Face, а токен Hugging Face ещё не подключён.");
                return;
            }
            bool allowed = await _hfClient.HasRepoAccessAsync(gatedRepo, spec.Files[0].RemotePath);
            if (!allowed)
            {
                await NotifyAssistantAboutFailureAsync(
                    $"Скачивание закрытой модели {spec.Name} ({gatedRepo})",
                    $"HTTP 403: токен Hugging Face подключён, но нужно подтвердить согласие с лицензией на странице https://huggingface.co/{gatedRepo}.");
                return;
            }
        }

        var wait = AppendStatusBubble($"Скачиваю «{spec.Name}» ({spec.TotalSizeGB:0.##} ГБ)... Прогресс отображается на зелёной полосе внизу.");

        _catalogDownloading = true;
        _catalogDlCts?.Dispose();
        _catalogDlCts = new CancellationTokenSource();
        CatalogDownloadCard.Visibility = Visibility.Visible;
        CatalogDlTitleText.Text = $"Установка: {spec.Name}";
        CatalogDlStatsText.Text = "Подготовка...";
        CatalogDlBar.Value = 0;
        RenderCatalogRecommendations();
        RefreshRuntimeStatusUi();
        RefreshAssistantStatusUi();

        try
        {
            await _hfClient.InstallCatalogBundleAsync(spec, p => Dispatcher.Invoke(() =>
            {
                CatalogDlTitleText.Text = $"[{p.FileIndex}/{p.TotalFiles}] {p.CurrentFile} — {p.StatusText}";
                CatalogDlStatsText.Text = p.SpeedMBs > 0.05
                    ? $"{p.Percent:F1}% • {p.DownloadedMB / 1024.0:F2}/{p.TotalMB / 1024.0:F2} ГБ • {p.SpeedMBs:F1} МБ/с"
                    : $"{p.Percent:F1}% • {p.StatusText}";
                CatalogDlBar.Value = p.Percent;
                StatusFooterText.Text = $"Скачивание {p.CurrentFile}: {p.Percent:F1}% ({p.SpeedMBs:F1} МБ/с)";
            }), _catalogDlCts.Token);

            AssistantMessagesPanel.Children.Remove(wait);
            LoadModelsTable();

            bool needLlama = spec.Category == "text" && !RuntimeInstaller.IsLlamaInstalled();
            bool needComfy = spec.Category is "photo" or "video" && !RuntimeInstaller.IsComfyInstalled();
            var nextActions = new List<AssistantAction>();
            string extraNote = "";
            if (needLlama)
            {
                extraNote = "\n⚠ Осталось установить движок llama.cpp, чтобы запустить эту модель.";
                nextActions.Add(new("install_runtime", "llama", "Установить llama.cpp (~620 МБ)"));
            }
            else if (needComfy)
            {
                extraNote = "\n⚠ Осталось установить движок ComfyUI Portable, чтобы генерировать с этой моделью.";
                nextActions.Add(new("install_runtime", "comfyui", "Установить ComfyUI (~2.0 ГБ)"));
            }
            else if (spec.Id != ModelCatalog.AssistantModelId)
            {
                nextActions.Add(new("open_tab", spec.Category == "text" ? "chat_llm" : "studio",
                    spec.Category == "text" ? "Открыть Чат 2.0 (LLM)" : "Открыть Студию"));
            }
            UpdateLocalChatVisionBadge();

            AppendAssistantBubble(
                $"✓ Модель «{spec.Name}» успешно скачана и проверена по SHA-256!{extraNote}",
                nextActions,
                "Модель установлена",
                null);
        }
        catch (OperationCanceledException)
        {
            AssistantMessagesPanel.Children.Remove(wait);
            AppendAssistantBubble("Скачивание остановлено (прогресс сохранён в .part — при повторе продолжится с того же места).", [], "Отменено", null);
        }
        catch (Exception ex)
        {
            AssistantMessagesPanel.Children.Remove(wait);
            await NotifyAssistantAboutFailureAsync($"Скачивание модели «{spec.Name}»", ex.Message);
        }
        finally
        {
            _catalogDownloading = false;
            CatalogDownloadCard.Visibility = Visibility.Collapsed;
            RenderCatalogRecommendations();
            RefreshRuntimeStatusUi();
            RefreshStudioEngineStatuses();
            RefreshAssistantStatusUi();
            _ = RefreshStatusAsync();
        }
    }

    private async Task NotifyAssistantAboutFailureAsync(string actionDescription, string errorMessage)
    {
        var hw = _detectedHardware ?? GetActiveCatalogHardware();
        var diag = await _assistant.ExplainActionFailureAsync(actionDescription, errorMessage, hw, _hfConnectedCached, _hfUserCached);
        AppendAssistantBubble(diag.Text, diag.Actions, $"Разбор ошибки ({diag.EngineUsed})", diag.RagSources);
    }

    private void AppendUserBubble(string text)
    {
        var border = new Border
        {
            Background = BrushFromHex("#1A1A20"),
            BorderBrush = BrushFromHex("#30303A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(90, 0, 0, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = text,
                Foreground = BrushFromHex("#F4F4F6"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            }
        };
        AssistantMessagesPanel.Children.Add(border);
        AssistantChatScroll.ScrollToEnd();
    }

    private Border AppendStatusBubble(string text)
    {
        var border = new Border
        {
            Background = BrushFromHex("#121215"),
            BorderBrush = BrushFromHex("#282830"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 90, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text,
                Foreground = BrushFromHex("#A1A1AA"),
                FontSize = 11.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap
            }
        };
        AssistantMessagesPanel.Children.Add(border);
        AssistantChatScroll.ScrollToEnd();
        return border;
    }

    private void AppendAssistantBubble(string text, List<AssistantAction> actions, string engineTag, List<string>? ragSources)
    {
        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var copyBtn = new System.Windows.Controls.Button
        {
            Content = "Копир.",
            Style = TryFindResource("GhostButton") as Style ?? (Style)FindResource("DarkButton"),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 10.5,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Копировать ответ"
        };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                ShowToast("Ответ скопирован", "success");
            }
            catch { }
        };
        DockPanel.SetDock(copyBtn, Dock.Right);
        header.Children.Add(copyBtn);

        if (ragSources is { Count: > 0 })
        {
            var srcText = new TextBlock
            {
                Text = $"RAG: {string.Join(", ", ragSources.Take(3))}",
                FontSize = 10.5,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = BrushFromHex("#71717A"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            DockPanel.SetDock(srcText, Dock.Right);
            header.Children.Add(srcText);
        }

        header.Children.Add(new TextBlock
        {
            Text = $"СПРАВКА • {engineTag.ToUpperInvariant()}",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#A1A1AA"),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        stack.Children.Add(MarkdownUiRenderer.Build(text, fontSize: 13, foregroundHex: "#E4E4E8"));

        if (actions.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            foreach (var act in actions)
            {
                string styleKey = act.Type is "install_model" or "install_runtime" or "apply_antiloop"
                    ? "SuccessButton"
                    : act.Type == "check_network" ? "PrimaryButton" : "DarkButton";
                var btn = new System.Windows.Controls.Button
                {
                    Content = act.Label,
                    Style = (Style)FindResource(styleKey),
                    Padding = new Thickness(11, 5, 11, 5),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 7, 5)
                };
                btn.Click += async (_, _) => await ExecuteAssistantActionAsync(act);
                wrap.Children.Add(btn);
            }
            stack.Children.Add(wrap);
        }

        var border = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = BrushFromHex("#202026"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 8, 4, 12),
            Margin = new Thickness(0, 0, 24, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ContextMenu = MarkdownUiRenderer.CreateCopyContextMenu(text),
            Child = stack
        };
        AssistantMessagesPanel.Children.Add(border);
        AssistantChatScroll.ScrollToEnd();
    }
}

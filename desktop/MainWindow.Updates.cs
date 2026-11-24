using System.Windows;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private readonly AppUpdateService _updater = new();
    private UpdateCheckResult? _availableUpdate;
    private bool _updateBusy;

    private void InitAutoUpdaterUi()
    {
        AppVersionBadgeText.Text = $"v{AppUpdateService.CurrentVersion} • Brazhnichenko Hub";
        UpdateSettingsTitleText.Text = $"Обновления Brazhnichenko Hub (текущая версия v{AppUpdateService.CurrentVersion})";
        LoadUpdateServerFields();
    }

    private void LoadUpdateServerFields()
    {
        var cfg = HubConfig.Current;
        CfgUpdateUrlBox.Text = string.IsNullOrWhiteSpace(cfg.UpdateServerUrl)
            ? AppUpdateService.DefaultServerBaseUrl
            : cfg.UpdateServerUrl;
        CfgUpdateTokenBox.Text = cfg.UpdateServerToken ?? "";
        CfgUpdateCertBox.Text = cfg.UpdateCertSha256 ?? "";
        CfgAutoCheckUpdatesCheck.IsChecked = cfg.AutoCheckUpdatesOnStartup;

        string activeUrl = AppUpdateService.EffectiveServerBaseUrl;
        UpdateSettingsStatusText.Text = $"Источник обновлений: {activeUrl} (GitHub Releases / HTTPS + SHA-256)";
    }

    internal void UpdateServerToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool open = UpdateServerConfigPanel.Visibility != Visibility.Visible;
        if (open) LoadUpdateServerFields();
        UpdateServerConfigPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateServerSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string url = CfgUpdateUrlBox.Text.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var cfg = HubConfig.Current;
        cfg.UpdateServerUrl = string.Equals(url, AppUpdateService.DefaultServerBaseUrl, StringComparison.OrdinalIgnoreCase) ? "" : url;
        cfg.UpdateServerToken = CfgUpdateTokenBox.Text.Trim();
        cfg.UpdateCertSha256 = CfgUpdateCertBox.Text.Trim();
        cfg.AutoCheckUpdatesOnStartup = CfgAutoCheckUpdatesCheck.IsChecked == true;
        HubConfig.Save();

        LoadUpdateServerFields();
        ShowToast($"Сервер обновлений сохранён: {AppUpdateService.EffectiveServerBaseUrl}", "success");
        UpdateCheckBtn_Click(this, new RoutedEventArgs());
    }

    internal void UpdateServerResetBtn_Click(object sender, RoutedEventArgs e)
    {
        var cfg = HubConfig.Current;
        cfg.UpdateServerUrl = "";
        cfg.UpdateServerToken = "";
        cfg.UpdateCertSha256 = "";
        cfg.AutoCheckUpdatesOnStartup = true;
        HubConfig.Save();

        LoadUpdateServerFields();
        ShowToast("Настройки сервера обновлений сброшены по умолчанию", "info");
    }

    private async Task CheckUpdatesSilentlyOnStartupAsync()
    {
        try
        {
            if (!HubConfig.Current.AutoCheckUpdatesOnStartup) return;
            await Task.Delay(2500);
            var res = await _updater.CheckForUpdateAsync();
            if (!res.Success) return;

            if (res.HasUpdate)
            {
                _availableUpdate = res;
                ShowAvailableUpdateUi(res);
            }
            else
            {
                UpdateSettingsStatusText.Text = $"✓ Актуальная версия v{res.CurrentVersion} • {AppUpdateService.EffectiveServerBaseUrl} (проверено {DateTime.Now:HH:mm})";
            }
        }
        catch { }
    }

    private void ShowAvailableUpdateUi(UpdateCheckResult res)
    {
        string notesShort = string.IsNullOrWhiteSpace(res.Notes) ? "" : $" — {res.Notes}";
        string sizeStr = res.SizeBytes > 0 ? $" ({res.SizeBytes / (1024.0 * 1024.0):F1} МБ)" : "";

        UpdateBannerText.Text = $"✨ Доступна новая версия Brazhnichenko Hub v{res.LatestVersion}{sizeStr}{notesShort}";
        UpdateNowBannerBtn.Content = $"⬇ Обновить до v{res.LatestVersion}";
        UpdateBanner.Visibility = Visibility.Visible;

        UpdateSettingsStatusText.Text = $"Доступно обновление v{res.LatestVersion}{sizeStr}: {(string.IsNullOrWhiteSpace(res.Notes) ? "улучшения и исправления" : res.Notes)}";
        UpdateSettingsStatusText.Foreground = BrushFromHex("#6AE89E");
        UpdateSettingsInstallBtn.Content = $"⬇ Обновить до v{res.LatestVersion}";
        UpdateSettingsInstallBtn.Visibility = Visibility.Visible;
    }

    private void UpdateBannerDismissBtn_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    internal async void UpdateCheckBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateCheckBtn.IsEnabled = false;
        UpdateSettingsStatusText.Text = "Проверяю наличие обновлений на сервере...";
        UpdateSettingsStatusText.Foreground = BrushFromHex("#A5B4CF");
        StatusFooterText.Text = "Проверка обновлений Local AI Hub...";
        try
        {
            var res = await _updater.CheckForUpdateAsync();
            if (!res.Success)
            {
                UpdateSettingsStatusText.Text = $"Не удалось связаться с сервером обновлений: {res.ErrorMessage}";
                UpdateSettingsStatusText.Foreground = BrushFromHex("#F5CB5C");
                StatusFooterText.Text = "Сервер обновлений временно недоступен.";
            }
            else if (res.HasUpdate)
            {
                _availableUpdate = res;
                ShowAvailableUpdateUi(res);
                StatusFooterText.Text = $"Найдено обновление Local AI Hub v{res.LatestVersion}!";
            }
            else
            {
                _availableUpdate = null;
                UpdateBanner.Visibility = Visibility.Collapsed;
                UpdateSettingsInstallBtn.Visibility = Visibility.Collapsed;
                UpdateSettingsStatusText.Text = $"✓ У тебя уже самая свежая версия v{res.CurrentVersion} (на сервере v{res.LatestVersion}).";
                UpdateSettingsStatusText.Foreground = BrushFromHex("#6AE89E");
                StatusFooterText.Text = $"Local AI Hub v{res.CurrentVersion} актуален.";
            }
        }
        finally
        {
            _updateBusy = false;
            UpdateCheckBtn.IsEnabled = true;
        }
    }

    internal async void UpdateInstallNowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _availableUpdate is null) return;
        _updateBusy = true;
        UpdateCheckBtn.IsEnabled = false;
        UpdateNowBannerBtn.IsEnabled = false;
        UpdateSettingsInstallBtn.IsEnabled = false;
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBannerProgress.Visibility = Visibility.Visible;
        UpdateBannerProgress.Value = 0;

        try
        {
            UpdateBannerText.Text = $"Скачивание Local AI Hub v{_availableUpdate.LatestVersion}...";
            StatusFooterText.Text = $"Скачиваю обновление v{_availableUpdate.LatestVersion}...";

            var prog = new Progress<double>(pct => Dispatcher.Invoke(() =>
            {
                UpdateBannerProgress.Value = pct;
                UpdateBannerText.Text = $"Скачивание v{_availableUpdate.LatestVersion}: {pct:0}% (проверка SHA-256)...";
            }));

            string setupExe = await _updater.DownloadAndVerifyInstallerAsync(_availableUpdate, prog);
            UpdateBannerText.Text = "Проверка SHA-256 пройдена! Запускаю обновление и перезапуск...";
            StatusFooterText.Text = "Обновление проверено (SHA-256 OK) — перезапуск приложения...";

            AppUpdateService.LaunchAutoUpdater(setupExe);
            _forceExit = true;
            Close();
        }
        catch (Exception ex)
        {
            UpdateBannerProgress.Visibility = Visibility.Collapsed;
            UpdateBannerText.Text = $"Ошибка обновления: {ex.Message}";
            StatusFooterText.Text = $"Ошибка обновления: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Обновление Local AI Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            _updateBusy = false;
            UpdateCheckBtn.IsEnabled = true;
            UpdateNowBannerBtn.IsEnabled = true;
            UpdateSettingsInstallBtn.IsEnabled = true;
        }
    }
}

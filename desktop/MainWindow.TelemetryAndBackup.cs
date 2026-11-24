using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private readonly TelemetryHistoryBuffer _vramHistory = new(30);
    private readonly TelemetryHistoryBuffer _tempHistory = new(30);
    private readonly TelemetryHistoryBuffer _ramHistory = new(30);

    private DateTime _lastLlmActivityUtc = DateTime.UtcNow;
    private double _lastLlmTokPerSec;
    private bool _idleUnloadInProgress;
    private bool _suppressIdleComboChange;

    private void InitTelemetryAndBackup()
    {
        SyncLlmIdleComboFromConfig();
    }

    internal void MarkLlmActivity(double? tokPerSec = null)
    {
        _lastLlmActivityUtc = DateTime.UtcNow;
        if (tokPerSec is > 0.05)
            _lastLlmTokPerSec = tokPerSec.Value;
    }

    private void SyncLlmIdleComboFromConfig()
    {
        if (CfgLlmIdleCombo is null) return;
        _suppressIdleComboChange = true;
        try
        {
            string tag = HubConfig.Current.LlmIdleUnloadMinutes.ToString();
            SelectComboByTag(CfgLlmIdleCombo, tag);
            if (CfgLlmIdleCombo.SelectedIndex < 0)
                CfgLlmIdleCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressIdleComboChange = false;
        }
    }

    private void UpdateTelemetrySparklines(double? vramPct, double? gpuTempC, double ramPct)
    {
        if (vramPct is double vp)
        {
            _vramHistory.Push(vp);
            if (VramSparkline is not null)
            {
                VramSparkline.Points = ToPointCollection(_vramHistory.ComputeSparklinePoints(64, 26, 0, 100, 2));
                VramSparkline.Stroke = vp > 85 ? BrushFromHex("#EF4444")
                                     : vp > 60 ? BrushFromHex("#F59E0B")
                                     : BrushFromHex("#FF521B");
            }
        }

        if (gpuTempC is double tc)
        {
            _tempHistory.Push(tc);
            if (TempSparkline is not null)
                TempSparkline.Points = ToPointCollection(_tempHistory.ComputeSparklinePoints(64, 26, 25, 95, 2));
        }

        _ramHistory.Push(ramPct);
        if (RamSparkline is not null)
            RamSparkline.Points = ToPointCollection(_ramHistory.ComputeSparklinePoints(64, 26, 0, 100, 2));

        if (GpuTelemetryCard is not null && _vramHistory.Count > 0)
        {
            string tpsLine = _lastLlmTokPerSec > 0 ? $"\nПоследняя скорость LLM: {_lastLlmTokPerSec:F1} tok/s" : "";
            string tempLine = _tempHistory.Count > 0 ? $"\nТемпература GPU: {_tempHistory.Min:0}–{_tempHistory.Max:0}°C (сейчас {_tempHistory.Latest:0}°C)" : "";
            GpuTelemetryCard.ToolTip = $"VRAM (75 сек): {_vramHistory.Min:0}–{_vramHistory.Max:0}%{tempLine}{tpsLine}";
        }

        if (RamTelemetryCard is not null && _ramHistory.Count > 0)
        {
            RamTelemetryCard.ToolTip = $"RAM (75 сек): {_ramHistory.Min:0}–{_ramHistory.Max:0}% (сейчас {_ramHistory.Latest:0}%)";
        }
    }

    private static PointCollection ToPointCollection(List<(double X, double Y)> pts)
    {
        var pc = new PointCollection(pts.Count);
        foreach (var (x, y) in pts)
            pc.Add(new System.Windows.Point(x, y));
        pc.Freeze();
        return pc;
    }

    private async Task CheckLlmIdleAutoUnloadAsync(Dictionary<string, bool> statuses, SystemMetrics m)
    {
        int limitMin = HubConfig.Current.LlmIdleUnloadMinutes;
        bool llmRunning = statuses.GetValueOrDefault("qwen");

        if (limitMin <= 0 || !llmRunning)
        {
            if (LlmIdleStatusText is not null && limitMin > 0 && !llmRunning)
                LlmIdleStatusText.Text = $"Авто-выгрузка активна ({limitMin} мин) • LLM сейчас остановлена.";
            return;
        }

        // Reset activity timestamp if any local AI workflow is busy OR GPU compute is active (e.g. external WebUI/Claude)
        bool busyNow = _localChatBusy || _directorBusy || _storyBusy || (m.Gpu is not null && m.Gpu.UtilPct >= 15);
        if (busyNow)
        {
            _lastLlmActivityUtc = DateTime.UtcNow;
            if (LlmIdleStatusText is not null)
                LlmIdleStatusText.Text = $"Авто-выгрузка ({limitMin} мин) • LLM активна сейчас.";
            return;
        }

        double idleMin = (DateTime.UtcNow - _lastLlmActivityUtc).TotalMinutes;
        double remainMin = Math.Max(0, limitMin - idleMin);

        if (LlmIdleStatusText is not null)
            LlmIdleStatusText.Text = $"Авто-выгрузка активна • до освобождения VRAM осталось ~{Math.Ceiling(remainMin):0} мин.";

        if (idleMin < limitMin || _idleUnloadInProgress) return;

        _idleUnloadInProgress = true;
        try
        {
            await _manager.StopServiceAsync("qwen");
            _lastLlmActivityUtc = DateTime.UtcNow;
            StatusFooterText.Text = $"LLM автоматически выгружена из VRAM после {limitMin} мин простоя.";
            ShowToast($"LLM выгружена из VRAM ({limitMin} мин простоя)", "info");
        }
        catch { }
        finally
        {
            _idleUnloadInProgress = false;
        }
    }

    internal void CfgLlmIdleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressIdleComboChange || !IsLoaded) return;
        if (CfgLlmIdleCombo?.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag as string, out int mins)) mins = 0;

        HubConfig.Current.LlmIdleUnloadMinutes = Math.Clamp(mins, 0, 240);
        HubConfig.Save();
        _lastLlmActivityUtc = DateTime.UtcNow;

        if (LlmIdleStatusText is not null)
        {
            LlmIdleStatusText.Text = mins > 0
                ? $"Авто-выгрузка включена: llama-server остановится через {mins} мин бездействия."
                : "Авто-выгрузка отключена — LLM работает до ручной остановки или запуска ComfyUI.";
        }
        ShowToast(mins > 0 ? $"Авто-выгрузка VRAM: через {mins} мин" : "Авто-выгрузка VRAM отключена", "success");
    }

    internal void ExportProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт профиля Brazhnichenko Hub",
            Filter = "ZIP-архив профиля|*.zip",
            FileName = $"BrazhnichenkoHub-Backup-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            string ver = AppUpdateService.CurrentVersion;
            var manifest = ProfileBackupService.ExportProfileZip(dlg.FileName, ver);
            StatusFooterText.Text = $"Профиль экспортирован ({manifest.IncludedFiles.Count} файлов): {dlg.FileName}";
            ShowToast($"Профиль сохранён ({manifest.IncludedFiles.Count} файлов)", "success");
        }
        catch (Exception ex)
        {
            HubDialogWindow.Show(ex.Message, "Ошибка экспорта профиля", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal void ImportProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт профиля Brazhnichenko Hub",
            Filter = "ZIP-архив профиля|*.zip|Все файлы|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var confirm = HubDialogWindow.Show(
            $"Восстановить настройки, пресеты, диалоги Чата, историю промптов и избранное из файла:\n{Path.GetFileName(dlg.FileName)}?\n\nТекущие файлы профиля будут перезаписаны.",
            "Импорт профиля Brazhnichenko Hub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var res = ProfileBackupService.ImportProfileZip(dlg.FileName);
            LoadSettingsForm();
            SyncLlmIdleComboFromConfig();
            InitLocalChat();
            InitQueueAndPresets();
            _ = RefreshStatusAsync();

            StatusFooterText.Text = $"Профиль восстановлен ({res.RestoredFileCount} файлов): {string.Join(", ", res.RestoredFiles)}";
            ShowToast($"Профиль импортирован ({res.RestoredFileCount} файлов)!", "success");
        }
        catch (Exception ex)
        {
            HubDialogWindow.Show(ex.Message, "Ошибка импорта профиля", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

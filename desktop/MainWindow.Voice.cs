using System.IO;
using System.Media;
using System.Windows;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private readonly LocalVoiceService _voiceService = new();
    private System.Windows.Controls.Button? _activeMicButton;
    private System.Windows.Controls.TextBox? _activeMicTargetBox;
    private bool _voiceBusy;
    private SoundPlayer? _ttsPlayer;

    private void InitLocalVoice()
    {
        Closed += (_, _) =>
        {
            try { _voiceService.Dispose(); } catch { }
            try { _ttsPlayer?.Stop(); _ttsPlayer?.Dispose(); } catch { }
        };

        RefreshVoiceStatusUi();
        RefreshTtsVoicesCombo();
    }

    private void RefreshTtsVoicesCombo()
    {
        if (VideoTtsVoiceCombo is null) return;
        string? prevId = (VideoTtsVoiceCombo.SelectedItem as TtsVoiceOption)?.Id;
        var voices = LocalVoiceService.GetAvailableTtsVoices();
        VideoTtsVoiceCombo.ItemsSource = voices;
        int idx = voices.FindIndex(v => v.Id == prevId);
        VideoTtsVoiceCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void RefreshVoiceStatusUi()
    {
        var st = LocalVoiceService.GetStatus();
        if (VoiceWhisperStatusText is not null)
        {
            VoiceWhisperStatusText.Text = st.WhisperReady
                ? $"● Установлен ({Path.GetFileName(st.WhisperModelPath)}) — микрофон готов в Чате, Фото и Видео"
                : "○ Не установлен — нажми кнопку справа для установки в 1 клик (~150 МБ)";
            VoiceWhisperStatusText.Foreground = BrushFromHex(st.WhisperReady ? "#34D399" : "#A1A1AA");
        }
        if (InstallWhisperBtn is not null)
        {
            InstallWhisperBtn.Content = st.WhisperReady ? "Whisper установлен ✓" : "Установить Whisper (~150 МБ)";
            if (TryFindResource(st.WhisperReady ? "DarkButton" : "SuccessButton") is Style ws)
                InstallWhisperBtn.Style = ws;
            InstallWhisperBtn.IsEnabled = !_voiceBusy;
        }

        if (VoicePiperStatusText is not null)
        {
            VoicePiperStatusText.Text = st.PiperReady
                ? $"● Установлен ({Path.GetFileName(st.PiperModelPath)}) + офлайн-голоса Windows"
                : "○ Не установлен (офлайн-голоса Windows Pavel/Irina уже работают; Piper добавит нейро-голос HD)";
            VoicePiperStatusText.Foreground = BrushFromHex(st.PiperReady ? "#34D399" : "#A1A1AA");
        }
        if (InstallPiperBtn is not null)
        {
            InstallPiperBtn.Content = st.PiperReady ? "Piper HD установлен ✓" : "Установить Piper HD (~80 МБ)";
            InstallPiperBtn.IsEnabled = !_voiceBusy;
        }
    }

    // ==================== MICROPHONE STT (Chat, Photo Prompt, Video Prompt) ====================

    internal async void LocalChatMicBtn_Click(object sender, RoutedEventArgs e) =>
        await ToggleVoiceDictationAsync(LocalChatMicBtn, LocalChatInputBox);

    internal async void PhotoMicBtn_Click(object sender, RoutedEventArgs e) =>
        await ToggleVoiceDictationAsync(PhotoMicBtn, PhotoPromptBox);

    internal async void VideoMicBtn_Click(object sender, RoutedEventArgs e) =>
        await ToggleVoiceDictationAsync(VideoMicBtn, VideoPromptBox);

    private async Task ToggleVoiceDictationAsync(System.Windows.Controls.Button micBtn, System.Windows.Controls.TextBox targetBox)
    {
        if (_voiceBusy) return;

        // If already recording: stop and transcribe
        if (_voiceService.IsRecording)
        {
            var prevBtn = _activeMicButton;
            var prevTarget = _activeMicTargetBox ?? targetBox;
            _activeMicButton = null;
            _activeMicTargetBox = null;
            RestoreMicButtonStyle(prevBtn);

            _voiceBusy = true;
            try
            {
                string wavPath = _voiceService.StopRecording();
                StatusFooterText.Text = "Распознавание речи (Whisper.cpp)...";
                ShowToast("Распознаю речь...", "info");

                string text = await _voiceService.TranscribeWavAsync(wavPath, "auto");
                try { File.Delete(wavPath); } catch { }

                if (string.IsNullOrWhiteSpace(text))
                {
                    StatusFooterText.Text = "Речь не распознана — попробуй говорить чуть громче.";
                    ShowToast("Речь не распознана", "warning");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(prevTarget.Text))
                        prevTarget.Text = text;
                    else
                        prevTarget.Text = prevTarget.Text.TrimEnd() + " " + text;
                    prevTarget.CaretIndex = prevTarget.Text.Length;
                    prevTarget.Focus();
                    StatusFooterText.Text = $"Распознано: {text}";
                    ShowToast("Текст вставлен с микрофона!", "success");
                }
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = $"Ошибка микрофона/STT: {ex.Message}";
                HubDialogWindow.Show(ex.Message, "Голосовой ввод (Whisper STT)", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _voiceBusy = false;
            }
            return;
        }

        // Not recording yet — check Whisper availability first
        var st = LocalVoiceService.GetStatus();
        if (!st.WhisperReady)
        {
            var ans = HubDialogWindow.Show(
                "Локальный движок распознавания речи Whisper.cpp (~150 МБ, работает 100% офлайн на русском и английском) ещё не установлен.\n\nУстановить Whisper прямо сейчас в 1 клик?",
                "Установка голосового ввода (Whisper STT)",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes)
                await InstallWhisperWithUiAsync();
            return;
        }

        try
        {
            _voiceService.StartRecording();
            _activeMicButton = micBtn;
            _activeMicTargetBox = targetBox;
            if (TryFindResource("DangerButton") is Style danger)
                micBtn.Style = danger;
            StatusFooterText.Text = "● Идёт запись с микрофона... Нажми кнопку микрофона ещё раз, чтобы закончить и распознать.";
            ShowToast("Запись с микрофона... Нажми ещё раз для стопа", "info");
        }
        catch (Exception ex)
        {
            RestoreMicButtonStyle(micBtn);
            HubDialogWindow.Show(ex.Message, "Ошибка микрофона", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreMicButtonStyle(System.Windows.Controls.Button? btn)
    {
        if (btn is not null && TryFindResource("DarkButton") is Style dark)
            btn.Style = dark;
    }

    // ==================== LOCAL TTS VOICEOVER FOR VIDEO ====================

    internal async void VideoTtsPreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceBusy) return;
        string text = VideoTtsTextBox?.Text.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Введи текст диктора для озвучки", "info");
            return;
        }
        if (VideoTtsVoiceCombo?.SelectedItem is not TtsVoiceOption voice) return;

        _voiceBusy = true;
        if (VideoTtsPreviewBtn is not null) VideoTtsPreviewBtn.IsEnabled = false;
        if (VideoTtsMuxBtn is not null) VideoTtsMuxBtn.IsEnabled = false;
        StatusFooterText.Text = $"Синтез речи ({voice.DisplayName})...";
        try
        {
            string wavPath = await _voiceService.SynthesizeToWavAsync(text, voice);
            _ttsPlayer?.Stop();
            _ttsPlayer?.Dispose();
            _ttsPlayer = new SoundPlayer(wavPath);
            _ttsPlayer.Play();
            StatusFooterText.Text = $"Прослушивание озвучки: {Path.GetFileName(wavPath)}";
            ShowToast("Проигрываю озвучку...", "success");
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка TTS: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Озвучка (Локальный TTS)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _voiceBusy = false;
            if (VideoTtsPreviewBtn is not null) VideoTtsPreviewBtn.IsEnabled = true;
            if (VideoTtsMuxBtn is not null) VideoTtsMuxBtn.IsEnabled = true;
        }
    }

    internal async void VideoTtsMuxBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceBusy || _storyBusy) return;
        string text = VideoTtsTextBox?.Text.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Сначала введи текст диктора для озвучки", "info");
            return;
        }
        if (VideoTtsVoiceCombo?.SelectedItem is not TtsVoiceOption voice) return;

        string? targetVideo = !string.IsNullOrWhiteSpace(_videoLastResultPath) && File.Exists(_videoLastResultPath)
            ? _videoLastResultPath
            : null;

        if (targetVideo is null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выбери MP4-видео для наложения озвучки",
                Filter = "Видео MP4|*.mp4;*.mkv;*.mov|Все файлы|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            targetVideo = dlg.FileName;
        }

        _voiceBusy = true;
        if (VideoTtsPreviewBtn is not null) VideoTtsPreviewBtn.IsEnabled = false;
        if (VideoTtsMuxBtn is not null) VideoTtsMuxBtn.IsEnabled = false;
        try
        {
            StatusFooterText.Text = $"Генерация голоса ({voice.DisplayName})...";
            string wavPath = await _voiceService.SynthesizeToWavAsync(text, voice);

            StatusFooterText.Text = "Наложение озвучки на видео через FFmpeg...";
            string voicedMp4 = await _voiceService.MuxAudioOntoVideoAsync(targetVideo, wavPath, keepOriginalAudio: true);

            _videoLastResultPath = voicedMp4;
            if (VideoResultMetaText is not null)
                VideoResultMetaText.Text = $"Видео с озвучкой ({voice.DisplayName})\n{Path.GetFileName(voicedMp4)}";
            if (VideoOpenResultBtn is not null)
                VideoOpenResultBtn.Visibility = Visibility.Visible;

            StatusFooterText.Text = $"Озвученное видео готово: {Path.GetFileName(voicedMp4)}";
            ShowToast($"Озвучка наложена: {Path.GetFileName(voicedMp4)}", "success");
            AiServiceManager.OpenExternal(voicedMp4);
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка наложения озвучки: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Озвучка видео (TTS + FFmpeg)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _voiceBusy = false;
            if (VideoTtsPreviewBtn is not null) VideoTtsPreviewBtn.IsEnabled = true;
            if (VideoTtsMuxBtn is not null) VideoTtsMuxBtn.IsEnabled = true;
        }
    }

    // ==================== ONE-CLICK INSTALLERS (Whisper & Piper HD) ====================

    internal async void InstallWhisperBtn_Click(object sender, RoutedEventArgs e) =>
        await InstallWhisperWithUiAsync();

    private async Task InstallWhisperWithUiAsync()
    {
        if (_voiceBusy) return;
        _voiceBusy = true;
        RefreshVoiceStatusUi();
        try
        {
            await _voiceService.InstallWhisperAsync(msg => Dispatcher.Invoke(() =>
            {
                StatusFooterText.Text = msg;
                if (VoiceWhisperStatusText is not null) VoiceWhisperStatusText.Text = msg;
            }));
            ShowToast("Whisper STT установлен! Микрофон готов к работе", "success");
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка установки Whisper: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Установка Whisper STT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _voiceBusy = false;
            RefreshVoiceStatusUi();
        }
    }

    internal async void InstallPiperBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceBusy) return;
        _voiceBusy = true;
        RefreshVoiceStatusUi();
        try
        {
            await _voiceService.InstallPiperRuAsync(msg => Dispatcher.Invoke(() =>
            {
                StatusFooterText.Text = msg;
                if (VoicePiperStatusText is not null) VoicePiperStatusText.Text = msg;
            }));
            RefreshTtsVoicesCombo();
            ShowToast("Piper HD TTS (русский нейро-голос) установлен!", "success");
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Ошибка установки Piper HD: {ex.Message}";
            HubDialogWindow.Show(ex.Message, "Установка Piper HD TTS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _voiceBusy = false;
            RefreshVoiceStatusUi();
        }
    }
}

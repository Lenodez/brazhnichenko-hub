package com.brazhnichenko.hub;

import android.Manifest;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.os.RemoteException;
import android.speech.RecognitionService;
import android.speech.SpeechRecognizer;

import java.util.ArrayList;

/**
 * Companion RecognitionService required by AOSP VoiceInteractionServiceInfo so Android 14–16
 * and Samsung One UI validate and bind HubAssistService as a full system digital assistant.
 * Supports 100% offline transcription via Sherpa-ONNX (GigaAM v2 / Whisper) when installed.
 */
public class HubRecognitionService extends RecognitionService {

    @Override
    protected void onStartListening(Intent recognizerIntent, final Callback listener) {
        if (listener == null) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            try { listener.error(SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS); } catch (RemoteException ignored) { }
            return;
        }
        String model = OfflineVoiceEngine.getActiveInstalledModel(this);
        if (model == null || model.isEmpty()) {
            try { listener.error(SpeechRecognizer.ERROR_CLIENT); } catch (RemoteException ignored) { }
            return;
        }
        try { listener.readyForSpeech(new Bundle()); } catch (RemoteException ignored) { }
        OfflineVoiceEngine.startCaptureAndTranscribe(this, new OfflineVoiceEngine.VoiceCallback() {
            private boolean startedSpeech = false;

            @Override
            public void onState(String state, String detail, int levelPct, double seconds) {
                try {
                    if ("recording".equals(state)) {
                        if (!startedSpeech && levelPct > 12) {
                            startedSpeech = true;
                            listener.beginningOfSpeech();
                        }
                        listener.rmsChanged(levelPct / 10.0f);
                    } else if ("transcribing".equals(state)) {
                        listener.endOfSpeech();
                    }
                } catch (RemoteException ignored) { }
            }

            @Override
            public void onResult(String text, long decodeMs) {
                try {
                    ArrayList<String> list = new ArrayList<>();
                    if (text != null && !text.trim().isEmpty()) {
                        list.add(text.trim());
                    }
                    if (list.isEmpty()) {
                        listener.error(SpeechRecognizer.ERROR_NO_MATCH);
                        return;
                    }
                    Bundle b = new Bundle();
                    b.putStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION, list);
                    listener.results(b);
                } catch (RemoteException ignored) { }
            }

            @Override
            public void onError(String message) {
                try { listener.error(SpeechRecognizer.ERROR_AUDIO); } catch (RemoteException ignored) { }
            }
        });
    }

    @Override
    protected void onStopListening(Callback listener) {
        if (OfflineVoiceEngine.isRecording()) {
            OfflineVoiceEngine.stopRecording(false);
        }
    }

    @Override
    protected void onCancel(Callback listener) {
        if (OfflineVoiceEngine.isRecording()) {
            OfflineVoiceEngine.stopRecording(true);
        }
    }
}

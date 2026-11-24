package com.brazhnichenko.hub;

import android.service.voice.VoiceInteractionService;

/**
 * Registers Brazhnichenko Hub as an Android System Digital Assistant (replaces Google Assistant / Gemini).
 * Zero background battery usage and zero LLM auto-warmup until the user explicitly invokes the assistant.
 */
public class HubAssistService extends VoiceInteractionService {
    public static volatile boolean isServiceReady = false;

    @Override
    public void onReady() {
        super.onReady();
        isServiceReady = true;
    }

    @Override
    public void onShutdown() {
        isServiceReady = false;
        super.onShutdown();
    }
}

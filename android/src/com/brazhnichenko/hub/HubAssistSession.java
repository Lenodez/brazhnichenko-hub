package com.brazhnichenko.hub;

import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.service.voice.VoiceInteractionSession;

import java.io.File;
import java.io.FileOutputStream;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Handles system assistant invocations (long-press power button, corner swipe, headset trigger).
 * Optionally captures the system-provided screenshot for offline Vision analysis,
 * then hands off cleanly to MainActivity in voice-first Assistant mode.
 */
public class HubAssistSession extends VoiceInteractionSession {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final AtomicBoolean launched = new AtomicBoolean(false);
    private final Runnable fallbackLaunch = () -> launchAssistantUi(false);

    public HubAssistSession(Context context) {
        super(context);
    }

    @Override
    public void onShow(Bundle args, int showFlags) {
        super.onShow(args, showFlags);
        launched.set(false);
        // Delete any stale screenshot from a previous invocation
        try {
            File f = getAssistScreenFile(getContext());
            if (f.exists()) f.delete();
        } catch (Exception ignored) { }

        // Give Android up to 180ms to deliver onHandleScreenshot(Bitmap) if screen analysis is enabled
        handler.removeCallbacks(fallbackLaunch);
        handler.postDelayed(fallbackLaunch, 180);
    }

    @Override
    public void onHandleScreenshot(Bitmap screenshot) {
        super.onHandleScreenshot(screenshot);
        handler.removeCallbacks(fallbackLaunch);
        if (screenshot == null) {
            launchAssistantUi(false);
            return;
        }
        new Thread(() -> {
            boolean saved = false;
            try {
                File out = getAssistScreenFile(getContext());
                int w = screenshot.getWidth();
                int h = screenshot.getHeight();
                int maxDim = 1080;
                Bitmap scaled = screenshot;
                if (Math.max(w, h) > maxDim) {
                    double r = maxDim / (double) Math.max(w, h);
                    scaled = Bitmap.createScaledBitmap(screenshot, Math.max(1, (int) (w * r)), Math.max(1, (int) (h * r)), true);
                }
                try (FileOutputStream fos = new FileOutputStream(out)) {
                    saved = scaled.compress(Bitmap.CompressFormat.JPEG, 85, fos);
                }
                if (scaled != screenshot) scaled.recycle();
            } catch (Exception ignored) {
                saved = false;
            }
            final boolean hasShot = saved;
            handler.post(() -> launchAssistantUi(hasShot));
        }).start();
    }

    private void launchAssistantUi(boolean hasScreenshot) {
        if (!launched.compareAndSet(false, true)) return;
        handler.removeCallbacks(fallbackLaunch);
        try {
            Intent intent = new Intent(getContext(), MainActivity.class);
            intent.setAction(MainActivity.ACTION_ASSIST_LAUNCH);
            intent.putExtra(MainActivity.EXTRA_HAS_SCREENSHOT, hasScreenshot);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            getContext().startActivity(intent);
        } catch (Exception ignored) {
            try {
                Intent fallback = new Intent(getContext(), MainActivity.class);
                fallback.setAction(MainActivity.ACTION_ASSIST_LAUNCH);
                fallback.putExtra(MainActivity.EXTRA_HAS_SCREENSHOT, hasScreenshot);
                startAssistantActivity(fallback);
            } catch (Exception ignored2) { }
        }
        try {
            finish();
        } catch (Exception ignored) { }
    }

    @Override
    public void onHide() {
        handler.removeCallbacks(fallbackLaunch);
        super.onHide();
    }

    public static File getAssistScreenFile(Context ctx) {
        return new File(ctx.getCacheDir(), "assist_screen.jpg");
    }
}

package com.brazhnichenko.hub;

import android.app.ActivityOptions;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.service.voice.VoiceInteractionSession;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Handles system assistant invocations (long-press power button / Side Key, corner swipe, headset trigger).
 * Optionally captures the system-provided screenshot for offline Vision analysis,
 * then launches MainActivity via privileged startAssistantActivity() (exempt from Android 14–16 BAL blocks).
 */
public class HubAssistSession extends VoiceInteractionSession {
    private static final String TAG = "HubAssist";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final AtomicBoolean launched = new AtomicBoolean(false);
    private final Runnable fallbackLaunch = () -> launchAssistantUi(false);

    public HubAssistSession(Context context) {
        super(context);
    }

    @Override
    public void onCreate() {
        super.onCreate();
        // Disable empty session Dialog window so startAssistantActivity transitions cleanly without focus flicker
        setUiEnabled(false);
    }

    @Override
    public void onShow(Bundle args, int showFlags) {
        super.onShow(args, showFlags);
        Log.i(TAG, "onShow invoked (showFlags=" + showFlags + ")");
        launched.set(false);
        try {
            File f = getAssistScreenFile(getContext());
            if (f.exists()) f.delete();
        } catch (Exception ignored) { }

        // If screenshot flag is not set or disabled by user/system, launch immediately without waiting 180ms
        handler.removeCallbacks(fallbackLaunch);
        if ((showFlags & SHOW_WITH_SCREENSHOT) != 0) {
            handler.postDelayed(fallbackLaunch, 180);
        } else {
            handler.postDelayed(fallbackLaunch, 60);
        }
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

        Intent assistIntent = new Intent(getContext(), MainActivity.class);
        assistIntent.setAction(MainActivity.ACTION_ASSIST_LAUNCH);
        assistIntent.putExtra(MainActivity.EXTRA_HAS_SCREENSHOT, hasScreenshot);
        assistIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);

        boolean started = false;
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                ActivityOptions opts = ActivityOptions.makeBasic();
                startAssistantActivity(assistIntent, opts.toBundle());
            } else {
                startAssistantActivity(assistIntent);
            }
            started = true;
            Log.i(TAG, "startAssistantActivity succeeded (hasScreenshot=" + hasScreenshot + ")");
        } catch (Exception e) {
            Log.w(TAG, "startAssistantActivity threw, falling back to Context.startActivity: " + e.getMessage());
        }

        if (!started) {
            try {
                getContext().startActivity(assistIntent);
            } catch (Exception e2) {
                Log.e(TAG, "Fallback startActivity failed: " + e2.getMessage());
            }
        }

        // Keep session alive briefly until ActivityTaskManager brings MainActivity forward
        handler.postDelayed(() -> {
            try { hide(); } catch (Exception ignored) { }
            try { finish(); } catch (Exception ignored) { }
        }, 350);
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

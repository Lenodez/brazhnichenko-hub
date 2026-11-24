package com.brazhnichenko.hub;

import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import android.os.Process;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Runs llama-server IN-PROCESS inside a dedicated Android process (:llama).
 * Because it is spawned by Zygote, Android's classloader-namespace honors
 * <uses-native-library> for libOpenCL.so (Adreno GPU) and libcdsprpc.so (Hexagon NPU)!
 * Stopping kills this isolated process cleanly to release 100% of RAM/NPU/GPU memory.
 */
public class LlamaServiceProcess extends Service {
    public static final String ACTION_START = "com.brazhnichenko.hub.LLAMA_START";
    public static final String ACTION_STOP = "com.brazhnichenko.hub.LLAMA_STOP";
    public static final String EXTRA_ARGS = "args";
    public static final String EXTRA_LOG_PATH = "log_path";
    public static final String EXTRA_STATE_PATH = "state_path";

    private static volatile boolean started = false;

    static {
        try { System.loadLibrary("hub_jni"); } catch (Throwable ignored) { }
    }

    private static native int nativeRunServer(String[] args, String nativeDir, String logPath);

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;
        String action = intent.getAction();
        if (ACTION_STOP.equals(action)) {
            stopSelf();
            Process.killProcess(Process.myPid());
            return START_NOT_STICKY;
        }
        if (ACTION_START.equals(action)) {
            final String[] args = intent.getStringArrayExtra(EXTRA_ARGS);
            final String logPath = intent.getStringExtra(EXTRA_LOG_PATH);
            final String statePath = intent.getStringExtra(EXTRA_STATE_PATH);
            if (args == null || args.length == 0) return START_NOT_STICKY;
            if (started) return START_NOT_STICKY;
            started = true;

            writeState(statePath, "RUNNING:" + Process.myPid());
            final String nativeDir = getApplicationInfo().nativeLibraryDir;

            Thread t = new Thread(() -> {
                try {
                    appendToLog(logPath, "[HubJNI] :llama PID=" + Process.myPid() + ", nativeDir=" + nativeDir);
                    // Pre-load in Android classloader-namespace so public vendor libs (libOpenCL.so, libcdsprpc.so) resolve!
                    System.loadLibrary("ggml-base");
                    System.loadLibrary("ggml-cpu");
                    try { System.loadLibrary("ggml-rpc"); } catch (Throwable ignored) { }
                    try { System.loadLibrary("ggml-opencl"); } catch (Throwable e) { appendToLog(logPath, "[HubJNI] OpenCL note: " + e.getMessage()); }
                    try { System.loadLibrary("ggml-hexagon"); } catch (Throwable e) { appendToLog(logPath, "[HubJNI] Hexagon note: " + e.getMessage()); }
                    System.loadLibrary("ggml");
                    System.loadLibrary("llama");
                    System.loadLibrary("mtmd");
                    System.loadLibrary("llama-common");
                    System.loadLibrary("llama-server-impl");

                    int rc = nativeRunServer(args, nativeDir, logPath != null ? logPath : "");
                    writeState(statePath, "EXITED:" + rc);
                } catch (Throwable ex) {
                    appendToLog(logPath, "[HubJNI] Fatal error: " + ex.toString());
                    writeState(statePath, "ERROR:" + ex.getMessage());
                } finally {
                    started = false;
                    stopSelf();
                    Process.killProcess(Process.myPid());
                }
            }, "LlamaServerMain");
            t.start();
        }
        return START_NOT_STICKY;
    }

    private static void writeState(String path, String text) {
        if (path == null || path.isEmpty()) return;
        try (FileOutputStream fos = new FileOutputStream(new File(path), false)) {
            fos.write(text.getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) { }
    }

    private static void appendToLog(String logPath, String line) {
        if (logPath == null || logPath.isEmpty()) return;
        try (FileOutputStream fos = new FileOutputStream(logPath, true)) {
            fos.write((line + "\n").getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) { }
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}

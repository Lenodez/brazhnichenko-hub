package com.brazhnichenko.hub;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Process;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;

/**
 * Controls the isolated :llama Android process running llama-server via JNI
 * inside Android's classloader-namespace (full access to Qualcomm Hexagon NPU HTP0 & Adreno GPU OpenCL!).
 */
public class LlamaProcessManager {
    public static final int LLAMA_PORT = 18080;

    public interface StatusListener {
        void onStatusChanged(String statusText);
    }

    private final Context ctx;
    private final ModelManager models;
    private final StatusListener listener;
    private final File logFile;
    private final File stateFile;
    private final Object lock = new Object();

    private volatile String runningModelFile = "";
    private volatile String activeBackend = "hexagon";
    private volatile boolean runningWithMmproj = false;
    private volatile boolean starting = false;
    private volatile String startingModelFile = "";
    private volatile String startupStage = "";
    private volatile long startupStartedMs = 0L;
    private volatile long lastActivityMs = System.currentTimeMillis();
    private volatile boolean streamingActive = false;

    public void touchActivity() { lastActivityMs = System.currentTimeMillis(); }
    public void setStreamingActive(boolean active) {
        streamingActive = active;
        lastActivityMs = System.currentTimeMillis();
    }

    public boolean isStarting() { return starting; }
    public String getStartingModelFile() { return starting ? startingModelFile : ""; }
    public String getStartupStage() { return starting ? startupStage : ""; }
    public double getStartupElapsedSec() {
        if (!starting || startupStartedMs <= 0L) return 0.0;
        return Math.round(((System.currentTimeMillis() - startupStartedMs) / 100.0)) / 10.0;
    }

    public void startAsyncIfNeeded(final String requestedModelFile) {
        final String target = (requestedModelFile != null && !requestedModelFile.trim().isEmpty())
                ? requestedModelFile.trim() : models.getActiveModelFile();
        if (target.isEmpty()) return;
        if (starting && target.equalsIgnoreCase(startingModelFile)) return;
        if (isRunning() && target.equalsIgnoreCase(runningModelFile)) {
            touchActivity();
            return;
        }
        Thread t = new Thread(() -> {
            try {
                start(target);
            } catch (Exception ignored) { }
        }, "LlamaWarmupThread");
        t.setDaemon(true);
        t.start();
    }

    public void checkIdleUnload() {
        int mins = models.getPrefs().getInt("idle_unload_min", 10);
        if (mins <= 0 || streamingActive || !isRunning()) return;
        long elapsed = System.currentTimeMillis() - lastActivityMs;
        if (elapsed >= mins * 60_000L) {
            appendLog("[Hub] Авто-выгрузка LLM после " + mins + " мин простоя (RAM и NPU освобождены).");
            stop();
        }
    }

    public LlamaProcessManager(Context ctx, ModelManager models, StatusListener listener) {
        this.ctx = ctx.getApplicationContext();
        this.models = models;
        this.listener = listener;
        this.logFile = new File(this.ctx.getFilesDir(), "llama_server.log");
        this.stateFile = new File(this.ctx.getFilesDir(), "llama_state.txt");
    }

    public boolean isRunning() {
        return pingHealth();
    }

    public String getRunningModelFile() { return isRunning() ? runningModelFile : ""; }
    public String getActiveBackend() { return activeBackend; }
    public boolean isRunningWithMmproj() { return isRunning() && runningWithMmproj; }

    public String getLogsTail() {
        if (!logFile.exists()) return "(Логи llama-server пока пусты)";
        Deque<String> lines = new ArrayDeque<>(220);
        try (BufferedReader br = new BufferedReader(new InputStreamReader(new FileInputStream(logFile), StandardCharsets.UTF_8))) {
            String line;
            while ((line = br.readLine()) != null) {
                if (lines.size() >= 200) lines.removeFirst();
                lines.addLast(line);
            }
        } catch (Exception e) {
            return "(Ошибка чтения логов: " + e.getMessage() + ")";
        }
        if (lines.isEmpty()) return "(Логи llama-server пока пусты)";
        StringBuilder sb = new StringBuilder();
        for (String l : lines) sb.append(l).append('\n');
        return sb.toString();
    }

    private void appendLog(String line) {
        try (FileOutputStream fos = new FileOutputStream(logFile, true)) {
            fos.write((line + "\n").getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) { }
    }

    private void clearLog() {
        try (FileOutputStream fos = new FileOutputStream(logFile, false)) {
            fos.write(new byte[0]);
        } catch (Exception ignored) { }
    }

    private String readState() {
        if (!stateFile.exists()) return "";
        try (FileInputStream fis = new FileInputStream(stateFile)) {
            byte[] b = new byte[(int) Math.min(256, stateFile.length())];
            int n = fis.read(b);
            return n > 0 ? new String(b, 0, n, StandardCharsets.UTF_8).trim() : "";
        } catch (Exception e) {
            return "";
        }
    }

    public void stop() {
        synchronized (lock) {
            String st = readState();
            if (st.startsWith("RUNNING:")) {
                try {
                    int pid = Integer.parseInt(st.substring("RUNNING:".length()).trim());
                    if (pid > 1 && pid != Process.myPid()) {
                        Process.killProcess(pid);
                    }
                } catch (Exception ignored) { }
            }
            if (pingHealth()) {
                try {
                    Intent stopIntent = new Intent(ctx, LlamaServiceProcess.class);
                    stopIntent.setAction(LlamaServiceProcess.ACTION_STOP);
                    ctx.startService(stopIntent);
                } catch (Exception ignored) { }
            }
            try { ctx.stopService(new Intent(ctx, LlamaServiceProcess.class)); } catch (Exception ignored) { }
            if (stateFile.exists()) stateFile.delete();

            for (int i = 0; i < 10; i++) {
                if (!pingHealth()) break;
                try { Thread.sleep(200); } catch (Exception ignored) { }
            }

            runningModelFile = "";
            runningWithMmproj = false;
            starting = false;
            startingModelFile = "";
            startupStage = "";
            startupStartedMs = 0L;
            if (listener != null) listener.onStatusChanged("LLM остановлена • RAM/NPU свободны");
        }
    }

    public void start(String requestedModelFile) throws Exception {
        synchronized (lock) {
            String modelFile = (requestedModelFile != null && !requestedModelFile.trim().isEmpty())
                    ? requestedModelFile.trim() : models.getActiveModelFile();
            if (modelFile.isEmpty()) {
                throw new IllegalStateException("Нет скачанных GGUF моделей. Скачай модель (например Qwen 3 4B) во вкладке «Модели».");
            }

            File modelPath = new File(models.getModelsDir(), modelFile);
            if (!modelPath.exists()) {
                throw new IllegalStateException("Файл модели не найден: " + modelFile);
            }

            if (isRunning() && modelFile.equalsIgnoreCase(runningModelFile)) {
                return;
            }

            stop();
            clearLog();
            models.setActiveModelFile(modelFile);

            SharedPreferences prefs = models.getPrefs();
            String desiredBackend = prefs.getString("backend_mode", "auto"); // auto | hexagon | opencl | cpu | eco
            int ctxSize = prefs.getInt("ctx_size", 8192);
            int defaultThreads = Math.max(2, Math.min(6, Runtime.getRuntime().availableProcessors() - 2));
            int threads = prefs.getInt("threads", defaultThreads);
            double temp = Double.parseDouble(prefs.getString("temp", "0.7"));
            double presence = Double.parseDouble(prefs.getString("presence_penalty", "1.2"));

            File mmproj = models.findMatchingMmproj(modelFile);

            if ("auto".equals(desiredBackend)) {
                for (String candidate : new String[]{"hexagon", "opencl", "cpu"}) {
                    try {
                        launchAndWait(modelPath, mmproj, candidate, ctxSize, threads, temp, presence);
                        return;
                    } catch (Exception err) {
                        appendLog("[Hub] Бэкенд " + candidate + " не подошёл (" + err.getMessage() + "), пробую следующий...");
                        stop();
                    }
                }
                throw new RuntimeException("Не удалось запустить модель ни на одном бэкенде. Смотри логи во вкладке Система.");
            }

            launchAndWait(modelPath, mmproj, desiredBackend, ctxSize,
                    "eco".equals(desiredBackend) ? Math.min(4, threads) : threads, temp, presence);
        }
    }

    private void launchAndWait(File modelPath, File mmproj, String backend,
                               int ctxSize, int threads, double temp, double presence) throws Exception {
        List<String> args = new ArrayList<>();
        args.add("llama-server");
        args.add("-m"); args.add(modelPath.getAbsolutePath());
        if (mmproj != null && mmproj.exists()) {
            args.add("--mmproj"); args.add(mmproj.getAbsolutePath());
        }
        args.add("--host"); args.add("127.0.0.1");
        args.add("--port"); args.add(String.valueOf(LLAMA_PORT));
        args.add("--ctx-size"); args.add(String.valueOf(ctxSize));
        args.add("--threads"); args.add(String.valueOf(threads));
        args.add("--parallel"); args.add("1");
        args.add("--cache-type-k"); args.add("q8_0");
        args.add("--cache-type-v"); args.add("q8_0");
        args.add("--jinja");
        args.add("--no-webui");
        args.add("--no-reasoning-preserve");
        args.add("--temp"); args.add(String.valueOf(temp));
        args.add("--presence-penalty"); args.add(String.valueOf(presence));

        if ("cpu".equals(backend) || "eco".equals(backend)) {
            args.add("-ngl"); args.add("0");
            args.add("--device"); args.add("none");
        } else if ("hexagon".equals(backend)) {
            args.add("-ngl"); args.add("99");
            args.add("--device"); args.add("HTP0");
        } else if ("opencl".equals(backend)) {
            args.add("-ngl"); args.add("99");
            args.add("--device"); args.add("GPUOpenCL");
            args.add("--flash-attn"); args.add("auto");
        } else {
            args.add("-ngl"); args.add("99");
        }

        if (stateFile.exists()) stateFile.delete();
        appendLog("[Hub] Запуск (" + backend + "): " + modelPath.getName() + (mmproj != null ? " + Vision mmproj" : ""));
        activeBackend = backend;
        starting = true;
        startingModelFile = modelPath.getName();
        startupStartedMs = System.currentTimeMillis();
        startupStage = "Инициализация " + backend.toUpperCase() + "...";

        Intent startIntent = new Intent(ctx, LlamaServiceProcess.class);
        startIntent.setAction(LlamaServiceProcess.ACTION_START);
        startIntent.putExtra(LlamaServiceProcess.EXTRA_ARGS, args.toArray(new String[0]));
        startIntent.putExtra(LlamaServiceProcess.EXTRA_LOG_PATH, logFile.getAbsolutePath());
        startIntent.putExtra(LlamaServiceProcess.EXTRA_STATE_PATH, stateFile.getAbsolutePath());
        ctx.startService(startIntent);

        if (listener != null) listener.onStatusChanged("Загрузка (" + backend.toUpperCase() + "): " + modelPath.getName() + "...");

        try {
            long deadline = System.currentTimeMillis() + 90_000L;
            while (System.currentTimeMillis() < deadline) {
                int hc = checkHealthCode();
                if (hc == 200) {
                    starting = false;
                    startingModelFile = "";
                    startupStage = "";
                    runningModelFile = modelPath.getName();
                    runningWithMmproj = (mmproj != null && mmproj.exists());
                    touchActivity();
                    String label = "hexagon".equals(backend) ? "Hexagon NPU"
                            : "opencl".equals(backend) ? "Adreno GPU" : "CPU";
                    if (listener != null) {
                        listener.onStatusChanged("Активна (" + label + "): " + modelPath.getName());
                    }
                    return;
                } else if (hc == 503) {
                    startupStage = "Загрузка весов в память (" + backend.toUpperCase() + ")"
                            + (mmproj != null && mmproj.exists() ? " + Vision" : "") + "...";
                }
                String st = readState();
                if (st.startsWith("EXITED:") || st.startsWith("ERROR:")) {
                    throw new RuntimeException("llama-server завершился (" + st + "). Смотри логи во вкладке Система.");
                }
                Thread.sleep(300);
            }
            stop();
            throw new RuntimeException("Таймаут запуска модели (90 с).");
        } catch (Exception ex) {
            starting = false;
            startingModelFile = "";
            startupStage = "";
            throw ex;
        }
    }

    private int checkHealthCode() {
        try {
            HttpURLConnection conn = (HttpURLConnection) new URL("http://127.0.0.1:" + LLAMA_PORT + "/health").openConnection();
            conn.setConnectTimeout(400);
            conn.setReadTimeout(400);
            int code = conn.getResponseCode();
            conn.disconnect();
            return code;
        } catch (Exception e) {
            return -1;
        }
    }

    private boolean pingHealth() {
        return checkHealthCode() == 200;
    }
}

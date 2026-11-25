package com.brazhnichenko.hub;

import android.app.ActivityManager;
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
    private volatile boolean cancelRequested = false;
    private volatile int activeLlamaPid = -1;
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

    private int findLlamaServicePid() {
        try {
            ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) {
                List<ActivityManager.RunningAppProcessInfo> procs = am.getRunningAppProcesses();
                if (procs != null) {
                    String targetProc = ctx.getPackageName() + ":llama";
                    for (ActivityManager.RunningAppProcessInfo p : procs) {
                        if (targetProc.equals(p.processName) && p.pid > 1) {
                            return p.pid;
                        }
                    }
                }
            }
        } catch (Exception ignored) { }
        return -1;
    }

    private boolean isLlamaProcessAlive(int pid) {
        if (pid > 1 && new File("/proc/" + pid).exists()) {
            return true;
        }
        int runningPid = findLlamaServicePid();
        return runningPid > 1 && (pid <= 1 || runningPid == pid);
    }

    private void killLlamaProcessNow() {
        try { ctx.stopService(new Intent(ctx, LlamaServiceProcess.class)); } catch (Exception ignored) { }
        int pid = activeLlamaPid;
        if (pid > 1 && pid != Process.myPid()) {
            try { Process.killProcess(pid); } catch (Exception ignored) { }
        }
        String st = readState();
        if (st.startsWith("RUNNING:")) {
            try {
                int spid = Integer.parseInt(st.substring("RUNNING:".length()).trim());
                if (spid > 1 && spid != Process.myPid()) {
                    Process.killProcess(spid);
                }
            } catch (Exception ignored) { }
        }
        int foundPid = findLlamaServicePid();
        if (foundPid > 1 && foundPid != Process.myPid()) {
            try { Process.killProcess(foundPid); } catch (Exception ignored) { }
        }
        if (stateFile.exists()) {
            try { stateFile.delete(); } catch (Exception ignored) { }
        }
        activeLlamaPid = -1;
    }

    public static String backendDisplayName(String backend) {
        if ("hexagon".equalsIgnoreCase(backend)) return "Hexagon NPU";
        if ("opencl".equalsIgnoreCase(backend)) return "Adreno GPU";
        if ("eco".equalsIgnoreCase(backend)) return "CPU Eco";
        return "CPU";
    }

    public void stop() {
        // Non-blocking immediate abort so UI / notification "Stop" NEVER softlocks during model loading!
        cancelRequested = true;
        boolean wasStarting = starting;
        starting = false;
        startingModelFile = "";
        startupStage = "";
        startupStartedMs = 0L;
        runningModelFile = "";
        runningWithMmproj = false;

        killLlamaProcessNow();
        if (wasStarting) {
            appendLog("[Hub] Загрузка модели отменена пользователем.");
        }
        if (listener != null) listener.onStatusChanged("LLM остановлена • RAM/NPU свободны");

        // Wait briefly for port release without deadlocking against start()
        for (int i = 0; i < 10; i++) {
            if (checkHealthCode() < 0 && findLlamaServicePid() <= 0) break;
            killLlamaProcessNow();
            try { Thread.sleep(150); } catch (Exception ignored) { break; }
        }
    }

    public void start(String requestedModelFile) throws Exception {
        synchronized (lock) {
            cancelRequested = false;
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

            killLlamaProcessNow();
            for (int i = 0; i < 12; i++) {
                if (checkHealthCode() < 0 && findLlamaServicePid() <= 0) break;
                killLlamaProcessNow();
                try { Thread.sleep(150); } catch (Exception ignored) { }
            }
            runningModelFile = "";
            runningWithMmproj = false;
            clearLog();
            models.setActiveModelFile(modelFile);

            SharedPreferences prefs = models.getPrefs();
            String desiredBackend = prefs.getString("backend_mode", "auto"); // auto | hexagon | opencl | cpu | eco
            int ctxSize = prefs.getInt("ctx_size", 8192);
            int defaultThreads = Math.max(2, Math.min(6, Runtime.getRuntime().availableProcessors() - 2));
            int threads = prefs.getInt("threads", defaultThreads);
            double temp = Double.parseDouble(prefs.getString("temp", "0.7"));
            double presence = Double.parseDouble(prefs.getString("presence_penalty", "1.2"));

            // Protect against Android LMK OOM on large 7B-8B models when available RAM is tight
            long modelBytes = modelPath.length();
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) am.getMemoryInfo(mi);
            if (modelBytes > 4_000_000_000L && mi.availMem > 0 && mi.availMem < (modelBytes + 1_200_000_000L)) {
                if (ctxSize > 4096) {
                    appendLog("[Hub] Мало свободной RAM (" + String.format("%.1f", mi.availMem / (1024.0 * 1024.0 * 1024.0))
                            + " ГБ) для модели " + String.format("%.2f", modelBytes / (1024.0 * 1024.0 * 1024.0))
                            + " ГБ — снижаю контекст с " + ctxSize + " до 4096 для защиты от OOM.");
                    ctxSize = 4096;
                }
            }

            File mmproj = models.findMatchingMmproj(modelFile);

            if ("auto".equals(desiredBackend)) {
                // On Snapdragon, Hexagon NPU -> CPU is fast and reliable (OpenCL stays available as manual option)
                for (String candidate : new String[]{"hexagon", "cpu"}) {
                    if (cancelRequested) {
                        throw new RuntimeException("Загрузка модели отменена.");
                    }
                    try {
                        launchAndWait(modelPath, mmproj, candidate, ctxSize, threads, temp, presence);
                        return;
                    } catch (Exception err) {
                        if (cancelRequested) {
                            throw new RuntimeException("Загрузка модели отменена.");
                        }
                        appendLog("[Hub] Бэкенд " + candidate + " не подошёл (" + err.getMessage() + "), пробую следующий...");
                        killLlamaProcessNow();
                        try { Thread.sleep(250); } catch (Exception ignored) { }
                    }
                }
                throw new RuntimeException("Не удалось запустить модель (" + modelFile + "). Проверь свободную RAM или логи во вкладке Система.");
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
        if (!"opencl".equals(backend)) {
            args.add("--cache-type-k"); args.add("q8_0");
            args.add("--cache-type-v"); args.add("q8_0");
        }
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
            args.add("--flash-attn"); args.add("off");
        } else {
            args.add("-ngl"); args.add("99");
        }

        if (stateFile.exists()) stateFile.delete();
        String bLabel = backendDisplayName(backend);
        appendLog("[Hub] Запуск (" + backend + "): " + modelPath.getName() + (mmproj != null ? " + Vision mmproj" : ""));
        activeBackend = backend;
        activeLlamaPid = -1;
        starting = true;
        startingModelFile = modelPath.getName();
        startupStartedMs = System.currentTimeMillis();
        startupStage = "Инициализация " + bLabel + "...";

        Intent startIntent = new Intent(ctx, LlamaServiceProcess.class);
        startIntent.setAction(LlamaServiceProcess.ACTION_START);
        startIntent.putExtra(LlamaServiceProcess.EXTRA_ARGS, args.toArray(new String[0]));
        startIntent.putExtra(LlamaServiceProcess.EXTRA_LOG_PATH, logFile.getAbsolutePath());
        startIntent.putExtra(LlamaServiceProcess.EXTRA_STATE_PATH, stateFile.getAbsolutePath());
        try {
            ctx.startService(startIntent);
        } catch (Exception startEx) {
            appendLog("[Hub] Ошибка startService(:llama): " + startEx.toString());
            throw new RuntimeException("Не удалось запустить сервис :llama: " + startEx.getMessage());
        }

        if (listener != null) listener.onStatusChanged("Загрузка (" + bLabel + "): " + modelPath.getName() + "...");

        try {
            long timeoutMs = "opencl".equals(backend) ? 45_000L : 60_000L;
            long startedAt = System.currentTimeMillis();
            long deadline = startedAt + timeoutMs;
            int seenPid = -1;
            int missingPidChecks = 0;
            boolean retriedServiceStart = false;

            while (System.currentTimeMillis() < deadline) {
                if (cancelRequested) {
                    killLlamaProcessNow();
                    throw new RuntimeException("Загрузка модели отменена.");
                }

                int hc = checkHealthCode();
                if (hc == 200) {
                    starting = false;
                    startingModelFile = "";
                    startupStage = "";
                    runningModelFile = modelPath.getName();
                    runningWithMmproj = (mmproj != null && mmproj.exists());
                    touchActivity();
                    if (listener != null) {
                        listener.onStatusChanged("Активна (" + bLabel + "): " + modelPath.getName());
                    }
                    return;
                } else if (hc == 503) {
                    startupStage = "Загрузка весов в память (" + bLabel + ")"
                            + (mmproj != null && mmproj.exists() ? " + Vision" : "") + "...";
                }

                String st = readState();
                if (st.startsWith("EXITED:") || st.startsWith("ERROR:")) {
                    throw new RuntimeException("llama-server завершился (" + st + "). Смотри логи во вкладке Система.");
                }
                if (st.startsWith("RUNNING:")) {
                    try {
                        int parsed = Integer.parseInt(st.substring("RUNNING:".length()).trim());
                        if (parsed > 1) {
                            seenPid = parsed;
                            activeLlamaPid = parsed;
                        }
                    } catch (Exception ignored) { }
                } else if (seenPid <= 1) {
                    int discovered = findLlamaServicePid();
                    if (discovered > 1) {
                        seenPid = discovered;
                        activeLlamaPid = discovered;
                    }
                }

                long waitedMs = System.currentTimeMillis() - startedAt;
                if (seenPid <= 1 && hc < 0) {
                    if (!retriedServiceStart && waitedMs > 4_000L) {
                        retriedServiceStart = true;
                        appendLog("[Hub] Повторный запуск сервиса :llama...");
                        try { ctx.startService(startIntent); } catch (Exception ignored) { }
                    } else if (waitedMs > 12_000L) {
                        appendLog("[Hub] Сервис :llama не стартовал за 12 с.");
                        throw new RuntimeException("Процесс :llama не запустился (возможно, Android ограничил запуск после остановки). Попробуй ещё раз.");
                    }
                }

                // Detect silent Android OOM kill (SIGKILL) immediately instead of hanging 60-90s!
                if (seenPid > 1 && !isLlamaProcessAlive(seenPid)) {
                    missingPidChecks++;
                    if (missingPidChecks >= 3) {
                        String tailCheck = readState();
                        if (tailCheck.startsWith("EXITED:") || tailCheck.startsWith("ERROR:")) {
                            throw new RuntimeException("llama-server завершился (" + tailCheck + ").");
                        }
                        appendLog("[Hub] Процесс :llama (PID=" + seenPid + ") внезапно исчез — вероятнее всего Android OOM kill (не хватило RAM для " + modelPath.getName() + ").");
                        throw new RuntimeException("Android завершил процесс модели из-за нехватки RAM (OOM kill для "
                                + modelPath.getName() + "). Нажми «Освободить RAM» или выбери модель 4B.");
                    }
                } else {
                    missingPidChecks = 0;
                }

                Thread.sleep(250);
            }
            killLlamaProcessNow();
            throw new RuntimeException("Таймаут запуска модели на " + bLabel + " (" + (timeoutMs / 1000) + " с). Попробуй Hexagon NPU или CPU.");
        } catch (Exception ex) {
            starting = false;
            startingModelFile = "";
            startupStage = "";
            startupStartedMs = 0L;
            killLlamaProcessNow();
            if (listener != null && !cancelRequested) {
                listener.onStatusChanged("Ошибка загрузки (" + bLabel + ")");
            }
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

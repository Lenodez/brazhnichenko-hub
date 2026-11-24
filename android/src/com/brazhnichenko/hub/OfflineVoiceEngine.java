package com.brazhnichenko.hub;

import android.annotation.SuppressLint;
import android.content.Context;
import android.content.SharedPreferences;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;

import com.k2fsa.sherpa.onnx.FeatureConfig;
import com.k2fsa.sherpa.onnx.OfflineModelConfig;
import com.k2fsa.sherpa.onnx.OfflineRecognizer;
import com.k2fsa.sherpa.onnx.OfflineRecognizerConfig;
import com.k2fsa.sherpa.onnx.OfflineRecognizerResult;
import com.k2fsa.sherpa.onnx.OfflineStream;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * 100% Offline Speech-to-Text engine backed by Sherpa-ONNX (GigaAM v2 Russian / Whisper Tiny RU+EN).
 * Works completely in Airplane Mode without Google services. Zero RAM usage until first voice capture.
 */
public final class OfflineVoiceEngine {
    public static final String MODEL_GIGAAM = "gigaam-v2-ru";
    public static final String MODEL_WHISPER = "whisper-tiny";
    private static final int SAMPLE_RATE = 16000;
    private static final int MAX_SECONDS = 30;

    public interface VoiceCallback {
        void onState(String state, String detail, int levelPct, double seconds);
        void onResult(String text, long decodeMs);
        void onError(String message);
    }

    private static final class SttFile {
        final String url;
        final String localName;
        SttFile(String url, String localName) { this.url = url; this.localName = localName; }
    }

    private static final class SttSpec {
        final String id;
        final String name;
        final String desc;
        final double sizeMB;
        final SttFile[] files;
        SttSpec(String id, String name, String desc, double sizeMB, SttFile[] files) {
            this.id = id; this.name = name; this.desc = desc; this.sizeMB = sizeMB; this.files = files;
        }
    }

    private static final SttSpec[] SPECS = new SttSpec[]{
            new SttSpec(
                    MODEL_GIGAAM,
                    "GigaAM v2 Russian (Сбер)",
                    "Лучшее качество русской речи офлайн (точнее Whisper Large)",
                    225.5,
                    new SttFile[]{
                            new SttFile("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19/resolve/main/model.int8.onnx", "model.int8.onnx"),
                            new SttFile("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19/resolve/main/tokens.txt", "tokens.txt")
                    }
            ),
            new SttSpec(
                    MODEL_WHISPER,
                    "Whisper Tiny (RU + EN)",
                    "Компактный мультиязычный распознаватель (русский + английский)",
                    98.8,
                    new SttFile[]{
                            new SttFile("https://huggingface.co/csukuangfj/sherpa-onnx-whisper-tiny/resolve/main/tiny-encoder.int8.onnx", "tiny-encoder.int8.onnx"),
                            new SttFile("https://huggingface.co/csukuangfj/sherpa-onnx-whisper-tiny/resolve/main/tiny-decoder.int8.onnx", "tiny-decoder.int8.onnx"),
                            new SttFile("https://huggingface.co/csukuangfj/sherpa-onnx-whisper-tiny/resolve/main/tiny-tokens.txt", "tiny-tokens.txt")
                    }
            )
    };

    private static OfflineRecognizer cachedRecognizer = null;
    private static String cachedModelId = "";
    private static final Object REC_LOCK = new Object();

    private static final AtomicBoolean recording = new AtomicBoolean(false);
    private static final AtomicBoolean cancelCapture = new AtomicBoolean(false);

    private static volatile String dlModelId = "";
    private static volatile String dlState = "idle"; // idle | downloading | error
    private static volatile double dlPercent = 0;
    private static volatile String dlError = "";
    private static final AtomicBoolean dlCancel = new AtomicBoolean(false);

    private OfflineVoiceEngine() { }

    public static File getSttRootDir(Context ctx) {
        File ext = ctx.getExternalFilesDir("stt");
        File dir = ext != null ? ext : new File(ctx.getFilesDir(), "stt");
        if (!dir.exists()) dir.mkdirs();
        return dir;
    }

    public static File getModelDir(Context ctx, String modelId) {
        return new File(getSttRootDir(ctx), modelId);
    }

    public static boolean isModelInstalled(Context ctx, String modelId) {
        SttSpec spec = findSpec(modelId);
        if (spec == null) return false;
        File dir = getModelDir(ctx, modelId);
        for (SttFile f : spec.files) {
            File lf = new File(dir, f.localName);
            if (!lf.exists() || lf.length() < 64) return false;
        }
        return true;
    }

    public static String getActiveInstalledModel(Context ctx) {
        SharedPreferences p = ctx.getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
        String mode = p.getString("stt_mode", "auto"); // auto | offline | system
        if ("system".equals(mode)) return "";
        String prefModel = p.getString("stt_model", MODEL_GIGAAM);
        if (isModelInstalled(ctx, prefModel)) return prefModel;
        for (SttSpec s : SPECS) {
            if (isModelInstalled(ctx, s.id)) return s.id;
        }
        return "";
    }

    public static boolean isRecording() {
        return recording.get();
    }

    public static void stopRecording(boolean cancel) {
        cancelCapture.set(cancel);
        recording.set(false);
    }

    public static void unloadRecognizer() {
        synchronized (REC_LOCK) {
            if (cachedRecognizer != null) {
                try { cachedRecognizer.release(); } catch (Exception ignored) { }
                cachedRecognizer = null;
                cachedModelId = "";
            }
        }
    }

    public static JSONObject getStatusJson(Context ctx) {
        JSONObject o = new JSONObject();
        try {
            SharedPreferences p = ctx.getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
            String mode = p.getString("stt_mode", "auto");
            String activeModel = getActiveInstalledModel(ctx);
            o.put("sttMode", mode);
            o.put("activeModel", activeModel);
            o.put("offlineReady", !activeModel.isEmpty());
            o.put("recording", recording.get());
            o.put("dlState", dlState);
            o.put("dlModelId", dlModelId);
            o.put("dlPercent", dlPercent);
            o.put("dlError", dlError);

            JSONArray arr = new JSONArray();
            for (SttSpec s : SPECS) {
                JSONObject m = new JSONObject();
                m.put("id", s.id);
                m.put("name", s.name);
                m.put("desc", s.desc);
                m.put("sizeMB", s.sizeMB);
                m.put("installed", isModelInstalled(ctx, s.id));
                m.put("active", s.id.equals(activeModel));
                arr.put(m);
            }
            o.put("models", arr);
        } catch (Exception ignored) { }
        return o;
    }

    public static void startDownload(Context ctx, String modelId) {
        SttSpec spec = findSpec(modelId);
        if (spec == null || "downloading".equals(dlState)) return;
        dlState = "downloading";
        dlModelId = modelId;
        dlPercent = 0;
        dlError = "";
        dlCancel.set(false);

        final Context appCtx = ctx.getApplicationContext();
        new Thread(() -> {
            try {
                File dir = getModelDir(appCtx, spec.id);
                if (!dir.exists()) dir.mkdirs();

                long totalEst = (long) (spec.sizeMB * 1048576.0);
                long doneTotal = 0;

                for (SttFile f : spec.files) {
                    if (dlCancel.get()) throw new Exception("Скачивание отменено");
                    File target = new File(dir, f.localName);
                    if (target.exists() && target.length() > 1024) {
                        doneTotal += target.length();
                        continue;
                    }
                    File part = new File(dir, f.localName + ".part");
                    if (part.exists()) part.delete();

                    HttpURLConnection conn = openFollowingRedirects(f.url);
                    try (InputStream is = new BufferedInputStream(conn.getInputStream(), 32768);
                         FileOutputStream fos = new FileOutputStream(part)) {
                        byte[] buf = new byte[32768];
                        int n;
                        while ((n = is.read(buf)) > 0) {
                            if (dlCancel.get()) {
                                conn.disconnect();
                                part.delete();
                                throw new Exception("Скачивание отменено");
                            }
                            fos.write(buf, 0, n);
                            doneTotal += n;
                            dlPercent = Math.min(99.5, Math.round((doneTotal * 1000.0) / Math.max(1, totalEst)) / 10.0);
                        }
                    } finally {
                        conn.disconnect();
                    }
                    if (!part.renameTo(target)) throw new Exception("Не удалось сохранить " + f.localName);
                }

                // Auto-select newly downloaded model
                SharedPreferences p = appCtx.getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
                p.edit().putString("stt_model", spec.id).apply();

                dlPercent = 100;
                dlState = "idle";
                dlModelId = "";
            } catch (Exception ex) {
                dlState = dlCancel.get() ? "idle" : "error";
                dlError = dlCancel.get() ? "" : (ex.getMessage() != null ? ex.getMessage() : "Ошибка загрузки");
                dlModelId = "";
            }
        }).start();
    }

    public static void cancelDownload() {
        dlCancel.set(true);
    }

    public static void deleteModel(Context ctx, String modelId) {
        if (modelId.equals(cachedModelId)) unloadRecognizer();
        File dir = getModelDir(ctx, modelId);
        if (dir.exists()) {
            File[] files = dir.listFiles();
            if (files != null) for (File f : files) f.delete();
            dir.delete();
        }
    }

    @SuppressLint("MissingPermission")
    public static void startCaptureAndTranscribe(Context ctx, VoiceCallback cb) {
        if (!recording.compareAndSet(false, true)) return;
        cancelCapture.set(false);
        final Context appCtx = ctx.getApplicationContext();
        final String modelId = getActiveInstalledModel(appCtx);
        if (modelId.isEmpty()) {
            recording.set(false);
            cb.onError("Офлайн-модель речи не установлена");
            return;
        }

        new Thread(() -> {
            AudioRecord ar = null;
            try {
                int minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
                int bufBytes = Math.max(minBuf * 2, 6400);
                ar = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, SAMPLE_RATE,
                        AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bufBytes);
                if (ar.getState() != AudioRecord.STATE_INITIALIZED) {
                    throw new Exception("Не удалось инициализировать микрофон");
                }

                SttSpec spec = findSpec(modelId);
                String label = spec != null ? spec.name : modelId;
                cb.onState("recording", label, 0, 0.0);

                ar.startRecording();
                List<short[]> chunks = new ArrayList<>();
                int totalSamples = 0;
                int maxSamples = SAMPLE_RATE * MAX_SECONDS;
                short[] frame = new short[1600]; // 100 ms per read

                boolean heardSpeech = false;
                int speechFrames = 0;
                int silenceFrames = 0;
                int frameCount = 0;

                while (recording.get() && totalSamples < maxSamples) {
                    int read = ar.read(frame, 0, frame.length);
                    if (read <= 0) continue;
                    short[] copy = new short[read];
                    System.arraycopy(frame, 0, copy, 0, read);
                    chunks.add(copy);
                    totalSamples += read;
                    frameCount++;

                    // Calculate RMS amplitude for live meter + auto-stop silence detection
                    double sumSq = 0;
                    for (int i = 0; i < read; i++) sumSq += (double) frame[i] * frame[i];
                    double rms = Math.sqrt(sumSq / read);
                    int levelPct = (int) Math.min(100, Math.round((rms / 3500.0) * 100.0));

                    if (rms > 360.0) {
                        speechFrames++;
                        silenceFrames = 0;
                        if (speechFrames >= 3) heardSpeech = true;
                    } else if (heardSpeech) {
                        silenceFrames++;
                        // Auto-stop after 1.6s of silence once user spoke
                        if (silenceFrames >= 16) {
                            recording.set(false);
                        }
                    }

                    if (frameCount % 2 == 0) {
                        cb.onState("recording", label, levelPct, totalSamples / (double) SAMPLE_RATE);
                    }
                }

                try { ar.stop(); } catch (Exception ignored) { }
                try { ar.release(); } catch (Exception ignored) { }
                ar = null;

                if (cancelCapture.get() || totalSamples < SAMPLE_RATE / 4) {
                    cb.onState("idle", "", 0, 0);
                    return;
                }

                cb.onState("transcribing", "Распознаю офлайн (" + label + ")...", 0, totalSamples / (double) SAMPLE_RATE);
                long t0 = System.nanoTime();

                float[] pcm = new float[totalSamples];
                int pos = 0;
                for (short[] c : chunks) {
                    for (short s : c) pcm[pos++] = s / 32768.0f;
                }

                OfflineRecognizer rec = getOrCreateRecognizer(appCtx, modelId);
                OfflineStream stream = rec.createStream();
                String text;
                try {
                    stream.acceptWaveform(pcm, SAMPLE_RATE);
                    rec.decode(stream);
                    OfflineRecognizerResult res = rec.getResult(stream);
                    text = res != null && res.text != null ? res.text.trim() : "";
                } finally {
                    stream.release();
                }

                long ms = Math.round((System.nanoTime() - t0) / 1_000_000.0);
                cb.onState("idle", "", 0, 0);
                if (text.isEmpty()) {
                    cb.onError("Речь не распознана — попробуй говорить чуть громче");
                } else {
                    cb.onResult(text, ms);
                }
            } catch (Exception ex) {
                recording.set(false);
                if (ar != null) {
                    try { ar.stop(); } catch (Exception ignored) { }
                    try { ar.release(); } catch (Exception ignored) { }
                }
                cb.onState("idle", "", 0, 0);
                cb.onError("Ошибка офлайн-голоса: " + (ex.getMessage() != null ? ex.getMessage() : "неизвестная ошибка"));
            }
        }).start();
    }

    private static OfflineRecognizer getOrCreateRecognizer(Context ctx, String modelId) {
        synchronized (REC_LOCK) {
            if (cachedRecognizer != null && modelId.equals(cachedModelId)) {
                return cachedRecognizer;
            }
            unloadRecognizer();

            File dir = getModelDir(ctx, modelId);
            OfflineRecognizerConfig cfg = new OfflineRecognizerConfig();
            cfg.featConfig = new FeatureConfig();
            cfg.featConfig.sampleRate = SAMPLE_RATE;
            cfg.featConfig.featureDim = 80;

            OfflineModelConfig mc = new OfflineModelConfig();
            mc.numThreads = Math.min(4, Math.max(2, Runtime.getRuntime().availableProcessors() / 2));
            mc.provider = "cpu";
            mc.debug = false;

            if (MODEL_GIGAAM.equals(modelId)) {
                mc.nemo.model = new File(dir, "model.int8.onnx").getAbsolutePath();
                mc.tokens = new File(dir, "tokens.txt").getAbsolutePath();
            } else {
                mc.whisper.encoder = new File(dir, "tiny-encoder.int8.onnx").getAbsolutePath();
                mc.whisper.decoder = new File(dir, "tiny-decoder.int8.onnx").getAbsolutePath();
                mc.whisper.language = "ru";
                mc.whisper.task = "transcribe";
                mc.tokens = new File(dir, "tiny-tokens.txt").getAbsolutePath();
                mc.modelType = "whisper";
            }

            cfg.modelConfig = mc;
            cachedRecognizer = new OfflineRecognizer(cfg);
            cachedModelId = modelId;
            return cachedRecognizer;
        }
    }

    private static SttSpec findSpec(String id) {
        for (SttSpec s : SPECS) if (s.id.equals(id)) return s;
        return null;
    }

    private static HttpURLConnection openFollowingRedirects(String urlStr) throws Exception {
        URL url = new URL(urlStr);
        for (int i = 0; i < 6; i++) {
            HttpURLConnection c = (HttpURLConnection) url.openConnection();
            c.setConnectTimeout(10000);
            c.setReadTimeout(25000);
            c.setInstanceFollowRedirects(false);
            c.setRequestProperty("User-Agent", "BrazhnichenkoHub/4.8");
            int rc = c.getResponseCode();
            if (rc == 301 || rc == 302 || rc == 303 || rc == 307 || rc == 308) {
                String loc = c.getHeaderField("Location");
                c.disconnect();
                url = new URL(url, loc);
                continue;
            }
            if (rc != 200) {
                c.disconnect();
                throw new Exception("HTTP " + rc);
            }
            return c;
        }
        throw new Exception("Слишком много редиректов");
    }
}

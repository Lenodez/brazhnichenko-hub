package com.brazhnichenko.hub;

import android.app.ActivityManager;
import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.StatFs;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;

public class ModelManager {
    public static final class FileSpec {
        public final String repoId;
        public final String remotePath;
        public final String localName;
        public final double sizeGB;

        public FileSpec(String repoId, String remotePath, String localName, double sizeGB) {
            this.repoId = repoId;
            this.remotePath = remotePath;
            this.localName = localName;
            this.sizeGB = sizeGB;
        }
    }

    public static final class CatalogModel {
        public final String id;
        public final String name;
        public final String tag;
        public final String summaryRu;
        public final double totalSizeGB;
        public final double minRamGB;
        public final double idealRamGB;
        public final boolean isVision;
        public final FileSpec[] files;

        public CatalogModel(String id, String name, String tag, String summaryRu,
                            double totalSizeGB, double minRamGB, double idealRamGB,
                            boolean isVision, FileSpec[] files) {
            this.id = id;
            this.name = name;
            this.tag = tag;
            this.summaryRu = summaryRu;
            this.totalSizeGB = totalSizeGB;
            this.minRamGB = minRamGB;
            this.idealRamGB = idealRamGB;
            this.isVision = isVision;
            this.files = files;
        }
    }

    public static final class DownloadJob {
        public final String id;
        public final String title;
        public final String retryModelId;
        public volatile String status = "running"; // running | done | error | cancelled
        public volatile String stage = "Подготовка...";
        public volatile double percent = 0;
        public volatile double speedMBs = 0;
        public volatile String error = null;
        public volatile HttpURLConnection activeConn = null;
        public volatile Thread workerThread = null;
        public final AtomicBoolean cancelled = new AtomicBoolean(false);

        public DownloadJob(String id, String title, String retryModelId) {
            this.id = id;
            this.title = title;
            this.retryModelId = retryModelId;
        }
    }

    private final Context ctx;
    private final SharedPreferences prefs;
    private final File modelsDir;
    private final ConcurrentHashMap<String, DownloadJob> jobs = new ConcurrentHashMap<>();
    private final List<CatalogModel> catalog = new ArrayList<>();

    public ModelManager(Context ctx) {
        this.ctx = ctx.getApplicationContext();
        this.prefs = this.ctx.getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
        File ext = this.ctx.getExternalFilesDir("models");
        this.modelsDir = (ext != null) ? ext : new File(this.ctx.getFilesDir(), "models");
        if (!this.modelsDir.exists()) this.modelsDir.mkdirs();
        initCatalog();
    }

    private void initCatalog() {
        catalog.add(new CatalogModel(
                "mob-qwen3-17b",
                "Qwen 3 1.7B Instruct (Сверхбыстрая)",
                "4–8+ ГБ RAM • 1.11 ГБ",
                "Мгновенный карманный ассистент на русском и английском: работает на любом Android-телефоне, почти не греет батарею.",
                1.11, 4, 6, false,
                new FileSpec[]{
                        new FileSpec("unsloth/Qwen3-1.7B-GGUF", "Qwen3-1.7B-Q4_K_M.gguf", "Qwen3-1.7B-Q4_K_M.gguf", 1.11)
                }
        ));
        catalog.add(new CatalogModel(
                "mob-qwen3-4b",
                "Qwen 3 4B Instruct (Оптимум для Fold 8)",
                "8–12+ ГБ RAM • 2.33 ГБ • Хит",
                "Золотой стандарт для флагманского Snapdragon: шикарный русский язык, быстрые ответы (~25–45 tok/s) и минимальный расход RAM.",
                2.33, 6, 8, false,
                new FileSpec[]{
                        new FileSpec("unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2.33)
                }
        ));
        catalog.add(new CatalogModel(
                "mob-gemma3-4b-vision",
                "Gemma 3 4B Vision (Google • Камера и фото)",
                "Vision • 8–12+ ГБ RAM • 3.30 ГБ",
                "Лёгкая мультимодальная модель от Google с файлом зрения mmproj в комплекте: анализирует фото с камеры, чеки, скриншоты и текст офлайн.",
                3.30, 8, 12, true,
                new FileSpec[]{
                        new FileSpec("unsloth/gemma-3-4b-it-GGUF", "gemma-3-4b-it-Q4_K_M.gguf", "gemma-3-4b-it-Q4_K_M.gguf", 2.49),
                        new FileSpec("unsloth/gemma-3-4b-it-GGUF", "mmproj-F16.gguf", "mmproj-gemma-3-4b-it-F16.gguf", 0.81)
                }
        ));
        catalog.add(new CatalogModel(
                "mob-qwen25-vl-7b-vision",
                "Qwen 2.5 VL 7B Vision (Топ OCR и фото)",
                "Vision • 12–16 ГБ RAM • 5.16 ГБ",
                "Сильнейшая Vision-модель для 12–16 ГБ RAM: идеально читает текст на фото, разбирает интерфейсы, документы и сцены полностью офлайн.",
                5.16, 10, 12, true,
                new FileSpec[]{
                        new FileSpec("ggml-org/Qwen2.5-VL-7B-Instruct-GGUF", "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf", "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf", 4.36),
                        new FileSpec("ggml-org/Qwen2.5-VL-7B-Instruct-GGUF", "mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf", "mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf", 0.80)
                }
        ));
        catalog.add(new CatalogModel(
                "mob-qwen3-8b",
                "Qwen 3 8B (Мощный текст и код)",
                "12–16 ГБ RAM • 4.68 ГБ",
                "Серьёзный локальный интеллект в кармане для Galaxy Z Fold 8: сложные вопросы, код, перевод и длинный контекст без интернета.",
                4.68, 10, 12, false,
                new FileSpec[]{
                        new FileSpec("unsloth/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", "Qwen3-8B-Q4_K_M.gguf", 4.68)
                }
        ));
        catalog.add(new CatalogModel(
                "mob-deepseek-r1-8b",
                "DeepSeek-R1-0528 8B (Логика и <think>)",
                "12–16 ГБ RAM • 4.92 ГБ",
                "Рассуждающая модель с пошаговым блоком <think> для математики, логики и программирования офлайн.",
                4.92, 10, 12, false,
                new FileSpec[]{
                        new FileSpec("unsloth/DeepSeek-R1-0528-Qwen3-8B-GGUF", "DeepSeek-R1-0528-Qwen3-8B-Q4_K_M.gguf", "DeepSeek-R1-0528-Qwen3-8B-Q4_K_M.gguf", 4.92)
                }
        ));
        catalog.add(new CatalogModel(
                "mob-gemma3-12b-vision",
                "Gemma 3 12B Vision (Google • 16 ГБ RAM)",
                "Vision • 16 ГБ RAM • 7.59 ГБ",
                "Флагманская мобильная Vision-модель для устройств с 16 ГБ ОЗУ (включая старший Fold 8): глубокое понимание фото и живой русский текст.",
                7.59, 14, 16, true,
                new FileSpec[]{
                        new FileSpec("unsloth/gemma-3-12b-it-GGUF", "gemma-3-12b-it-Q4_K_M.gguf", "gemma-3-12b-it-Q4_K_M.gguf", 6.80),
                        new FileSpec("unsloth/gemma-3-12b-it-GGUF", "mmproj-F16.gguf", "mmproj-gemma-3-12b-it-F16.gguf", 0.79)
                }
        ));
    }

    public File getModelsDir() { return modelsDir; }
    public SharedPreferences getPrefs() { return prefs; }
    public List<CatalogModel> getCatalog() { return catalog; }

    public String getHfToken() { return prefs.getString("hf_token", ""); }
    public void setHfToken(String token) { prefs.edit().putString("hf_token", token == null ? "" : token.trim()).apply(); }

    public String getActiveModelFile() {
        String saved = prefs.getString("active_model", "");
        List<File> installed = listInstalledLlmFiles();
        for (File f : installed) {
            if (f.getName().equalsIgnoreCase(saved)) return f.getName();
        }
        return installed.isEmpty() ? "" : installed.get(0).getName();
    }

    public void setActiveModelFile(String fileName) {
        if (fileName != null) prefs.edit().putString("active_model", fileName.trim()).apply();
    }

    public List<File> listInstalledLlmFiles() {
        List<File> out = new ArrayList<>();
        File[] files = modelsDir.listFiles();
        if (files == null) return out;
        for (File f : files) {
            if (!f.isFile()) continue;
            String n = f.getName().toLowerCase(Locale.ROOT);
            if (!n.endsWith(".gguf") || n.contains("mmproj")) continue;
            if (f.length() < 5L * 1024 * 1024) continue;
            out.add(f);
        }
        Collections.sort(out, Comparator.comparing(File::getName));
        return out;
    }

    public File findMatchingMmproj(String modelFileName) {
        if (modelFileName == null || modelFileName.trim().isEmpty()) return null;
        File[] files = modelsDir.listFiles();
        if (files == null) return null;
        List<File> mmprojs = new ArrayList<>();
        for (File f : files) {
            if (f.isFile() && f.getName().toLowerCase(Locale.ROOT).endsWith(".gguf")
                    && f.getName().toLowerCase(Locale.ROOT).contains("mmproj")
                    && f.length() > 1024 * 1024) {
                mmprojs.add(f);
            }
        }
        if (mmprojs.isEmpty()) return null;

        String stem = modelFileName.replace(".gguf", "").toLowerCase(Locale.ROOT);
        String[] tokens = stem.split("[-_.\\s]+");
        File best = null;
        int bestScore = 0;
        for (File mp : mmprojs) {
            String mpLower = mp.getName().toLowerCase(Locale.ROOT);
            int score = 0;
            for (String t : tokens) {
                if (t.length() >= 3 && !t.startsWith("q4") && !t.startsWith("q5") && !t.startsWith("q6") && mpLower.contains(t)) {
                    score++;
                }
            }
            if (score > bestScore) {
                bestScore = score;
                best = mp;
            }
        }
        return bestScore > 0 ? best : null;
    }

    public double[] getRamStatsGB() {
        ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
        ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
        if (am != null) {
            am.getMemoryInfo(mi);
            double total = mi.totalMem / (1024.0 * 1024.0 * 1024.0);
            double avail = mi.availMem / (1024.0 * 1024.0 * 1024.0);
            return new double[]{round1(Math.max(0, total - avail)), round1(total), round1(avail)};
        }
        return new double[]{0, 8, 4};
    }

    public double[] getStorageStatsGB() {
        try {
            StatFs sf = new StatFs(modelsDir.getAbsolutePath());
            double total = (sf.getBlockCountLong() * (double) sf.getBlockSizeLong()) / (1024.0 * 1024.0 * 1024.0);
            double free = (sf.getAvailableBlocksLong() * (double) sf.getBlockSizeLong()) / (1024.0 * 1024.0 * 1024.0);
            return new double[]{round1(free), round1(total)};
        } catch (Exception e) {
            return new double[]{0, 0};
        }
    }

    public String getDeviceSocLabel() {
        String soc = "";
        if (Build.VERSION.SDK_INT >= 31) {
            soc = Build.SOC_MODEL != null ? Build.SOC_MODEL : "";
        }
        if (soc.isEmpty() || "unknown".equalsIgnoreCase(soc)) {
            soc = Build.HARDWARE != null ? Build.HARDWARE : "ARM64";
        }
        return Build.MANUFACTURER + " " + Build.MODEL + " (" + soc + ")";
    }

    public JSONArray buildCatalogJson() throws Exception {
        double[] ram = getRamStatsGB();
        double totalRam = ram[1];
        JSONArray arr = new JSONArray();
        for (CatalogModel m : catalog) {
            boolean allInstalled = true;
            double missingGB = 0;
            for (FileSpec fs : m.files) {
                File f = new File(modelsDir, fs.localName);
                if (!f.exists() || f.length() < 5L * 1024 * 1024) {
                    allInstalled = false;
                    missingGB += fs.sizeGB;
                }
            }

            String level;
            String badge;
            String speed;
            if (totalRam >= m.idealRamGB - 0.5) {
                level = "ideal";
                badge = "Идеально";
                speed = m.totalSizeGB <= 1.5 ? "~40–65 tok/s"
                        : m.totalSizeGB <= 3.5 ? "~22–40 tok/s"
                        : m.totalSizeGB <= 5.5 ? "~12–22 tok/s" : "~8–14 tok/s";
            } else if (totalRam >= m.minRamGB - 0.5) {
                level = "tight";
                badge = "Впритык";
                speed = "~7–14 tok/s (закрой фоновые приложения)";
            } else {
                level = "unsupported";
                badge = "Мало RAM";
                speed = "Нужно " + (int) m.minRamGB + "+ ГБ RAM";
            }

            JSONObject obj = new JSONObject();
            obj.put("id", m.id);
            obj.put("name", m.name);
            obj.put("tag", m.tag);
            obj.put("summaryRu", m.summaryRu);
            obj.put("totalSizeGB", m.totalSizeGB);
            obj.put("minRamGB", m.minRamGB);
            obj.put("isVision", m.isVision);
            obj.put("installed", allInstalled);
            obj.put("missingGB", round2(missingGB));
            obj.put("fitLevel", level);
            obj.put("fitBadge", badge);
            obj.put("speedHint", speed);
            arr.put(obj);
        }
        return arr;
    }

    public String startCatalogInstall(String modelId) {
        CatalogModel target = null;
        for (CatalogModel m : catalog) {
            if (m.id.equalsIgnoreCase(modelId)) { target = m; break; }
        }
        if (target == null) throw new IllegalArgumentException("Модель не найдена: " + modelId);
        final CatalogModel spec = target;
        String jobId = UUID.randomUUID().toString().substring(0, 8);
        DownloadJob job = new DownloadJob(jobId, "Скачивание " + spec.name, spec.id);
        jobs.put(jobId, job);

        Thread worker = new Thread(() -> {
            try {
                for (int i = 0; i < spec.files.length; i++) {
                    if (job.cancelled.get()) { job.status = "cancelled"; job.stage = "Отменено"; return; }
                    FileSpec fs = spec.files[i];
                    File dest = new File(modelsDir, fs.localName);
                    if (dest.exists() && dest.length() > 5L * 1024 * 1024) continue;
                    String url = "https://huggingface.co/" + fs.repoId + "/resolve/main/" + fs.remotePath;
                    final int idx = i;
                    downloadWithResume(url, dest, job, idx, spec.files.length, fs.localName);
                }
                if (job.cancelled.get()) {
                    job.status = "cancelled";
                    job.stage = "Отменено";
                } else {
                    job.percent = 100;
                    job.status = "done";
                    job.stage = "Готово! Можно запускать в Чате";
                    if (getActiveModelFile().isEmpty() && spec.files.length > 0) {
                        setActiveModelFile(spec.files[0].localName);
                    }
                }
            } catch (Exception ex) {
                if (job.cancelled.get()) {
                    job.status = "cancelled";
                    job.stage = "Отменено";
                } else {
                    job.status = "error";
                    job.error = ex.getMessage();
                    job.stage = "Обрыв сети (прогресс сохранён — можно докачать): " + ex.getMessage();
                }
            } finally {
                job.activeConn = null;
            }
        });
        job.workerThread = worker;
        worker.start();
        return jobId;
    }

    public String startSingleHfDownload(String repoId, String remotePath) {
        String fileName = new File(remotePath).getName();
        String jobId = UUID.randomUUID().toString().substring(0, 8);
        DownloadJob job = new DownloadJob(jobId, "HF: " + fileName, null);
        jobs.put(jobId, job);

        Thread worker = new Thread(() -> {
            try {
                String url = "https://huggingface.co/" + repoId + "/resolve/main/" + remotePath;
                File dest = new File(modelsDir, fileName);
                downloadWithResume(url, dest, job, 0, 1, fileName);
                if (!job.cancelled.get()) {
                    job.percent = 100;
                    job.status = "done";
                    job.stage = "Скачано: " + fileName;
                }
            } catch (Exception ex) {
                job.status = job.cancelled.get() ? "cancelled" : "error";
                job.error = ex.getMessage();
                job.stage = job.cancelled.get() ? "Отменено" : ("Обрыв сети: " + ex.getMessage());
            } finally {
                job.activeConn = null;
            }
        });
        job.workerThread = worker;
        worker.start();
        return jobId;
    }

    public void deleteCatalogModel(String modelId) {
        for (CatalogModel m : catalog) {
            if (m.id.equalsIgnoreCase(modelId)) {
                for (FileSpec fs : m.files) {
                    new File(modelsDir, fs.localName).delete();
                    new File(modelsDir, fs.localName + ".part").delete();
                }
                return;
            }
        }
    }

    public void deleteModelFile(String fileName) {
        if (fileName == null || fileName.contains("/") || fileName.contains("..")) return;
        new File(modelsDir, fileName).delete();
        new File(modelsDir, fileName + ".part").delete();
    }

    public void cancelJob(String jobId) {
        DownloadJob j = jobs.remove(jobId);
        if (j != null) {
            j.cancelled.set(true);
            j.status = "cancelled";
            j.stage = "Отменено";
            final HttpURLConnection conn = j.activeConn;
            if (conn != null) {
                new Thread(() -> {
                    try { conn.disconnect(); } catch (Exception ignored) { }
                }).start();
            }
            final Thread wt = j.workerThread;
            if (wt != null) {
                try { wt.interrupt(); } catch (Exception ignored) { }
            }
        }
    }

    public JSONArray getJobsJson() throws Exception {
        JSONArray arr = new JSONArray();
        for (Map.Entry<String, DownloadJob> e : jobs.entrySet()) {
            DownloadJob j = e.getValue();
            JSONObject o = new JSONObject();
            o.put("id", j.id);
            o.put("title", j.title);
            o.put("status", j.status);
            o.put("stage", j.stage);
            o.put("percent", round1(j.percent));
            o.put("speedMBs", round1(j.speedMBs));
            if (j.retryModelId != null) o.put("retryModelId", j.retryModelId);
            if (j.error != null) o.put("error", j.error);
            arr.put(o);
        }
        return arr;
    }

    public JSONArray searchHuggingFace(String query) throws Exception {
        String q = query.trim();
        if (!q.toLowerCase(Locale.ROOT).contains("gguf")) q = q + " gguf";
        String url = "https://huggingface.co/api/models?search=" + URLEncoder.encode(q, "UTF-8") + "&sort=downloads&direction=-1&limit=12";
        String body = httpGetString(url);
        JSONArray raw = new JSONArray(body);
        JSONArray out = new JSONArray();
        for (int i = 0; i < raw.length(); i++) {
            JSONObject r = raw.getJSONObject(i);
            JSONObject o = new JSONObject();
            o.put("repoId", r.optString("id"));
            o.put("downloads", r.optLong("downloads", 0));
            o.put("likes", r.optLong("likes", 0));
            o.put("gated", r.optBoolean("gated", false));
            out.put(o);
        }
        return out;
    }

    public JSONArray listHfRepoFiles(String repoId) throws Exception {
        String url = "https://huggingface.co/api/models/" + repoId + "/tree/main?recursive=true";
        String body = httpGetString(url);
        JSONArray raw = new JSONArray(body);
        JSONArray out = new JSONArray();
        for (int i = 0; i < raw.length(); i++) {
            JSONObject el = raw.getJSONObject(i);
            if (!"file".equals(el.optString("type"))) continue;
            String path = el.optString("path");
            if (!path.toLowerCase(Locale.ROOT).endsWith(".gguf")) continue;
            JSONObject o = new JSONObject();
            o.put("path", path);
            o.put("sizeBytes", el.optLong("size", 0));
            out.put(o);
        }
        return out;
    }

    private String httpGetString(String urlStr) throws Exception {
        HttpURLConnection conn = (HttpURLConnection) new URL(urlStr).openConnection();
        conn.setConnectTimeout(12000);
        conn.setReadTimeout(15000);
        conn.setRequestProperty("User-Agent", "BrazhnichenkoHub-Android/4.3");
        String tok = getHfToken();
        if (!tok.isEmpty()) conn.setRequestProperty("Authorization", "Bearer " + tok);
        int code = conn.getResponseCode();
        InputStream is = (code >= 200 && code < 300) ? conn.getInputStream() : conn.getErrorStream();
        StringBuilder sb = new StringBuilder();
        try (BufferedReader br = new BufferedReader(new InputStreamReader(is, StandardCharsets.UTF_8))) {
            String line;
            while ((line = br.readLine()) != null) sb.append(line);
        } finally {
            conn.disconnect();
        }
        if (code < 200 || code >= 300) throw new RuntimeException("HTTP " + code);
        return sb.toString();
    }

    private void downloadWithResume(String urlStr, File dest, DownloadJob job, int fileIdx, int totalFiles, String label) throws Exception {
        File part = new File(dest.getAbsolutePath() + ".part");
        long existing = part.exists() ? part.length() : 0;

        HttpURLConnection conn = (HttpURLConnection) new URL(urlStr).openConnection();
        job.activeConn = conn;
        conn.setInstanceFollowRedirects(true);
        conn.setConnectTimeout(10000);
        conn.setReadTimeout(12000);
        conn.setRequestProperty("User-Agent", "BrazhnichenkoHub-Android/4.3");
        String tok = getHfToken();
        if (!tok.isEmpty()) conn.setRequestProperty("Authorization", "Bearer " + tok);
        if (existing > 0) conn.setRequestProperty("Range", "bytes=" + existing + "-");

        int code = conn.getResponseCode();
        if (code == 416) {
            part.delete();
            conn.disconnect();
            downloadWithResume(urlStr, dest, job, fileIdx, totalFiles, label);
            return;
        }
        if (code != 200 && code != 206) {
            conn.disconnect();
            throw new RuntimeException("HTTP " + code + " при скачивании " + label);
        }

        boolean resume = (code == 206);
        if (!resume) existing = 0;
        long contentLen = conn.getContentLengthLong();
        long total = contentLen > 0 ? existing + contentLen : -1;

        long downloaded = existing;
        long sessionBytes = 0;
        long startNs = System.nanoTime();
        long lastUi = 0;

        try (InputStream in = new BufferedInputStream(conn.getInputStream(), 65536);
             FileOutputStream out = new FileOutputStream(part, resume)) {
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) {
                if (job.cancelled.get()) return;
                out.write(buf, 0, n);
                downloaded += n;
                sessionBytes += n;
                long now = System.currentTimeMillis();
                if (now - lastUi >= 250) {
                    lastUi = now;
                    double sec = Math.max(0.1, (System.nanoTime() - startNs) / 1e9);
                    job.speedMBs = (sessionBytes / 1048576.0) / sec;
                    double filePct = total > 0 ? Math.min(100.0, downloaded * 100.0 / total) : 50.0;
                    job.percent = ((fileIdx * 100.0) + filePct) / totalFiles;
                    job.stage = String.format(Locale.US, "[%d/%d] %s — %.1f / %.1f МБ",
                            fileIdx + 1, totalFiles, label, downloaded / 1048576.0, total > 0 ? total / 1048576.0 : 0);
                }
            }
        } finally {
            conn.disconnect();
        }

        if (job.cancelled.get()) return;
        if (dest.exists()) dest.delete();
        if (!part.renameTo(dest)) {
            throw new RuntimeException("Не удалось сохранить файл " + dest.getName());
        }
    }

    private static double round1(double v) { return Math.round(v * 10.0) / 10.0; }
    private static double round2(double v) { return Math.round(v * 100.0) / 100.0; }
}

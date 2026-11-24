package com.brazhnichenko.hub;

import android.app.ActivityManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.BatteryManager;
import android.os.Build;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URL;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class LocalHubServer {
    public static final int PORT = 17901;

    private final Context ctx;
    private final ModelManager models;
    private final LlamaProcessManager llama;
    private final File sessionsFile;
    private final ExecutorService pool = Executors.newCachedThreadPool();
    private volatile boolean running = false;
    private ServerSocket serverSocket;

    public LocalHubServer(Context ctx, ModelManager models, LlamaProcessManager llama) {
        this.ctx = ctx.getApplicationContext();
        this.models = models;
        this.llama = llama;
        this.sessionsFile = new File(this.ctx.getFilesDir(), "chat_sessions.json");
    }

    public void start() {
        if (running) return;
        running = true;
        Thread acceptThread = new Thread(() -> {
            try {
                serverSocket = new ServerSocket(PORT, 32, InetAddress.getByName("127.0.0.1"));
                while (running) {
                    Socket sock = serverSocket.accept();
                    pool.submit(() -> handleClient(sock));
                }
            } catch (Exception ignored) { }
        });
        acceptThread.setDaemon(true);
        acceptThread.start();
    }

    public void stop() {
        running = false;
        try { if (serverSocket != null) serverSocket.close(); } catch (Exception ignored) { }
        pool.shutdownNow();
    }

    private void handleClient(Socket sock) {
        try (Socket s = sock) {
            s.setSoTimeout(30000);
            InputStream rawIn = new BufferedInputStream(s.getInputStream());
            OutputStream rawOut = new BufferedOutputStream(s.getOutputStream());

            String reqLine = readLine(rawIn);
            if (reqLine == null || reqLine.isEmpty()) return;
            String[] parts = reqLine.split(" ");
            if (parts.length < 2) return;
            String method = parts[0];
            String fullTarget = parts[1];

            String path = fullTarget;
            String queryStr = "";
            int qIdx = fullTarget.indexOf('?');
            if (qIdx >= 0) {
                path = fullTarget.substring(0, qIdx);
                queryStr = fullTarget.substring(qIdx + 1);
            }
            Map<String, String> query = parseQuery(queryStr);

            int contentLength = 0;
            String headerLine;
            while ((headerLine = readLine(rawIn)) != null && !headerLine.isEmpty()) {
                int colon = headerLine.indexOf(':');
                if (colon > 0) {
                    String k = headerLine.substring(0, colon).trim().toLowerCase(Locale.ROOT);
                    String v = headerLine.substring(colon + 1).trim();
                    if ("content-length".equals(k)) {
                        try { contentLength = Integer.parseInt(v); } catch (Exception ignored) { }
                    }
                }
            }

            byte[] bodyBytes = new byte[0];
            if (contentLength > 0 && contentLength < 25 * 1024 * 1024) {
                bodyBytes = readExact(rawIn, contentLength);
            }
            String bodyStr = new String(bodyBytes, StandardCharsets.UTF_8);

            // Route API
            if ("/api/health".equals(path)) {
                JSONObject o = new JSONObject();
                o.put("ok", true);
                o.put("app", "Brazhnichenko Hub Android");
                o.put("version", "4.8.0");
                sendJson(rawOut, 200, o.toString());
                return;
            }
            if ("/api/stt/status".equals(path)) {
                sendJson(rawOut, 200, OfflineVoiceEngine.getStatusJson(ctx).toString());
                return;
            }
            if ("/api/stt/download".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                OfflineVoiceEngine.startDownload(ctx, req.optString("modelId", OfflineVoiceEngine.MODEL_GIGAAM));
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/stt/cancel".equals(path) && "POST".equals(method)) {
                OfflineVoiceEngine.cancelDownload();
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/stt/delete".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                OfflineVoiceEngine.deleteModel(ctx, req.optString("modelId", ""));
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/stt/select".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                SharedPreferences.Editor ed = models.getPrefs().edit();
                if (req.has("sttMode")) ed.putString("stt_mode", req.getString("sttMode"));
                if (req.has("sttModel")) ed.putString("stt_model", req.getString("sttModel"));
                ed.apply();
                sendJson(rawOut, 200, OfflineVoiceEngine.getStatusJson(ctx).toString());
                return;
            }
            if ("/api/update/check".equals(path)) {
                sendJson(rawOut, 200, AppUpdater.checkLatestRelease(ctx).toString());
                return;
            }
            if ("/api/update/status".equals(path)) {
                sendJson(rawOut, 200, AppUpdater.getDownloadStatus(ctx).toString());
                return;
            }
            if ("/api/update/download".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                String apkUrl = req.optString("apkUrl", "").trim();
                String ver = req.optString("version", "").trim();
                if (apkUrl.isEmpty()) {
                    sendJson(rawOut, 400, "{\"error\":\"Не указан URL APK\"}");
                    return;
                }
                AppUpdater.startDownloadAndInstall(ctx, apkUrl, ver);
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/update/install".equals(path) && "POST".equals(method)) {
                sendJson(rawOut, 200, AppUpdater.launchInstaller(ctx).toString());
                return;
            }
            if ("/api/system/free-ram".equals(path) && "POST".equals(method)) {
                sendJson(rawOut, 200, handleFreeRam().toString());
                return;
            }
            if ("/api/search/test".equals(path)) {
                String q = query.getOrDefault("q", "").trim();
                WebSearchEngine.SearchResponse sr = WebSearchEngine.search(q, models.getPrefs());
                JSONObject o = new JSONObject();
                o.put("provider", sr.provider);
                o.put("directAnswer", sr.directAnswer);
                o.put("count", sr.results.size());
                o.put("results", sr.resultsToJson());
                sendJson(rawOut, 200, o.toString());
                return;
            }
            if ("/api/status".equals(path)) {
                sendJson(rawOut, 200, buildStatusJson().toString());
                return;
            }
            if ("/api/catalog".equals(path)) {
                JSONObject o = new JSONObject();
                o.put("models", models.buildCatalogJson());
                sendJson(rawOut, 200, o.toString());
                return;
            }
            if ("/api/catalog/install".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                String jobId = models.startCatalogInstall(req.getString("modelId"));
                sendJson(rawOut, 200, new JSONObject().put("jobId", jobId).toString());
                return;
            }
            if ("/api/catalog/delete".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                if (req.has("modelId")) models.deleteCatalogModel(req.getString("modelId"));
                if (req.has("fileName")) models.deleteModelFile(req.getString("fileName"));
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/jobs/cancel".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                models.cancelJob(req.getString("jobId"));
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/hf/search".equals(path)) {
                String q = query.getOrDefault("q", "");
                sendJson(rawOut, 200, q.isEmpty() ? "[]" : models.searchHuggingFace(q).toString());
                return;
            }
            if ("/api/hf/files".equals(path)) {
                String repo = query.getOrDefault("repo", "");
                sendJson(rawOut, 200, repo.isEmpty() ? "[]" : models.listHfRepoFiles(repo).toString());
                return;
            }
            if ("/api/hf/download".equals(path) && "POST".equals(method)) {
                JSONObject req = new JSONObject(bodyStr);
                String jobId = models.startSingleHfDownload(req.getString("repoId"), req.getString("filePath"));
                sendJson(rawOut, 200, new JSONObject().put("jobId", jobId).toString());
                return;
            }
            if ("/api/llm/start".equals(path) && "POST".equals(method)) {
                try {
                    JSONObject req = bodyStr.isEmpty() ? new JSONObject() : new JSONObject(bodyStr);
                    llama.start(req.optString("modelFile", ""));
                    sendJson(rawOut, 200, "{\"ok\":true}");
                } catch (Exception ex) {
                    sendJson(rawOut, 400, new JSONObject().put("error", ex.getMessage()).toString());
                }
                return;
            }
            if ("/api/llm/stop".equals(path) && "POST".equals(method)) {
                llama.stop();
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/llm/logs".equals(path)) {
                sendJson(rawOut, 200, new JSONObject().put("logs", llama.getLogsTail()).toString());
                return;
            }
            if ("/api/settings".equals(path)) {
                SharedPreferences p = models.getPrefs();
                if ("POST".equals(method)) {
                    JSONObject req = new JSONObject(bodyStr);
                    SharedPreferences.Editor ed = p.edit();
                    if (req.has("backendMode")) ed.putString("backend_mode", req.getString("backendMode"));
                    if (req.has("ctxSize")) ed.putInt("ctx_size", Math.max(1024, Math.min(32768, req.getInt("ctxSize"))));
                    if (req.has("threads")) ed.putInt("threads", Math.max(1, Math.min(12, req.getInt("threads"))));
                    if (req.has("temp")) ed.putString("temp", String.valueOf(req.getDouble("temp")));
                    if (req.has("presencePenalty")) ed.putString("presence_penalty", String.valueOf(req.getDouble("presencePenalty")));
                    if (req.has("idleUnloadMin")) ed.putInt("idle_unload_min", Math.max(0, Math.min(120, req.getInt("idleUnloadMin"))));
                    if (req.has("hfToken")) ed.putString("hf_token", req.getString("hfToken").trim());
                    if (req.has("assistAutoVoice")) ed.putBoolean("assist_auto_voice", req.getBoolean("assistAutoVoice"));
                    if (req.has("assistAutoTts")) ed.putBoolean("assist_auto_tts", req.getBoolean("assistAutoTts"));
                    if (req.has("assistAttachScreen")) ed.putBoolean("assist_attach_screen", req.getBoolean("assistAttachScreen"));
                    if (req.has("assistWebSearch")) ed.putBoolean("assist_web_search", req.getBoolean("assistWebSearch"));
                    if (req.has("searchProvider")) ed.putString("search_provider", req.getString("searchProvider"));
                    if (req.has("tavilyKey")) ed.putString("tavily_api_key", req.getString("tavilyKey").trim());
                    if (req.has("braveKey")) ed.putString("brave_api_key", req.getString("braveKey").trim());
                    ed.apply();
                }
                int defThreads = Math.max(2, Math.min(6, Runtime.getRuntime().availableProcessors() - 2));
                JSONObject o = new JSONObject();
                o.put("backendMode", p.getString("backend_mode", "auto"));
                o.put("ctxSize", p.getInt("ctx_size", 8192));
                o.put("threads", p.getInt("threads", defThreads));
                o.put("maxThreads", Runtime.getRuntime().availableProcessors());
                o.put("temp", Double.parseDouble(p.getString("temp", "0.7")));
                o.put("presencePenalty", Double.parseDouble(p.getString("presence_penalty", "1.2")));
                o.put("idleUnloadMin", p.getInt("idle_unload_min", 10));
                o.put("hfConnected", !models.getHfToken().isEmpty());
                o.put("assistAutoVoice", p.getBoolean("assist_auto_voice", true));
                o.put("assistAutoTts", p.getBoolean("assist_auto_tts", true));
                o.put("assistAttachScreen", p.getBoolean("assist_attach_screen", false));
                o.put("assistWebSearch", p.getBoolean("assist_web_search", true));
                o.put("searchProvider", p.getString("search_provider", "auto"));
                o.put("tavilyConnected", !p.getString("tavily_api_key", "").trim().isEmpty());
                o.put("braveConnected", !p.getString("brave_api_key", "").trim().isEmpty());
                sendJson(rawOut, 200, o.toString());
                return;
            }
            if ("/api/sessions".equals(path)) {
                if ("GET".equals(method)) {
                    JSONArray list = loadSessions();
                    for (int i = 0; i < list.length(); i++) {
                        enrichSessionWithContextStats(list.getJSONObject(i));
                    }
                    sendJson(rawOut, 200, list.toString());
                    return;
                }
                if ("POST".equals(method)) {
                    JSONArray list = loadSessions();
                    JSONObject sObj = new JSONObject();
                    sObj.put("id", UUID.randomUUID().toString().substring(0, 8));
                    sObj.put("title", "Новый чат");
                    sObj.put("personaId", "default");
                    sObj.put("temperature", 0.7);
                    sObj.put("customSystemPrompt", "");
                    sObj.put("memorySummary", "");
                    sObj.put("compactedCount", 0);
                    sObj.put("messages", new JSONArray());
                    JSONArray next = new JSONArray();
                    next.put(sObj);
                    for (int i = 0; i < Math.min(list.length(), 40); i++) next.put(list.get(i));
                    saveSessions(next);
                    enrichSessionWithContextStats(sObj);
                    sendJson(rawOut, 200, sObj.toString());
                    return;
                }
            }
            if (path.startsWith("/api/sessions/") && path.endsWith("/compact") && "POST".equals(method)) {
                s.setSoTimeout(0);
                String id = path.substring("/api/sessions/".length(), path.length() - "/compact".length());
                handleCompactSession(rawOut, id);
                return;
            }
            if (path.startsWith("/api/sessions/") && path.endsWith("/clear") && "POST".equals(method)) {
                String id = path.substring("/api/sessions/".length(), path.length() - "/clear".length());
                JSONArray list = loadSessions();
                JSONObject updated = null;
                for (int i = 0; i < list.length(); i++) {
                    JSONObject item = list.getJSONObject(i);
                    if (id.equals(item.optString("id"))) {
                        item.put("messages", new JSONArray());
                        item.put("memorySummary", "");
                        item.put("compactedCount", 0);
                        item.remove("lastTotalTokens");
                        item.remove("lastTotalMsgCount");
                        updated = item;
                        break;
                    }
                }
                if (updated == null) { sendJson(rawOut, 404, "{\"error\":\"Session not found\"}"); return; }
                saveSessions(list);
                enrichSessionWithContextStats(updated);
                sendJson(rawOut, 200, updated.toString());
                return;
            }
            if (path.startsWith("/api/sessions/") && path.endsWith("/pop-last") && "POST".equals(method)) {
                String id = path.substring("/api/sessions/".length(), path.length() - "/pop-last".length());
                JSONArray list = loadSessions();
                JSONObject target = null;
                for (int i = 0; i < list.length(); i++) {
                    JSONObject item = list.getJSONObject(i);
                    if (id.equals(item.optString("id"))) { target = item; break; }
                }
                if (target == null) { sendJson(rawOut, 404, "{\"error\":\"Session not found\"}"); return; }
                JSONArray hist = target.optJSONArray("messages");
                if (hist == null || hist.length() == 0) {
                    sendJson(rawOut, 400, "{\"error\":\"Нет сообщений для повтора\"}");
                    return;
                }
                // Remove trailing assistant message(s) and pop the last user prompt to re-run
                String lastUserPrompt = "";
                int cut = hist.length();
                while (cut > 0 && "assistant".equals(hist.getJSONObject(cut - 1).optString("role"))) {
                    cut--;
                }
                if (cut > 0 && "user".equals(hist.getJSONObject(cut - 1).optString("role"))) {
                    lastUserPrompt = hist.getJSONObject(cut - 1).optString("content", "");
                    cut--;
                }
                JSONArray trimmed = new JSONArray();
                for (int i = 0; i < cut; i++) trimmed.put(hist.get(i));
                target.put("messages", trimmed);
                target.remove("lastTotalTokens");
                target.remove("lastTotalMsgCount");
                saveSessions(list);
                enrichSessionWithContextStats(target);
                JSONObject resp = new JSONObject();
                resp.put("ok", true);
                resp.put("prompt", lastUserPrompt);
                resp.put("session", target);
                sendJson(rawOut, 200, resp.toString());
                return;
            }
            if (path.startsWith("/api/sessions/") && "PATCH".equals(method)) {
                String id = path.substring("/api/sessions/".length());
                JSONObject req = bodyStr.isEmpty() ? new JSONObject() : new JSONObject(bodyStr);
                JSONArray list = loadSessions();
                JSONObject updated = null;
                for (int i = 0; i < list.length(); i++) {
                    JSONObject item = list.getJSONObject(i);
                    if (id.equals(item.optString("id"))) {
                        if (req.has("title") && !req.optString("title", "").trim().isEmpty()) {
                            item.put("title", req.getString("title").trim());
                        }
                        if (req.has("personaId")) item.put("personaId", req.getString("personaId"));
                        if (req.has("temperature")) item.put("temperature", Math.max(0.0, Math.min(1.5, req.getDouble("temperature"))));
                        if (req.has("customSystemPrompt")) {
                            item.put("customSystemPrompt", req.optString("customSystemPrompt", "").trim());
                            item.remove("lastTotalTokens");
                        }
                        if (req.has("memorySummary")) {
                            item.put("memorySummary", req.optString("memorySummary", "").trim());
                            item.remove("lastTotalTokens");
                        }
                        updated = item;
                        break;
                    }
                }
                if (updated == null) {
                    sendJson(rawOut, 404, "{\"error\":\"Session not found\"}");
                    return;
                }
                saveSessions(list);
                enrichSessionWithContextStats(updated);
                sendJson(rawOut, 200, updated.toString());
                return;
            }
            if (path.startsWith("/api/sessions/") && "DELETE".equals(method)) {
                String id = path.substring("/api/sessions/".length());
                JSONArray list = loadSessions();
                JSONArray next = new JSONArray();
                for (int i = 0; i < list.length(); i++) {
                    JSONObject item = list.getJSONObject(i);
                    if (!id.equals(item.optString("id"))) next.put(item);
                }
                saveSessions(next);
                sendJson(rawOut, 200, "{\"ok\":true}");
                return;
            }
            if ("/api/chat/stream".equals(path) && "POST".equals(method)) {
                s.setSoTimeout(0);
                handleChatStream(rawOut, bodyStr);
                return;
            }

            // Static assets from APK assets/wwwroot/
            serveAsset(rawOut, "/".equals(path) ? "/index.html" : path);
        } catch (Exception ignored) { }
    }

    private void handleChatStream(OutputStream out, String bodyStr) throws Exception {
        String headers = "HTTP/1.1 200 OK\r\n"
                + "Content-Type: text/event-stream; charset=utf-8\r\n"
                + "Cache-Control: no-cache\r\n"
                + "Connection: close\r\n\r\n";
        out.write(headers.getBytes(StandardCharsets.UTF_8));
        out.flush();

        llama.setStreamingActive(true);
        try {
            JSONObject req = new JSONObject(bodyStr);
            String sessionId = req.optString("sessionId", "");
            String userText = req.optString("message", "").trim();
            String personaId = req.optString("personaId", "default");
            double temp = req.optDouble("temperature", 0.7);
            String modelFile = req.optString("modelFile", "");
            String imageBase64 = req.optString("imageBase64", "");
            String docName = req.optString("docName", "").trim();
            String docText = req.optString("docText", "").trim();
            boolean webSearch = req.optBoolean("webSearch", false);

            if (!modelFile.isEmpty()) models.setActiveModelFile(modelFile);
            String activeFile = models.getActiveModelFile();
            if (activeFile.isEmpty()) {
                sendSse(out, "error", "Нет установленных моделей. Открой вкладку «Модели» и скачай Qwen 3 4B или Gemma 3 4B Vision.");
                return;
            }

            boolean hasImage = !imageBase64.isEmpty();
            File mmproj = models.findMatchingMmproj(activeFile);
            if (hasImage && mmproj == null) {
                sendSse(out, "vision_missing", "Текущая модель «" + activeFile + "» работает только с текстом. Для фото с камеры скачай Gemma 3 4B Vision (3.3 ГБ) или Qwen 2.5 VL 7B Vision (5.16 ГБ) во вкладке «Модели».");
                return;
            }

            WebSearchEngine.SearchResponse searchResp = null;
            String webContextBlock = "";
            if (webSearch && !userText.isEmpty()) {
                sendSse(out, "status", "Поиск в интернете...");
                searchResp = WebSearchEngine.search(userText, models.getPrefs());
                if (searchResp != null && !searchResp.results.isEmpty()) {
                    webContextBlock = WebSearchEngine.buildPromptContext(searchResp);
                    JSONObject srcEvt = new JSONObject();
                    srcEvt.put("provider", searchResp.provider);
                    srcEvt.put("items", searchResp.resultsToJson());
                    sendSse(out, "sources", srcEvt);
                } else {
                    sendSse(out, "status", "Сеть недоступна — отвечаю офлайн...");
                }
            }

            if (!llama.isRunning() || !activeFile.equalsIgnoreCase(llama.getRunningModelFile())) {
                sendSse(out, "status", "Запуск локальной модели " + activeFile + "...");
                llama.start(activeFile);
            }

            JSONArray sessions = loadSessions();
            JSONObject session = null;
            for (int i = 0; i < sessions.length(); i++) {
                JSONObject item = sessions.getJSONObject(i);
                if (sessionId.equals(item.optString("id"))) { session = item; break; }
            }
            if (session == null) {
                session = new JSONObject();
                session.put("id", UUID.randomUUID().toString().substring(0, 8));
                session.put("title", "Новый чат");
                session.put("personaId", personaId);
                session.put("temperature", temp);
                session.put("messages", new JSONArray());
                sessions.put(session);
            }
            session.put("personaId", personaId);
            session.put("temperature", temp);

            JSONArray hist = session.optJSONArray("messages");
            if (hist == null) { hist = new JSONArray(); session.put("messages", hist); }
            if (hist.length() == 0 && "Новый чат".equals(session.optString("title", "Новый чат"))) {
                String titleSeed = !userText.isEmpty() ? userText : (!docName.isEmpty() ? docName : "Фото Vision");
                session.put("title", titleSeed.length() > 36 ? titleSeed.substring(0, 36) + "…" : titleSeed);
            }

            String effectivePrompt = userText;
            if (!docText.isEmpty()) {
                String header = "[Прикреплённый документ: " + (docName.isEmpty() ? "document" : docName) + "]\n" + docText + "\n\n";
                effectivePrompt = header + (userText.isEmpty() ? "Сделай краткую структурированную выжимку этого документа." : userText);
            }

            int ctxSize = models.getPrefs().getInt("ctx_size", 8192);
            String fullSysPrompt = buildFullSystemPrompt(session, personaId);
            if (!webContextBlock.isEmpty()) {
                fullSysPrompt = fullSysPrompt + "\n\n" + webContextBlock;
            }
            JSONArray apiMsgs = new JSONArray();
            apiMsgs.put(new JSONObject().put("role", "system").put("content", fullSysPrompt));

            // Pack recent history dynamically up to ~75% of ctxSize instead of a hard 12-message limit
            int promptBudget = Math.max(512, (int) (ctxSize * 0.75) - estimateTokens(fullSysPrompt) - estimateTokens(effectivePrompt) - (hasImage ? 512 : 0));
            int startIdx = hist.length();
            int usedHistTok = 0;
            for (int i = hist.length() - 1; i >= 0; i--) {
                JSONObject m = hist.getJSONObject(i);
                int mt = estimateTokens(m.optString("content", "")) + 10;
                if (usedHistTok + mt > promptBudget && startIdx < hist.length()) break;
                usedHistTok += mt;
                startIdx = i;
            }
            for (int i = startIdx; i < hist.length(); i++) {
                JSONObject m = hist.getJSONObject(i);
                apiMsgs.put(new JSONObject().put("role", m.optString("role")).put("content", m.optString("content")));
            }

            if (hasImage) {
                String dataUrl = imageBase64.startsWith("data:") ? imageBase64 : ("data:image/jpeg;base64," + imageBase64);
                JSONArray contentArr = new JSONArray();
                contentArr.put(new JSONObject().put("type", "text").put("text", effectivePrompt.isEmpty() ? "Опиши подробно, что на этом фото." : effectivePrompt));
                contentArr.put(new JSONObject().put("type", "image_url").put("image_url", new JSONObject().put("url", dataUrl)));
                apiMsgs.put(new JSONObject().put("role", "user").put("content", contentArr));
            } else {
                apiMsgs.put(new JSONObject().put("role", "user").put("content", effectivePrompt));
            }

            JSONObject payload = new JSONObject();
            payload.put("messages", apiMsgs);
            payload.put("temperature", temp);
            payload.put("stream", true);
            payload.put("stream_options", new JSONObject().put("include_usage", true));

            HttpURLConnection conn = (HttpURLConnection) new URL("http://127.0.0.1:" + LlamaProcessManager.LLAMA_PORT + "/v1/chat/completions").openConnection();
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setConnectTimeout(15000);
            conn.setReadTimeout(0);
            conn.setRequestProperty("Content-Type", "application/json");
            byte[] pBytes = payload.toString().getBytes(StandardCharsets.UTF_8);
            try (OutputStream cos = conn.getOutputStream()) { cos.write(pBytes); }

            int code = conn.getResponseCode();
            if (code != 200) {
                String err = readAll(conn.getErrorStream());
                conn.disconnect();
                sendSse(out, "error", "LLM HTTP " + code + ": " + err);
                return;
            }

            StringBuilder fullContent = new StringBuilder();
            StringBuilder fullThink = new StringBuilder();
            int tokens = 0;
            int promptTokens = 0;
            int completionTokens = 0;
            int totalTokens = 0;
            double serverTokPerSec = 0;
            long t0 = System.nanoTime();

            try (BufferedReader br = new BufferedReader(new InputStreamReader(conn.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = br.readLine()) != null) {
                    if (!line.startsWith("data:")) continue;
                    String data = line.substring(5).trim();
                    if ("[DONE]".equals(data)) break;
                    try {
                        JSONObject chunk = new JSONObject(data);
                        if (chunk.has("usage") && !chunk.isNull("usage")) {
                            JSONObject u = chunk.getJSONObject("usage");
                            promptTokens = u.optInt("prompt_tokens", promptTokens);
                            completionTokens = u.optInt("completion_tokens", completionTokens);
                            totalTokens = u.optInt("total_tokens", totalTokens);
                        }
                        if (chunk.has("timings") && !chunk.isNull("timings")) {
                            JSONObject tm = chunk.getJSONObject("timings");
                            double pps = tm.optDouble("predicted_per_second", 0);
                            if (pps > 0) serverTokPerSec = Math.round(pps * 10.0) / 10.0;
                            if (promptTokens == 0) promptTokens = tm.optInt("prompt_n", 0);
                            if (completionTokens == 0) completionTokens = tm.optInt("predicted_n", 0);
                        }
                        JSONArray choices = chunk.optJSONArray("choices");
                        if (choices != null && choices.length() > 0) {
                            JSONObject delta = choices.getJSONObject(0).optJSONObject("delta");
                            if (delta != null) {
                                String rc = delta.isNull("reasoning_content") ? "" : delta.optString("reasoning_content", "");
                                if (!rc.isEmpty()) {
                                    fullThink.append(rc);
                                    tokens++;
                                    sendSse(out, "think", rc);
                                }
                                String ct = delta.isNull("content") ? "" : delta.optString("content", "");
                                if (!ct.isEmpty()) {
                                    fullContent.append(ct);
                                    tokens++;
                                    sendSse(out, "token", ct);
                                }
                            }
                        }
                    } catch (Exception ignored) { }
                }
            } finally {
                conn.disconnect();
            }

            if (completionTokens > 0) tokens = completionTokens;
            if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0)) totalTokens = promptTokens + completionTokens;
            double sec = Math.max(0.15, (System.nanoTime() - t0) / 1e9);
            double tokPerSec = serverTokPerSec > 0 ? serverTokPerSec : (Math.round((tokens / sec) * 10.0) / 10.0);

            String storedUser = !docName.isEmpty()
                    ? ("[" + docName + "]" + (userText.isEmpty() ? "" : "\n" + userText))
                    : (userText.isEmpty() ? "[Фото]" : userText);
            hist.put(new JSONObject().put("role", "user").put("content", storedUser));
            JSONObject asstMsg = new JSONObject()
                    .put("role", "assistant")
                    .put("content", fullContent.toString())
                    .put("reasoning", fullThink.toString())
                    .put("tokPerSec", tokPerSec)
                    .put("tokens", tokens)
                    .put("backend", llama.getActiveBackend());
            if (promptTokens > 0) asstMsg.put("promptTokens", promptTokens);
            if (totalTokens > 0) asstMsg.put("totalTokens", totalTokens);
            if (searchResp != null && !searchResp.results.isEmpty()) {
                asstMsg.put("webProvider", searchResp.provider);
                asstMsg.put("webSources", searchResp.resultsToJson());
            }
            hist.put(asstMsg);

            if (totalTokens > 0) {
                session.put("lastTotalTokens", totalTokens);
                session.put("lastTotalMsgCount", hist.length());
            } else {
                session.remove("lastTotalTokens");
                session.remove("lastTotalMsgCount");
            }
            saveSessions(sessions);
            enrichSessionWithContextStats(session);

            JSONObject done = new JSONObject();
            done.put("sessionId", session.optString("id"));
            done.put("tokens", tokens);
            done.put("promptTokens", promptTokens);
            done.put("totalTokens", session.optInt("ctxUsedTokens", totalTokens));
            done.put("ctxMaxTokens", session.optInt("ctxMaxTokens", ctxSize));
            done.put("ctxRemainingTokens", session.optInt("ctxRemainingTokens", Math.max(0, ctxSize - totalTokens)));
            done.put("ctxPercent", session.optInt("ctxPercent", 0));
            done.put("tokPerSec", tokPerSec);
            done.put("backend", llama.getActiveBackend());
            sendSse(out, "done", done);
        } catch (Exception ex) {
            sendSse(out, "error", ex.getMessage() != null ? ex.getMessage() : "Ошибка генерации");
        } finally {
            llama.setStreamingActive(false);
        }
    }

    private JSONObject buildStatusJson() throws Exception {
        double[] ram = models.getRamStatsGB();
        double[] storage = models.getStorageStatsGB();
        Intent bat = ctx.registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        int batPct = 0;
        double batTemp = 0;
        if (bat != null) {
            int lvl = bat.getIntExtra(BatteryManager.EXTRA_LEVEL, -1);
            int scale = bat.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
            if (lvl >= 0 && scale > 0) batPct = (lvl * 100) / scale;
            int t = bat.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0);
            batTemp = Math.round(t) / 10.0;
        }

        String activeLlm = models.getActiveModelFile();
        File mmproj = models.findMatchingMmproj(activeLlm);
        JSONArray avail = new JSONArray();
        for (File f : models.listInstalledLlmFiles()) {
            avail.put(new JSONObject()
                    .put("fileName", f.getName())
                    .put("sizeGB", Math.round((f.length() / 1073741824.0) * 100.0) / 100.0)
                    .put("hasVision", models.findMatchingMmproj(f.getName()) != null));
        }

        JSONObject o = new JSONObject();
        o.put("version", "4.8.0");
        o.put("device", models.getDeviceSocLabel());
        o.put("androidVersion", "Android " + Build.VERSION.RELEASE);
        o.put("cpuCores", Runtime.getRuntime().availableProcessors());
        o.put("ramUsedGB", ram[0]);
        o.put("ramTotalGB", ram[1]);
        o.put("ramAvailGB", ram[2]);
        o.put("diskFreeGB", storage[0]);
        o.put("diskTotalGB", storage[1]);
        o.put("batteryPct", batPct);
        o.put("batteryTempC", batTemp);
        o.put("llmRunning", llama.isRunning());
        o.put("runningModel", llama.getRunningModelFile());
        o.put("activeBackend", llama.getActiveBackend());
        o.put("activeLlm", activeLlm);
        o.put("activeLlmHasVision", mmproj != null);
        o.put("activeLlmMmproj", mmproj != null ? mmproj.getName() : "");
        o.put("availableLlms", avail);
        o.put("ctxSize", models.getPrefs().getInt("ctx_size", 8192));
        o.put("jobs", models.getJobsJson());
        o.put("modelsDir", models.getModelsDir().getAbsolutePath());
        return o;
    }

    private JSONObject handleFreeRam() throws Exception {
        ActivityManager am = (ActivityManager) ctx.getSystemService(Context.ACTIVITY_SERVICE);
        ActivityManager.MemoryInfo miBefore = new ActivityManager.MemoryInfo();
        if (am != null) am.getMemoryInfo(miBefore);
        long beforeAvail = miBefore.availMem;
        long totalMem = miBefore.totalMem;

        int trimmed = 0;
        if (am != null) {
            try {
                PackageManager pm = ctx.getPackageManager();
                String selfPkg = ctx.getPackageName();
                List<ApplicationInfo> apps = pm.getInstalledApplications(0);
                for (ApplicationInfo ai : apps) {
                    String pkg = ai.packageName;
                    if (pkg == null || pkg.equals(selfPkg)) continue;
                    // Skip core Android OS, SystemUI, keyboards, telecom & launchers
                    if ("android".equals(pkg)
                            || pkg.startsWith("com.android.systemui")
                            || pkg.startsWith("com.android.phone")
                            || pkg.startsWith("com.android.providers.")
                            || pkg.contains("inputmethod")
                            || pkg.contains("honeyboard")
                            || pkg.contains("launcher")
                            || pkg.contains("bluetooth")) {
                        continue;
                    }
                    try {
                        am.killBackgroundProcesses(pkg);
                        trimmed++;
                    } catch (Exception ignored) { }
                }
            } catch (Exception ignored) { }
        }

        // Clean stale temporary files in our own cache directory
        try {
            File cache = ctx.getCacheDir();
            if (cache != null && cache.isDirectory()) {
                File[] files = cache.listFiles();
                if (files != null) {
                    long cutoff = System.currentTimeMillis() - 60_000L;
                    for (File f : files) {
                        if (f.isFile() && f.lastModified() < cutoff) {
                            try { f.delete(); } catch (Exception ignored) { }
                        }
                    }
                }
            }
        } catch (Exception ignored) { }

        OfflineVoiceEngine.unloadRecognizer();
        System.gc();
        Runtime.getRuntime().gc();
        try { Thread.sleep(380); } catch (InterruptedException ignored) { }

        ActivityManager.MemoryInfo miAfter = new ActivityManager.MemoryInfo();
        if (am != null) am.getMemoryInfo(miAfter);
        long afterAvail = miAfter.availMem;

        long beforeMB = Math.round(beforeAvail / 1048576.0);
        long afterMB = Math.round(afterAvail / 1048576.0);
        long freedMB = Math.max(0, afterMB - beforeMB);
        double afterGB = Math.round((afterAvail / 1073741824.0) * 100.0) / 100.0;
        double totalGB = Math.round((totalMem / 1073741824.0) * 100.0) / 100.0;

        JSONObject o = new JSONObject();
        o.put("ok", true);
        o.put("beforeAvailMB", beforeMB);
        o.put("afterAvailMB", afterMB);
        o.put("freedMB", freedMB);
        o.put("afterAvailGB", afterGB);
        o.put("totalGB", totalGB);
        o.put("trimmedCount", trimmed);
        o.put("llmRunning", llama.isRunning());
        return o;
    }

    private void handleCompactSession(OutputStream rawOut, String sessionId) throws Exception {
        llama.setStreamingActive(true);
        try {
            JSONArray list = loadSessions();
            JSONObject session = null;
            for (int i = 0; i < list.length(); i++) {
                JSONObject item = list.getJSONObject(i);
                if (sessionId.equals(item.optString("id"))) { session = item; break; }
            }
            if (session == null) {
                sendJson(rawOut, 404, "{\"error\":\"Чат не найден\"}");
                return;
            }
            JSONArray hist = session.optJSONArray("messages");
            if (hist == null || hist.length() == 0) {
                sendJson(rawOut, 400, "{\"error\":\"В чате ещё нет сообщений для сжатия\"}");
                return;
            }

            String activeFile = models.getActiveModelFile();
            if (activeFile.isEmpty()) {
                sendJson(rawOut, 400, "{\"error\":\"Нет скачанной LLM для сжатия контекста\"}");
                return;
            }
            if (!llama.isRunning() || !activeFile.equalsIgnoreCase(llama.getRunningModelFile())) {
                llama.start(activeFile);
            }

            enrichSessionWithContextStats(session);
            int beforeTokens = session.optInt("ctxUsedTokens", 0);

            // Keep last 2 messages (1 user+assistant pair) verbatim if we have more than 2 messages
            int keepTail = hist.length() > 2 ? 2 : 0;
            int summarizeEnd = hist.length() - keepTail;

            StringBuilder transcript = new StringBuilder();
            String prevSummary = session.optString("memorySummary", "").trim();
            if (!prevSummary.isEmpty()) {
                transcript.append("Предыдущая выжимка памяти:\n").append(prevSummary).append("\n\n");
            }
            transcript.append("Новые сообщения диалога для сжатия:\n");
            for (int i = 0; i < summarizeEnd; i++) {
                JSONObject m = hist.getJSONObject(i);
                String role = "user".equals(m.optString("role")) ? "Пользователь" : "Ассистент";
                String content = m.optString("content", "");
                if (content.length() > 2400) content = content.substring(0, 2400) + "...";
                transcript.append(role).append(": ").append(content).append("\n\n");
            }

            JSONArray apiMsgs = new JSONArray();
            apiMsgs.put(new JSONObject().put("role", "system").put("content",
                    "Ты модуль сжатия памяти диалога (Context Compactor). "
                            + "Сожми диалог в компактную, но максимально точную структурированную выжимку на русском языке (до 8-12 коротких буллитов). "
                            + "Обязательно сохрани: цели пользователя, ключевые факты, числа, имена, названия файлов/функций, принятые решения и нерешённые задачи. "
                            + "Без воды и приветствий — только факты для продолжения диалога."));
            apiMsgs.put(new JSONObject().put("role", "user").put("content", transcript.toString()));

            JSONObject payload = new JSONObject();
            payload.put("messages", apiMsgs);
            payload.put("temperature", 0.2);
            payload.put("max_tokens", 512);
            payload.put("stream", false);

            HttpURLConnection conn = (HttpURLConnection) new URL("http://127.0.0.1:" + LlamaProcessManager.LLAMA_PORT + "/v1/chat/completions").openConnection();
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setConnectTimeout(15000);
            conn.setReadTimeout(120000);
            conn.setRequestProperty("Content-Type", "application/json");
            byte[] pBytes = payload.toString().getBytes(StandardCharsets.UTF_8);
            try (OutputStream cos = conn.getOutputStream()) { cos.write(pBytes); }

            int code = conn.getResponseCode();
            if (code != 200) {
                String err = readAll(conn.getErrorStream());
                conn.disconnect();
                sendJson(rawOut, 500, new JSONObject().put("error", "LLM HTTP " + code + ": " + err).toString());
                return;
            }
            String respText = readAll(conn.getInputStream());
            conn.disconnect();

            JSONObject respJson = new JSONObject(respText);
            String summary = respJson.getJSONArray("choices").getJSONObject(0)
                    .getJSONObject("message").optString("content", "").trim();
            // Strip any <think>...</think> blocks if model emitted reasoning
            summary = summary.replaceAll("(?s)<think>.*?</think>", "").trim();
            if (summary.isEmpty()) {
                sendJson(rawOut, 500, "{\"error\":\"Модель вернула пустую выжимку\"}");
                return;
            }

            JSONArray kept = new JSONArray();
            for (int i = summarizeEnd; i < hist.length(); i++) {
                kept.put(hist.getJSONObject(i));
            }
            session.put("messages", kept);
            session.put("memorySummary", summary);
            session.put("compactedCount", session.optInt("compactedCount", 0) + summarizeEnd);
            session.remove("lastTotalTokens");
            session.remove("lastTotalMsgCount");

            saveSessions(list);
            enrichSessionWithContextStats(session);
            int afterTokens = session.optInt("ctxUsedTokens", 0);
            int savedTokens = Math.max(0, beforeTokens - afterTokens);
            int savedPct = beforeTokens > 0 ? Math.min(99, (int) Math.round((savedTokens * 100.0) / beforeTokens)) : 0;

            JSONObject outObj = new JSONObject();
            outObj.put("ok", true);
            outObj.put("beforeTokens", beforeTokens);
            outObj.put("afterTokens", afterTokens);
            outObj.put("savedTokens", savedTokens);
            outObj.put("savedPct", savedPct);
            outObj.put("compactedMessages", summarizeEnd);
            outObj.put("summary", summary);
            outObj.put("session", session);
            sendJson(rawOut, 200, outObj.toString());
        } catch (Exception ex) {
            sendJson(rawOut, 500, new JSONObject().put("error", ex.getMessage() != null ? ex.getMessage() : "Ошибка сжатия").toString());
        } finally {
            llama.setStreamingActive(false);
        }
    }

    private String buildFullSystemPrompt(JSONObject session, String personaId) {
        StringBuilder sb = new StringBuilder(getPersonaPrompt(personaId));
        String today = new java.text.SimpleDateFormat("yyyy-MM-dd (EEEE)", new Locale("ru", "RU")).format(new java.util.Date());
        sb.append("\nТекущая дата на устройстве: ").append(today).append(".");
        String custom = session != null ? session.optString("customSystemPrompt", "").trim() : "";
        if (!custom.isEmpty()) {
            sb.append("\n\nДополнительные инструкции пользователя:\n").append(custom);
        }
        String mem = session != null ? session.optString("memorySummary", "").trim() : "";
        if (!mem.isEmpty()) {
            sb.append("\n\n[Сжатая память предыдущей части диалога — учитывай эти факты]:\n").append(mem);
        }
        return sb.toString();
    }

    private static int estimateTokens(String text) {
        if (text == null || text.isEmpty()) return 0;
        double tok = 0.0;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c >= 0x0400 && c <= 0x04FF) tok += 0.42;      // Cyrillic ~2.4 chars/tok
            else if (c > 0x007F) tok += 0.55;                 // Emoji / CJK / other
            else tok += 0.27;                                 // Latin / digits / code ~3.7 chars/tok
        }
        return Math.max(1, (int) Math.ceil(tok));
    }

    private void enrichSessionWithContextStats(JSONObject session) {
        try {
            int ctxMax = models.getPrefs().getInt("ctx_size", 8192);
            JSONArray hist = session.optJSONArray("messages");
            int msgCount = hist != null ? hist.length() : 0;

            int exactTotal = session.optInt("lastTotalTokens", 0);
            int exactMsgCount = session.optInt("lastTotalMsgCount", -1);
            boolean isExact = exactTotal > 0 && exactMsgCount == msgCount;

            int used;
            if (isExact) {
                used = exactTotal;
            } else {
                String sys = buildFullSystemPrompt(session, session.optString("personaId", "default"));
                used = estimateTokens(sys) + 16;
                if (hist != null) {
                    for (int i = 0; i < hist.length(); i++) {
                        JSONObject m = hist.getJSONObject(i);
                        used += estimateTokens(m.optString("content", "")) + 10;
                    }
                }
            }
            int remaining = Math.max(0, ctxMax - used);
            int pct = ctxMax > 0 ? Math.min(100, (int) Math.round((used * 100.0) / ctxMax)) : 0;

            session.put("ctxUsedTokens", used);
            session.put("ctxMaxTokens", ctxMax);
            session.put("ctxRemainingTokens", remaining);
            session.put("ctxPercent", pct);
            session.put("ctxIsExact", isExact);
            if (!session.has("memorySummary")) session.put("memorySummary", "");
            if (!session.has("customSystemPrompt")) session.put("customSystemPrompt", "");
            if (!session.has("compactedCount")) session.put("compactedCount", 0);
        } catch (Exception ignored) { }
    }

    private String getPersonaPrompt(String id) {
        if ("coder".equals(id)) return "Ты senior software engineer. Отвечай чётко, с рабочим кодом и без лишней воды.";
        if ("translator".equals(id)) return "Ты профессиональный переводчик RU↔EN. Переводи естественно и точно, поясняя сложные термины при необходимости.";
        if ("concise".equals(id)) return "Отвечай максимально кратко, тезисно, по делу.";
        if ("privacy".equals(id)) return "Ты офлайн-специалист по проверке документов и приватности. Анализируй договоры на скрытые риски/штрафы, структурируй реквизиты и при запросе маскируй персональные данные ([ФИО], [ТЕЛЕФОН], [ПАСПОРТ], [АДРЕС], [КАРТА]).";
        if ("notes".equals(id)) return "Ты структурировщик заметок и голосовых диктовок. Превращай сумбурный текст в аккуратный чек-лист задач с приоритетами, сроками и главными тезисами.";
        if ("tutor".equals(id)) return "Ты терпеливый репетитор иностранных языков. Веди живой диалог, мягко исправляй ошибки с коротким объяснением и давай 2-3 полезных выражения.";
        return "Ты умный, лаконичный и полезный локальный AI-ассистент Brazhnichenko Hub. Отвечай на языке пользователя чётко и по делу.";
    }

    private synchronized JSONArray loadSessions() {
        try {
            if (sessionsFile.exists()) {
                try (FileInputStream fis = new FileInputStream(sessionsFile)) {
                    String s = readAll(fis);
                    if (!s.trim().isEmpty()) return new JSONArray(s);
                }
            }
        } catch (Exception ignored) { }
        return new JSONArray();
    }

    private synchronized void saveSessions(JSONArray arr) {
        try (FileOutputStream fos = new FileOutputStream(sessionsFile)) {
            fos.write(arr.toString().getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) { }
    }

    private void serveAsset(OutputStream out, String urlPath) throws Exception {
        String clean = urlPath.replace("..", "");
        if (clean.startsWith("/")) clean = clean.substring(1);
        String assetPath = "wwwroot/" + clean;
        try (InputStream is = ctx.getAssets().open(assetPath)) {
            ByteArrayOutputStream baos = new ByteArrayOutputStream();
            byte[] buf = new byte[16384];
            int n;
            while ((n = is.read(buf)) > 0) baos.write(buf, 0, n);
            byte[] data = baos.toByteArray();
            String mime = clean.endsWith(".html") ? "text/html; charset=utf-8"
                    : clean.endsWith(".css") ? "text/css; charset=utf-8"
                    : clean.endsWith(".js") ? "application/javascript; charset=utf-8"
                    : clean.endsWith(".png") ? "image/png" : "application/octet-stream";
            String hdr = "HTTP/1.1 200 OK\r\nContent-Type: " + mime + "\r\nContent-Length: " + data.length + "\r\nConnection: close\r\n\r\n";
            out.write(hdr.getBytes(StandardCharsets.UTF_8));
            out.write(data);
            out.flush();
        } catch (Exception e) {
            sendJson(out, 404, "{\"error\":\"Not found\"}");
        }
    }

    private void sendSse(OutputStream out, String type, Object data) throws Exception {
        JSONObject ev = new JSONObject();
        ev.put("type", type);
        ev.put("data", data);
        String msg = "data: " + ev.toString() + "\n\n";
        out.write(msg.getBytes(StandardCharsets.UTF_8));
        out.flush();
    }

    private void sendJson(OutputStream out, int code, String json) throws Exception {
        byte[] data = json.getBytes(StandardCharsets.UTF_8);
        String status = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 500 ? "Internal Server Error" : "Not Found";
        String hdr = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + data.length + "\r\nConnection: close\r\n\r\n";
        out.write(hdr.getBytes(StandardCharsets.UTF_8));
        out.write(data);
        out.flush();
    }

    private static String readLine(InputStream in) throws Exception {
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        int b;
        boolean any = false;
        while ((b = in.read()) != -1) {
            any = true;
            if (b == '\n') break;
            if (b != '\r') baos.write(b);
        }
        return any ? baos.toString("UTF-8") : null;
    }

    private static byte[] readExact(InputStream in, int len) throws Exception {
        byte[] buf = new byte[len];
        int off = 0;
        while (off < len) {
            int n = in.read(buf, off, len - off);
            if (n < 0) break;
            off += n;
        }
        return buf;
    }

    private static String readAll(InputStream is) throws Exception {
        if (is == null) return "";
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) > 0) baos.write(buf, 0, n);
        return baos.toString("UTF-8");
    }

    private static Map<String, String> parseQuery(String q) {
        Map<String, String> map = new HashMap<>();
        if (q == null || q.isEmpty()) return map;
        for (String pair : q.split("&")) {
            int eq = pair.indexOf('=');
            if (eq > 0) {
                try {
                    map.put(URLDecoder.decode(pair.substring(0, eq), "UTF-8"),
                            URLDecoder.decode(pair.substring(eq + 1), "UTF-8"));
                } catch (Exception ignored) { }
            }
        }
        return map;
    }
}

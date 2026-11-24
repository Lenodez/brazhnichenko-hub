package com.brazhnichenko.hub;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.pdf.PdfRenderer;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.provider.Settings;
import android.content.SharedPreferences;
import android.speech.RecognizerIntent;
import android.speech.tts.TextToSpeech;
import android.util.Base64;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;
import android.webkit.JavascriptInterface;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.Inflater;

public class MainActivity extends Activity {
    public static final String ACTION_SHORTCUT_ASK = "com.brazhnichenko.hub.SHORTCUT_ASK";
    public static final String ACTION_SHORTCUT_VOICE = "com.brazhnichenko.hub.SHORTCUT_VOICE";
    public static final String ACTION_SHORTCUT_CAMERA = "com.brazhnichenko.hub.SHORTCUT_CAMERA";
    public static final String ACTION_SHORTCUT_DOC = "com.brazhnichenko.hub.SHORTCUT_DOC";
    public static final String ACTION_SHORTCUT_NEW_CHAT = "com.brazhnichenko.hub.SHORTCUT_NEW_CHAT";
    public static final String ACTION_ASSIST_LAUNCH = "com.brazhnichenko.hub.ASSIST_LAUNCH";
    public static final String EXTRA_HAS_SCREENSHOT = "has_screenshot";

    private static final int FILE_CHOOSER_REQ = 4102;
    private static final int NOTIF_PERM_REQ = 4103;
    private static final int VOICE_REQ = 4104;
    private static final int CAMERA_REQ = 4105;
    private static final int DOC_REQ = 4106;
    private static final int AUDIO_PERM_REQ = 4107;
    private static final String HUB_URL = "http://127.0.0.1:" + LocalHubServer.PORT + "/";

    private WebView webView;
    private ValueCallback<Uri[]> filePathCallback;
    private Uri pendingCameraUri;
    private TextToSpeech tts;
    private boolean ttsReady = false;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private volatile boolean webReady = false;
    private final ArrayList<String> pendingJsQueue = new ArrayList<>();
    private volatile boolean pendingAssistAutoSend = false;
    private volatile boolean pendingAssistAutoTts = false;
    private volatile boolean pendingAssistWebSearch = false;
    private volatile boolean jsOverlayOpen = false;
    private Object backInvokedCallbackObj = null;
    private boolean backCallbackRegistered = false;

    @SuppressLint({"SetJavaScriptEnabled", "AddJavascriptInterface"})
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Window win = getWindow();
        win.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        win.setStatusBarColor(Color.parseColor("#0D1017"));
        win.setNavigationBarColor(Color.parseColor("#0D1017"));

        if (Build.VERSION.SDK_INT >= 33) {
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, NOTIF_PERM_REQ);
            }
        }

        Intent svcIntent = new Intent(this, HubForegroundService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(svcIntent);
        } else {
            startService(svcIntent);
        }

        tts = new TextToSpeech(getApplicationContext(), status -> {
            if (status == TextToSpeech.SUCCESS) {
                ttsReady = true;
                tts.setLanguage(new Locale("ru", "RU"));
            }
        });

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.parseColor("#0D1017"));
        if (Build.VERSION.SDK_INT >= 30) {
            root.setOnApplyWindowInsetsListener((v, insets) -> {
                android.graphics.Insets sys = insets.getInsets(
                        WindowInsets.Type.systemBars() | WindowInsets.Type.ime() | WindowInsets.Type.displayCutout());
                v.setPadding(sys.left, sys.top, sys.right, sys.bottom);
                return WindowInsets.CONSUMED;
            });
        } else {
            root.setFitsSystemWindows(true);
        }

        webView = new WebView(this);
        webView.setBackgroundColor(Color.parseColor("#0D1017"));
        webView.setFocusable(true);
        webView.setFocusableInTouchMode(true);
        webView.requestFocus(View.FOCUS_DOWN);

        WebSettings ws = webView.getSettings();
        ws.setJavaScriptEnabled(true);
        ws.setDomStorageEnabled(true);
        ws.setDatabaseEnabled(true);
        ws.setAllowFileAccess(true);
        ws.setAllowContentAccess(true);
        ws.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        ws.setMediaPlaybackRequiresUserGesture(false);

        webView.addJavascriptInterface(new AndroidBridge(), "AndroidHub");

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                Uri uri = request.getUrl();
                if (uri != null && "127.0.0.1".equals(uri.getHost())) {
                    return false;
                }
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, uri));
                    return true;
                } catch (Exception ignored) {
                    return false;
                }
            }
        });

        webView.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onShowFileChooser(WebView view, ValueCallback<Uri[]> cb, FileChooserParams params) {
                if (filePathCallback != null) filePathCallback.onReceiveValue(null);
                filePathCallback = cb;
                try {
                    Intent intent = params.createIntent();
                    intent.setType("image/*");
                    startActivityForResult(intent, FILE_CHOOSER_REQ);
                    return true;
                } catch (Exception e) {
                    filePathCallback = null;
                    return false;
                }
            }
        });

        root.addView(webView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));
        setContentView(root);

        handleIncomingIntent(getIntent());
        loadWhenServerReady(0);
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIncomingIntent(intent);
    }

    private void handleIncomingIntent(Intent intent) {
        if (intent == null) return;
        String action = intent.getAction();
        if (action == null || Intent.ACTION_MAIN.equals(action)) return;
        // Clear consumed action so configuration changes / recents never re-trigger it
        intent.setAction(Intent.ACTION_MAIN);

        if (ACTION_SHORTCUT_VOICE.equals(action)) {
            evalJsWhenReady("window.onAndroidQuickAction && window.onAndroidQuickAction('voice')");
            mainHandler.postDelayed(this::launchVoiceInput, 250);
            return;
        }
        if (ACTION_SHORTCUT_CAMERA.equals(action)) {
            evalJsWhenReady("window.onAndroidQuickAction && window.onAndroidQuickAction('camera')");
            mainHandler.postDelayed(this::launchCamera, 250);
            return;
        }
        if (ACTION_SHORTCUT_DOC.equals(action)) {
            evalJsWhenReady("window.onAndroidQuickAction && window.onAndroidQuickAction('doc')");
            mainHandler.postDelayed(this::launchDocumentPicker, 250);
            return;
        }
        if (ACTION_SHORTCUT_NEW_CHAT.equals(action)) {
            evalJsWhenReady("window.onAndroidQuickAction && window.onAndroidQuickAction('new_chat')");
            return;
        }
        if (ACTION_SHORTCUT_ASK.equals(action)) {
            evalJsWhenReady("window.onAndroidQuickAction && window.onAndroidQuickAction('ask')");
            return;
        }

        // System Digital Assistant invocation (Power button / Corner swipe / Bluetooth headset / ACTION_ASSIST)
        if (ACTION_ASSIST_LAUNCH.equals(action)
                || Intent.ACTION_ASSIST.equals(action)
                || Intent.ACTION_VOICE_COMMAND.equals(action)) {
            SharedPreferences p = getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
            boolean autoVoice = p.getBoolean("assist_auto_voice", true) || Intent.ACTION_VOICE_COMMAND.equals(action);
            boolean autoTts = p.getBoolean("assist_auto_tts", true);
            boolean autoAttachScreen = p.getBoolean("assist_attach_screen", false);
            boolean assistWeb = p.getBoolean("assist_web_search", true);
            boolean hasShot = intent.getBooleanExtra(EXTRA_HAS_SCREENSHOT, false)
                    && HubAssistSession.getAssistScreenFile(this).exists();
            try {
                JSONObject opts = new JSONObject();
                opts.put("hasScreenshot", hasShot);
                opts.put("autoAttachScreen", autoAttachScreen);
                opts.put("autoVoice", autoVoice);
                opts.put("autoTts", autoTts);
                opts.put("webSearch", assistWeb);
                evalJsWhenReady("window.onAndroidAssistLaunch && window.onAndroidAssistLaunch(" + opts.toString() + ")");
            } catch (Exception ignored) { }

            if (autoVoice) {
                pendingAssistAutoSend = true;
                pendingAssistAutoTts = autoTts;
                pendingAssistWebSearch = assistWeb;
                mainHandler.postDelayed(this::launchVoiceInput, 220);
            }
            return;
        }

        // Highlighted text menu: "Спросить в Hub AI"
        if (Intent.ACTION_PROCESS_TEXT.equals(action)) {
            CharSequence cs = intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT);
            if (cs != null && cs.toString().trim().length() > 0) {
                String txt = cs.toString().trim();
                evalJsWhenReady("window.onAndroidSharedText && window.onAndroidSharedText("
                        + JSONObject.quote(txt) + ", 'Выделенный текст')");
            }
            return;
        }

        // Android Share Sheet (text, image, PDF)
        if (Intent.ACTION_SEND.equals(action)) {
            String mime = intent.getType() != null ? intent.getType().toLowerCase(Locale.ROOT) : "";
            if (mime.startsWith("text/")) {
                String shared = intent.getStringExtra(Intent.EXTRA_TEXT);
                String subj = intent.getStringExtra(Intent.EXTRA_SUBJECT);
                String combined = (subj != null && !subj.trim().isEmpty() && shared != null && !shared.contains(subj))
                        ? (subj.trim() + "\n\n" + shared.trim())
                        : (shared != null ? shared.trim() : "");
                if (!combined.isEmpty()) {
                    evalJsWhenReady("window.onAndroidSharedText && window.onAndroidSharedText("
                            + JSONObject.quote(combined) + ", 'Поделиться')");
                } else {
                    Uri stream = intent.getParcelableExtra(Intent.EXTRA_STREAM);
                    if (stream != null) new Thread(() -> handleDocumentPick(stream)).start();
                }
            } else if (mime.startsWith("image/")) {
                Uri imgUri = intent.getParcelableExtra(Intent.EXTRA_STREAM);
                if (imgUri != null) {
                    new Thread(() -> handleSharedImage(imgUri)).start();
                }
            } else if (mime.equals("application/pdf")) {
                Uri pdfUri = intent.getParcelableExtra(Intent.EXTRA_STREAM);
                if (pdfUri != null) {
                    new Thread(() -> handleDocumentPick(pdfUri)).start();
                }
            }
        }
    }

    private void handleSharedImage(Uri uri) {
        try {
            Bitmap bmp;
            try (InputStream is = getContentResolver().openInputStream(uri)) {
                bmp = BitmapFactory.decodeStream(is);
            }
            if (bmp == null) {
                showToast("Не удалось прочитать изображение");
                return;
            }
            String dataUrl = bitmapToJpegDataUrl(bmp, 1024);
            bmp.recycle();
            evalJsWhenReady("window.onAndroidCameraImage && window.onAndroidCameraImage(" + JSONObject.quote(dataUrl) + ")");
        } catch (Exception ex) {
            showToast("Ошибка прикрепления фото: " + ex.getMessage());
        }
    }

    private void loadWhenServerReady(int attempt) {
        new Thread(() -> {
            boolean up = false;
            try {
                HttpURLConnection conn = (HttpURLConnection) new URL(HUB_URL + "api/health").openConnection();
                conn.setConnectTimeout(400);
                conn.setReadTimeout(400);
                up = conn.getResponseCode() == 200;
                conn.disconnect();
            } catch (Exception ignored) { }

            if (up || attempt >= 25) {
                mainHandler.post(() -> webView.loadUrl(HUB_URL));
            } else {
                mainHandler.postDelayed(() -> loadWhenServerReady(attempt + 1), 150);
            }
        }).start();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode == FILE_CHOOSER_REQ && filePathCallback != null) {
            Uri[] results = null;
            if (resultCode == RESULT_OK && data != null && data.getDataString() != null) {
                results = new Uri[]{Uri.parse(data.getDataString())};
            }
            filePathCallback.onReceiveValue(results);
            filePathCallback = null;
            return;
        }

        if (requestCode == VOICE_REQ) {
            boolean autoSend = pendingAssistAutoSend;
            boolean autoTts = pendingAssistAutoTts;
            boolean webSearch = pendingAssistWebSearch;
            pendingAssistAutoSend = false;
            pendingAssistAutoTts = false;
            pendingAssistWebSearch = false;
            if (resultCode == RESULT_OK && data != null) {
                ArrayList<String> matches = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
                if (matches != null && !matches.isEmpty()) {
                    String text = matches.get(0);
                    evalJs("window.onAndroidVoiceResult && window.onAndroidVoiceResult("
                            + JSONObject.quote(text) + ", " + autoSend + ", " + autoTts + ", " + webSearch + ")");
                }
            }
            return;
        }

        if (requestCode == CAMERA_REQ) {
            final Uri uri = pendingCameraUri;
            pendingCameraUri = null;
            if (resultCode == RESULT_OK && uri != null) {
                new Thread(() -> handleCameraCapture(uri)).start();
            } else if (uri != null) {
                try { getContentResolver().delete(uri, null, null); } catch (Exception ignored) { }
            }
            return;
        }

        if (requestCode == DOC_REQ && resultCode == RESULT_OK && data != null && data.getData() != null) {
            final Uri docUri = data.getData();
            new Thread(() -> handleDocumentPick(docUri)).start();
        }
    }

    private void handleCameraCapture(Uri uri) {
        try {
            Bitmap bmp;
            try (InputStream is = getContentResolver().openInputStream(uri)) {
                bmp = BitmapFactory.decodeStream(is);
            }
            try { getContentResolver().delete(uri, null, null); } catch (Exception ignored) { }
            if (bmp == null) return;

            String dataUrl = bitmapToJpegDataUrl(bmp, 1024);
            bmp.recycle();
            evalJs("window.onAndroidCameraImage && window.onAndroidCameraImage(" + JSONObject.quote(dataUrl) + ")");
        } catch (Exception ex) {
            showToast("Ошибка фото с камеры: " + ex.getMessage());
        }
    }

    private void handleDocumentPick(Uri uri) {
        try {
            String name = queryDisplayName(uri);
            String lower = name.toLowerCase(Locale.ROOT);

            if (lower.endsWith(".pdf")) {
                byte[] pdfBytes = readUriBytes(uri, 12 * 1024 * 1024);
                String extracted = extractPdfTextBestEffort(pdfBytes);
                if (extracted.trim().length() >= 80) {
                    if (extracted.length() > 32000) extracted = extracted.substring(0, 32000) + "\n...[обрезано]";
                    JSONObject payload = new JSONObject();
                    payload.put("name", name);
                    payload.put("kind", "text");
                    payload.put("text", extracted.trim());
                    evalJs("window.onAndroidDocAttached && window.onAndroidDocAttached(" + payload.toString() + ")");
                } else {
                    // Scanned/image PDF -> render page 1 to JPEG for offline Vision LLM!
                    String dataUrl = renderPdfFirstPageDataUrl(uri);
                    if (dataUrl != null) {
                        JSONObject payload = new JSONObject();
                        payload.put("name", name + " (стр. 1)");
                        payload.put("kind", "image");
                        payload.put("imageDataUrl", dataUrl);
                        evalJs("window.onAndroidDocAttached && window.onAndroidDocAttached(" + payload.toString() + ")");
                    } else {
                        showToast("Не удалось прочитать текст или страницу из PDF");
                    }
                }
            } else {
                byte[] bytes = readUriBytes(uri, 512 * 1024);
                String text = new String(bytes, StandardCharsets.UTF_8);
                if (text.length() > 32000) text = text.substring(0, 32000) + "\n...[обрезано]";
                JSONObject payload = new JSONObject();
                payload.put("name", name);
                payload.put("kind", "text");
                payload.put("text", text);
                evalJs("window.onAndroidDocAttached && window.onAndroidDocAttached(" + payload.toString() + ")");
            }
        } catch (Exception ex) {
            showToast("Ошибка чтения файла: " + ex.getMessage());
        }
    }

    private String renderPdfFirstPageDataUrl(Uri uri) {
        try (ParcelFileDescriptor pfd = getContentResolver().openFileDescriptor(uri, "r")) {
            if (pfd == null) return null;
            try (PdfRenderer renderer = new PdfRenderer(pfd)) {
                if (renderer.getPageCount() <= 0) return null;
                try (PdfRenderer.Page page = renderer.openPage(0)) {
                    int w = page.getWidth();
                    int h = page.getHeight();
                    double scale = Math.min(1280.0 / Math.max(1, Math.max(w, h)), 2.0);
                    int bw = Math.max(1, (int) Math.round(w * scale));
                    int bh = Math.max(1, (int) Math.round(h * scale));
                    Bitmap bmp = Bitmap.createBitmap(bw, bh, Bitmap.Config.ARGB_8888);
                    Canvas c = new Canvas(bmp);
                    c.drawColor(Color.WHITE);
                    page.render(bmp, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY);
                    String url = bitmapToJpegDataUrl(bmp, 1080);
                    bmp.recycle();
                    return url;
                }
            }
        } catch (Exception e) {
            return null;
        }
    }

    private static String extractPdfTextBestEffort(byte[] pdf) {
        // Extracts readable ASCII/UTF text from uncompressed or FlateDecode PDF content streams
        StringBuilder sb = new StringBuilder();
        String latin = new String(pdf, StandardCharsets.ISO_8859_1);
        Pattern streamPat = Pattern.compile("stream\\r?\\n(.*?)\\r?\\nendstream", Pattern.DOTALL);
        Matcher m = streamPat.matcher(latin);
        int chunks = 0;
        while (m.find() && chunks < 80 && sb.length() < 34000) {
            chunks++;
            String raw = m.group(1);
            byte[] rawBytes = raw.getBytes(StandardCharsets.ISO_8859_1);
            String content = tryInflate(rawBytes);
            if (content == null) content = raw;
            if (!content.contains("Tj") && !content.contains("TJ")) continue;
            Matcher tm = Pattern.compile("\\(([^()\\\\]{1,240})\\)").matcher(content);
            while (tm.find() && sb.length() < 34000) {
                String seg = tm.group(1).replaceAll("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", "");
                if (!seg.trim().isEmpty()) {
                    sb.append(seg).append(' ');
                }
            }
            sb.append('\n');
        }
        return sb.toString();
    }

    private static String tryInflate(byte[] data) {
        try {
            Inflater inf = new Inflater();
            inf.setInput(data);
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            int total = 0;
            while (!inf.finished() && total < 512 * 1024) {
                int n = inf.inflate(buf);
                if (n <= 0) break;
                out.write(buf, 0, n);
                total += n;
            }
            inf.end();
            return total > 0 ? out.toString("UTF-8") : null;
        } catch (Exception e) {
            return null;
        }
    }

    private static String bitmapToJpegDataUrl(Bitmap src, int maxDim) {
        int w = src.getWidth();
        int h = src.getHeight();
        Bitmap scaled = src;
        if (Math.max(w, h) > maxDim) {
            double r = maxDim / (double) Math.max(w, h);
            scaled = Bitmap.createScaledBitmap(src, Math.max(1, (int) (w * r)), Math.max(1, (int) (h * r)), true);
        }
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        scaled.compress(Bitmap.CompressFormat.JPEG, 86, baos);
        if (scaled != src) scaled.recycle();
        String b64 = Base64.encodeToString(baos.toByteArray(), Base64.NO_WRAP);
        return "data:image/jpeg;base64," + b64;
    }

    private byte[] readUriBytes(Uri uri, int maxBytes) throws Exception {
        try (InputStream is = getContentResolver().openInputStream(uri)) {
            if (is == null) return new byte[0];
            ByteArrayOutputStream baos = new ByteArrayOutputStream();
            byte[] buf = new byte[16384];
            int n, total = 0;
            while ((n = is.read(buf)) > 0 && total < maxBytes) {
                baos.write(buf, 0, Math.min(n, maxBytes - total));
                total += n;
            }
            return baos.toByteArray();
        }
    }

    private String queryDisplayName(Uri uri) {
        try (Cursor c = getContentResolver().query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (c != null && c.moveToFirst()) {
                String n = c.getString(0);
                if (n != null && !n.isEmpty()) return n;
            }
        } catch (Exception ignored) { }
        return "document.txt";
    }

    private void evalJs(String js) {
        evalJsWhenReady(js);
    }

    private void evalJsWhenReady(String js) {
        mainHandler.post(() -> {
            if (webReady && webView != null) {
                webView.evaluateJavascript(js, null);
            } else {
                synchronized (pendingJsQueue) {
                    pendingJsQueue.add(js);
                }
            }
        });
    }

    private void flushPendingJs() {
        mainHandler.post(() -> {
            webReady = true;
            ArrayList<String> copy;
            synchronized (pendingJsQueue) {
                copy = new ArrayList<>(pendingJsQueue);
                pendingJsQueue.clear();
            }
            if (webView != null) {
                for (String js : copy) {
                    webView.evaluateJavascript(js, null);
                }
            }
        });
    }

    private void launchVoiceInput() {
        mainHandler.post(() -> {
            if (OfflineVoiceEngine.isRecording()) {
                OfflineVoiceEngine.stopRecording(false);
                return;
            }
            String offlineModel = OfflineVoiceEngine.getActiveInstalledModel(this);
            if (!offlineModel.isEmpty()) {
                if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                    requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, AUDIO_PERM_REQ);
                    return;
                }
                startOfflineVoiceCapture();
                return;
            }
            launchSystemVoiceInput();
        });
    }

    private void startOfflineVoiceCapture() {
        OfflineVoiceEngine.startCaptureAndTranscribe(this, new OfflineVoiceEngine.VoiceCallback() {
            @Override
            public void onState(String state, String detail, int levelPct, double seconds) {
                evalJs("window.onAndroidVoiceState && window.onAndroidVoiceState("
                        + JSONObject.quote(state) + ", " + JSONObject.quote(detail) + ", "
                        + levelPct + ", " + String.format(Locale.US, "%.1f", seconds) + ")");
            }

            @Override
            public void onResult(String text, long decodeMs) {
                boolean autoSend = pendingAssistAutoSend;
                boolean autoTts = pendingAssistAutoTts;
                boolean webSearch = pendingAssistWebSearch;
                pendingAssistAutoSend = false;
                pendingAssistAutoTts = false;
                pendingAssistWebSearch = false;
                evalJs("window.onAndroidVoiceResult && window.onAndroidVoiceResult("
                        + JSONObject.quote(text) + ", " + autoSend + ", " + autoTts + ", " + webSearch + ", " + decodeMs + ")");
            }

            @Override
            public void onError(String message) {
                pendingAssistAutoSend = false;
                pendingAssistAutoTts = false;
                pendingAssistWebSearch = false;
                showToast(message);
            }
        });
    }

    private void launchSystemVoiceInput() {
        try {
            Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
            intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
            intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, "ru-RU");
            intent.putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true);
            intent.putExtra(RecognizerIntent.EXTRA_PROMPT, "Говорите запрос для локальной LLM...");
            startActivityForResult(intent, VOICE_REQ);
        } catch (Exception e) {
            Toast.makeText(MainActivity.this, "Голосовой ввод недоступен на устройстве", Toast.LENGTH_SHORT).show();
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == AUDIO_PERM_REQ) {
            if (grantResults != null && grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                startOfflineVoiceCapture();
            } else {
                showToast("Нужен доступ к микрофону для офлайн-распознавания речи");
            }
        }
    }

    private void launchCamera() {
        mainHandler.post(() -> {
            try {
                ContentValues cv = new ContentValues();
                cv.put(MediaStore.Images.Media.DISPLAY_NAME, "hub_vision_" + System.currentTimeMillis() + ".jpg");
                cv.put(MediaStore.Images.Media.MIME_TYPE, "image/jpeg");
                Uri uri = getContentResolver().insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, cv);
                if (uri == null) {
                    Toast.makeText(MainActivity.this, "Не удалось подготовить файл для камеры", Toast.LENGTH_SHORT).show();
                    return;
                }
                pendingCameraUri = uri;
                Intent cam = new Intent(MediaStore.ACTION_IMAGE_CAPTURE);
                cam.putExtra(MediaStore.EXTRA_OUTPUT, uri);
                startActivityForResult(cam, CAMERA_REQ);
            } catch (Exception e) {
                Toast.makeText(MainActivity.this, "Не удалось открыть камеру: " + e.getMessage(), Toast.LENGTH_SHORT).show();
            }
        });
    }

    private void launchDocumentPicker() {
        mainHandler.post(() -> {
            try {
                Intent doc = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                doc.addCategory(Intent.CATEGORY_OPENABLE);
                doc.setType("*/*");
                doc.putExtra(Intent.EXTRA_MIME_TYPES, new String[]{
                        "application/pdf",
                        "text/plain",
                        "text/markdown",
                        "text/csv",
                        "application/json",
                        "text/xml"
                });
                startActivityForResult(doc, DOC_REQ);
            } catch (Exception e) {
                Toast.makeText(MainActivity.this, "Не удалось открыть выбор документа", Toast.LENGTH_SHORT).show();
            }
        });
    }

    private void showToast(String msg) {
        mainHandler.post(() -> Toast.makeText(MainActivity.this, msg, Toast.LENGTH_SHORT).show());
    }

    private void syncPredictiveBackCallback(boolean overlayOpen) {
        jsOverlayOpen = overlayOpen;
        if (Build.VERSION.SDK_INT >= 33) {
            mainHandler.post(() -> {
                try {
                    if (backInvokedCallbackObj == null) {
                        backInvokedCallbackObj = (OnBackInvokedCallback) () -> {
                            evalJs("window.handleAndroidBack && window.handleAndroidBack()");
                            syncPredictiveBackCallback(false);
                        };
                    }
                    OnBackInvokedDispatcher disp = getOnBackInvokedDispatcher();
                    if (overlayOpen && !backCallbackRegistered) {
                        disp.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, (OnBackInvokedCallback) backInvokedCallbackObj);
                        backCallbackRegistered = true;
                    } else if (!overlayOpen && backCallbackRegistered) {
                        disp.unregisterOnBackInvokedCallback((OnBackInvokedCallback) backInvokedCallbackObj);
                        backCallbackRegistered = false;
                    }
                } catch (Exception ignored) { }
            });
        }
    }

    @Override
    public void onBackPressed() {
        if (jsOverlayOpen && webView != null && webReady) {
            evalJs("window.handleAndroidBack && window.handleAndroidBack()");
            syncPredictiveBackCallback(false);
            return;
        }
        if (webView != null && webView.canGoBack()) {
            webView.goBack();
        } else {
            moveTaskToBack(true);
        }
    }

    @Override
    protected void onDestroy() {
        if (tts != null) {
            try { tts.stop(); tts.shutdown(); } catch (Exception ignored) { }
        }
        super.onDestroy();
    }

    public final class AndroidBridge {
        @JavascriptInterface
        public void speak(String text, String lang) {
            if (!ttsReady || tts == null || text == null || text.trim().isEmpty()) return;
            mainHandler.post(() -> {
                try {
                    tts.setLanguage("en".equalsIgnoreCase(lang) ? Locale.US : new Locale("ru", "RU"));
                    tts.speak(text.trim(), TextToSpeech.QUEUE_FLUSH, null, "hub_tts_" + System.currentTimeMillis());
                } catch (Exception ignored) { }
            });
        }

        @JavascriptInterface
        public void stopSpeak() {
            if (tts != null) {
                try { tts.stop(); } catch (Exception ignored) { }
            }
        }

        @JavascriptInterface
        public void copyText(String text) {
            mainHandler.post(() -> {
                ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                if (cm != null) {
                    cm.setPrimaryClip(ClipData.newPlainText("Brazhnichenko Hub", text));
                    Toast.makeText(MainActivity.this, "Скопировано", Toast.LENGTH_SHORT).show();
                }
            });
        }

        @JavascriptInterface
        public void shareText(String text) {
            if (text == null || text.trim().isEmpty()) return;
            mainHandler.post(() -> {
                try {
                    Intent send = new Intent(Intent.ACTION_SEND);
                    send.setType("text/plain");
                    send.putExtra(Intent.EXTRA_TEXT, text);
                    startActivity(Intent.createChooser(send, "Поделиться ответом"));
                } catch (Exception ignored) { }
            });
        }

        @JavascriptInterface
        public void onWebReady() {
            flushPendingJs();
        }

        @JavascriptInterface
        public void startVoiceInput() {
            launchVoiceInput();
        }

        @JavascriptInterface
        public void stopOfflineVoice(boolean cancel) {
            OfflineVoiceEngine.stopRecording(cancel);
        }

        @JavascriptInterface
        public void openCamera() {
            launchCamera();
        }

        @JavascriptInterface
        public void pickDocument() {
            launchDocumentPicker();
        }

        @JavascriptInterface
        public void openAssistantSettings() {
            mainHandler.post(() -> {
                try {
                    Intent i = new Intent(Settings.ACTION_VOICE_INPUT_SETTINGS);
                    startActivity(i);
                } catch (Exception e1) {
                    try {
                        Intent i2 = new Intent(Settings.ACTION_MANAGE_DEFAULT_APPS_SETTINGS);
                        startActivity(i2);
                    } catch (Exception e2) {
                        Toast.makeText(MainActivity.this, "Открой Настройки → Приложения по умолчанию → Цифровой помощник", Toast.LENGTH_LONG).show();
                    }
                }
            });
        }

        @JavascriptInterface
        public boolean isDefaultAssistant() {
            if (HubAssistService.isServiceReady) return true;
            try {
                String pkg = getPackageName();
                String assist = Settings.Secure.getString(getContentResolver(), "assistant");
                if (assist != null && assist.contains(pkg)) return true;
                String vis = Settings.Secure.getString(getContentResolver(), "voice_interaction_service");
                if (vis != null && vis.contains(pkg)) return true;
            } catch (Exception ignored) { }
            return false;
        }

        @JavascriptInterface
        public void clearWebViewCache() {
            mainHandler.post(() -> {
                try {
                    if (webView != null) webView.clearCache(false);
                } catch (Exception ignored) { }
            });
        }

        @JavascriptInterface
        public void setOverlayOpen(boolean open) {
            syncPredictiveBackCallback(open);
        }

        @JavascriptInterface
        public void haptic(String kind) {
            mainHandler.post(() -> {
                try {
                    if (webView == null) return;
                    int code = HapticFeedbackConstants.CLOCK_TICK;
                    if ("confirm".equals(kind)) code = HapticFeedbackConstants.CONTEXT_CLICK;
                    else if ("long".equals(kind)) code = HapticFeedbackConstants.LONG_PRESS;
                    webView.performHapticFeedback(code);
                } catch (Exception ignored) { }
            });
        }

        @JavascriptInterface
        public void attachLastAssistScreen() {
            new Thread(() -> {
                try {
                    File f = HubAssistSession.getAssistScreenFile(MainActivity.this);
                    if (!f.exists()) {
                        showToast("Скрин экрана не найден");
                        return;
                    }
                    byte[] bytes;
                    try (FileInputStream fis = new FileInputStream(f);
                         ByteArrayOutputStream baos = new ByteArrayOutputStream()) {
                        byte[] buf = new byte[16384];
                        int n;
                        while ((n = fis.read(buf)) > 0) baos.write(buf, 0, n);
                        bytes = baos.toByteArray();
                    }
                    String b64 = Base64.encodeToString(bytes, Base64.NO_WRAP);
                    String dataUrl = "data:image/jpeg;base64," + b64;
                    evalJsWhenReady("window.onAndroidCameraImage && window.onAndroidCameraImage("
                            + JSONObject.quote(dataUrl) + ", 'Скрин экрана (Vision)')");
                } catch (Exception ex) {
                    showToast("Ошибка прикрепления скрина: " + ex.getMessage());
                }
            }).start();
        }
    }
}

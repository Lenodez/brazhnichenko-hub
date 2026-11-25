package com.brazhnichenko.hub;

import android.Manifest;
import android.app.ActivityOptions;
import android.app.Dialog;
import android.app.assist.AssistContent;
import android.app.assist.AssistStructure;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.service.voice.VoiceInteractionSession;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import android.speech.tts.TextToSpeech;
import android.text.TextUtils;
import android.util.Base64;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Locale;

/**
 * Floating Industrial Mono Assistant Overlay powered by Android's privileged VoiceInteractionSession.
 * Appears directly over the user's current app without switching tasks or requiring SYSTEM_ALERT_WINDOW:
 *  - Immediately pre-warms the configured LLM into Hexagon NPU / Adreno GPU / RAM on open
 *  - Clearly displays live model-loading state ("Загрузка в память NPU...") vs Ready state
 *  - Captures screen context (AssistStructure text + optional Vision screenshot)
 *  - Streams live voice transcription and concise LLM answers with optional offline TTS
 */
public class HubAssistSession extends VoiceInteractionSession {
    private static final String TAG = "HubAssist";
    private static final String HUB_BASE = "http://127.0.0.1:" + LocalHubServer.PORT;

    private final Handler handler = new Handler(Looper.getMainLooper());

    // UI Elements
    private FrameLayout rootBackdrop;
    private LinearLayout cardRoot;
    private View topAccentBar;
    private TextView engineBadge;
    private LinearLayout warmupBanner;
    private TextView warmupText;
    private ImageView screenThumbView;
    private TextView chipScreenToggle;
    private TextView chipWebToggle;
    private ScrollView answerScroll;
    private LinearLayout answerSection;
    private TextView statusLineView;
    private TextView answerTextView;
    private LinearLayout answerFooter;
    private TextView answerMetaView;
    private TextView btnSpeakToggle;
    private LinearLayout voiceStrip;
    private TextView voiceTitleView;
    private View voiceLevelBar;
    private EditText inputEdit;
    private ImageView btnMic;
    private TextView btnSend;

    // Session state
    private volatile boolean sessionVisible = false;
    private volatile boolean streaming = false;
    private volatile HttpURLConnection activeStreamConn = null;
    private volatile boolean hasCapturedScreenshot = false;
    private volatile String capturedScreenshotB64 = "";
    private volatile String foregroundAppInfo = "";
    private volatile String extractedScreenText = "";
    private boolean attachScreenOn = false;
    private boolean webSearchOn = true;
    private boolean autoTtsOn = true;
    private boolean autoSendVoiceOn = true;
    private String configuredAssistModel = "";
    private volatile boolean targetModelVisionCapable = false;
    private String lastAnswerRaw = "";
    private boolean hasGeneratedInOverlay = false;

    // Voice & TTS
    private SpeechRecognizer systemRecognizer = null;
    private boolean systemListening = false;
    private TextToSpeech tts = null;
    private boolean ttsReady = false;

    private final Runnable statusPollRunnable = new Runnable() {
        @Override
        public void run() {
            if (!sessionVisible) return;
            pollEngineStatusAsync();
            handler.postDelayed(this, 500);
        }
    };

    public HubAssistSession(Context context) {
        super(context);
    }

    @Override
    public void onCreate() {
        super.onCreate();
        tts = new TextToSpeech(getContext().getApplicationContext(), st -> {
            if (st == TextToSpeech.SUCCESS) {
                ttsReady = true;
                try { tts.setLanguage(new Locale("ru", "RU")); } catch (Exception ignored) { }
            }
        });
    }

    @Override
    public View onCreateContentView() {
        final Context ctx = getContext();

        rootBackdrop = new FrameLayout(ctx);
        rootBackdrop.setBackgroundColor(Color.parseColor("#8A040406"));
        rootBackdrop.setClickable(true);
        rootBackdrop.setOnClickListener(v -> cancelAndHide());

        cardRoot = new LinearLayout(ctx);
        cardRoot.setOrientation(LinearLayout.VERTICAL);
        cardRoot.setClickable(true);
        GradientDrawable cardBg = new GradientDrawable();
        cardBg.setColor(Color.parseColor("#F8111114"));
        cardBg.setCornerRadius(dp(12));
        cardBg.setStroke(dp(1), Color.parseColor("#2E2E38"));
        cardRoot.setBackground(cardBg);
        cardRoot.setElevation(dp(16));

        int screenW = ctx.getResources().getDisplayMetrics().widthPixels;
        int maxW = dp(580);
        int cardW = screenW > (maxW + dp(28)) ? maxW : ViewGroup.LayoutParams.MATCH_PARENT;
        FrameLayout.LayoutParams cardLp = new FrameLayout.LayoutParams(cardW, ViewGroup.LayoutParams.WRAP_CONTENT);
        cardLp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        cardLp.leftMargin = dp(10);
        cardLp.rightMargin = dp(10);
        cardLp.bottomMargin = dp(10);

        if (Build.VERSION.SDK_INT >= 30) {
            rootBackdrop.setOnApplyWindowInsetsListener((v, insets) -> {
                android.graphics.Insets bars = insets.getInsets(
                        WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
                v.setPadding(bars.left, bars.top, bars.right, bars.bottom);
                return WindowInsets.CONSUMED;
            });
        }

        // Top 2dp hardware indicator bar
        topAccentBar = new View(ctx);
        GradientDrawable topBarBg = new GradientDrawable();
        topBarBg.setColor(Color.parseColor("#FF521B"));
        topBarBg.setCornerRadii(new float[]{dp(12), dp(12), dp(12), dp(12), 0, 0, 0, 0});
        topAccentBar.setBackground(topBarBg);
        cardRoot.addView(topAccentBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(3)));

        LinearLayout inner = new LinearLayout(ctx);
        inner.setOrientation(LinearLayout.VERTICAL);
        inner.setPadding(dp(13), dp(10), dp(13), dp(12));
        cardRoot.addView(inner, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // 1. HEADER ROW: Brand + Live Engine Readiness Badge + Open Full Chat + Close
        LinearLayout headRow = new LinearLayout(ctx);
        headRow.setOrientation(LinearLayout.HORIZONTAL);
        headRow.setGravity(Gravity.CENTER_VERTICAL);

        View dialDot = new View(ctx);
        dialDot.setBackground(makeRect(Color.parseColor("#FF521B"), Color.TRANSPARENT, dp(2), 0));
        LinearLayout.LayoutParams dotLp = new LinearLayout.LayoutParams(dp(8), dp(8));
        dotLp.rightMargin = dp(7);
        headRow.addView(dialDot, dotLp);

        TextView titleTv = new TextView(ctx);
        titleTv.setText("HUB ASSIST");
        titleTv.setTextColor(Color.parseColor("#F4F4F6"));
        titleTv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12.5f);
        titleTv.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        titleTv.setLetterSpacing(0.06f);
        headRow.addView(titleTv);

        engineBadge = new TextView(ctx);
        engineBadge.setText("◧ ПРОВЕРКА...");
        engineBadge.setTextSize(TypedValue.COMPLEX_UNIT_SP, 10.5f);
        engineBadge.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        engineBadge.setPadding(dp(7), dp(3), dp(7), dp(3));
        engineBadge.setSingleLine(true);
        engineBadge.setEllipsize(TextUtils.TruncateAt.END);
        styleEngineBadge("starting");
        LinearLayout.LayoutParams badgeLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        badgeLp.leftMargin = dp(8);
        badgeLp.rightMargin = dp(6);
        headRow.addView(engineBadge, badgeLp);

        TextView btnOpenFull = makeSmallButton(ctx, "В чат ↗", false);
        btnOpenFull.setOnClickListener(v -> openInFullApp());
        LinearLayout.LayoutParams openLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(32));
        openLp.rightMargin = dp(5);
        headRow.addView(btnOpenFull, openLp);

        TextView btnClose = makeSmallButton(ctx, "✕", false);
        btnClose.setMinWidth(dp(34));
        btnClose.setOnClickListener(v -> cancelAndHide());
        headRow.addView(btnClose, new LinearLayout.LayoutParams(dp(34), dp(32)));

        inner.addView(headRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // 2. MODEL WARMUP / LOADING BANNER (clearly explains model is still loading into memory!)
        warmupBanner = new LinearLayout(ctx);
        warmupBanner.setOrientation(LinearLayout.VERTICAL);
        warmupBanner.setBackground(makeRect(Color.parseColor("#23190C"), Color.parseColor("#6B4915"), dp(7), dp(1)));
        warmupBanner.setPadding(dp(10), dp(7), dp(10), dp(7));
        warmupBanner.setVisibility(View.GONE);
        LinearLayout.LayoutParams wbLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        wbLp.topMargin = dp(8);

        warmupText = new TextView(ctx);
        warmupText.setText("Модель загружается в память NPU и ещё не готова отвечать — запрос можно говорить уже сейчас");
        warmupText.setTextColor(Color.parseColor("#FCD34D"));
        warmupText.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11.5f);
        warmupBanner.addView(warmupText);

        ProgressBar warmupProg = new ProgressBar(ctx, null, android.R.attr.progressBarStyleHorizontal);
        warmupProg.setIndeterminate(true);
        LinearLayout.LayoutParams wpLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(6));
        wpLp.topMargin = dp(4);
        warmupBanner.addView(warmupProg, wpLp);

        inner.addView(warmupBanner, wbLp);

        // 3. SCREEN CONTEXT & QUICK ACTION CHIPS STRIP
        HorizontalScrollView chipsScroll = new HorizontalScrollView(ctx);
        chipsScroll.setHorizontalScrollBarEnabled(false);
        LinearLayout.LayoutParams csLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        csLp.topMargin = dp(8);

        LinearLayout chipsRow = new LinearLayout(ctx);
        chipsRow.setOrientation(LinearLayout.HORIZONTAL);
        chipsRow.setGravity(Gravity.CENTER_VERTICAL);

        screenThumbView = new ImageView(ctx);
        screenThumbView.setScaleType(ImageView.ScaleType.CENTER_CROP);
        screenThumbView.setBackground(makeRect(Color.parseColor("#18181D"), Color.parseColor("#2A2A33"), dp(5), dp(1)));
        screenThumbView.setVisibility(View.GONE);
        LinearLayout.LayoutParams thLp = new LinearLayout.LayoutParams(dp(30), dp(30));
        thLp.rightMargin = dp(6);
        chipsRow.addView(screenThumbView, thLp);

        chipScreenToggle = makeChip(ctx, "Экран");
        chipScreenToggle.setOnClickListener(v -> {
            attachScreenOn = !attachScreenOn;
            syncToggleChips();
        });
        addChipMargin(chipsRow, chipScreenToggle);

        chipWebToggle = makeChip(ctx, "Сеть");
        chipWebToggle.setOnClickListener(v -> {
            webSearchOn = !webSearchOn;
            syncToggleChips();
        });
        addChipMargin(chipsRow, chipWebToggle);

        TextView qExplain = makeChip(ctx, "Что на экране?");
        qExplain.setOnClickListener(v -> runQuickOverlayPrompt("Кратко объясни, что сейчас на моём экране, и выдели самое важное.", true));
        addChipMargin(chipsRow, qExplain);

        TextView qTranslate = makeChip(ctx, "Переведи экран");
        qTranslate.setOnClickListener(v -> runQuickOverlayPrompt("Переведи весь важный текст на этом экране на русский язык.", true));
        addChipMargin(chipsRow, qTranslate);

        TextView qSummary = makeChip(ctx, "Суть в 3 пунктах");
        qSummary.setOnClickListener(v -> runQuickOverlayPrompt("Выдели суть видимого текста на экране в 3 коротких пунктах.", true));
        addChipMargin(chipsRow, qSummary);

        chipsScroll.addView(chipsRow);
        inner.addView(chipsScroll, csLp);

        // 4. STREAMING ANSWER SECTION (compact, scrollable)
        answerSection = new LinearLayout(ctx);
        answerSection.setOrientation(LinearLayout.VERTICAL);
        answerSection.setBackground(makeRect(Color.parseColor("#0B0B0E"), Color.parseColor("#22222B"), dp(8), dp(1)));
        answerSection.setPadding(dp(11), dp(9), dp(11), dp(8));
        answerSection.setVisibility(View.GONE);
        LinearLayout.LayoutParams ansSecLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        ansSecLp.topMargin = dp(8);

        statusLineView = new TextView(ctx);
        statusLineView.setTextColor(Color.parseColor("#FF7A4D"));
        statusLineView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11.5f);
        statusLineView.setTypeface(Typeface.MONOSPACE, Typeface.NORMAL);
        statusLineView.setVisibility(View.GONE);
        answerSection.addView(statusLineView);

        answerScroll = new ScrollView(ctx) {
            @Override
            protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
                int maxH = dp(260);
                int cappedSpec = MeasureSpec.makeMeasureSpec(maxH, MeasureSpec.AT_MOST);
                super.onMeasure(widthMeasureSpec, cappedSpec);
            }
        };
        answerTextView = new TextView(ctx);
        answerTextView.setTextColor(Color.parseColor("#F4F4F6"));
        answerTextView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f);
        answerTextView.setLineSpacing(0f, 1.32f);
        answerTextView.setTextIsSelectable(true);
        answerScroll.addView(answerTextView, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        answerSection.addView(answerScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        answerFooter = new LinearLayout(ctx);
        answerFooter.setOrientation(LinearLayout.HORIZONTAL);
        answerFooter.setGravity(Gravity.CENTER_VERTICAL);
        answerFooter.setVisibility(View.GONE);
        LinearLayout.LayoutParams afLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        afLp.topMargin = dp(7);

        answerMetaView = new TextView(ctx);
        answerMetaView.setTextColor(Color.parseColor("#8E8E99"));
        answerMetaView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 10.5f);
        answerMetaView.setTypeface(Typeface.MONOSPACE, Typeface.NORMAL);
        answerFooter.addView(answerMetaView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        TextView btnCopy = makeSmallButton(ctx, "Копия", false);
        btnCopy.setOnClickListener(v -> copyAnswer());
        LinearLayout.LayoutParams cpLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(30));
        cpLp.rightMargin = dp(5);
        answerFooter.addView(btnCopy, cpLp);

        btnSpeakToggle = makeSmallButton(ctx, "Озвучить", false);
        btnSpeakToggle.setOnClickListener(v -> toggleSpeakAnswer());
        answerFooter.addView(btnSpeakToggle, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(30)));

        answerSection.addView(answerFooter, afLp);
        inner.addView(answerSection, ansSecLp);

        // 5. LIVE VOICE RECORDING BAR (shown while listening)
        voiceStrip = new LinearLayout(ctx);
        voiceStrip.setOrientation(LinearLayout.HORIZONTAL);
        voiceStrip.setGravity(Gravity.CENTER_VERTICAL);
        voiceStrip.setBackground(makeRect(Color.parseColor("#181011"), Color.parseColor("#80FF521B"), dp(8), dp(1)));
        voiceStrip.setPadding(dp(10), dp(7), dp(8), dp(7));
        voiceStrip.setVisibility(View.GONE);
        LinearLayout.LayoutParams vsLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        vsLp.topMargin = dp(8);

        View recDot = new View(ctx);
        recDot.setBackground(makeRect(Color.parseColor("#FF521B"), Color.TRANSPARENT, dp(2), 0));
        LinearLayout.LayoutParams rdLp = new LinearLayout.LayoutParams(dp(8), dp(8));
        rdLp.rightMargin = dp(8);
        voiceStrip.addView(recDot, rdLp);

        LinearLayout voiceMid = new LinearLayout(ctx);
        voiceMid.setOrientation(LinearLayout.VERTICAL);
        voiceTitleView = new TextView(ctx);
        voiceTitleView.setText("Слушаю... говорите запрос");
        voiceTitleView.setTextColor(Color.parseColor("#F4F4F6"));
        voiceTitleView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f);
        voiceMid.addView(voiceTitleView);

        FrameLayout meterTrack = new FrameLayout(ctx);
        meterTrack.setBackground(makeRect(Color.parseColor("#080809"), Color.TRANSPARENT, dp(2), 0));
        LinearLayout.LayoutParams mtLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(4));
        mtLp.topMargin = dp(4);
        voiceLevelBar = new View(ctx);
        voiceLevelBar.setBackground(makeRect(Color.parseColor("#FF521B"), Color.TRANSPARENT, dp(2), 0));
        meterTrack.addView(voiceLevelBar, new FrameLayout.LayoutParams(dp(24), ViewGroup.LayoutParams.MATCH_PARENT));
        voiceMid.addView(meterTrack, mtLp);

        LinearLayout.LayoutParams vmLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        vmLp.rightMargin = dp(8);
        voiceStrip.addView(voiceMid, vmLp);

        TextView btnVoiceDone = makeSmallButton(ctx, "Готово", true);
        btnVoiceDone.setOnClickListener(v -> finishVoiceCapture(false));
        voiceStrip.addView(btnVoiceDone, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(32)));

        inner.addView(voiceStrip, vsLp);

        // 6. INPUT & TRIGGER BAR
        LinearLayout inputRow = new LinearLayout(ctx);
        inputRow.setOrientation(LinearLayout.HORIZONTAL);
        inputRow.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams irLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        irLp.topMargin = dp(8);

        inputEdit = new EditText(ctx);
        inputEdit.setHint("Спроси ассистента...");
        inputEdit.setHintTextColor(Color.parseColor("#71717A"));
        inputEdit.setTextColor(Color.parseColor("#F4F4F6"));
        inputEdit.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14.5f);
        inputEdit.setBackground(makeRect(Color.parseColor("#080809"), Color.parseColor("#2A2A33"), dp(8), dp(1)));
        inputEdit.setPadding(dp(11), dp(9), dp(11), dp(9));
        inputEdit.setMaxLines(4);
        inputEdit.setImeOptions(EditorInfo.IME_ACTION_SEND);
        inputEdit.setOnTouchListener((v, ev) -> {
            if (ev.getAction() == MotionEvent.ACTION_DOWN) {
                enableKeyboardFocus();
            }
            return false;
        });
        inputEdit.setOnEditorActionListener((v, actionId, event) -> {
            if (actionId == EditorInfo.IME_ACTION_SEND
                    || (event != null && event.getKeyCode() == KeyEvent.KEYCODE_ENTER && event.getAction() == KeyEvent.ACTION_DOWN)) {
                submitOverlayQuery(null);
                return true;
            }
            return false;
        });
        LinearLayout.LayoutParams edLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        edLp.rightMargin = dp(6);
        inputRow.addView(inputEdit, edLp);

        btnMic = new ImageView(ctx);
        btnMic.setImageResource(R.drawable.ic_sc_voice);
        btnMic.setScaleType(ImageView.ScaleType.CENTER_INSIDE);
        btnMic.setPadding(dp(10), dp(10), dp(10), dp(10));
        btnMic.setBackground(makeRect(Color.parseColor("#18181D"), Color.parseColor("#2A2A33"), dp(8), dp(1)));
        btnMic.setContentDescription("Голосовой ввод");
        btnMic.setOnClickListener(v -> toggleVoiceCapture());
        LinearLayout.LayoutParams micLp = new LinearLayout.LayoutParams(dp(42), dp(42));
        micLp.rightMargin = dp(6);
        inputRow.addView(btnMic, micLp);

        btnSend = new TextView(ctx);
        btnSend.setText("↑");
        btnSend.setGravity(Gravity.CENTER);
        btnSend.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f);
        btnSend.setTypeface(Typeface.DEFAULT_BOLD);
        btnSend.setTextColor(Color.WHITE);
        btnSend.setBackground(makeRect(Color.parseColor("#FF521B"), Color.TRANSPARENT, dp(8), 0));
        btnSend.setOnClickListener(v -> {
            if (streaming) {
                abortActiveStream();
            } else {
                submitOverlayQuery(null);
            }
        });
        inputRow.addView(btnSend, new LinearLayout.LayoutParams(dp(42), dp(42)));

        inner.addView(inputRow, irLp);

        rootBackdrop.addView(cardRoot, cardLp);
        return rootBackdrop;
    }

    @Override
    public void onPrepareShow(Bundle args, int showFlags) {
        super.onPrepareShow(args, showFlags);
        SharedPreferences p = getContext().getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
        boolean overlayEnabled = p.getBoolean("assist_overlay_mode", true);
        setUiEnabled(overlayEnabled);
    }

    @Override
    public void onShow(Bundle args, int showFlags) {
        super.onShow(args, showFlags);
        Log.i(TAG, "onShow invoked (showFlags=" + showFlags + ")");

        // Ensure HubForegroundService (LocalHubServer :17901) is running even on cold boot
        try {
            Intent svc = new Intent(getContext(), HubForegroundService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                getContext().startForegroundService(svc);
            } else {
                getContext().startService(svc);
            }
        } catch (Exception ignored) { }

        SharedPreferences p = getContext().getSharedPreferences("hub_prefs", Context.MODE_PRIVATE);
        boolean overlayMode = p.getBoolean("assist_overlay_mode", true);
        attachScreenOn = p.getBoolean("assist_attach_screen", false);
        webSearchOn = p.getBoolean("assist_web_search", true);
        autoTtsOn = p.getBoolean("assist_auto_tts", true);
        boolean autoVoice = p.getBoolean("assist_auto_voice", true);
        autoSendVoiceOn = true;
        configuredAssistModel = p.getString("assist_model", "").trim();

        hasCapturedScreenshot = false;
        capturedScreenshotB64 = "";
        foregroundAppInfo = "";
        extractedScreenText = "";
        lastAnswerRaw = "";
        hasGeneratedInOverlay = false;

        try {
            File f = getAssistScreenFile(getContext());
            if (f.exists()) f.delete();
        } catch (Exception ignored) { }

        if (!overlayMode) {
            // Classic full-screen fallback if user turned off overlay toggle
            handler.postDelayed(() -> launchFullMainActivity(hasCapturedScreenshot, false), 150);
            return;
        }

        sessionVisible = true;
        configureDialogWindow();
        resetOverlayUiForNewInvocation();

        // Immediately trigger non-blocking LLM pre-warmup in background while user speaks/types!
        triggerBackgroundWarmup();
        handler.removeCallbacks(statusPollRunnable);
        handler.post(statusPollRunnable);

        if (autoVoice) {
            handler.postDelayed(this::startVoiceCapture, 140);
        }
    }

    @Override
    public void onHandleAssist(Bundle data, AssistStructure structure, AssistContent content) {
        super.onHandleAssist(data, structure, content);
        extractAssistStructureContext(structure);
    }

    @Override
    public void onHandleScreenshot(final Bitmap screenshot) {
        super.onHandleScreenshot(screenshot);
        if (screenshot == null) return;

        new Thread(() -> {
            try {
                File out = getAssistScreenFile(getContext());
                int w = screenshot.getWidth();
                int h = screenshot.getHeight();
                int maxDim = 1024;
                Bitmap scaled = screenshot;
                if (Math.max(w, h) > maxDim) {
                    double r = maxDim / (double) Math.max(w, h);
                    scaled = Bitmap.createScaledBitmap(screenshot, Math.max(1, (int) (w * r)), Math.max(1, (int) (h * r)), true);
                }
                ByteArrayOutputStream baos = new ByteArrayOutputStream();
                scaled.compress(Bitmap.CompressFormat.JPEG, 84, baos);
                byte[] jpg = baos.toByteArray();
                try (FileOutputStream fos = new FileOutputStream(out)) {
                    fos.write(jpg);
                }
                capturedScreenshotB64 = "data:image/jpeg;base64," + Base64.encodeToString(jpg, Base64.NO_WRAP);
                hasCapturedScreenshot = true;

                final Bitmap thumbBmp = Bitmap.createScaledBitmap(scaled, dp(60), dp(60), true);
                if (scaled != screenshot) scaled.recycle();

                handler.post(() -> {
                    if (screenThumbView != null) {
                        screenThumbView.setImageBitmap(thumbBmp);
                        screenThumbView.setVisibility(View.VISIBLE);
                    }
                    syncToggleChips();
                });
            } catch (Exception ignored) { }
        }, "AssistScreenEncode").start();
    }

    private void extractAssistStructureContext(final AssistStructure structure) {
        if (structure == null) return;
        new Thread(() -> {
            try {
                ComponentName comp = structure.getActivityComponent();
                String pkg = comp != null ? comp.getPackageName() : "";
                if (pkg.equals(getContext().getPackageName())) return;

                String appLabel = pkg;
                try {
                    PackageManager pm = getContext().getPackageManager();
                    CharSequence cs = pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0));
                    if (cs != null && cs.length() > 0) appLabel = cs.toString() + " (" + pkg + ")";
                } catch (Exception ignored) { }
                foregroundAppInfo = appLabel;

                StringBuilder sb = new StringBuilder();
                int winCount = structure.getWindowNodeCount();
                for (int i = 0; i < winCount && sb.length() < 1600; i++) {
                    AssistStructure.WindowNode wn = structure.getWindowNodeAt(i);
                    if (wn != null && wn.getRootViewNode() != null) {
                        collectVisibleText(wn.getRootViewNode(), sb);
                    }
                }
                extractedScreenText = sb.toString().trim();
                handler.post(this::syncToggleChips);
            } catch (Exception ignored) { }
        }, "AssistStructExtract").start();
    }

    private void collectVisibleText(AssistStructure.ViewNode node, StringBuilder out) {
        if (node == null || out.length() >= 1600) return;
        if (node.getVisibility() != View.VISIBLE) return;
        CharSequence t = node.getText();
        if (t != null) {
            String s = t.toString().trim();
            if (s.length() >= 2 && s.length() <= 400) {
                out.append(s).append('\n');
            }
        }
        int count = node.getChildCount();
        for (int i = 0; i < count && out.length() < 1600; i++) {
            collectVisibleText(node.getChildAt(i), out);
        }
    }

    private void configureDialogWindow() {
        try {
            Dialog d = getWindow();
            Window w = d != null ? d.getWindow() : null;
            if (w != null) {
                w.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
                w.setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
                // Keep keyboard closed initially; enable focus on EditText touch
                w.addFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_ALT_FOCUSABLE_IM);
                w.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE | WindowManager.LayoutParams.SOFT_INPUT_STATE_HIDDEN);
            }
        } catch (Exception ignored) { }
    }

    private void enableKeyboardFocus() {
        try {
            Dialog d = getWindow();
            Window w = d != null ? d.getWindow() : null;
            if (w != null) {
                w.clearFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_ALT_FOCUSABLE_IM);
                w.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE | WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE);
            }
            if (inputEdit != null) {
                inputEdit.requestFocus();
                InputMethodManager imm = (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
                if (imm != null) imm.showSoftInput(inputEdit, InputMethodManager.SHOW_IMPLICIT);
            }
        } catch (Exception ignored) { }
    }

    private void resetOverlayUiForNewInvocation() {
        if (cardRoot == null) return;
        if (inputEdit != null) inputEdit.setText("");
        if (answerSection != null) answerSection.setVisibility(View.GONE);
        if (answerFooter != null) answerFooter.setVisibility(View.GONE);
        if (statusLineView != null) statusLineView.setVisibility(View.GONE);
        if (answerTextView != null) answerTextView.setText("");
        if (screenThumbView != null) screenThumbView.setVisibility(View.GONE);
        if (voiceStrip != null) voiceStrip.setVisibility(View.GONE);
        setSendButtonStreaming(false);
        syncToggleChips();
    }

    private void syncToggleChips() {
        if (chipScreenToggle != null) {
            boolean hasAnyScreen = hasCapturedScreenshot || !extractedScreenText.isEmpty();
            chipScreenToggle.setText(attachScreenOn
                    ? (hasCapturedScreenshot && targetModelVisionCapable ? "■ Экран (Vision)" : "■ Экран")
                    : "Экран");
            styleChip(chipScreenToggle, attachScreenOn && hasAnyScreen);
        }
        if (chipWebToggle != null) {
            chipWebToggle.setText(webSearchOn ? "■ Сеть" : "Сеть");
            styleChip(chipWebToggle, webSearchOn);
        }
    }

    // ==================== BACKGROUND WARMUP & LIVE LOADING TELEMETRY ====================

    private void triggerBackgroundWarmup() {
        new Thread(() -> {
            for (int attempt = 0; attempt < 15 && sessionVisible; attempt++) {
                try {
                    HttpURLConnection c = (HttpURLConnection) new URL(HUB_BASE + "/api/llm/warmup").openConnection();
                    c.setRequestMethod("POST");
                    c.setDoOutput(true);
                    c.setConnectTimeout(600);
                    c.setReadTimeout(1200);
                    c.setRequestProperty("Content-Type", "application/json");
                    JSONObject body = new JSONObject();
                    if (!configuredAssistModel.isEmpty()) body.put("modelFile", configuredAssistModel);
                    byte[] b = body.toString().getBytes(StandardCharsets.UTF_8);
                    try (OutputStream os = c.getOutputStream()) { os.write(b); }
                    int rc = c.getResponseCode();
                    c.disconnect();
                    if (rc == 200) return;
                } catch (Exception ignored) { }
                try { Thread.sleep(200); } catch (InterruptedException e) { return; }
            }
        }, "OverlayWarmupTrigger").start();
    }

    private void pollEngineStatusAsync() {
        new Thread(() -> {
            try {
                HttpURLConnection c = (HttpURLConnection) new URL(HUB_BASE + "/api/status").openConnection();
                c.setConnectTimeout(500);
                c.setReadTimeout(700);
                if (c.getResponseCode() != 200) {
                    c.disconnect();
                    return;
                }
                StringBuilder sb = new StringBuilder();
                try (BufferedReader br = new BufferedReader(new InputStreamReader(c.getInputStream(), StandardCharsets.UTF_8))) {
                    String line;
                    while ((line = br.readLine()) != null) sb.append(line);
                }
                c.disconnect();

                JSONObject st = new JSONObject(sb.toString());
                final boolean running = st.optBoolean("llmRunning", false);
                final boolean starting = st.optBoolean("llmStarting", false);
                final String backend = st.optString("activeBackend", "npu").toUpperCase(Locale.ROOT);
                final String runningModel = st.optString("runningModel", "");
                final String startingModel = st.optString("startingModel", "");
                final String activeLlm = st.optString("activeLlm", "");
                final double elapsed = st.optDouble("llmLoadElapsedSec", 0.0);

                String target = !configuredAssistModel.isEmpty() ? configuredAssistModel : activeLlm;
                boolean vis = false;
                JSONArray avail = st.optJSONArray("availableLlms");
                if (avail != null) {
                    for (int i = 0; i < avail.length(); i++) {
                        JSONObject m = avail.getJSONObject(i);
                        if (target.equalsIgnoreCase(m.optString("fileName"))) {
                            vis = m.optBoolean("hasVision", false);
                            break;
                        }
                    }
                }
                targetModelVisionCapable = vis;

                handler.post(() -> {
                    if (!sessionVisible || engineBadge == null) return;
                    if (avail == null || avail.length() == 0) {
                        engineBadge.setText("▲ НЕТ МОДЕЛИ");
                        styleEngineBadge("error");
                        if (warmupBanner != null) {
                            warmupBanner.setVisibility(View.VISIBLE);
                            warmupText.setText("Модель не скачана — нажми «В чат ↗» и скачай Qwen 3 4B или Gemma 3 Vision");
                        }
                        return;
                    }

                    boolean targetReady = running && (target.isEmpty() || target.equalsIgnoreCase(runningModel));
                    if (targetReady) {
                        String shortM = shortModel(runningModel);
                        engineBadge.setText("■ ГОТОВА · " + backend + (shortM.isEmpty() ? "" : " · " + shortM));
                        styleEngineBadge("ready");
                        if (warmupBanner != null) warmupBanner.setVisibility(View.GONE);
                        setTopBarColor(streaming ? "#FF521B" : "#34D399");
                    } else if (starting) {
                        String shortM = shortModel(startingModel.isEmpty() ? target : startingModel);
                        String secStr = elapsed > 0 ? (" · " + String.format(Locale.US, "%.1fс", elapsed)) : "";
                        engineBadge.setText("◧ ПРОГРЕВ NPU" + secStr);
                        styleEngineBadge("starting");
                        if (warmupBanner != null) {
                            warmupBanner.setVisibility(View.VISIBLE);
                            warmupText.setText("Модель " + shortM + " загружается в память ("
                                    + String.format(Locale.US, "%.1f с", elapsed)
                                    + ") и пока не готова отвечать — говорите или пишите, ответ стартует сразу после загрузки");
                        }
                        setTopBarColor("#F59E0B");
                    } else {
                        engineBadge.setText("◧ ПОДГОТОВКА NPU...");
                        styleEngineBadge("starting");
                    }
                    syncToggleChips();
                });
            } catch (Exception ignored) { }
        }, "OverlayStatusPoll").start();
    }

    private static String shortModel(String fileName) {
        if (fileName == null) return "";
        String s = fileName.replace(".gguf", "")
                .replace("-Instruct-2507", "")
                .replace("-Instruct", "")
                .replace("-Q4_K_M", "")
                .replace("-it", "");
        return s.length() > 16 ? s.substring(0, 16) + "…" : s;
    }

    // ==================== VOICE INPUT (OFFLINE SHERPA-ONNX OR IN-CARD SPEECHRECOGNIZER) ====================

    private void toggleVoiceCapture() {
        if (OfflineVoiceEngine.isRecording() || systemListening) {
            finishVoiceCapture(false);
        } else {
            startVoiceCapture();
        }
    }

    private void startVoiceCapture() {
        Context ctx = getContext();
        if (ctx.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            if (voiceStrip != null) {
                voiceStrip.setVisibility(View.VISIBLE);
                voiceTitleView.setText("Разреши микрофон в приложении («В чат ↗») или напиши текстом");
            }
            return;
        }

        if (tts != null) {
            try { tts.stop(); } catch (Exception ignored) { }
        }

        String offlineStt = OfflineVoiceEngine.getActiveInstalledModel(ctx);
        if (offlineStt != null && !offlineStt.isEmpty()) {
            startSherpaOfflineCapture();
            return;
        }
        startInCardSystemRecognizer();
    }

    private void startSherpaOfflineCapture() {
        if (voiceStrip != null) {
            voiceStrip.setVisibility(View.VISIBLE);
            voiceTitleView.setText("Слушаю офлайн (Sherpa STT)...");
        }
        styleMicActive(true);
        OfflineVoiceEngine.startCaptureAndTranscribe(getContext(), new OfflineVoiceEngine.VoiceCallback() {
            @Override
            public void onState(String state, String detail, int levelPct, double seconds) {
                handler.post(() -> {
                    if (!sessionVisible) return;
                    if ("recording".equals(state)) {
                        voiceStrip.setVisibility(View.VISIBLE);
                        voiceTitleView.setText("Слушаю офлайн · " + String.format(Locale.US, "%.1fс", seconds));
                        updateVoiceMeter(levelPct);
                    } else if ("transcribing".equals(state)) {
                        voiceTitleView.setText("Распознаю речь офлайн...");
                        updateVoiceMeter(100);
                    } else {
                        voiceStrip.setVisibility(View.GONE);
                        styleMicActive(false);
                    }
                });
            }

            @Override
            public void onResult(String text, long decodeMs) {
                handler.post(() -> {
                    voiceStrip.setVisibility(View.GONE);
                    styleMicActive(false);
                    if (text != null && !text.trim().isEmpty()) {
                        inputEdit.setText(text.trim());
                        inputEdit.setSelection(inputEdit.getText().length());
                        if (autoSendVoiceOn) submitOverlayQuery(null);
                    }
                });
            }

            @Override
            public void onError(String message) {
                handler.post(() -> {
                    voiceStrip.setVisibility(View.GONE);
                    styleMicActive(false);
                    Toast.makeText(getContext(), message, Toast.LENGTH_SHORT).show();
                });
            }
        });
    }

    private void startInCardSystemRecognizer() {
        try {
            destroySystemRecognizer();
            if (!SpeechRecognizer.isRecognitionAvailable(getContext())) {
                if (voiceStrip != null) {
                    voiceStrip.setVisibility(View.VISIBLE);
                    voiceTitleView.setText("Скачай офлайн-голос GigaAM во вкладке Система или введи вопрос текстом");
                }
                return;
            }
            systemRecognizer = SpeechRecognizer.createSpeechRecognizer(getContext());
            systemListening = true;
            styleMicActive(true);
            if (voiceStrip != null) {
                voiceStrip.setVisibility(View.VISIBLE);
                voiceTitleView.setText("Слушаю... говорите запрос");
                updateVoiceMeter(12);
            }

            systemRecognizer.setRecognitionListener(new RecognitionListener() {
                @Override public void onReadyForSpeech(Bundle params) { }
                @Override public void onBeginningOfSpeech() { }
                @Override
                public void onRmsChanged(float rmsdB) {
                    int pct = Math.max(8, Math.min(100, (int) ((rmsdB + 2f) * 9f)));
                    updateVoiceMeter(pct);
                }
                @Override public void onBufferReceived(byte[] buffer) { }
                @Override
                public void onEndOfSpeech() {
                    if (voiceTitleView != null) voiceTitleView.setText("Обработка речи...");
                }
                @Override
                public void onError(int error) {
                    systemListening = false;
                    styleMicActive(false);
                    if (voiceStrip != null) voiceStrip.setVisibility(View.GONE);
                }
                @Override
                public void onResults(Bundle results) {
                    systemListening = false;
                    styleMicActive(false);
                    if (voiceStrip != null) voiceStrip.setVisibility(View.GONE);
                    ArrayList<String> list = results != null ? results.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION) : null;
                    if (list != null && !list.isEmpty() && !list.get(0).trim().isEmpty()) {
                        String spoken = list.get(0).trim();
                        inputEdit.setText(spoken);
                        inputEdit.setSelection(spoken.length());
                        if (autoSendVoiceOn) submitOverlayQuery(null);
                    }
                }
                @Override
                public void onPartialResults(Bundle partialResults) {
                    ArrayList<String> list = partialResults != null ? partialResults.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION) : null;
                    if (list != null && !list.isEmpty() && !list.get(0).trim().isEmpty()) {
                        String part = list.get(0).trim();
                        inputEdit.setText(part);
                        inputEdit.setSelection(part.length());
                    }
                }
                @Override public void onEvent(int eventType, Bundle params) { }
            });

            Intent recIntent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
            recIntent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
            recIntent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, "ru-RU");
            recIntent.putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true);
            recIntent.putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true);
            systemRecognizer.startListening(recIntent);
        } catch (Exception ex) {
            systemListening = false;
            styleMicActive(false);
            if (voiceStrip != null) voiceStrip.setVisibility(View.GONE);
        }
    }

    private void finishVoiceCapture(boolean cancel) {
        if (OfflineVoiceEngine.isRecording()) {
            OfflineVoiceEngine.stopRecording(cancel);
        }
        if (systemRecognizer != null) {
            try {
                if (cancel) systemRecognizer.cancel();
                else systemRecognizer.stopListening();
            } catch (Exception ignored) { }
            if (cancel) destroySystemRecognizer();
        }
        systemListening = false;
        styleMicActive(false);
        if (cancel && voiceStrip != null) voiceStrip.setVisibility(View.GONE);
    }

    private void destroySystemRecognizer() {
        if (systemRecognizer != null) {
            try { systemRecognizer.destroy(); } catch (Exception ignored) { }
            systemRecognizer = null;
        }
        systemListening = false;
    }

    private void updateVoiceMeter(int pct) {
        if (voiceLevelBar == null || voiceLevelBar.getParent() == null) return;
        View parent = (View) voiceLevelBar.getParent();
        int totalW = parent.getWidth();
        if (totalW <= 0) totalW = dp(180);
        ViewGroup.LayoutParams lp = voiceLevelBar.getLayoutParams();
        lp.width = Math.max(dp(12), (totalW * Math.max(6, Math.min(100, pct))) / 100);
        voiceLevelBar.setLayoutParams(lp);
    }

    // ==================== STREAMING QUERY & RESPONSE ====================

    private void runQuickOverlayPrompt(String prompt, boolean forceScreen) {
        if (forceScreen && (hasCapturedScreenshot || !extractedScreenText.isEmpty())) {
            attachScreenOn = true;
            syncToggleChips();
        }
        submitOverlayQuery(prompt);
    }

    private void submitOverlayQuery(String overridePrompt) {
        finishVoiceCapture(true);
        if (tts != null) {
            try { tts.stop(); } catch (Exception ignored) { }
        }
        String text = overridePrompt != null ? overridePrompt.trim()
                : (inputEdit != null ? inputEdit.getText().toString().trim() : "");
        if (text.isEmpty() && !attachScreenOn) return;

        if (overridePrompt == null && inputEdit != null) {
            inputEdit.setText("");
        }
        // Hide soft keyboard cleanly so answer area has full space
        try {
            InputMethodManager imm = (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
            if (imm != null && inputEdit != null) imm.hideSoftInputFromWindow(inputEdit.getWindowToken(), 0);
        } catch (Exception ignored) { }

        answerSection.setVisibility(View.VISIBLE);
        answerFooter.setVisibility(View.GONE);
        statusLineView.setVisibility(View.VISIBLE);
        statusLineView.setText("Подготовка запроса...");
        answerTextView.setText("");
        lastAnswerRaw = "";
        streaming = true;
        setSendButtonStreaming(true);
        setTopBarColor("#FF521B");

        final String sendText = text;
        final boolean useScreen = attachScreenOn;
        final boolean useWeb = webSearchOn;

        new Thread(() -> {
            HttpURLConnection conn = null;
            try {
                JSONObject body = new JSONObject();
                body.put("sessionId", "assist");
                body.put("assistantMode", true);
                body.put("personaId", "assistant");
                body.put("temperature", 0.55);
                body.put("message", sendText);
                body.put("webSearch", useWeb);
                if (!configuredAssistModel.isEmpty()) {
                    body.put("modelFile", configuredAssistModel);
                    body.put("temporaryModel", true);
                }

                if (useScreen) {
                    StringBuilder sc = new StringBuilder();
                    if (!foregroundAppInfo.isEmpty()) {
                        sc.append("Открытое приложение: ").append(foregroundAppInfo).append('\n');
                    }
                    if (!extractedScreenText.isEmpty()) {
                        sc.append("Видимый текст интерфейса:\n").append(extractedScreenText);
                    }
                    if (sc.length() > 0) {
                        body.put("screenContext", sc.toString());
                    }
                    if (hasCapturedScreenshot && targetModelVisionCapable && !capturedScreenshotB64.isEmpty()) {
                        body.put("imageBase64", capturedScreenshotB64);
                    }
                }

                conn = (HttpURLConnection) new URL(HUB_BASE + "/api/chat/stream").openConnection();
                activeStreamConn = conn;
                conn.setRequestMethod("POST");
                conn.setDoOutput(true);
                conn.setConnectTimeout(10000);
                conn.setReadTimeout(0);
                conn.setRequestProperty("Content-Type", "application/json");
                byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
                try (OutputStream os = conn.getOutputStream()) { os.write(bytes); }

                if (conn.getResponseCode() != 200) {
                    throw new RuntimeException("HTTP " + conn.getResponseCode());
                }

                final StringBuilder acc = new StringBuilder();
                long lastUiUpdate = 0L;

                try (BufferedReader br = new BufferedReader(new InputStreamReader(conn.getInputStream(), StandardCharsets.UTF_8))) {
                    String line;
                    while (sessionVisible && streaming && (line = br.readLine()) != null) {
                        if (!line.startsWith("data:")) continue;
                        String payload = line.substring(5).trim();
                        if (payload.isEmpty()) continue;
                        JSONObject ev = new JSONObject(payload);
                        String type = ev.optString("type", "");

                        if ("status".equals(type)) {
                            final String stMsg = ev.optString("data", "");
                            handler.post(() -> {
                                statusLineView.setVisibility(View.VISIBLE);
                                statusLineView.setText(stMsg);
                            });
                        } else if ("think".equals(type)) {
                            handler.post(() -> {
                                if (acc.length() == 0) {
                                    statusLineView.setVisibility(View.VISIBLE);
                                    statusLineView.setText("Ассистент обдумывает ответ...");
                                }
                            });
                        } else if ("token".equals(type)) {
                            acc.append(ev.optString("data", ""));
                            long now = System.currentTimeMillis();
                            if (now - lastUiUpdate > 65) {
                                lastUiUpdate = now;
                                final String clean = cleanMarkdownLight(acc.toString());
                                handler.post(() -> {
                                    statusLineView.setVisibility(View.GONE);
                                    answerTextView.setText(clean);
                                    answerScroll.post(() -> answerScroll.fullScroll(View.FOCUS_DOWN));
                                });
                            }
                        } else if ("error".equals(type) || "vision_missing".equals(type)) {
                            final String errMsg = ev.optString("data", "Ошибка");
                            handler.post(() -> {
                                statusLineView.setVisibility(View.GONE);
                                answerTextView.setText(errMsg);
                            });
                        } else if ("done".equals(type)) {
                            JSONObject d = ev.optJSONObject("data");
                            final String finalRaw = acc.toString().trim();
                            final String finalClean = cleanMarkdownLight(finalRaw);
                            final String meta = d != null
                                    ? (d.optDouble("tokPerSec", 0) + " t/s · " + d.optInt("tokens", 0) + " ток · " + d.optString("backend", "NPU").toUpperCase(Locale.ROOT))
                                    : "Локальная LLM";
                            lastAnswerRaw = finalRaw;
                            hasGeneratedInOverlay = true;
                            handler.post(() -> {
                                statusLineView.setVisibility(View.GONE);
                                answerTextView.setText(finalClean);
                                answerMetaView.setText(meta);
                                answerFooter.setVisibility(View.VISIBLE);
                                if (autoTtsOn && !finalRaw.isEmpty()) {
                                    speakTextNow(finalRaw);
                                }
                            });
                        }
                    }
                }
            } catch (Exception ex) {
                if (streaming && sessionVisible) {
                    final String msg = ex.getMessage() != null ? ex.getMessage() : "Сбой соединения с локальной LLM";
                    handler.post(() -> {
                        statusLineView.setVisibility(View.GONE);
                        if (answerTextView.getText().length() == 0) {
                            answerTextView.setText("Ошибка: " + msg);
                        }
                    });
                }
            } finally {
                activeStreamConn = null;
                streaming = false;
                if (conn != null) try { conn.disconnect(); } catch (Exception ignored) { }
                handler.post(() -> {
                    setSendButtonStreaming(false);
                    pollEngineStatusAsync();
                });
            }
        }, "OverlayChatStream").start();
    }

    private void abortActiveStream() {
        streaming = false;
        HttpURLConnection c = activeStreamConn;
        if (c != null) {
            new Thread(() -> {
                try { c.disconnect(); } catch (Exception ignored) { }
            }).start();
        }
        setSendButtonStreaming(false);
        if (statusLineView != null) statusLineView.setVisibility(View.GONE);
    }

    private static String cleanMarkdownLight(String s) {
        if (s == null) return "";
        return s.replaceAll("(?m)^#{1,4}\\s+", "")
                .replace("**", "")
                .replace("__", "")
                .trim();
    }

    private void copyAnswer() {
        if (lastAnswerRaw.isEmpty()) return;
        ClipboardManager cm = (ClipboardManager) getContext().getSystemService(Context.CLIPBOARD_SERVICE);
        if (cm != null) {
            cm.setPrimaryClip(ClipData.newPlainText("Hub Assist", lastAnswerRaw));
            Toast.makeText(getContext(), "Ответ скопирован", Toast.LENGTH_SHORT).show();
        }
    }

    private void toggleSpeakAnswer() {
        if (!ttsReady || tts == null || lastAnswerRaw.isEmpty()) return;
        if (tts.isSpeaking()) {
            tts.stop();
            btnSpeakToggle.setText("Озвучить");
        } else {
            speakTextNow(lastAnswerRaw);
            btnSpeakToggle.setText("Тишина");
        }
    }

    private void speakTextNow(String raw) {
        if (!ttsReady || tts == null) return;
        String clean = raw.replaceAll("```[\\s\\S]*?```", " блок кода ")
                .replaceAll("[`*_~#>|]", " ")
                .replaceAll("\\s+", " ")
                .trim();
        if (clean.isEmpty()) return;
        try {
            tts.speak(clean, TextToSpeech.QUEUE_FLUSH, null, "assist_tts_" + System.currentTimeMillis());
        } catch (Exception ignored) { }
    }

    // ==================== OPEN FULL APP OR HIDE OVERLAY ====================

    private void openInFullApp() {
        abortActiveStream();
        finishVoiceCapture(true);
        launchFullMainActivity(hasCapturedScreenshot, hasGeneratedInOverlay);
    }

    private void launchFullMainActivity(boolean hasShot, boolean openAssistSession) {
        Intent intent = new Intent(getContext(), MainActivity.class);
        if (openAssistSession) {
            intent.setAction(MainActivity.ACTION_OPEN_ASSIST_SESSION);
        } else {
            intent.setAction(MainActivity.ACTION_ASSIST_LAUNCH);
            intent.putExtra(MainActivity.EXTRA_HAS_SCREENSHOT, hasShot);
        }
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        boolean ok = false;
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                startAssistantActivity(intent, ActivityOptions.makeBasic().toBundle());
            } else {
                startAssistantActivity(intent);
            }
            ok = true;
        } catch (Exception ignored) { }
        if (!ok) {
            try { getContext().startActivity(intent); } catch (Exception ignored) { }
        }
        handler.postDelayed(this::cancelAndHide, 250);
    }

    private void cancelAndHide() {
        sessionVisible = false;
        handler.removeCallbacks(statusPollRunnable);
        abortActiveStream();
        finishVoiceCapture(true);
        if (tts != null) {
            try { tts.stop(); } catch (Exception ignored) { }
        }
        try { hide(); } catch (Exception ignored) { }
    }

    @Override
    public void onHide() {
        sessionVisible = false;
        handler.removeCallbacks(statusPollRunnable);
        abortActiveStream();
        finishVoiceCapture(true);
        if (tts != null) {
            try { tts.stop(); } catch (Exception ignored) { }
        }
        super.onHide();
    }

    @Override
    public void onDestroy() {
        destroySystemRecognizer();
        if (tts != null) {
            try { tts.stop(); tts.shutdown(); } catch (Exception ignored) { }
        }
        super.onDestroy();
    }

    public static File getAssistScreenFile(Context ctx) {
        return new File(ctx.getCacheDir(), "assist_screen.jpg");
    }

    // ==================== STYLING HELPERS ====================

    private int dp(int v) {
        return Math.round(TypedValue.applyDimension(
                TypedValue.COMPLEX_UNIT_DIP, v, getContext().getResources().getDisplayMetrics()));
    }

    private GradientDrawable makeRect(int fill, int strokeColor, int radiusPx, int strokePx) {
        GradientDrawable gd = new GradientDrawable();
        gd.setColor(fill);
        gd.setCornerRadius(radiusPx);
        if (strokePx > 0) gd.setStroke(strokePx, strokeColor);
        return gd;
    }

    private TextView makeSmallButton(Context ctx, String label, boolean primary) {
        TextView tv = new TextView(ctx);
        tv.setText(label);
        tv.setGravity(Gravity.CENTER);
        tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11.5f);
        tv.setTypeface(Typeface.DEFAULT_BOLD);
        tv.setPadding(dp(10), 0, dp(10), 0);
        if (primary) {
            tv.setTextColor(Color.parseColor("#09090B"));
            tv.setBackground(makeRect(Color.parseColor("#F2F2F5"), Color.TRANSPARENT, dp(6), 0));
        } else {
            tv.setTextColor(Color.parseColor("#E4E4E7"));
            tv.setBackground(makeRect(Color.parseColor("#18181D"), Color.parseColor("#2A2A33"), dp(6), dp(1)));
        }
        return tv;
    }

    private TextView makeChip(Context ctx, String label) {
        TextView tv = new TextView(ctx);
        tv.setText(label);
        tv.setGravity(Gravity.CENTER);
        tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11.5f);
        tv.setTypeface(Typeface.DEFAULT_BOLD);
        tv.setPadding(dp(10), dp(5), dp(10), dp(5));
        styleChip(tv, false);
        return tv;
    }

    private void styleChip(TextView tv, boolean active) {
        if (active) {
            tv.setTextColor(Color.parseColor("#FF7A4D"));
            tv.setBackground(makeRect(Color.parseColor("#241410"), Color.parseColor("#FF521B"), dp(6), dp(1)));
        } else {
            tv.setTextColor(Color.parseColor("#D4D4D8"));
            tv.setBackground(makeRect(Color.parseColor("#18181D"), Color.parseColor("#2A2A33"), dp(6), dp(1)));
        }
    }

    private void addChipMargin(LinearLayout row, View chip) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.rightMargin = dp(6);
        row.addView(chip, lp);
    }

    private void styleEngineBadge(String state) {
        if (engineBadge == null) return;
        if ("ready".equals(state)) {
            engineBadge.setTextColor(Color.parseColor("#6EE7B7"));
            engineBadge.setBackground(makeRect(Color.parseColor("#0E2219"), Color.parseColor("#215E42"), dp(5), dp(1)));
        } else if ("error".equals(state)) {
            engineBadge.setTextColor(Color.parseColor("#FCA5A5"));
            engineBadge.setBackground(makeRect(Color.parseColor("#2A1216"), Color.parseColor("#6E232C"), dp(5), dp(1)));
        } else {
            engineBadge.setTextColor(Color.parseColor("#FCD34D"));
            engineBadge.setBackground(makeRect(Color.parseColor("#281D0E"), Color.parseColor("#6B4915"), dp(5), dp(1)));
        }
    }

    private void setTopBarColor(String hex) {
        if (topAccentBar == null) return;
        GradientDrawable gd = new GradientDrawable();
        gd.setColor(Color.parseColor(hex));
        gd.setCornerRadii(new float[]{dp(12), dp(12), dp(12), dp(12), 0, 0, 0, 0});
        topAccentBar.setBackground(gd);
    }

    private void styleMicActive(boolean active) {
        if (btnMic == null) return;
        if (active) {
            btnMic.setBackground(makeRect(Color.parseColor("#281410"), Color.parseColor("#FF521B"), dp(8), dp(1)));
        } else {
            btnMic.setBackground(makeRect(Color.parseColor("#18181D"), Color.parseColor("#2A2A33"), dp(8), dp(1)));
        }
    }

    private void setSendButtonStreaming(boolean isStream) {
        if (btnSend == null) return;
        if (isStream) {
            btnSend.setText("■");
            btnSend.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f);
            btnSend.setBackground(makeRect(Color.parseColor("#EF4444"), Color.TRANSPARENT, dp(8), 0));
        } else {
            btnSend.setText("↑");
            btnSend.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f);
            btnSend.setBackground(makeRect(Color.parseColor("#FF521B"), Color.TRANSPARENT, dp(8), 0));
        }
    }
}

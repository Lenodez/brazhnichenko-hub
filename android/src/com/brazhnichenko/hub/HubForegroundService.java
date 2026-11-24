package com.brazhnichenko.hub;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;

public class HubForegroundService extends Service {
    public static final String CHANNEL_ID = "brazhnichenko_hub_llm";
    public static final String ACTION_START_LLM = "com.brazhnichenko.hub.ACTION_START_LLM";
    public static final String ACTION_STOP_LLM = "com.brazhnichenko.hub.ACTION_STOP_LLM";
    private static final int NOTIF_ID = 17901;

    private static volatile boolean sLlmRunning = false;
    private static volatile boolean sLlmStarting = false;
    private static volatile String sWidgetStatus = "Brazhnichenko Hub · Офлайн";
    private static volatile String sShortModelLabel = "";

    private LocalHubServer server;
    private LlamaProcessManager llamaManager;
    private ModelManager modelManager;
    private volatile boolean watchdogAlive = true;

    public static boolean isLlmRunningStatic() { return sLlmRunning; }
    public static boolean isLlmStartingStatic() { return sLlmStarting; }
    public static String getWidgetStatusText() { return sWidgetStatus; }
    public static String getShortModelLabel() { return sShortModelLabel; }

    @Override
    public void onCreate() {
        super.onCreate();
        createNotificationChannel();
        Notification initialNotif = buildNotification("Готов к работе • локальный AI");
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIF_ID, initialNotif, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        } else {
            startForeground(NOTIF_ID, initialNotif);
        }

        modelManager = new ModelManager(getApplicationContext());
        llamaManager = new LlamaProcessManager(getApplicationContext(), modelManager, statusText -> {
            updateExternalSurfaces(statusText);
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) {
                nm.notify(NOTIF_ID, buildNotification(statusText));
            }
        });
        server = new LocalHubServer(getApplicationContext(), modelManager, llamaManager);
        server.start();

        // Idle GPU/NPU/RAM auto-unload watchdog (does NOT auto-warmup on launch)
        Thread wd = new Thread(() -> {
            while (watchdogAlive) {
                try {
                    Thread.sleep(30_000);
                    if (llamaManager != null) llamaManager.checkIdleUnload();
                } catch (InterruptedException e) {
                    break;
                } catch (Exception ignored) { }
            }
        }, "IdleUnloadWatchdog");
        wd.setDaemon(true);
        wd.start();
    }

    private void updateExternalSurfaces(String statusText) {
        String st = statusText == null ? "" : statusText;
        if (st.startsWith("Активна")) {
            sLlmStarting = false;
            sLlmRunning = true;
            String model = llamaManager != null ? llamaManager.getRunningModelFile() : "";
            String shortName = model.replace(".gguf", "");
            if (shortName.length() > 18) shortName = shortName.substring(0, 18) + "…";
            String backend = llamaManager != null ? llamaManager.getActiveBackend().toUpperCase() : "NPU";
            sShortModelLabel = backend + ": " + (shortName.isEmpty() ? "LLM" : shortName);
            sWidgetStatus = sShortModelLabel;
        } else if (st.startsWith("Загрузка")) {
            sLlmStarting = true;
            sLlmRunning = false;
            sShortModelLabel = "Запуск NPU...";
            sWidgetStatus = st;
        } else {
            sLlmStarting = false;
            sLlmRunning = false;
            sShortModelLabel = "";
            sWidgetStatus = "Brazhnichenko Hub · Офлайн";
        }
        HubWidgetProvider.updateAllWidgets(getApplicationContext(), sLlmRunning, sWidgetStatus);
        HubTileService.requestSync(getApplicationContext());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP_LLM.equals(intent.getAction())) {
            if (llamaManager != null) {
                new Thread(() -> llamaManager.stop()).start();
            }
        } else if (intent != null && ACTION_START_LLM.equals(intent.getAction())) {
            if (llamaManager != null && !sLlmRunning && !sLlmStarting) {
                new Thread(() -> {
                    try {
                        llamaManager.start("");
                    } catch (Exception e) {
                        updateExternalSurfaces("Ошибка запуска: " + e.getMessage());
                    }
                }).start();
            }
        }
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        watchdogAlive = false;
        sLlmRunning = false;
        sLlmStarting = false;
        if (llamaManager != null) llamaManager.stop();
        if (server != null) server.stop();
        HubWidgetProvider.updateAllWidgets(getApplicationContext(), false, "Brazhnichenko Hub · Офлайн");
        HubTileService.requestSync(getApplicationContext());
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel ch = new NotificationChannel(
                    CHANNEL_ID,
                    "Brazhnichenko Hub (Локальная LLM)",
                    NotificationManager.IMPORTANCE_LOW
            );
            ch.setDescription("Поддерживает работу локальной LLM в фоне на вашем устройстве");
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) nm.createNotificationChannel(ch);
        }
    }

    private Notification buildNotification(String text) {
        Intent openIntent = new Intent(this, MainActivity.class);
        PendingIntent openPi = PendingIntent.getActivity(
                this, 0, openIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Intent stopIntent = new Intent(this, HubForegroundService.class);
        stopIntent.setAction(ACTION_STOP_LLM);
        PendingIntent stopPi = PendingIntent.getService(
                this, 1, stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification.Builder b = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        return b.setContentTitle("Brazhnichenko Hub")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_menu_compass)
                .setOngoing(true)
                .setContentIntent(openPi)
                .addAction(new Notification.Action.Builder(
                        null, "Остановить LLM", stopPi).build())
                .build();
    }
}

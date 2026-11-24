package com.brazhnichenko.hub;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.widget.RemoteViews;

public class HubWidgetProvider extends AppWidgetProvider {

    @Override
    public void onUpdate(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds) {
        String subtitle = HubForegroundService.getWidgetStatusText();
        boolean running = HubForegroundService.isLlmRunningStatic();
        for (int appWidgetId : appWidgetIds) {
            appWidgetManager.updateAppWidget(appWidgetId, buildRemoteViews(context, running, subtitle));
        }
    }

    public static void updateAllWidgets(Context context, boolean running, String statusText) {
        try {
            AppWidgetManager mgr = AppWidgetManager.getInstance(context);
            int[] ids = mgr.getAppWidgetIds(new ComponentName(context, HubWidgetProvider.class));
            if (ids == null || ids.length == 0) return;
            RemoteViews rv = buildRemoteViews(context, running, statusText);
            for (int id : ids) {
                mgr.updateAppWidget(id, rv);
            }
        } catch (Exception ignored) { }
    }

    private static RemoteViews buildRemoteViews(Context ctx, boolean running, String statusText) {
        RemoteViews rv = new RemoteViews(ctx.getPackageName(), R.layout.widget_hub);

        String sub = (statusText != null && !statusText.trim().isEmpty())
                ? statusText
                : (running ? "■ Hexagon NPU активен" : "Brazhnichenko Hub · Офлайн");
        rv.setTextViewText(R.id.widget_status, sub);
        rv.setTextColor(R.id.widget_status, Color.parseColor(running ? "#34D399" : "#8E8E99"));

        rv.setOnClickPendingIntent(R.id.widget_ask, makeActionPi(ctx, MainActivity.ACTION_SHORTCUT_ASK, 101));
        rv.setOnClickPendingIntent(R.id.widget_camera, makeActionPi(ctx, MainActivity.ACTION_SHORTCUT_CAMERA, 102));
        rv.setOnClickPendingIntent(R.id.widget_doc, makeActionPi(ctx, MainActivity.ACTION_SHORTCUT_DOC, 103));
        rv.setOnClickPendingIntent(R.id.widget_voice, makeActionPi(ctx, MainActivity.ACTION_SHORTCUT_VOICE, 104));
        return rv;
    }

    private static PendingIntent makeActionPi(Context ctx, String action, int reqCode) {
        Intent i = new Intent(ctx, MainActivity.class);
        i.setAction(action);
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        return PendingIntent.getActivity(
                ctx, reqCode, i,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }
}

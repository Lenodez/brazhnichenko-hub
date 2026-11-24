package com.brazhnichenko.hub;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;

/**
 * Android Quick Settings Tile ("Local AI") in the notification shade.
 * Tap toggles local Hexagon NPU LLM on/off; long-press opens Brazhnichenko Hub UI.
 */
public class HubTileService extends TileService {

    public static void requestSync(Context ctx) {
        try {
            TileService.requestListeningState(ctx, new ComponentName(ctx, HubTileService.class));
        } catch (Exception ignored) { }
    }

    @Override
    public void onStartListening() {
        super.onStartListening();
        syncTileUi(HubForegroundService.isLlmRunningStatic(), HubForegroundService.isLlmStartingStatic(),
                HubForegroundService.getShortModelLabel());
    }

    @Override
    public void onClick() {
        super.onClick();
        boolean running = HubForegroundService.isLlmRunningStatic();
        Intent svc = new Intent(this, HubForegroundService.class);
        if (running) {
            svc.setAction(HubForegroundService.ACTION_STOP_LLM);
            syncTileUi(false, false, "Выкл");
        } else {
            svc.setAction(HubForegroundService.ACTION_START_LLM);
            syncTileUi(false, true, "Запуск NPU...");
        }
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(svc);
            } else {
                startService(svc);
            }
        } catch (Exception ignored) { }
    }

    private void syncTileUi(boolean running, boolean starting, String subtitle) {
        Tile tile = getQsTile();
        if (tile == null) return;
        tile.setLabel("Local AI");
        if (starting) {
            tile.setState(Tile.STATE_ACTIVE);
            if (Build.VERSION.SDK_INT >= 29) tile.setSubtitle("Запуск NPU...");
        } else if (running) {
            tile.setState(Tile.STATE_ACTIVE);
            if (Build.VERSION.SDK_INT >= 29) {
                tile.setSubtitle(subtitle != null && !subtitle.isEmpty() ? subtitle : "NPU активен");
            }
        } else {
            tile.setState(Tile.STATE_INACTIVE);
            if (Build.VERSION.SDK_INT >= 29) tile.setSubtitle("Выкл · нажми");
        }
        tile.updateTile();
    }
}

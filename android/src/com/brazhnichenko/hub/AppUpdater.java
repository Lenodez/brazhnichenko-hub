package com.brazhnichenko.hub;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.ClipData;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.net.Uri;
import android.os.Build;
import android.os.Looper;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * Self-updater backed by GitHub Releases (Lenodez/brazhnichenko-hub).
 * Checks latest release tag, downloads signed arm64 APK with live progress,
 * validates archive integrity, and launches Android's native PackageInstaller
 * directly from the foreground MainActivity (avoiding Android 14–17 BAL blocks).
 */
public final class AppUpdater {
    public static final String GITHUB_REPO = "Lenodez/brazhnichenko-hub";
    private static final String LATEST_API = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";
    private static final String UPDATE_CHANNEL_ID = "brazhnichenko_hub_updates";
    private static final int UPDATE_NOTIF_ID = 17909;

    // idle | downloading | ready | need_permission | installing | error
    private static volatile String dlState = "idle";
    private static volatile double dlPercent = 0;
    private static volatile double dlSpeedMBs = 0;
    private static volatile String dlError = "";
    private static volatile String dlTargetVersion = "";
    private static volatile String dlApkUrl = "";

    private AppUpdater() { }

    public static File getUpdateApkFile(Context ctx) {
        return new File(new File(ctx.getCacheDir(), "updates"), "BrazhnichenkoHub-update.apk");
    }

    public static String getCurrentVersion(Context ctx) {
        try {
            PackageInfo pi = ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0);
            if (pi != null && pi.versionName != null && !pi.versionName.isEmpty()) {
                return pi.versionName;
            }
        } catch (Exception ignored) { }
        return "4.9.3";
    }

    public static long getCurrentVersionCode(Context ctx) {
        try {
            PackageInfo pi = ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0);
            if (pi != null) {
                return Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
                        ? pi.getLongVersionCode()
                        : pi.versionCode;
            }
        } catch (Exception ignored) { }
        return 0L;
    }

    /**
     * Verifies that the downloaded file is a valid, non-truncated Android APK for our package.
     * Returns null if valid, or a human-readable Russian error string if invalid.
     */
    public static String validateApkFile(Context ctx, File apk) {
        if (apk == null || !apk.exists() || apk.length() < 500_000) {
            return "Файл обновления отсутствует или повреждён";
        }
        try (FileInputStream fis = new FileInputStream(apk)) {
            byte[] magic = new byte[4];
            if (fis.read(magic) != 4
                    || magic[0] != 0x50 || magic[1] != 0x4B
                    || magic[2] != 0x03 || magic[3] != 0x04) {
                return "Скачанный файл не является ZIP/APK архивом";
            }
        } catch (Exception ex) {
            return "Не удалось прочитать файл APK";
        }
        try {
            PackageInfo archiveInfo = ctx.getPackageManager()
                    .getPackageArchiveInfo(apk.getAbsolutePath(), 0);
            if (archiveInfo == null || archiveInfo.packageName == null) {
                return "Архив APK скачался не полностью (ошибка разбора пакета)";
            }
            if (!ctx.getPackageName().equals(archiveInfo.packageName)) {
                return "Несовпадение пакета APK: " + archiveInfo.packageName;
            }
            long archiveCode = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
                    ? archiveInfo.getLongVersionCode()
                    : archiveInfo.versionCode;
            long installedCode = getCurrentVersionCode(ctx);
            if (installedCode > 0 && archiveCode > 0 && archiveCode < installedCode) {
                return "Версия в APK (" + archiveCode + ") старше установленной (" + installedCode + ")";
            }
        } catch (Exception ex) {
            return "Ошибка проверки пакета APK: " + ex.getMessage();
        }
        return null;
    }

    public static JSONObject checkLatestRelease(Context ctx) {
        JSONObject out = new JSONObject();
        String current = getCurrentVersion(ctx);
        try {
            out.put("currentVersion", current);
            out.put("repo", GITHUB_REPO);
            HttpURLConnection c = (HttpURLConnection) new URL(LATEST_API).openConnection();
            c.setConnectTimeout(6000);
            c.setReadTimeout(8000);
            c.setRequestProperty("Accept", "application/vnd.github+json");
            c.setRequestProperty("User-Agent", "BrazhnichenkoHub-Android/" + current);

            int code = c.getResponseCode();
            if (code == 404) {
                c.disconnect();
                out.put("ok", true);
                out.put("hasUpdate", false);
                out.put("latestVersion", current);
                out.put("message", "Релизов на GitHub пока нет");
                return out;
            }
            if (code != 200) {
                c.disconnect();
                out.put("ok", false);
                out.put("error", "GitHub HTTP " + code);
                return out;
            }

            String raw = readAll(c.getInputStream());
            c.disconnect();
            JSONObject rel = new JSONObject(raw);
            String tag = rel.optString("tag_name", "").trim();
            String latest = tag.startsWith("v") || tag.startsWith("V") ? tag.substring(1) : tag;
            String releaseUrl = rel.optString("html_url", "https://github.com/" + GITHUB_REPO + "/releases");
            String notes = rel.optString("body", "").trim();
            if (notes.length() > 800) notes = notes.substring(0, 800) + "…";

            String apkUrl = "";
            String apkName = "";
            double apkSizeMB = 0;
            JSONArray assets = rel.optJSONArray("assets");
            if (assets != null) {
                for (int i = 0; i < assets.length(); i++) {
                    JSONObject a = assets.getJSONObject(i);
                    String name = a.optString("name", "");
                    if (name.toLowerCase(Locale.ROOT).endsWith(".apk")) {
                        apkName = name;
                        apkUrl = a.optString("browser_download_url", "");
                        apkSizeMB = Math.round((a.optLong("size", 0) / 1048576.0) * 10.0) / 10.0;
                        if (name.toLowerCase(Locale.ROOT).contains("arm64")) break;
                    }
                }
            }

            boolean hasUpdate = !latest.isEmpty() && compareVersions(latest, current) > 0 && !apkUrl.isEmpty();
            out.put("ok", true);
            out.put("latestVersion", latest.isEmpty() ? current : latest);
            out.put("hasUpdate", hasUpdate);
            out.put("releaseUrl", releaseUrl);
            out.put("notes", notes);
            out.put("apkUrl", apkUrl);
            out.put("apkName", apkName);
            out.put("apkSizeMB", apkSizeMB);
            return out;
        } catch (Exception ex) {
            try {
                out.put("ok", false);
                out.put("error", ex.getMessage() != null ? ex.getMessage() : "Нет связи с GitHub");
            } catch (Exception ignored) { }
            return out;
        }
    }

    public static void startDownloadAndInstall(Context ctx, String apkUrl, String targetVersion) {
        if ("downloading".equals(dlState)) return;
        final Context appCtx = ctx.getApplicationContext();
        final String cleanVer = targetVersion != null ? targetVersion.trim() : "";
        dlApkUrl = apkUrl != null ? apkUrl.trim() : "";

        // Reuse already downloaded valid APK for the same target version without re-downloading 70+ MB
        File existing = getUpdateApkFile(appCtx);
        if (!cleanVer.isEmpty()
                && cleanVer.equals(dlTargetVersion)
                && existing.exists()
                && validateApkFile(appCtx, existing) == null) {
            dlPercent = 100;
            dlError = "";
            dlState = "ready";
            triggerForegroundOrNotificationInstall(appCtx);
            return;
        }

        dlState = "downloading";
        dlPercent = 0;
        dlSpeedMBs = 0;
        dlError = "";
        dlTargetVersion = cleanVer;

        new Thread(() -> {
            HttpURLConnection conn = null;
            try {
                File dir = new File(appCtx.getCacheDir(), "updates");
                if (!dir.exists()) dir.mkdirs();
                File part = new File(dir, "BrazhnichenkoHub-update.apk.part");
                File target = getUpdateApkFile(appCtx);
                if (part.exists()) part.delete();

                URL url = new URL(apkUrl);
                // Follow GitHub -> S3/Azure redirects cleanly
                for (int hops = 0; hops < 8; hops++) {
                    conn = (HttpURLConnection) url.openConnection();
                    conn.setConnectTimeout(12000);
                    conn.setReadTimeout(25000);
                    conn.setInstanceFollowRedirects(false);
                    conn.setRequestProperty("Accept", "application/octet-stream,*/*");
                    conn.setRequestProperty("User-Agent", "BrazhnichenkoHub-Android/" + getCurrentVersion(appCtx));
                    int rc = conn.getResponseCode();
                    if (rc == 301 || rc == 302 || rc == 303 || rc == 307 || rc == 308) {
                        String loc = conn.getHeaderField("Location");
                        conn.disconnect();
                        if (loc == null || loc.isEmpty()) throw new Exception("Пустой редирект загрузки");
                        url = new URL(url, loc);
                        continue;
                    }
                    if (rc != 200) {
                        throw new Exception("HTTP " + rc + " при скачивании APK");
                    }
                    break;
                }

                long total = conn.getContentLengthLong();
                long done = 0;
                long tStart = System.nanoTime();
                try (InputStream is = new BufferedInputStream(conn.getInputStream(), 32768);
                     FileOutputStream fos = new FileOutputStream(part)) {
                    byte[] buf = new byte[32768];
                    int n;
                    while ((n = is.read(buf)) > 0) {
                        fos.write(buf, 0, n);
                        done += n;
                        if (total > 0) {
                            dlPercent = Math.min(99.5, Math.round((done * 1000.0) / total) / 10.0);
                        }
                        double sec = Math.max(0.1, (System.nanoTime() - tStart) / 1e9);
                        dlSpeedMBs = Math.round(((done / 1048576.0) / sec) * 10.0) / 10.0;
                    }
                    fos.getFD().sync();
                }
                conn.disconnect();

                if (total > 0 && done != total) {
                    part.delete();
                    throw new Exception("Загрузка оборвалась (" + (done / 1048576) + " из " + (total / 1048576) + " МБ)");
                }

                String validationErr = validateApkFile(appCtx, part);
                if (validationErr != null) {
                    part.delete();
                    throw new Exception(validationErr);
                }

                if (target.exists()) target.delete();
                if (!part.renameTo(target)) {
                    copyFileFallback(part, target);
                    part.delete();
                }

                dlPercent = 100;
                dlState = "ready";
                triggerForegroundOrNotificationInstall(appCtx);
            } catch (Exception ex) {
                dlState = "error";
                dlError = ex.getMessage() != null ? ex.getMessage() : "Ошибка загрузки обновления";
                if (conn != null) try { conn.disconnect(); } catch (Exception ignored) { }
            }
        }, "ApkUpdateDownloader").start();
    }

    private static void triggerForegroundOrNotificationInstall(Context appCtx) {
        MainActivity fg = MainActivity.getForegroundActivity();
        if (fg != null) {
            launchInstallerFromActivity(fg);
        } else {
            // App was minimized during download: Android 14–17 blocks background startActivity,
            // so post a high-priority notification that brings MainActivity forward to install!
            postUpdateReadyNotification(appCtx, dlTargetVersion);
        }
    }

    public static JSONObject getDownloadStatus(Context ctx) {
        JSONObject o = new JSONObject();
        try {
            File target = getUpdateApkFile(ctx);
            o.put("state", dlState);
            o.put("percent", dlPercent);
            o.put("speedMBs", dlSpeedMBs);
            o.put("error", dlError);
            o.put("targetVersion", dlTargetVersion);
            o.put("apkUrl", dlApkUrl);
            o.put("fileReady", target.exists() && target.length() > 500_000);
        } catch (Exception ignored) { }
        return o;
    }

    public static JSONObject launchInstaller(Context ctx) {
        MainActivity fg = (ctx instanceof MainActivity)
                ? (MainActivity) ctx
                : MainActivity.getForegroundActivity();
        if (fg != null) {
            return launchInstallerFromActivity(fg);
        }

        // No foreground Activity available (e.g. triggered while minimized): post notification
        JSONObject o = new JSONObject();
        try {
            File target = getUpdateApkFile(ctx);
            String err = validateApkFile(ctx, target);
            if (err != null) {
                o.put("ok", false);
                o.put("error", err);
                return o;
            }
            postUpdateReadyNotification(ctx.getApplicationContext(), dlTargetVersion);
            o.put("ok", true);
            o.put("backgroundNotified", true);
            o.put("message", "Открой уведомление Brazhnichenko Hub для подтверждения установки");
        } catch (Exception ex) {
            try {
                o.put("ok", false);
                o.put("error", ex.getMessage() != null ? ex.getMessage() : "Не удалось открыть установщик");
            } catch (Exception ignored) { }
        }
        return o;
    }

    /**
     * Launches the system APK installer directly from the foreground MainActivity on the UI thread.
     * This avoids Android 14–17 Background Activity Launch (BAL) silent drops and propagates
     * read URI permissions cleanly to Samsung One UI / AOSP PackageInstaller.
     */
    public static JSONObject launchInstallerFromActivity(final MainActivity activity) {
        final JSONObject result = new JSONObject();
        if (activity == null || activity.isFinishing() || activity.isDestroyed()) {
            try {
                result.put("ok", false);
                result.put("error", "Окно приложения не активно — нажми «Установить APK» ещё раз");
            } catch (Exception ignored) { }
            return result;
        }

        File target = getUpdateApkFile(activity);
        String validationErr = validateApkFile(activity, target);
        if (validationErr != null) {
            dlState = "error";
            dlError = validationErr;
            try {
                result.put("ok", false);
                result.put("error", validationErr);
            } catch (Exception ignored) { }
            return result;
        }

        Runnable uiTask = () -> {
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                        && !activity.getPackageManager().canRequestPackageInstalls()) {
                    dlState = "need_permission";
                    activity.requestUnknownSourcesPermissionForUpdate();
                    result.put("ok", true);
                    result.put("needsPermission", true);
                    result.put("message", "Разреши «Установку неизвестных приложений» — обновление продолжится автоматически");
                    return;
                }

                Uri apkUri = ApkFileProvider.UPDATE_APK_URI;
                Intent install = new Intent(Intent.ACTION_VIEW);
                install.setDataAndType(apkUri, "application/vnd.android.package-archive");
                install.setClipData(ClipData.newRawUri("BrazhnichenkoHub-update.apk", apkUri));
                install.putExtra(Intent.EXTRA_NOT_UNKNOWN_SOURCE, true);
                install.putExtra(Intent.EXTRA_INSTALLER_PACKAGE_NAME, activity.getPackageName());
                install.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_SINGLE_TOP);

                // Explicitly grant URI read permission to all resolved installer activities/trampolines
                List<ResolveInfo> resolved = activity.getPackageManager()
                        .queryIntentActivities(install, PackageManager.MATCH_DEFAULT_ONLY);
                if (resolved != null) {
                    for (ResolveInfo ri : resolved) {
                        if (ri.activityInfo != null && ri.activityInfo.packageName != null) {
                            try {
                                activity.grantUriPermission(
                                        ri.activityInfo.packageName,
                                        apkUri,
                                        Intent.FLAG_GRANT_READ_URI_PERMISSION);
                            } catch (Exception ignored) { }
                        }
                    }
                }

                activity.startActivity(install);
                dlState = "installing";
                result.put("ok", true);
                result.put("needsPermission", false);
            } catch (Exception ex) {
                dlState = "ready";
                try {
                    result.put("ok", false);
                    result.put("error", ex.getMessage() != null ? ex.getMessage() : "Не удалось открыть системный установщик Android");
                } catch (Exception ignored) { }
            }
        };

        if (Looper.myLooper() == Looper.getMainLooper()) {
            uiTask.run();
        } else {
            final CountDownLatch latch = new CountDownLatch(1);
            activity.runOnUiThread(() -> {
                try {
                    uiTask.run();
                } finally {
                    latch.countDown();
                }
            });
            try { latch.await(4, TimeUnit.SECONDS); } catch (InterruptedException ignored) { }
        }
        return result;
    }

    private static void postUpdateReadyNotification(Context appCtx, String ver) {
        try {
            NotificationManager nm = (NotificationManager) appCtx.getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm == null) return;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                NotificationChannel ch = new NotificationChannel(
                        UPDATE_CHANNEL_ID,
                        "Обновления Brazhnichenko Hub",
                        NotificationManager.IMPORTANCE_HIGH);
                ch.setDescription("Уведомление о готовности скачанного APK к установке");
                nm.createNotificationChannel(ch);
            }
            Intent open = new Intent(appCtx, MainActivity.class);
            open.setAction(MainActivity.ACTION_INSTALL_UPDATE);
            open.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            PendingIntent pi = PendingIntent.getActivity(
                    appCtx,
                    904,
                    open,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

            Notification.Builder b = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                    ? new Notification.Builder(appCtx, UPDATE_CHANNEL_ID)
                    : new Notification.Builder(appCtx);

            String title = ver != null && !ver.isEmpty()
                    ? "Обновление v" + ver + " скачано"
                    : "Обновление скачано";
            b.setContentTitle(title)
                    .setContentText("Нажми, чтобы подтвердить установку новой версии")
                    .setSmallIcon(android.R.drawable.stat_sys_download_done)
                    .setAutoCancel(true)
                    .setContentIntent(pi);

            nm.notify(UPDATE_NOTIF_ID, b.build());
        } catch (Exception ignored) { }
    }

    private static void copyFileFallback(File src, File dst) throws Exception {
        try (FileInputStream fis = new FileInputStream(src);
             FileOutputStream fos = new FileOutputStream(dst)) {
            byte[] buf = new byte[32768];
            int n;
            while ((n = fis.read(buf)) > 0) fos.write(buf, 0, n);
            fos.getFD().sync();
        }
    }

    public static int compareVersions(String a, String b) {
        String[] pa = a.replaceAll("[^0-9.]", "").split("\\.");
        String[] pb = b.replaceAll("[^0-9.]", "").split("\\.");
        int len = Math.max(pa.length, pb.length);
        for (int i = 0; i < len; i++) {
            int va = i < pa.length && !pa[i].isEmpty() ? parseIntSafe(pa[i]) : 0;
            int vb = i < pb.length && !pb[i].isEmpty() ? parseIntSafe(pb[i]) : 0;
            if (va != vb) return Integer.compare(va, vb);
        }
        return 0;
    }

    private static int parseIntSafe(String s) {
        try { return Integer.parseInt(s); } catch (Exception e) { return 0; }
    }

    private static String readAll(InputStream is) throws Exception {
        if (is == null) return "";
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) > 0) baos.write(buf, 0, n);
        return baos.toString("UTF-8");
    }
}

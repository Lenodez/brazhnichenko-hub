package com.brazhnichenko.hub;

import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.Locale;

/**
 * Self-updater backed by GitHub Releases (Lenodez/brazhnichenko-hub).
 * Checks latest release tag, downloads signed arm64 APK with live progress,
 * and hands off to Android's native PackageInstaller via ApkFileProvider.
 */
public final class AppUpdater {
    public static final String GITHUB_REPO = "Lenodez/brazhnichenko-hub";
    private static final String LATEST_API = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";

    private static volatile String dlState = "idle"; // idle | downloading | ready | error
    private static volatile double dlPercent = 0;
    private static volatile double dlSpeedMBs = 0;
    private static volatile String dlError = "";
    private static volatile String dlTargetVersion = "";

    private AppUpdater() { }

    public static String getCurrentVersion(Context ctx) {
        try {
            PackageInfo pi = ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0);
            if (pi != null && pi.versionName != null && !pi.versionName.isEmpty()) {
                return pi.versionName;
            }
        } catch (Exception ignored) { }
        return "4.7.0";
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
        dlState = "downloading";
        dlPercent = 0;
        dlSpeedMBs = 0;
        dlError = "";
        dlTargetVersion = targetVersion != null ? targetVersion : "";

        final Context appCtx = ctx.getApplicationContext();
        new Thread(() -> {
            HttpURLConnection conn = null;
            try {
                File dir = new File(appCtx.getCacheDir(), "updates");
                if (!dir.exists()) dir.mkdirs();
                File part = new File(dir, "BrazhnichenkoHub-update.apk.part");
                File target = new File(dir, "BrazhnichenkoHub-update.apk");
                if (part.exists()) part.delete();

                URL url = new URL(apkUrl);
                // Follow GitHub -> S3 redirects cleanly
                for (int hops = 0; hops < 6; hops++) {
                    conn = (HttpURLConnection) url.openConnection();
                    conn.setConnectTimeout(10000);
                    conn.setReadTimeout(20000);
                    conn.setInstanceFollowRedirects(false);
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
                }
                conn.disconnect();

                if (done < 500_000) {
                    part.delete();
                    throw new Exception("Скачанный APK повреждён (слишком малый размер)");
                }
                if (target.exists()) target.delete();
                if (!part.renameTo(target)) {
                    throw new Exception("Не удалось сохранить файл обновления");
                }
                dlPercent = 100;
                dlState = "ready";
                launchInstaller(appCtx);
            } catch (Exception ex) {
                dlState = "error";
                dlError = ex.getMessage() != null ? ex.getMessage() : "Ошибка загрузки обновления";
                if (conn != null) try { conn.disconnect(); } catch (Exception ignored) { }
            }
        }).start();
    }

    public static JSONObject getDownloadStatus(Context ctx) {
        JSONObject o = new JSONObject();
        try {
            File target = new File(new File(ctx.getCacheDir(), "updates"), "BrazhnichenkoHub-update.apk");
            o.put("state", dlState);
            o.put("percent", dlPercent);
            o.put("speedMBs", dlSpeedMBs);
            o.put("error", dlError);
            o.put("targetVersion", dlTargetVersion);
            o.put("fileReady", target.exists() && target.length() > 500_000);
        } catch (Exception ignored) { }
        return o;
    }

    public static JSONObject launchInstaller(Context ctx) {
        JSONObject o = new JSONObject();
        try {
            File target = new File(new File(ctx.getCacheDir(), "updates"), "BrazhnichenkoHub-update.apk");
            if (!target.exists() || target.length() < 500_000) {
                o.put("ok", false);
                o.put("error", "Файл обновления ещё не скачан");
                return o;
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !ctx.getPackageManager().canRequestPackageInstalls()) {
                Intent perm = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + ctx.getPackageName()));
                perm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                ctx.startActivity(perm);
                o.put("ok", true);
                o.put("needsPermission", true);
                o.put("message", "Разреши установку обновлений для Brazhnichenko Hub, затем нажми «Установить» ещё раз");
                return o;
            }
            Intent install = new Intent(Intent.ACTION_VIEW);
            install.setDataAndType(ApkFileProvider.UPDATE_APK_URI, "application/vnd.android.package-archive");
            install.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
            ctx.startActivity(install);
            o.put("ok", true);
            o.put("needsPermission", false);
            return o;
        } catch (Exception ex) {
            try {
                o.put("ok", false);
                o.put("error", ex.getMessage() != null ? ex.getMessage() : "Не удалось открыть установщик");
            } catch (Exception ignored) { }
            return o;
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

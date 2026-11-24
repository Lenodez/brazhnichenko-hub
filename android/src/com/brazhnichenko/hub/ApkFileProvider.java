package com.brazhnichenko.hub;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;

/**
 * Minimal zero-dependency FileProvider strictly scoped to our own downloaded APK update file.
 */
public class ApkFileProvider extends ContentProvider {
    public static final String AUTHORITY = "com.brazhnichenko.hub.apkprovider";
    public static final Uri UPDATE_APK_URI = Uri.parse("content://" + AUTHORITY + "/BrazhnichenkoHub-update.apk");

    private File getTargetFile() {
        File dir = new File(getContext().getCacheDir(), "updates");
        return new File(dir, "BrazhnichenkoHub-update.apk");
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        File f = getTargetFile();
        if (!f.exists() || !f.isFile()) {
            throw new FileNotFoundException("Update APK not found");
        }
        return ParcelFileDescriptor.open(f, ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public String getType(Uri uri) {
        return "application/vnd.android.package-archive";
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        File f = getTargetFile();
        String[] cols = (projection != null && projection.length > 0)
                ? projection
                : new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE};
        MatrixCursor mc = new MatrixCursor(cols, 1);
        Object[] row = new Object[cols.length];
        for (int i = 0; i < cols.length; i++) {
            if (OpenableColumns.DISPLAY_NAME.equals(cols[i])) row[i] = f.getName();
            else if (OpenableColumns.SIZE.equals(cols[i])) row[i] = f.exists() ? f.length() : 0L;
        }
        mc.addRow(row);
        return mc;
    }

    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}

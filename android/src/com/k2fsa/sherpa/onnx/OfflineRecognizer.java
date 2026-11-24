package com.k2fsa.sherpa.onnx;

import android.content.res.AssetManager;

public class OfflineRecognizer {
    private long ptr;
    public final OfflineRecognizerConfig config;

    static {
        System.loadLibrary("sherpa-onnx-jni");
    }

    public OfflineRecognizer(OfflineRecognizerConfig config) {
        this.config = config;
        this.ptr = newFromFile(config);
        if (this.ptr == 0L) {
            throw new IllegalStateException("Failed to initialize native OfflineRecognizer");
        }
    }

    public OfflineStream createStream() {
        return new OfflineStream(createStream(ptr));
    }

    public void decode(OfflineStream stream) {
        decode(ptr, stream.ptr);
    }

    public OfflineRecognizerResult getResult(OfflineStream stream) {
        return getResult(stream.ptr);
    }

    public synchronized void release() {
        if (ptr != 0L) {
            delete(ptr);
            ptr = 0L;
        }
    }

    @Override
    protected void finalize() {
        release();
    }

    private native void delete(long ptr);
    private native long createStream(long ptr);
    private native long createStreamWithHotwords(long ptr, String hotwords);
    private native void setConfig(long ptr, OfflineRecognizerConfig config);
    private native long newFromAsset(AssetManager assetManager, OfflineRecognizerConfig config);
    private native long newFromFile(OfflineRecognizerConfig config);
    private native void decode(long ptr, long streamPtr);
    private native OfflineRecognizerResult getResult(long streamPtr);
}

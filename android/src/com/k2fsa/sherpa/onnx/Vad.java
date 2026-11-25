package com.k2fsa.sherpa.onnx;

import android.content.res.AssetManager;

public class Vad {
    private long ptr;
    public final VadModelConfig config;

    static {
        System.loadLibrary("sherpa-onnx-jni");
    }

    public Vad(AssetManager assetManager, VadModelConfig config) {
        this.config = config;
        if (assetManager != null) {
            this.ptr = newFromAsset(assetManager, config);
        } else {
            this.ptr = newFromFile(config);
        }
        if (this.ptr == 0L) {
            throw new IllegalStateException("Failed to initialize native Sherpa-ONNX Vad");
        }
    }

    public void acceptWaveform(float[] samples) {
        if (ptr != 0L) acceptWaveform(ptr, samples);
    }

    public float compute(float[] samples) {
        return ptr != 0L ? compute(ptr, samples) : 0f;
    }

    public boolean empty() {
        return ptr == 0L || empty(ptr);
    }

    public void pop() {
        if (ptr != 0L) pop(ptr);
    }

    public void clear() {
        if (ptr != 0L) clear(ptr);
    }

    public SpeechSegment front() {
        return ptr != 0L ? front(ptr) : null;
    }

    public boolean isSpeechDetected() {
        return ptr != 0L && isSpeechDetected(ptr);
    }

    public void reset() {
        if (ptr != 0L) reset(ptr);
    }

    public void flush() {
        if (ptr != 0L) flush(ptr);
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
    private native long newFromAsset(AssetManager assetManager, VadModelConfig config);
    private native long newFromFile(VadModelConfig config);
    private native void acceptWaveform(long ptr, float[] samples);
    private native float compute(long ptr, float[] samples);
    private native boolean empty(long ptr);
    private native void pop(long ptr);
    private native void clear(long ptr);
    private native SpeechSegment front(long ptr);
    private native boolean isSpeechDetected(long ptr);
    private native void reset(long ptr);
    private native void flush(long ptr);
}

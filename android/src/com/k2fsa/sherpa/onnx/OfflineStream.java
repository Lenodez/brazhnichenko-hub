package com.k2fsa.sherpa.onnx;

public class OfflineStream {
    public long ptr;

    static {
        System.loadLibrary("sherpa-onnx-jni");
    }

    public OfflineStream(long ptr) {
        if (ptr == 0L) throw new IllegalArgumentException("Failed to create native OfflineStream");
        this.ptr = ptr;
    }

    public void acceptWaveform(float[] samples, int sampleRate) {
        acceptWaveform(ptr, samples, sampleRate);
    }

    public void release() {
        if (ptr != 0L) {
            delete(ptr);
            ptr = 0L;
        }
    }

    @Override
    protected void finalize() {
        release();
    }

    private native void acceptWaveform(long ptr, float[] samples, int sampleRate);
    private native void setOption(long ptr, String key, String value);
    private native String getOption(long ptr, String key);
    private native void delete(long ptr);
}

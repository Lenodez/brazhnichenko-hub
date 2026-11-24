package com.k2fsa.sherpa.onnx;

public class OfflineRecognizerResult {
    public final String text;
    public final String[] tokens;
    public final float[] timestamps;
    public final String lang;
    public final String emotion;
    public final String event;
    public final float[] durations;
    public final int[] words;

    public OfflineRecognizerResult(
            String text,
            String[] tokens,
            float[] timestamps,
            String lang,
            String emotion,
            String event,
            float[] durations,
            int[] words) {
        this.text = text != null ? text : "";
        this.tokens = tokens != null ? tokens : new String[0];
        this.timestamps = timestamps != null ? timestamps : new float[0];
        this.lang = lang != null ? lang : "";
        this.emotion = emotion != null ? emotion : "";
        this.event = event != null ? event : "";
        this.durations = durations != null ? durations : new float[0];
        this.words = words != null ? words : new int[0];
    }
}

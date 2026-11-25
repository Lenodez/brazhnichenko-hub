package com.brazhnichenko.hub;

import android.content.Context;
import android.media.MediaCodec;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.net.Uri;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;

/**
 * Zero-dependency Android hardware/software audio decoder backed by MediaExtractor + MediaCodec.
 * Decodes Telegram voice notes (.ogg Opus), WhatsApp (.m4a AAC), MP3, WAV, FLAC, and video audio (.mp4)
 * directly into 16 kHz mono float[] PCM suitable for Silero VAD and Sherpa-ONNX STT.
 */
public final class AudioFileDecoder {
    public static final int TARGET_SAMPLE_RATE = 16000;
    private static final int MAX_DURATION_SEC = 3600; // cap at 60 minutes to stay RAM-safe

    public interface DecodeProgress {
        void onProgress(int pct, double decodedSec);
    }

    public static final class DecodedAudio {
        public final float[] samples;
        public final double durationSec;

        public DecodedAudio(float[] samples, double durationSec) {
            this.samples = samples;
            this.durationSec = durationSec;
        }
    }

    private AudioFileDecoder() { }

    public static DecodedAudio decodeUriTo16kMono(Context ctx, Uri uri, DecodeProgress progress) throws Exception {
        File tmp = new File(ctx.getCacheDir(), "import_audio_" + System.currentTimeMillis() + ".tmp");
        try {
            try (InputStream is = ctx.getContentResolver().openInputStream(uri);
                 FileOutputStream fos = new FileOutputStream(tmp)) {
                if (is == null) throw new Exception("Не удалось открыть аудиофайл");
                byte[] buf = new byte[32768];
                int n;
                long copied = 0;
                while ((n = is.read(buf)) > 0) {
                    fos.write(buf, 0, n);
                    copied += n;
                    if (copied > 250L * 1024 * 1024) {
                        throw new Exception("Файл слишком большой (> 250 МБ)");
                    }
                }
            }
            return decodeFileTo16kMono(tmp, progress);
        } finally {
            try { tmp.delete(); } catch (Exception ignored) { }
        }
    }

    public static DecodedAudio decodeFileTo16kMono(File file, DecodeProgress progress) throws Exception {
        // Fast path for standard 16-bit PCM WAV
        DecodedAudio wavFast = tryDecodeSimpleWav(file);
        if (wavFast != null) {
            if (progress != null) progress.onProgress(100, wavFast.durationSec);
            return wavFast;
        }

        MediaExtractor extractor = new MediaExtractor();
        MediaCodec codec = null;
        try {
            extractor.setDataSource(file.getAbsolutePath());
            int audioTrack = -1;
            MediaFormat format = null;
            for (int i = 0; i < extractor.getTrackCount(); i++) {
                MediaFormat f = extractor.getTrackFormat(i);
                String mime = f.getString(MediaFormat.KEY_MIME);
                if (mime != null && mime.startsWith("audio/")) {
                    audioTrack = i;
                    format = f;
                    break;
                }
            }
            if (audioTrack < 0 || format == null) {
                throw new Exception("В файле не найдена аудиодорожка");
            }
            extractor.selectTrack(audioTrack);

            String mime = format.getString(MediaFormat.KEY_MIME);
            int srcRate = format.containsKey(MediaFormat.KEY_SAMPLE_RATE)
                    ? format.getInteger(MediaFormat.KEY_SAMPLE_RATE) : 16000;
            int srcChannels = format.containsKey(MediaFormat.KEY_CHANNEL_COUNT)
                    ? format.getInteger(MediaFormat.KEY_CHANNEL_COUNT) : 1;
            long durationUs = format.containsKey(MediaFormat.KEY_DURATION)
                    ? format.getLong(MediaFormat.KEY_DURATION) : 0L;

            codec = MediaCodec.createDecoderByType(mime);
            codec.configure(format, null, null, 0);
            codec.start();

            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            boolean inputDone = false;
            boolean outputDone = false;

            // Accumulate resampled 16 kHz mono floats dynamically
            int maxTargetSamples = TARGET_SAMPLE_RATE * MAX_DURATION_SEC;
            float[] outBuf = new float[TARGET_SAMPLE_RATE * 15];
            int outLen = 0;
            double resamplePos = 0.0;
            float prevMono = 0f;
            boolean hasPrev = false;
            int lastPct = -1;

            while (!outputDone && outLen < maxTargetSamples) {
                if (!inputDone) {
                    int inIdx = codec.dequeueInputBuffer(10_000);
                    if (inIdx >= 0) {
                        ByteBuffer inBuf = codec.getInputBuffer(inIdx);
                        if (inBuf != null) {
                            int sampleSize = extractor.readSampleData(inBuf, 0);
                            if (sampleSize < 0) {
                                codec.queueInputBuffer(inIdx, 0, 0, 0L, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                                inputDone = true;
                            } else {
                                long pts = extractor.getSampleTime();
                                codec.queueInputBuffer(inIdx, 0, sampleSize, pts, 0);
                                extractor.advance();
                            }
                        }
                    }
                }

                int outIdx = codec.dequeueOutputBuffer(info, 10_000);
                if (outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    MediaFormat outFmt = codec.getOutputFormat();
                    if (outFmt.containsKey(MediaFormat.KEY_SAMPLE_RATE)) {
                        srcRate = outFmt.getInteger(MediaFormat.KEY_SAMPLE_RATE);
                    }
                    if (outFmt.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) {
                        srcChannels = outFmt.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
                    }
                } else if (outIdx >= 0) {
                    ByteBuffer outByteBuf = codec.getOutputBuffer(outIdx);
                    if (outByteBuf != null && info.size > 0) {
                        outByteBuf.position(info.offset);
                        outByteBuf.limit(info.offset + info.size);
                        int shortCount = info.size / 2;
                        int ch = Math.max(1, srcChannels);
                        int frames = shortCount / ch;

                        // Convert interleaved PCM16 -> mono float[]
                        float[] mono = new float[frames];
                        ByteBuffer slice = outByteBuf.slice().order(ByteOrder.LITTLE_ENDIAN);
                        for (int f = 0; f < frames; f++) {
                            float acc = 0f;
                            for (int c = 0; c < ch; c++) {
                                acc += slice.getShort() / 32768.0f;
                            }
                            mono[f] = acc / ch;
                        }

                        // Resample srcRate -> 16000 Hz linearly
                        double ratio = (double) srcRate / (double) TARGET_SAMPLE_RATE;
                        if (srcRate == TARGET_SAMPLE_RATE) {
                            if (outLen + frames > outBuf.length) {
                                outBuf = growBuffer(outBuf, Math.min(maxTargetSamples, Math.max(outBuf.length * 2, outLen + frames)));
                            }
                            int copyN = Math.min(frames, maxTargetSamples - outLen);
                            System.arraycopy(mono, 0, outBuf, outLen, copyN);
                            outLen += copyN;
                        } else if (frames > 0) {
                            while (resamplePos < frames && outLen < maxTargetSamples) {
                                int idx = (int) Math.floor(resamplePos);
                                float frac = (float) (resamplePos - idx);
                                float s0 = (idx < 0) ? (hasPrev ? prevMono : mono[0]) : mono[idx];
                                float s1 = (idx + 1 < frames) ? mono[idx + 1] : mono[frames - 1];
                                float val = s0 + (s1 - s0) * frac;
                                if (outLen >= outBuf.length) {
                                    outBuf = growBuffer(outBuf, Math.min(maxTargetSamples, outBuf.length * 2));
                                }
                                outBuf[outLen++] = val;
                                resamplePos += ratio;
                            }
                            resamplePos -= frames;
                            prevMono = mono[frames - 1];
                            hasPrev = true;
                        }

                        if (progress != null && durationUs > 0 && info.presentationTimeUs > 0) {
                            int pct = (int) Math.min(99, (info.presentationTimeUs * 100L) / durationUs);
                            if (pct >= lastPct + 5) {
                                lastPct = pct;
                                progress.onProgress(pct, outLen / (double) TARGET_SAMPLE_RATE);
                            }
                        }
                    }
                    codec.releaseOutputBuffer(outIdx, false);
                    if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                        outputDone = true;
                    }
                }
            }

            if (outLen < TARGET_SAMPLE_RATE / 10) {
                throw new Exception("Аудио слишком короткое или пустое");
            }
            float[] trimmed = Arrays.copyOf(outBuf, outLen);
            double dur = outLen / (double) TARGET_SAMPLE_RATE;
            if (progress != null) progress.onProgress(100, dur);
            return new DecodedAudio(trimmed, dur);
        } finally {
            if (codec != null) {
                try { codec.stop(); } catch (Exception ignored) { }
                try { codec.release(); } catch (Exception ignored) { }
            }
            try { extractor.release(); } catch (Exception ignored) { }
        }
    }

    private static float[] growBuffer(float[] old, int newSize) {
        return Arrays.copyOf(old, Math.max(old.length + 16000, newSize));
    }

    private static DecodedAudio tryDecodeSimpleWav(File file) {
        if (file.length() < 44 || file.length() > 80L * 1024 * 1024) return null;
        try (FileInputStream fis = new FileInputStream(file)) {
            byte[] hdr = new byte[44];
            if (fis.read(hdr) != 44) return null;
            if (hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F') return null;
            if (hdr[8] != 'W' || hdr[9] != 'A' || hdr[10] != 'V' || hdr[11] != 'E') return null;
            ByteBuffer bb = ByteBuffer.wrap(hdr).order(ByteOrder.LITTLE_ENDIAN);
            int audioFmt = bb.getShort(20) & 0xFFFF;
            int ch = bb.getShort(22) & 0xFFFF;
            int sr = bb.getInt(24);
            int bits = bb.getShort(34) & 0xFFFF;
            if (audioFmt != 1 || bits != 16 || sr != TARGET_SAMPLE_RATE || ch != 1) return null;
            if (hdr[36] != 'd' || hdr[37] != 'a' || hdr[38] != 't' || hdr[39] != 'a') return null;

            ByteArrayOutputStream baos = new ByteArrayOutputStream();
            byte[] buf = new byte[16384];
            int n;
            while ((n = fis.read(buf)) > 0) baos.write(buf, 0, n);
            byte[] pcmBytes = baos.toByteArray();
            int count = pcmBytes.length / 2;
            float[] out = new float[count];
            ByteBuffer pb = ByteBuffer.wrap(pcmBytes).order(ByteOrder.LITTLE_ENDIAN);
            for (int i = 0; i < count; i++) out[i] = pb.getShort() / 32768.0f;
            return new DecodedAudio(out, count / (double) TARGET_SAMPLE_RATE);
        } catch (Exception ignored) {
            return null;
        }
    }
}

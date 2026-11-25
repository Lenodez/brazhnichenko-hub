using System.IO;
using System.Runtime.InteropServices;

namespace LocalAIHub.Services;

/// <summary>
/// Real-time 16 kHz 16-bit mono Windows PCM microphone recorder backed by WinMM (waveIn*).
/// Provides live volume level (0..100%) and adaptive end-of-speech silence auto-stop
/// matching the snappy mobile voice UX (zero external audio dependencies).
/// </summary>
public sealed class LiveMicRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private const int ChunkMs = 100;
    private const int SamplesPerBuffer = SampleRate * ChunkMs / 1000; // 1600 samples = 3200 bytes
    private const int BufferCount = 4;
    private const int MaxDurationMs = 45000;
    private const int NoiseCalibMs = 300;
    private const int MinSpeechMs = 200;
    private const int SilenceStopMs = 1250;

    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const uint MM_WIM_DATA = 0x3C0;
    private const uint WAVE_MAPPER = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, WaveInProc dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    private readonly object _lock = new();
    private readonly List<float> _samples = new(SampleRate * 10);
    private IntPtr _hWaveIn = IntPtr.Zero;
    private WaveInProc? _callbackPin;
    private readonly IntPtr[] _hdrPtrs = new IntPtr[BufferCount];
    private readonly IntPtr[] _dataPtrs = new IntPtr[BufferCount];
    private volatile bool _recording;
    private volatile bool _autoStopEnabled;
    private volatile bool _autoStopFired;
    private int _elapsedMs;
    private int _speechMs;
    private int _silenceAfterSpeechMs;
    private double _noiseFloorRms;
    private int _calibChunks;

    public bool IsRecording => _recording;

    public event Action<int, double, bool>? LevelUpdated;
    public event Action<float[]>? AutoStopped;

    public void Start(bool autoStopOnSilence = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LiveMicRecorder поддерживается только на Windows.");

        lock (_lock)
        {
            if (_recording)
                throw new InvalidOperationException("Запись с микрофона уже активна.");

            _samples.Clear();
            _elapsedMs = 0;
            _speechMs = 0;
            _silenceAfterSpeechMs = 0;
            _noiseFloorRms = 0.004;
            _calibChunks = 0;
            _autoStopEnabled = autoStopOnSilence;
            _autoStopFired = false;

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
                cbSize = 0
            };

            _callbackPin = OnWaveInMessage;
            int rc = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref fmt, _callbackPin, IntPtr.Zero, CALLBACK_FUNCTION);
            if (rc != 0 || _hWaveIn == IntPtr.Zero)
                throw new InvalidOperationException($"Не удалось открыть микрофон Windows (waveInOpen код {rc}). Проверь доступ к микрофону.");

            int byteLen = SamplesPerBuffer * 2;
            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < BufferCount; i++)
            {
                _dataPtrs[i] = Marshal.AllocHGlobal(byteLen);
                _hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);
                var hdr = new WAVEHDR
                {
                    lpData = _dataPtrs[i],
                    dwBufferLength = (uint)byteLen,
                    dwUser = new IntPtr(i)
                };
                Marshal.StructureToPtr(hdr, _hdrPtrs[i], false);
                waveInPrepareHeader(_hWaveIn, _hdrPtrs[i], hdrSize);
                waveInAddBuffer(_hWaveIn, _hdrPtrs[i], hdrSize);
            }

            _recording = true;
            rc = waveInStart(_hWaveIn);
            if (rc != 0)
            {
                CleanupLocked();
                throw new InvalidOperationException($"Не удалось начать запись (waveInStart код {rc}).");
            }
        }
    }

    private void OnWaveInMessage(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != MM_WIM_DATA || !_recording || dwParam1 == IntPtr.Zero) return;

        bool triggerAutoStop = false;
        int levelPct = 0;
        double elapsedSec = 0;
        bool speechDetected = false;

        lock (_lock)
        {
            if (!_recording || _hWaveIn == IntPtr.Zero) return;

            var hdr = Marshal.PtrToStructure<WAVEHDR>(dwParam1);
            int sampleCount = (int)(hdr.dwBytesRecorded / 2);
            if (sampleCount > 0 && hdr.lpData != IntPtr.Zero)
            {
                short[] pcm16 = new short[sampleCount];
                Marshal.Copy(hdr.lpData, pcm16, 0, sampleCount);

                double sumSq = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    float f = pcm16[i] / 32768f;
                    _samples.Add(f);
                    sumSq += f * f;
                }

                double rms = Math.Sqrt(sumSq / sampleCount);
                _elapsedMs += (sampleCount * 1000) / SampleRate;
                elapsedSec = _elapsedMs / 1000.0;

                if (_elapsedMs <= NoiseCalibMs)
                {
                    _calibChunks++;
                    _noiseFloorRms = ((_noiseFloorRms * (_calibChunks - 1)) + rms) / _calibChunks;
                }

                double speechThresh = Math.Clamp(Math.Max(0.011, _noiseFloorRms * 2.6), 0.011, 0.075);
                bool isVoiceFrame = rms >= speechThresh;
                if (isVoiceFrame)
                {
                    _speechMs += ChunkMs;
                    _silenceAfterSpeechMs = 0;
                }
                else if (_speechMs >= MinSpeechMs)
                {
                    _silenceAfterSpeechMs += ChunkMs;
                }

                speechDetected = _speechMs >= MinSpeechMs;
                levelPct = (int)Math.Clamp(rms * 420.0, 0, 100);

                if (_autoStopEnabled && !_autoStopFired)
                {
                    bool finishedSentence = speechDetected && _silenceAfterSpeechMs >= SilenceStopMs;
                    bool timedOut = _elapsedMs >= MaxDurationMs;
                    if (finishedSentence || timedOut)
                    {
                        _autoStopFired = true;
                        triggerAutoStop = true;
                    }
                }
            }

            if (_recording && !triggerAutoStop)
            {
                try { waveInAddBuffer(_hWaveIn, dwParam1, Marshal.SizeOf<WAVEHDR>()); } catch { }
            }
        }

        try { LevelUpdated?.Invoke(levelPct, elapsedSec, speechDetected); } catch { }

        if (triggerAutoStop)
        {
            // Never call waveInReset/waveInClose inside the WinMM callback thread (causes deadlock!)
            _ = Task.Run(() =>
            {
                float[] audio = StopAndGetSamples();
                try { AutoStopped?.Invoke(audio); } catch { }
            });
        }
    }

    public float[] StopAndGetSamples()
    {
        lock (_lock)
        {
            if (!_recording && _hWaveIn == IntPtr.Zero)
                return _samples.ToArray();

            CleanupLocked();
            return _samples.ToArray();
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            CleanupLocked();
            _samples.Clear();
        }
    }

    private void CleanupLocked()
    {
        _recording = false;
        if (_hWaveIn != IntPtr.Zero)
        {
            IntPtr h = _hWaveIn;
            _hWaveIn = IntPtr.Zero;
            try { waveInReset(h); } catch { }
            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < BufferCount; i++)
            {
                if (_hdrPtrs[i] != IntPtr.Zero)
                {
                    try { waveInUnprepareHeader(h, _hdrPtrs[i], hdrSize); } catch { }
                    Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = IntPtr.Zero;
                }
                if (_dataPtrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_dataPtrs[i]);
                    _dataPtrs[i] = IntPtr.Zero;
                }
            }
            try { waveInClose(h); } catch { }
        }
        _callbackPin = null;
    }

    public static void SaveFloatPcmToWav(float[] samples, string wavPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
        using var fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            bw.Write(s);
        }
    }

    public void Dispose() => Cancel();
}

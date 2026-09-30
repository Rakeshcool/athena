// Microphone capture.
// Capture runs in the device's native mix format (always accepted in WASAPI
// shared mode) and is transcoded to 16 kHz mono 16-bit after stop — the same
// "encode at key-up" shape as Athena's FLAC pipeline, and immune to devices that
// reject a non-mix capture format. The RMS meter reads the native format.
//
// Live streaming: a parallel tap converts each buffer to PCM16 mono 16 kHz
// (the realtime endpoint's format) and hands it to PcmChunk AS SOON AS the
// buffer arrives — the file is still the durable record; the tap is fire-and-
// forget for the HUD/live-text path. Conversion is allocation-light: one
// resampler per recorder, reused across buffers.

using System.IO;
using Athena.Core;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Athena.App.Audio;

public sealed class WavRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _rawPath;
    private readonly SynchronizationContext? _sync;

    // Warm-graph state: the capture side (device + WASAPI client) is built and
    // initialized by Warm() while the app is idle, so a key press pays only
    // StartRecording(). Capturing the UI SynchronizationContext from whichever
    // thread warmed keeps level events marshaled to the UI thread no matter who
    // constructed the recorder (WarmRecorderPool builds on a worker thread).
    private string? _deviceId;

    // Live-tap state: sub-frame carry bytes only — no client-side resampler
    // (the web client's contract: stream native-rate mono PCM16, declare the
    // native rate; the server resamples internally).
    private byte[] _tapCarry = Array.Empty<byte>();

    /// <summary>0…1 Athena level per buffer (AudioLevelCurve), for the HUD and the
    /// silence / trailing-speech decisions.</summary>
    public event Action<float>? Level;
    public event Action<Exception>? Failed;

    /// <summary>Live tap: PCM16 mono 16 kHz chunk, roughly every capture buffer.
    /// Fired on the audio thread; subscribers must be fast and non-throwing.</summary>
    public event Action<ReadOnlyMemory<byte>>? PcmChunk;

    /// <summary>Latest metered level, readable synchronously by the trailing-
    /// capture loop (events alone would need a closure to poll).</summary>
    public volatile float CurrentLevel;

    /// <summary>The capture graph's actual format — internal to the recorder
    /// (the tap downmix and the file writer read it; the stream declares
    /// NativeSampleRate, available before Start via the warm graph).</summary>
    private WaveFormat? CaptureFormat => _capture?.WaveFormat;

    /// <summary>The capture graph's native sample rate, available BEFORE
    /// Start() once Warm() has built the graph (the warm pool guarantees
    /// this). Lets the coordinator open the realtime stream with the
    /// correct declared rate before the first buffer exists — the session
    /// must exist to receive the first words, exactly like the web client
    /// (socket open → session.update → THEN getUserMedia flows).
    /// Null only on a cold, never-warmed recorder; callers fall back to
    /// 48000, the Windows shared-mode default.</summary>
    public int? NativeSampleRate => _capture?.WaveFormat.SampleRate;

    /// <summary>Monotonic capture clock for the hotkey grammar and duration math.</summary>
    public static double Now => Environment.TickCount64 / 1000.0;

    public string? CurrentPath => _rawPath;
    public bool IsRecording => _capture is { CaptureState: CaptureState.Starting or CaptureState.Capturing };

    /// <summary>Identity of the capture device this recorder is bound to, or
    /// null when no graph exists. The warm pool compares it against the current
    /// default before trusting a spare.</summary>
    public string? DeviceId => _deviceId;

    /// <summary>The exception that made the last Warm() fail, for diagnostics —
    /// a swallowed prewarm failure must still be observable.</summary>
    public Exception? LastWarmError { get; private set; }

    /// <summary>True when a capture graph exists and is initialized but not
    /// started. Preparing is not recording: no audio flows and no microphone
    /// indicator appears until Start().</summary>
    public bool IsWarmed => _capture is not null && _capture.CaptureState == CaptureState.Stopped;

    public WavRecorder(SynchronizationContext? uiContext = null)
    {
        // A recorder built by the warm pool's background thread would otherwise
        // post level events with no marshaling target — HUD updates must land
        // on the UI thread regardless of who constructed the recorder.
        _sync = uiContext ?? SynchronizationContext.Current;
    }

    /// <summary>Build the capture graph ahead of time (device resolution + WASAPI
    /// client activation + format negotiation). No audio flows; the mic
    /// indicator does not light. Safe to call twice; never throws out.
    /// Returns false when no input device exists (Start will report that too).</summary>
    public bool Warm()
    {
        try
        {
            if (IsRecording) return true;
            if (_capture is not null) return true; // already warm
            var device = WasapiCapture.GetDefaultCaptureDevice();
            _deviceId = device.ID;
            _capture = new WasapiCapture(device)
            {
                ShareMode = AudioClientShareMode.Shared,
            };
            // Native mix format: capture stays in whatever the device delivers.
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Failed?.Invoke(e.Exception);
            };
            return true;
        }
        catch (Exception ex)
        {
            // A failed prewarm must never poison a session: Start() rebuilds
            // from scratch and surfaces any real error through its own path.
            LastWarmError = ex;
            _capture = null;
            _deviceId = null;
            return false;
        }
    }

    public void Start(string path)
    {
        _rawPath = path;
        EnsureCaptureGraph();
        _writer = new WaveFileWriter(path, _capture!.WaveFormat);
        // Fresh tap state per session: warm-pool recorders are RECYCLED, and
        // a reused carry would bleed the previous session's final samples
        // into this session's first streamed chunk.
        _tapCarry = Array.Empty<byte>();
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    /// <summary>Reuse the warm graph if one exists (device unchanged), otherwise
    /// build cold exactly as before. A stale graph from a device swap is torn
    /// down here — the session always wins over the optimization.</summary>
    private void EnsureCaptureGraph()
    {
        if (_capture is not null)
        {
            var current = SafeCurrentDeviceId();
            if (current is null || current != _deviceId)
            {
                // Default input changed (or enumeration failed) under the warm
                // graph: drop it and build fresh against today's default.
                _capture.Dispose();
                _capture = null;
                _deviceId = null;
            }
        }
        if (_capture is null)
        {
            var device = WasapiCapture.GetDefaultCaptureDevice();
            _deviceId = device.ID;
            _capture = new WasapiCapture(device)
            {
                ShareMode = AudioClientShareMode.Shared,
            };
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Failed?.Invoke(e.Exception);
            };
        }
    }

    private static string? SafeCurrentDeviceId()
    {
        try { return WasapiCapture.GetDefaultCaptureDevice().ID; }
        catch { return null; }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);

            var format = _capture?.WaveFormat;
            if (format is null || e.BytesRecorded == 0) return;

            float rms = 0;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                var sampleCount = e.BytesRecorded / 4;
                double sum = 0;
                for (var i = 0; i < sampleCount; i++)
                {
                    var sample = BitConverter.ToSingle(e.Buffer, i * 4);
                    sum += sample * sample;
                }
                rms = MathF.Sqrt((float)(sum / sampleCount));
            }
            else // PCM 16 (or fallback — treat as 16-bit)
            {
                var sampleCount = e.BytesRecorded / 2;
                double sum = 0;
                for (var i = 0; i < sampleCount; i++)
                {
                    var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    sum += sample * sample;
                }
                rms = MathF.Sqrt((float)(sum / sampleCount));
            }

            var level = AudioLevelCurve.LevelFromRms(rms);
            CurrentLevel = level;
            if (_sync is not null) _sync.Post(_ => Level?.Invoke(level), null);
            else Level?.Invoke(level);

            PushLiveTap(e.Buffer, e.BytesRecorded, format);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    /// <summary>Emit this buffer as PCM16 MONO at the NATIVE capture rate —
    /// exactly what the server's own web client streams (StereoPanner→mono,
    /// Math.round(x*32767), no resampling). Downmix (L+R)/2; carry torn
    /// frames. The declared session rate matches, so the server resamples
    /// internally. Any tap failure drops that buffer only — the durable
    /// file is untouched.</summary>
    private void PushLiveTap(byte[] buffer, int bytes, WaveFormat source)
    {
        try
        {
            if (PcmChunk is null || bytes <= 0) return;

            // Prior partial-frame carry + this buffer: the downmix never sees
            // a torn sample frame at a buffer boundary. HOT PATH (~every 10ms):
            // zero LINQ — the old Concat/Skip/ToArray chain allocated 4+ arrays
            // per buffer on the audio thread. This allocates ONE joined buffer
            // only when a carry exists (the common aligned case reads `buffer`
            // directly) and keeps the carry in a right-sized array.
            var frameSize = source.Channels * (source.BitsPerSample / 8);
            var total = _tapCarry.Length + bytes;
            var whole = total - (total % frameSize);
            var carryLen = total - whole;

            byte[] input;
            if (_tapCarry.Length == 0)
            {
                input = buffer; // aligned: no copy at all
            }
            else
            {
                input = new byte[total];
                Buffer.BlockCopy(_tapCarry, 0, input, 0, _tapCarry.Length);
                Buffer.BlockCopy(buffer, 0, input, _tapCarry.Length, bytes);
            }

            // Stash the tail (whole..total) as the next carry.
            if (carryLen > 0)
            {
                if (_tapCarry.Length != carryLen) _tapCarry = new byte[carryLen];
                Buffer.BlockCopy(input, whole, _tapCarry, 0, carryLen);
            }
            else _tapCarry = Array.Empty<byte>();

            var frames = whole / frameSize;
            if (frames <= 0) return;
            var mono = new byte[frames * 2];
            var channels = source.Channels;
            // WASAPI shared mode delivers IEEE float32 (−1…1); int16 sources
            // are normalized to the same range first — then BOTH convert to
            // PCM16 exactly as the server's web client does:
            // round(x * 32767), clamped.
            var isFloat = source.BitsPerSample == 32
                && source.Encoding == WaveFormatEncoding.IeeeFloat;
            var sampleBytes = source.BitsPerSample / 8;
            for (var f = 0; f < frames; f++)
            {
                double acc = 0;
                for (var c = 0; c < channels; c++)
                {
                    var idx = f * frameSize + c * sampleBytes;
                    acc += isFloat
                        ? BitConverter.ToSingle(input, idx)
                        : BitConverter.ToInt16(input, idx) / 32768.0;
                }
                var v = (int)Math.Clamp(
                    (int)Math.Round(acc / channels * 32767.0),
                    short.MinValue, short.MaxValue);
                Buffer.BlockCopy(new[] { (short)v }, 0, mono, f * 2, 2);
            }
            PcmChunk.Invoke(mono);
        }
        catch
        {
            // Live path is best-effort only.
        }
    }

    /// <summary>Stop capture, flush the raw file, then transcode to the 16 kHz
    /// mono 16-bit format the ASR server expects (Athena's encode-at-key-up step).
    /// The returned path is the final audio.wav; the raw capture is removed.</summary>
    public async Task<string?> StopAsync(CancellationToken ct = default)
    {
        if (_capture is null) return _rawPath;
        var rawPath = _rawPath;

        var tcs = new TaskCompletionSource();
        void Stopped(object? s, StoppedEventArgs e) => tcs.TrySetResult();
        _capture.RecordingStopped += Stopped;
        _capture.StopRecording();
        await tcs.Task.WaitAsync(ct);
        _capture.RecordingStopped -= Stopped;

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        _capture.Dispose();
        _capture = null;

        if (rawPath is null || !File.Exists(rawPath)) return rawPath;

        // Transcode native-format capture → 16k mono 16-bit for ASR.
        var finalPath = Path.Combine(
            Path.GetDirectoryName(rawPath)!, "audio.wav");
        try
        {
            // Disposal MUST complete before the raw file is deleted: the reader
            // holds capture.wav open until the end of its using scope, and a
            // File.Delete under an open handle throws on Windows — the old
            // block-scoped version silently failed EVERY transcode and
            // returned the raw 48kHz capture instead.
            await using (var source = new AudioFileReader(rawPath))
            using (var resampler = new MediaFoundationResampler(source, new WaveFormat(16000, 16, 1))
            {
                ResamplerQuality = 60,
            })
            {
                WaveFileWriter.CreateWaveFile(finalPath, resampler);
            }
            // Debug aid (ATHENA_DUMP_TAP=1): keep the raw stereo capture next
            // to the transcode so the live tap's downmix can be diffed against
            // the mic's ground truth when words go missing.
            if (Environment.GetEnvironmentVariable("ATHENA_DUMP_TAP") == "1")
                File.Copy(rawPath,
                    Path.Combine(Path.GetDirectoryName(rawPath)!, "capture-debug.wav"),
                    overwrite: true);
            File.Delete(rawPath);
            return finalPath;
        }
        catch
        {
            // Transcode failure must not lose words: keep the raw file and let
            // the pipeline try it as-is (most servers accept other PCM rates).
            return rawPath;
        }
    }

    public static TimeSpan DurationOf(string path)
    {
        try
        {
            using var wf = new WaveFileReader(path);
            return wf.TotalTime;
        }
        catch { return TimeSpan.Zero; }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _writer?.Dispose();
        _capture = null;
        _writer = null;
    }
}

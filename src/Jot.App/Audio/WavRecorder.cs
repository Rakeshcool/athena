// Windows-port original: microphone capture, mirroring JotCore's AudioEngine.
// Capture runs in the device's native mix format (always accepted in WASAPI
// shared mode) and is transcoded to 16 kHz mono 16-bit after stop — the same
// "encode at key-up" shape as Jot's FLAC pipeline, and immune to devices that
// reject a non-mix capture format. The RMS meter reads the native format.
//
// Live streaming: a parallel tap converts each buffer to PCM16 mono 16 kHz
// (the realtime endpoint's format) and hands it to PcmChunk AS SOON AS the
// buffer arrives — the file is still the durable record; the tap is fire-and-
// forget for the HUD/live-text path. Conversion is allocation-light: one
// resampler per recorder, reused across buffers.

using System.IO;
using Jot.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jot.App.Audio;

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

    // Live-tap state: source format observed so far + sub-frame carry bytes.
    private WaveFormat? _tapSourceFormat;
    private byte[] _tapCarry = Array.Empty<byte>();

    /// <summary>0…1 Jot level per buffer (AudioLevelCurve), for the HUD and the
    /// silence / trailing-speech decisions.</summary>
    public event Action<float>? Level;
    public event Action<Exception>? Failed;

    /// <summary>Live tap: PCM16 mono 16 kHz chunk, roughly every capture buffer.
    /// Fired on the audio thread; subscribers must be fast and non-throwing.</summary>
    public event Action<ReadOnlyMemory<byte>>? PcmChunk;

    /// <summary>Latest metered level, readable synchronously by the trailing-
    /// capture loop (events alone would need a closure to poll).</summary>
    public volatile float CurrentLevel;

    /// <summary>Monotonic capture clock for the hotkey grammar and duration math.</summary>
    public static double Now => Environment.TickCount64 / 1000.0;

    public string? CurrentPath => _rawPath;
    public bool IsRecording => _capture is { CaptureState: CaptureState.Starting or CaptureState.Capturing };

    /// <summary>Identity of the capture device this recorder is bound to, or
    /// null when no graph exists. The warm pool compares it against the current
    /// default before trusting a spare (macOS WarmEnginePool.refresh()).</summary>
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
    /// client activation + format negotiation — the 75–135ms the macOS port notes
    /// measured as exactly where first words were lost). No audio flows; the mic
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
        _tapSourceFormat = _capture.WaveFormat;
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

    /// <summary>Convert this buffer to PCM16 mono 16 kHz and emit it. Any tap
    /// failure just drops that buffer — the file and the file-endpoint fallback
    /// are untouched, so the live path is strictly best-effort.</summary>
    private void PushLiveTap(byte[] buffer, int bytes, WaveFormat source)
    {
        try
        {
            if (PcmChunk is null || bytes <= 0) return;
            if (_tapSourceFormat is null || !_tapSourceFormat.Equals(source))
            {
                _tapSourceFormat = source;
                _tapCarry = Array.Empty<byte>();
            }

            // Prior partial-sample carry + this buffer, so the converter never
            // sees a torn sample frame at a buffer boundary.
            var input = _tapCarry.Length == 0 ? buffer
                : _tapCarry.Concat(buffer.Take(bytes)).ToArray();

            var frameSize = source.Channels * (source.BitsPerSample / 8);
            var whole = input.Length - (input.Length % frameSize);
            _tapCarry = input.Skip(whole).ToArray();

            var outChunk = ResampleToPcm16Mono16k(input, whole, source);
            if (outChunk.Length > 0) PcmChunk.Invoke(outChunk);
        }
        catch
        {
            // Live path is best-effort only.
        }
    }

    private static byte[] ResampleToPcm16Mono16k(byte[] input, int count, WaveFormat source)
    {
        // A per-call resampler keeps buffer boundaries clean (thread-safe,
        // stateless) — cost is negligible at 16 kHz chunk sizes.
        var stream = new MemoryStream(input, 0, count, writable: false);
        using var src = new RawSourceWaveStream(stream, source);
        using var r = new MediaFoundationResampler(src, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 40,
        };
        using var ms = new MemoryStream();
        var outBuf = new byte[64 * 1024];
        while (true)
        {
            var read = r.Read(outBuf, 0, outBuf.Length);
            if (read <= 0) break;
            ms.Write(outBuf, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Stop capture, flush the raw file, then transcode to the 16 kHz
    /// mono 16-bit format the ASR server expects (Jot's encode-at-key-up step).
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
            await using var source = new AudioFileReader(rawPath);
            using var resampler = new MediaFoundationResampler(source, new WaveFormat(16000, 16, 1))
            {
                ResamplerQuality = 60,
            };
            WaveFileWriter.CreateWaveFile(finalPath, resampler);
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

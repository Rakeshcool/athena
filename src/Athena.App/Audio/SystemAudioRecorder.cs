// Windows-port original: SYSTEM AUDIO capture via WASAPI loopback
// (WasapiLoopbackCapture). Records whatever the selected Windows output device
// is currently PLAYING — a Zoom/Meet call, YouTube, Spotify, a video, game
// audio — without touching the microphone. The pipeline shape mirrors
// WavRecorder exactly (native mix format to disk from t=0; PCM16-mono live tap
// for the realtime stream; transcode to 16 kHz mono at stop) so the
// coordinator treats the two recorders identically.
//
// Loopback nuances the mic path doesn't have:
//  - Capture starts from t=0 like the mic, but a SILENT device can deliver
//    NO buffers at all (WASAPI loopback only pushes data while a render
//    client plays). A silent meeting at latch time = an empty file; the
//    existing Silent-status path already handles that honestly.
//  - The output device can change mid-take (user switches to headphones).
//    The capture graph then goes quiet forever — a watchdog detects a
//    bufferless stretch after the first byte and raises Failed so the take
//    can be retried against the new device rather than recording silence.
//  - A device with NO output endpoint fails at construction; the latch
//    refuses and the grammar resets (no phantom session).

using System.IO;
using Athena.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Athena.App.Audio;

public sealed class SystemAudioRecorder : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _rawPath;
    private readonly SynchronizationContext? _sync;
    // DownmixToMonoPcm16 mutates its carry by ref; WavRecorder's field pattern
    // doesn't translate through closures, so the loopback tap keeps its carry
    // in a one-element box.
    private byte[][] _carryBox = [Array.Empty<byte>()];

    private long _lastBufferAtTicks;
    private bool _sawFirstBuffer;

    public event Action<float>? Level;
    public event Action<Exception>? Failed;
    /// <summary>Live tap: PCM16 mono at the NATIVE output rate, per buffer.</summary>
    public event Action<ReadOnlyMemory<byte>>? PcmChunk;

    public volatile float CurrentLevel;
    public string? CurrentPath => _rawPath;
    public bool IsRecording => _capture is { CaptureState: CaptureState.Starting or CaptureState.Capturing };
    public int? NativeSampleRate => _capture?.WaveFormat.SampleRate;

    /// <summary>The watchdog fires after this much silence following the first
    /// buffer — the signature of the output device having changed under us.</summary>
    private static readonly int DeviceChangeSilenceMs = 3000;

    public SystemAudioRecorder(SynchronizationContext? uiContext = null)
    {
        // Same rule as WavRecorder: level events must reach the UI thread no
        // matter which thread constructed the recorder.
        _sync = uiContext ?? SynchronizationContext.Current;
    }

    /// <summary>Warm the loopback graph (device activation + format negotiation)
    /// while idle. Never throws out; safe to call twice; false when no output
    /// endpoint exists.</summary>
    public bool Warm()
    {
        try
        {
            if (IsRecording) return true;
            if (_capture is not null) return true;
            BuildGraph();
            return true;
        }
        catch (Exception ex)
        {
            LastWarmError = ex;
            _capture = null;
            return false;
        }
    }

    public Exception? LastWarmError { get; private set; }

    private void BuildGraph()
    {
        _capture = new WasapiLoopbackCapture(); // default OUTPUT device, shared mode
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Failed?.Invoke(e.Exception);
        };
    }

    /// <summary>Start capturing to <paramref name="path"/>'s DIRECTORY. The RAW
    /// capture is named system-capture.wav (mirroring the mic's capture.wav)
    /// so a dual-source session never has its two writers collide on one
    /// capture file — that collision surfaces as "file used by another
    /// process" and fails the transcription. StopAsync later transcodes to a
    /// DIFFERENT final name (audio-system.wav) — raw and final must not share
    /// a name, or the transcode would overwrite its own source and the
    /// following cleanup would delete the output. Returns the actual raw
    /// path; the coordinator stores what this returns.</summary>
    public string Start(string path)
    {
        if (_capture is null) BuildGraph();
        var dir = Path.GetDirectoryName(path)!;
        _rawPath = Path.Combine(dir, "system-capture.wav");
        _writer = new WaveFileWriter(_rawPath, _capture!.WaveFormat);
        _sawFirstBuffer = false;
        _lastBufferAtTicks = Environment.TickCount64;
        _carryBox[0] = Array.Empty<byte>();
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
        return _rawPath;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            _sawFirstBuffer |= e.BytesRecorded > 0;
            _lastBufferAtTicks = Environment.TickCount64;

            var format = _capture?.WaveFormat;
            if (format is null) return;

            var rms = AudioTapMath.Rms(e.Buffer, e.BytesRecorded, format);
            var level = AudioLevelCurve.LevelFromRms(rms);
            CurrentLevel = level;
            if (_sync is not null) _sync.Post(_ => Level?.Invoke(level), null);
            else Level?.Invoke(level);

            if (PcmChunk is not null)
                AudioTapMath.DownmixToMonoPcm16(e.Buffer, e.BytesRecorded, format, ref _carryBox[0],
                    chunk => PcmChunk.Invoke(chunk));
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    /// <summary>Called by the owner while latched: raises Failed when the output
    /// device went silent-under-us (changed, unplugged) — otherwise a take
    /// could sit recording nothing until Esc. Only arms after the first real
    /// buffer: loopback delivers NOTHING on a genuinely silent device, and
    /// silence is not a failure.</summary>
    public void PollForDeviceChange()
    {
        if (!_sawFirstBuffer || _capture is null) return;
        if (Environment.TickCount64 - _lastBufferAtTicks > DeviceChangeSilenceMs)
            Failed?.Invoke(new IOException("system audio went silent — output device may have changed"));
    }

    /// <summary>Stop, flush, transcode native mix format → 16 kHz mono 16-bit
    /// (the file endpoint's format). Same disposal ordering as WavRecorder:
    /// the reader must be closed before the raw file is deleted.</summary>
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

        // A DIFFERENT final name than the raw capture (see Start), and
        // different from the MIC half's audio.wav: writing audio.wav here too
        // meant the two transcodes collided — whichever finished second
        // overwrote the first, and the success-path cleanup then deleted the
        // file the history row references (broken playback for dual takes).
        var finalPath = Path.Combine(Path.GetDirectoryName(rawPath)!, "audio-system.wav");
        try
        {
            await using (var source = new AudioFileReader(rawPath))
            using (var resampler = new MediaFoundationResampler(source, new WaveFormat(16000, 16, 1))
            {
                ResamplerQuality = 60,
            })
            {
                WaveFileWriter.CreateWaveFile(finalPath, resampler);
            }
            File.Delete(rawPath);
            return finalPath;
        }
        catch
        {
            // Transcode failure must not lose words: keep the raw file and let
            // the pipeline try it as-is.
            return rawPath;
        }
    }

    public static TimeSpan DurationOf(string path) => WavRecorder.DurationOf(path);

    public void Dispose()
    {
        _capture?.Dispose();
        _writer?.Dispose();
        _capture = null;
        _writer = null;
    }
}

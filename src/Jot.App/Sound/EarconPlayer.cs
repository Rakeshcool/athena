// Windows-port original: plays the synthesized G-major earcon family at state
// transitions (frame-synced to SetState, like the macOS SoundEngine). Sounds
// are rendered once at startup into memory WAVs; System.Media.SoundPlayer
// plays them async so the UI thread never blocks on audio.

using System.IO;
using Jot.Core;

namespace Jot.App.Sound;

public sealed class EarconPlayer : IDisposable
{
    private readonly Dictionary<Earcon, System.Media.SoundPlayer> _players = new();
    private readonly Dictionary<Earcon, MemoryStream> _streams = new();

    public bool Enabled { get; set; } = true;

    public EarconPlayer()
    {
        foreach (var earcon in Enum.GetValues<Earcon>())
        {
            var wav = EarconSynth.Render(earcon);
            var stream = new MemoryStream(wav);
            _streams[earcon] = stream;
            var player = new System.Media.SoundPlayer(stream);
            player.Load(); // pre-load so the first play has no latency
            _players[earcon] = player;
        }
    }

    public void Play(Earcon earcon)
    {
        if (!Enabled) return;
        try
        {
            _players[earcon].Play();
        }
        catch { /* audio device contention must never break a session */ }
    }

    public void Dispose()
    {
        foreach (var p in _players.Values) p.Dispose();
        foreach (var s in _streams.Values) s.Dispose();
    }
}

// Ported from JotCore/Sources/TranscriptionClient/TimeoutPolicy.swift.
// The single source of truth for every network deadline in the app.
// (Critic reconciliation #9 — no other file may define timeout constants.)

namespace Athena.Core;

public static class TimeoutPolicy
{
    public const double ConnectSeconds = 5;
    /// <summary>Time to the first byte after the request body is sent.</summary>
    public const double TimeToFirstByteSeconds = 10;
    /// <summary>Max gap between chunks once streaming has begun (unused for batch local calls).</summary>
    public const double InterChunkStallSeconds = 10;
    /// <summary>When the HUD flips to the "Still working…" slow state.</summary>
    public const double SlowStateUISeconds = 3;

    /// <summary>Overall per-request deadline. Scales gently with audio length:
    /// 5s clip → 31s; 10min clip → 2.5min. Local servers are fast, but a cold
    /// model load can be slow — bounded, because the user is staring at a pill.</summary>
    public static TimeSpan OverallDeadline(TimeSpan audioDuration) =>
        TimeSpan.FromSeconds(30 + audioDuration.TotalSeconds / 4);

    /// <summary>LLM cleanup pass deadline — the cleanup model is small/local but
    /// reasoning models can think for a while on long transcripts.</summary>
    public static TimeSpan CleanupDeadline(TimeSpan audioDuration) =>
        TimeSpan.FromSeconds(20 + audioDuration.TotalSeconds / 2);
}

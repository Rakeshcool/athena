// Windows-port original: which transcript becomes the FINAL inserted text.
// The realtime stream and the file endpoint are separate products:
//
//   • partials (live words in the HUD) come only from the stream, when it is on
//   • the final text comes from the stream OR from the on-disk recording's
//     file decode, decided per session by policy — never by accident
//
// Evidence for the default (stream partials + file final): replaying a
// session's own recording through the realtime endpoint dropped mid-stream
// words that the file endpoint transcribed completely from the same audio —
// the file decode sees the whole utterance at once, the stream commits to
// words with no forward context.

namespace Athena.Core;

/// <summary>Where the inserted text of a dictation comes from.</summary>
public enum TranscriptSource
{
    /// <summary>The realtime stream's final transcript (lowest latency; a
    /// server-side stream loss is a text loss).</summary>
    LiveStream,
    /// <summary>The file endpoint's decode of the on-disk recording (whole-
    /// utterance context; the recording always exists — crash-safety rule).</summary>
    RecordedFile,
}

public static class TranscriptSourcePolicy
{
    /// <summary>Decide the final-text source from the two toggles:
    /// streaming partials on + file fallback on → stream final (the stream
    /// is trusted end-to-end; the file path remains failure recovery);
    /// fallback off with streaming on → RecordedFile (partials are display
    /// only, the file decode is the final source); streaming off → the file
    /// path, whatever the fallback toggle says (nothing streamed exists).
    /// Failure recovery is not a policy decision: a dead stream always
    /// degrades to the file regardless of this verdict.</summary>
    public static TranscriptSource FinalSource(bool streaming, bool fileFallback) =>
        streaming && fileFallback ? TranscriptSource.LiveStream : TranscriptSource.RecordedFile;
}

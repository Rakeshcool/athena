// Tests for the partials/final-source split. The two toggles answer different
// questions: streaming = "do live partial words appear at all", file fallback
// = "may the stream's final be the INSERTED text". The matrix is the whole
// contract — especially that streaming off means the file path regardless of
// the fallback toggle (no stream exists to insert from).

using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

public class TranscriptSourcePolicyTests
{
    [Fact]
    public void Streaming_on_plus_fallback_on_trusts_the_stream_final() =>
        Assert.Equal(TranscriptSource.LiveStream,
            TranscriptSourcePolicy.FinalSource(streaming: true, fileFallback: true));

    [Fact]
    public void Streaming_on_plus_fallback_off_keeps_the_file_decode_as_final() =>
        // The live evidence: a session's own audio replayed realtime dropped
        // words the file endpoint returned completely — partials are display
        // only, the whole-utterance decode is the final source.
        Assert.Equal(TranscriptSource.RecordedFile,
            TranscriptSourcePolicy.FinalSource(streaming: true, fileFallback: false));

    [Fact]
    public void Streaming_off_means_the_file_path_even_with_fallback_on() =>
        // No stream session exists — there is nothing to insert from.
        Assert.Equal(TranscriptSource.RecordedFile,
            TranscriptSourcePolicy.FinalSource(streaming: false, fileFallback: true));

    [Fact]
    public void Both_off_is_the_file_path() =>
        Assert.Equal(TranscriptSource.RecordedFile,
            TranscriptSourcePolicy.FinalSource(streaming: false, fileFallback: false));
}

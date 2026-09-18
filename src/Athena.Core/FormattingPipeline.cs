// Windows-port original: orchestrates the ported formatting stack.
// raw transcript → steering prompt → local LLM → artifact strip → validation
// gate → deterministic replacements. On gate rejection the RAW transcript is
// used (it already has punctuation from the ASR model — a high-quality
// fallback), mirroring JotCore's "never insert garbage" rule.

using Athena.Core.Clients;

namespace Athena.Core;public sealed class FormattingPipeline
{
    private readonly ICleaner _cleaner;
    private readonly DictionaryStore? _dictionary;

    public FormattingPipeline(ICleaner cleaner) => _cleaner = cleaner;

    /// <summary>Dictionary-aware pipeline: terms feed the prompt (suggest),
    /// replacement rules run after the model (enforce).</summary>
    public FormattingPipeline(DictionaryStore dictionary, ICleaner cleaner)
        : this(cleaner) => _dictionary = dictionary;

    public async Task<string> ProcessAsync(
        string raw,
        ToneCategory tone = ToneCategory.Neutral,
        IReadOnlyList<string>? vocabulary = null,
        IReadOnlyList<ReplacementEngine.Rule>? spellings = null,
        CancellationToken ct = default)
    {
        if (_dictionary is not null)
        {
            vocabulary ??= _dictionary.TermsSnapshot();
            spellings ??= _dictionary.RulesSnapshot();
        }
        // Chat structure: rules in the system message, raw in the user message,
        // "CLEAN:" prefilled as the assistant turn. Live-probed against the local
        // reasoning LLM: no reasoning tokens, sub-second latency, and — unlike the
        // long single-prompt form — self-corrections are applied correctly.
        var system = PromptV1.CleanupSystemPrompt(tone, vocabulary,
            spellings?.Select(r => (r.Wrong, r.Right)).ToList());

        var cleanedRaw = await _cleaner.CleanAsync(
            system, PromptV1.RawUserMessage(raw), PromptV1.AssistantPrefill, ct);
        var cleaned = ValidationGate.StripArtifacts(cleanedRaw);

        if (cleaned.Length == 0)
            return raw; // empty output → raw fallback (never insert nothing)

        var verdict = ValidationGate.Validate(raw, cleaned);
        if (!verdict.Accepted)
            return raw; // gate rejected → raw fallback

        var spell = spellings is { Count: > 0 } ? spellings : Array.Empty<ReplacementEngine.Rule>();
        return ReplacementEngine.Apply(spell, cleaned);
    }
}

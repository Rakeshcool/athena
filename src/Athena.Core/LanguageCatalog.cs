// Windows-port original: the ASR language catalog. The local NeMo ASR server
// accepts a BCP-47 `language` hint (session.update.language over the realtime
// WebSocket; `language` field on the file endpoint). en-US is the default.
// "auto" selects model-side language detection (the model then appends an
// <xx-XX> tag after terminal punctuation — StripLanguageTags removes it).

using System.Text.RegularExpressions;

namespace Athena.Core;

/// <summary>Languages the ASR model supports (mirrors the server's own UI list).</summary>
public static class LanguageCatalog
{
    public const string Default = "en-US";
    public const string Auto = "auto";

    /// <summary>Code → display name, in UI order (auto first, opt-in).</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All = new[]
    {
        (Auto, "Auto-detect"),
        ("en-US", "English (US)"),
        ("en-GB", "English (UK)"),
        ("es-ES", "Spanish (Spain)"),
        ("es-US", "Spanish (US)"),
        ("it-IT", "Italian"),
        ("pt-BR", "Portuguese (Brazil)"),
        ("pt-PT", "Portuguese (Portugal)"),
        ("hi-IN", "Hindi"),
        ("ko-KR", "Korean"),
        ("de-DE", "German"),
        ("fr-FR", "French (France)"),
        ("fr-CA", "French (Canada)"),
        ("ru-RU", "Russian"),
        ("tr-TR", "Turkish"),
        ("vi-VN", "Vietnamese"),
        ("nl-NL", "Dutch"),
        ("ja-JP", "Japanese"),
        ("ar-AR", "Arabic"),
        ("uk-UA", "Ukrainian"),
    };

    /// <summary>True when the code is one the server documents. Unknown codes
    /// still pass through — the server owns the final say.</summary>
    public static bool IsSupported(string code) =>
        All.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>BCP-47 code, normalized (case-insensitive match, canonical casing).</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Default;
        var hit = All.FirstOrDefault(l => l.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit.Code ?? code.Trim();
    }

    private static readonly Regex LanguageTag = new(
        "</?[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?>",
        RegexOptions.Compiled);

    /// <summary>Auto-detect mode emits a <xx-XX> tag after terminal punctuation;
    /// strip them so transcripts never leak model markup into the editor.</summary>
    public static string StripLanguageTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var stripped = LanguageTag.Replace(text, " ");
        return Regex.Replace(stripped, "\\s+", " ").Trim();
    }
}

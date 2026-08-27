using System.Text;

namespace DecoSOP.Services.Search;

/// <summary>A user's typed query, translated into FTS5 MATCH expressions.</summary>
public sealed record ParsedQuery(string MatchAll, string MatchAny, IReadOnlyList<string> Terms)
{
    public bool IsEmpty => Terms.Count == 0;
    public static readonly ParsedQuery Empty = new(string.Empty, string.Empty, []);
}

/// <summary>
/// Turns free text into a safe FTS5 MATCH expression.
///
/// Everything the user types is quoted before it reaches FTS5, so stray operators can't produce
/// a syntax error mid-typing — someone halfway through typing an apostrophe or a quotation mark
/// should get no results, never an exception.
/// </summary>
public static class SearchQueryParser
{
    public static ParsedQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ParsedQuery.Empty;

        var tokens = Tokenize(raw);
        if (tokens.Count == 0) return ParsedQuery.Empty;

        // Every bare word is broadened to a prefix search on its root, which does the job a
        // stemmer would without the dead zone a stemmed index creates while you are still
        // typing. Quoted phrases are left exactly as written.
        var parts = new List<string>(tokens.Count);
        foreach (var (text, isPhrase) in tokens)
        {
            if (isPhrase)
            {
                parts.Add(Quote(text));
                continue;
            }

            var root = Broaden(text);
            // Very short words are left exact — "ok*" would match half the library.
            parts.Add(root.Length >= 3 ? Quote(root) + "*" : Quote(text));
        }

        return new ParsedQuery(
            MatchAll: string.Join(" AND ", parts),
            MatchAny: string.Join(" OR ", parts),
            Terms: tokens.Select(t => t.Text).ToList());
    }

    private static string Quote(string text) => '"' + text.Replace("\"", "\"\"") + '"';

    /// <summary>
    /// Common English suffixes, longest first so "ization" wins over "ation" and "s".
    /// </summary>
    private static readonly string[] Suffixes =
    [
        "izations", "isations", "ization", "isation", "ements", "ements", "ations", "ement",
        "ation", "ities", "ility", "ances", "ancies", "ance", "ences", "ence", "ments",
        "ment", "ings", "ing", "izes", "ized", "ize", "ises", "ised", "ise", "ies", "ied",
        "ers", "est", "ally", "ally", "als", "ed", "es", "ly", "s",
    ];

    /// <summary>
    /// Reduce a word to a root that can be prefix-matched, so "sterilize", "sterilization" and
    /// "sterilizing" all reach the same documents. This deliberately only ever widens the search:
    /// the result is always a prefix of the word the user typed, so a match can never disappear
    /// as they keep typing — which is exactly the failure a stemmed index produces.
    /// </summary>
    internal static string Broaden(string token)
    {
        // Anything with digits or punctuation is likely a code or a filename; leave it alone.
        if (token.Length < 5 || !token.All(char.IsLetter)) return token;

        foreach (var suffix in Suffixes)
        {
            if (token.Length - suffix.Length < 4) continue;
            if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return token[..^suffix.Length];
        }
        return token;
    }

    private static List<(string Text, bool IsPhrase)> Tokenize(string raw)
    {
        var tokens = new List<(string, bool)>();
        var current = new StringBuilder();
        var inQuotes = false;

        void Flush(bool isPhrase)
        {
            var text = current.ToString().Trim();
            current.Clear();
            // Punctuation-only fragments contribute nothing and would match everything.
            if (text.Length > 0 && text.Any(char.IsLetterOrDigit)) tokens.Add((text, isPhrase));
        }

        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                Flush(inQuotes);
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch)) Flush(false);
            else current.Append(ch);
        }
        Flush(inQuotes);

        // Guard against someone pasting an essay into the box.
        return tokens.Take(16).ToList();
    }
}

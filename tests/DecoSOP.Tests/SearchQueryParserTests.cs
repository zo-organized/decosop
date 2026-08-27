using DecoSOP.Services.Search;
using Xunit;

namespace DecoSOP.Tests;

/// <summary>
/// The query parser is where this feature's subtlest bug lived. The original index used a porter
/// stemmer, which stems the query as well as the content and so punched a hole in the middle of
/// every prefix search: "sterilization" indexed as "steril", making "steril" match but "sterili"
/// and "steriliz" match nothing at all. Results vanished mid-word. Morphology moved to the query
/// side to fix it, and the property that matters is captured here as a test so it cannot regress.
/// </summary>
public class SearchQueryParserTests
{
    // ---- the regression that motivated the design ----

    [Theory]
    [InlineData("sterilization")]
    [InlineData("insurance")]
    [InlineData("radiographs")]
    [InlineData("appointment")]
    public void BroadeningNeverGrowsAsAWordIsTyped(string word)
    {
        // Typing a word one letter at a time must never narrow to something that cannot match
        // what the finished word matches. The root of each prefix must stay a prefix of the
        // root of the whole word — that is exactly the invariant the stemmer violated.
        var finalRoot = SearchQueryParser.Broaden(word);

        for (var length = 4; length < word.Length; length++)
        {
            var typedSoFar = word[..length];
            var rootSoFar = SearchQueryParser.Broaden(typedSoFar);

            Assert.True(
                finalRoot.StartsWith(rootSoFar, StringComparison.OrdinalIgnoreCase)
                || rootSoFar.StartsWith(finalRoot, StringComparison.OrdinalIgnoreCase),
                $"Typing '{typedSoFar}' produced root '{rootSoFar}', which is unrelated to the " +
                $"finished word's root '{finalRoot}'. Results would blink out mid-word.");
        }
    }

    [Theory]
    [InlineData("sterilize", "sterilization")]
    [InlineData("refund", "refunds")]
    [InlineData("radiograph", "radiographs")]
    [InlineData("billing", "billed")]
    [InlineData("extraction", "extractions")]
    public void RelatedWordFormsShareARoot(string one, string other)
    {
        var a = SearchQueryParser.Broaden(one);
        var b = SearchQueryParser.Broaden(other);

        Assert.True(
            a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a, StringComparison.OrdinalIgnoreCase),
            $"'{one}' reduced to '{a}' and '{other}' to '{b}' — they would not find each other.");
    }

    // ---- short words are acronyms here, not word-stems ----

    [Theory]
    [InlineData("COB")]
    [InlineData("EOB")]
    [InlineData("par")]
    [InlineData("PPO")]
    public void ShortWordsAreMatchedExactlyNotAsPrefixes(string acronym)
    {
        // "par levels" broadened to par* once returned 159 results with nothing about par levels
        // in them, because par* also matches part, partial, parent, parameters.
        var parsed = SearchQueryParser.Parse(acronym);
        Assert.DoesNotContain("*", parsed.MatchAll);
    }

    [Fact]
    public void OrdinaryWordsDoGetAPrefixMatch()
    {
        Assert.Contains("*", SearchQueryParser.Parse("insurance").MatchAll);
    }

    // ---- safety: everything the user types is quoted before it reaches FTS5 ----

    [Theory]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("crown OR NOT")]
    [InlineData("a\"b\"c")]
    [InlineData("(unbalanced")]
    [InlineData("NEAR/2")]
    [InlineData("x' OR '1'='1")]
    public void HostileInputProducesABalancedQuotedExpression(string input)
    {
        var parsed = SearchQueryParser.Parse(input);

        // Every emitted token is wrapped in double quotes, with any embedded quote doubled.
        // An odd number of quote characters would mean an unterminated string in the MATCH
        // expression, which is the shape of an FTS5 syntax error.
        Assert.Equal(0, parsed.MatchAll.Count(c => c == '"') % 2);
        Assert.Equal(0, parsed.MatchAny.Count(c => c == '"') % 2);
    }

    [Fact]
    public void QuotesDelimitPhrasesRatherThanBeingSearchedFor()
    {
        // Corrects my own wrong assumption: a quote is phrase syntax, so it is consumed by the
        // tokenizer and never survives into a token. The doubling in Quote() is belt-and-braces
        // for a token that somehow contains one, which is why the hostile-input test above
        // asserts balance rather than escaping.
        var parsed = SearchQueryParser.Parse("say \"hello there\" now");
        Assert.Equal(new[] { "say", "hello there", "now" }, parsed.Terms);
    }

    // ---- phrases and structure ----

    [Fact]
    public void QuotedPhrasesStayExactAndUnbroadened()
    {
        var parsed = SearchQueryParser.Parse("\"space maintainer\"");
        Assert.Single(parsed.Terms);
        Assert.Equal("space maintainer", parsed.Terms[0]);
        Assert.DoesNotContain("*", parsed.MatchAll);
    }

    [Fact]
    public void AllTermsAreRequiredFirstAndAnyTermIsTheFallback()
    {
        var parsed = SearchQueryParser.Parse("insurance refund");
        Assert.Contains(" AND ", parsed.MatchAll);
        Assert.Contains(" OR ", parsed.MatchAny);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("!!! ??? ...")]
    public void NothingSearchableProducesAnEmptyQuery(string? input)
    {
        Assert.True(SearchQueryParser.Parse(input).IsEmpty);
    }

    [Fact]
    public void APastedEssayIsCappedRatherThanBuildingAnEnormousExpression()
    {
        var parsed = SearchQueryParser.Parse(string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}")));
        Assert.True(parsed.Terms.Count <= 16, $"Expected the token list to be capped, got {parsed.Terms.Count}.");
    }

    [Fact]
    public void CodesAndHyphenatedTermsAreNotMangled()
    {
        // Procedure codes and terms like x-ray contain non-letters; broadening must leave them be.
        Assert.Equal("D1110", SearchQueryParser.Broaden("D1110"));
        Assert.Equal("x-ray", SearchQueryParser.Broaden("x-ray"));
        Assert.Equal("pre-auth", SearchQueryParser.Broaden("pre-auth"));
    }

    [Fact]
    public void BroadeningNeverStripsAWordDownToANub()
    {
        // A root of one or two letters would match a large share of the library.
        foreach (var word in new[] { "billing", "stations", "operations", "sealants", "denial", "classes" })
            Assert.True(SearchQueryParser.Broaden(word).Length >= 4,
                $"'{word}' was reduced to '{SearchQueryParser.Broaden(word)}'.");
    }
}

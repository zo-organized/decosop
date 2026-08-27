using DecoSOP.Services.Extraction;
using Xunit;

namespace DecoSOP.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void RunsOfWhitespaceCollapseToASingleSpace()
    {
        Assert.Equal("one two", TextNormalizer.Normalize("one     \t  two"));
    }

    [Fact]
    public void AtMostOneBlankLineSurvives()
    {
        Assert.Equal("a\n\nb", TextNormalizer.Normalize("a\n\n\n\n\n\nb"));
    }

    [Fact]
    public void WindowsLineEndingsCountOnce()
    {
        Assert.Equal("a\nb", TextNormalizer.Normalize("a\r\nb"));
    }

    [Fact]
    public void ControlCharactersAreRemoved()
    {
        Assert.DoesNotContain('\u0007', TextNormalizer.Normalize("bell\u0007here"));
    }

    [Fact]
    public void PrivateUseGlyphsAreRemoved()
    {
        // Word stores Wingdings and Symbol characters in U+E000-U+F8FF. Left in, they reached
        // search snippets as tofu boxes.
        var normalized = TextNormalizer.Normalize("bullet \uf0b7 arrow \uf0e0 box \uf0a8 end");
        Assert.DoesNotContain('\uf0b7', normalized);
        Assert.DoesNotContain('\uf0e0', normalized);
        Assert.DoesNotContain('\uf0a8', normalized);
        Assert.Contains("bullet", normalized);
        Assert.Contains("end", normalized);
    }

    [Fact]
    public void TheHighlightSentinelsCannotOccurInIndexedText()
    {
        // The snippet defence assumes document content never contains U+0002 or U+0003, because
        // those stand in for the mark tags before HTML encoding. Normalization is what guarantees it.
        var normalized = TextNormalizer.Normalize("before \u0002 injected \u0003 after");
        Assert.DoesNotContain('\u0002', normalized);
        Assert.DoesNotContain('\u0003', normalized);
    }

    [Fact]
    public void OutputIsCappedSoOneHugeFileCannotDominateTheIndex()
    {
        var normalized = TextNormalizer.Normalize(new string('a', 50_000), maxChars: 1_000);
        Assert.Equal(1_000, normalized.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInputIsEmptyOutput(string? input)
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void RealAccentedTextIsPreserved()
    {
        Assert.Equal("Crème brûlée café", TextNormalizer.Normalize("Crème  brûlée   café"));
    }
}

using DecoSOP.Services.Online;
using Xunit;

namespace DecoSOP.Tests;

public class OnlineHtmlTests
{
    [Fact]
    public void ScriptsAndEventHandlersAreRemoved()
    {
        // No login means anyone can write a page that everyone else then renders.
        var html = OnlineHtml.Sanitize("<p onclick=\"steal()\">Hi</p><script>steal()</script><iframe src=\"x\"></iframe>");
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hi", html);
    }

    [Fact]
    public void JavascriptLinksAreRemoved()
    {
        var html = OnlineHtml.Sanitize("<a href=\"javascript:alert(1)\">x</a>");
        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TablesKeepTheirStructureAndSizing()
    {
        var html = OnlineHtml.Sanitize(
            "<table border=\"1\" style=\"width: 100%;\"><tr><td colspan=\"2\" style=\"background-color: #ffff00;\">A</td></tr></table>");
        Assert.Contains("<table", html);
        Assert.Contains("colspan=\"2\"", html);
        Assert.Contains("width", html);
        Assert.Contains("background-color", html);
    }

    [Fact]
    public void LinksToOtherSopsSurvive()
    {
        var html = OnlineHtml.Sanitize("<a href=\"/sops/12\">Sterilization</a>");
        Assert.Contains("href=\"/sops/12\"", html);
    }

    [Fact]
    public void PastedImagesAreKeptButDataLinksAreNot()
    {
        var image = OnlineHtml.Sanitize("<img src=\"data:image/png;base64,iVBORw0KGgo=\">");
        Assert.Contains("data:image/png", image);

        var link = OnlineHtml.Sanitize("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>");
        Assert.DoesNotContain("data:", link);
    }

    [Fact]
    public void PlainTextSeparatesParagraphsAndTableCells()
    {
        var text = OnlineHtml.ToPlainText("<p>Sterilize</p><p>instruments</p><table><tr><td>Autoclave</td><td>121&deg;C</td></tr></table>");
        Assert.Contains("Sterilize", text);
        Assert.DoesNotContain("Sterilizeinstruments", text);
        Assert.DoesNotContain("Autoclave121", text);
        Assert.Contains("121°C", text);
    }

    [Fact]
    public void PlainTextDropsInlineImageData()
    {
        var text = OnlineHtml.ToPlainText("<p>Tray setup</p><img src=\"data:image/png;base64,AAAABBBBCCCC\">");
        Assert.Contains("Tray setup", text);
        Assert.DoesNotContain("AAAABBBBCCCC", text);
    }
}

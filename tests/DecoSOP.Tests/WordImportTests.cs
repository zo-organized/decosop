using DecoSOP.Services.Online;
using Xunit;

namespace DecoSOP.Tests;

public class WordImportTests
{
    [Theory]
    [InlineData("!  Protocols TO FILE")]
    [InlineData("! MULTI Mgmt TO GO THRU")]
    [InlineData("!! Department TOC & INFO docs")]
    [InlineData("SAFETY SYSTEMS to Go Thru")]
    [InlineData("zz ARCHIVE")]
    [InlineData("z~ FACIAL Cosmetic SERVICES PROTOCOLS")]
    [InlineData("z TOCs")]
    [InlineData("Z REFERENCES")]
    [InlineData("tmp")]
    public void WorkInProgressAndArchiveFoldersAreSkipped(string folder)
        => Assert.True(WordImportRules.SkipFolder(folder));

    [Theory]
    [InlineData("z CATALOGS")]
    [InlineData("z QUICK REFS")]
    [InlineData("SAFETY Protocols")]
    [InlineData("^ DENTAL CLINIC PROTOCOLS")]
    [InlineData("0 GENERAL BIZ Protocols")]
    public void LiveFoldersAreImported(string folder)
        => Assert.False(WordImportRules.SkipFolder(folder));

    [Theory]
    [InlineData("Sterilization.docx", false)]
    [InlineData("Old format.doc", false)]
    [InlineData("! Management PLAN 2026.docx", true)]
    [InlineData("Billing TO FILE.docx", true)]
    [InlineData("~$rilization.docx", true)]
    [InlineData("Fee schedule.xlsx", true)]
    [InlineData("Scan.pdf", true)]
    public void OnlyLiveWordFilesAreImported(string file, bool skipped)
        => Assert.Equal(skipped, WordImportRules.SkipFile(file));

    [Fact]
    public void ARepeatedTitleAtTheTopIsDropped()
    {
        var html = WordImporter.ApplyHouseStyle("<p></p><h1>Autoclave  Sterilization</h1><p>Step one.</p>", "AUTOCLAVE STERILIZATION");
        Assert.DoesNotContain("<h1>", html);
        Assert.Contains("Step one.", html);
    }

    [Fact]
    public void WideTablesAreTaggedToShrink()
    {
        static string Row(int n) => "<tr>" + string.Concat(Enumerable.Repeat("<td>x</td>", n)) + "</tr>";
        Assert.Contains("fit-9", WordImporter.ApplyHouseStyle($"<table>{Row(6)}</table>", "T"));
        Assert.Contains("fit-8", WordImporter.ApplyHouseStyle($"<table>{Row(10)}</table>", "T"));
        Assert.DoesNotContain("fit-", WordImporter.ApplyHouseStyle($"<table>{Row(3)}</table>", "T"));
    }

    [Fact]
    public void TextColourSurvivesConversion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"decosop-color-{Guid.NewGuid():N}.docx");
        try
        {
            WriteDocx(path,
                """<w:p><w:r><w:t xml:space="preserve">Plain </w:t></w:r><w:r><w:rPr><w:color w:val="FF0000"/></w:rPr><w:t>new content</w:t></w:r><w:r><w:rPr><w:color w:val="000000"/></w:rPr><w:t> black</w:t></w:r></w:p>""");

            var (docx, map) = WordColors.Tag(path);
            string html;
            using (docx) html = new Mammoth.DocumentConverter().AddStyleMap(map).ConvertToHtml(docx).Value;
            html = OnlineHtml.Sanitize(WordImporter.ApplyHouseStyle(html, "T"));

            // The sanitizer may re-serialise the colour (e.g. as rgba), so match either spelling.
            Assert.Matches(@"<span style=""color: ?(#FF0000|rgba?\(255, 0, 0(, 1)?\))"">new content</span>", html);
            Assert.DoesNotContain("deco-color", html);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<span"));   // black stays plain
        }
        finally { File.Delete(path); }
    }

    /// <summary>The smallest .docx Mammoth will read: one body of paragraphs and an empty style sheet.</summary>
    private static void WriteDocx(string path, string bodyXml)
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var s = new StreamWriter(zip.CreateEntry(name).Open());
            s.Write(xml);
        }
        Add("[Content_Types].xml", """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>""");
        Add("_rels/.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
        Add("word/_rels/document.xml.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Add("word/styles.xml", $"""<?xml version="1.0"?><w:styles xmlns:w="{w}"/>""");
        Add("word/document.xml", $"""<?xml version="1.0"?><w:document xmlns:w="{w}"><w:body>{bodyXml}</w:body></w:document>""");
    }
}

using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace DecoSOP.Services.Extraction;

/// <summary>
/// Reads both spreadsheet formats. Indexes sheet names and every cell that contains words,
/// across all sheets — but deliberately skips cells that are purely numeric, dates or numeric
/// formula results. Fee schedules and ledgers would otherwise contribute tens of thousands of
/// meaningless tokens each, inflating the index and dragging on relevance without making
/// anything findable that people actually search for.
/// </summary>
public sealed class SpreadsheetExtractor : SingleFileExtractor
{
    public override string Name => "npoi-spreadsheet";
    public override IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xls", ".xlsx", ".xlsm" };

    protected override string ExtractOne(string path, CancellationToken ct)
    {
        var isLegacy = Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase);

        using var fs = File.OpenRead(path);
        IWorkbook workbook = isLegacy ? new HSSFWorkbook(fs) : new XSSFWorkbook(fs);

        try
        {
            var sb = new StringBuilder();

            for (var s = 0; s < workbook.NumberOfSheets; s++)
            {
                ct.ThrowIfCancellationRequested();
                if (sb.Length > ExtractionLimits.MaxChars) break;

                var sheet = workbook.GetSheetAt(s);
                if (sheet is null) continue;

                // The sheet name is often the most descriptive text in the whole workbook.
                sb.Append(sheet.SheetName).Append('\n');

                for (var r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
                {
                    if (sb.Length > ExtractionLimits.MaxChars) break;

                    var row = sheet.GetRow(r);
                    if (row is null) continue;

                    var wroteCell = false;
                    foreach (var cell in row.Cells)
                    {
                        var value = TextValueOf(cell);
                        if (value is null) continue;

                        if (wroteCell) sb.Append(' ');
                        sb.Append(value);
                        wroteCell = true;
                    }
                    if (wroteCell) sb.Append('\n');
                }
                sb.Append('\n');
            }

            return sb.ToString();
        }
        finally
        {
            try { workbook.Close(); } catch { /* best effort */ }
        }
    }

    /// <summary>The cell's text, or null when it holds no words worth indexing.</summary>
    private static string? TextValueOf(ICell cell)
    {
        var type = cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;
        if (type != CellType.String) return null;

        var value = cell.StringCellValue;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

using DecoSOP.Data;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

/// <summary>Database figures and the "replace database" restore, for the Settings page.</summary>
public class DatabaseMaintenanceService(IDbContextFactory<AppDbContext> dbFactory)
{
    public sealed record Stats(string FileSize, int SopFolders, int SopFiles, int DocFolders, int Documents);

    public sealed record ReplaceResult(bool Success, string Message);

    public async Task<Stats> GetStatsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var dbPath = db.Database.GetDbConnection().DataSource;
        var fileSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;

        return new Stats(
            SopFileService.FormatFileSize(fileSize),
            await db.SopCategories.CountAsync(),
            await db.SopFiles.CountAsync(),
            await db.DocumentCategories.CountAsync(),
            await db.OfficeDocuments.CountAsync());
    }

    /// <summary>
    /// Replaces the database file with an uploaded backup. The upload is checked to be an SQLite
    /// database first; the app must be restarted afterwards to use it.
    /// </summary>
    public async Task<ReplaceResult> ReplaceDatabaseAsync(Stream upload)
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create))
                await upload.CopyToAsync(fs);

            var header = new byte[16];
            await using (var fs = File.OpenRead(tempPath))
            {
                if (fs.Length < header.Length) return new(false, "Invalid file. Please upload a valid SQLite database.");
                await fs.ReadExactlyAsync(header);
            }
            if (System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                return new(false, "Invalid file. Please upload a valid SQLite database.");

            await using var db = await dbFactory.CreateDbContextAsync();
            var dbPath = db.Database.GetDbConnection().DataSource;
            await db.Database.CloseConnectionAsync();
            File.Copy(tempPath, dbPath, overwrite: true);

            return new(true, "Database imported successfully. Please restart the application for changes to take effect.");
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }
}

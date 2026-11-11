using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Data;

/// <summary>
/// The databases are created with EnsureCreated, which does nothing once the file exists — so a
/// table added in a later version would never appear on a server that already has a database.
/// This adds any tables and indexes the model has that the file lacks, by running EF's own
/// create script with IF NOT EXISTS. It only ever adds whole tables: a new column on an existing
/// table still needs its own upgrade step.
/// </summary>
public static partial class SchemaUpgrader
{
    public static async Task EnsureTablesAsync(DbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        var script = IfNotExists().Replace(db.Database.GenerateCreateScript(), "CREATE $1 IF NOT EXISTS ");
        await db.Database.ExecuteSqlRawAsync(script, ct);
    }

    [GeneratedRegex(@"\bCREATE (TABLE|UNIQUE INDEX|INDEX) (?!IF NOT EXISTS)")]
    private static partial Regex IfNotExists();
}

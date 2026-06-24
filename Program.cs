using DecoSOP.Components;
using DecoSOP.Data;
using DecoSOP.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// Read port from port.config if it exists (written by installer), otherwise default to 5098
var port = "5098";
var portConfigPath = Path.Combine(AppContext.BaseDirectory, "port.config");
if (File.Exists(portConfigPath))
{
    foreach (var line in File.ReadAllLines(portConfigPath))
    {
        if (line.StartsWith("PORT=", StringComparison.OrdinalIgnoreCase))
        {
            var value = line["PORT=".Length..].Trim();
            if (int.TryParse(value, out var p) && p > 0 && p <= 65535)
                port = value;
            break;
        }
    }
}
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// In development, use project root so import scripts and the app share the same DB.
// In production (Windows Service), use the exe directory.
var dataDir = builder.Environment.IsDevelopment()
    ? builder.Environment.ContentRootPath
    : AppContext.BaseDirectory;
var dbPath = Path.Combine(dataDir, "decosop.db");
DocumentService.DataDirectory = dataDir;
SopFileService.DataDirectory = dataDir;

// Folder-sync: point each module at its watched folder (OneDrive-synced / network share / local).
builder.Services.Configure<FolderSyncOptions>(builder.Configuration.GetSection("FolderSync"));
var folderSync = builder.Configuration.GetSection("FolderSync").Get<FolderSyncOptions>() ?? new FolderSyncOptions();
if (folderSync.Enabled)
{
    if (!string.IsNullOrWhiteSpace(folderSync.Sop.Root)) SopFileService.SyncRoot = folderSync.Sop.Root;
    if (!string.IsNullOrWhiteSpace(folderSync.Doc.Root)) DocumentService.SyncRoot = folderSync.Doc.Root;
    SopFileService.OpenBase = folderSync.Sop.OpenBase;
    DocumentService.OpenBase = folderSync.Doc.OpenBase;
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"), ServiceLifetime.Scoped);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientIdentityService>();
builder.Services.AddScoped<UserPreferenceService>();
builder.Services.AddScoped<SopFileService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DataCacheService>();
builder.Services.AddScoped<ContextMenuState>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<SyncNotificationService>();
builder.Services.AddHostedService<FolderSyncBackgroundService>();

var app = builder.Build();

// Auto-create/migrate the database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Schema migrations for existing databases (EnsureCreated only creates new DBs)
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();

    // One-time removal of the legacy Web SOPs / Web Docs modules: back up the DB,
    // drop their tables, clean orphaned preferences, and reclaim space. Gated by a
    // sentinel file so it runs exactly once. (Runs after EnsureCreated, which no
    // longer includes these tables in the model so won't recreate them.)
    var webRemovalSentinel = Path.Combine(dataDir, "webmodules-removed.flag");
    if (!File.Exists(webRemovalSentinel))
    {
        try
        {
            if (File.Exists(dbPath))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var src = dbPath + suffix;
                    if (File.Exists(src))
                        File.Copy(src, $"{dbPath}.bak-{stamp}{suffix}", overwrite: true);
                }
            }

            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = """
                    DROP TABLE IF EXISTS WebDocuments;
                    DROP TABLE IF EXISTS WebDocCategories;
                    DROP TABLE IF EXISTS Documents;
                    DROP TABLE IF EXISTS Categories;
                    DELETE FROM UserPreferences
                        WHERE EntityType IN ('Category','SopDocument','WebDocCategory','WebDocument');
                    """;
                await drop.ExecuteNonQueryAsync();
            }
            using (var vacuum = conn.CreateCommand())
            {
                vacuum.CommandText = "VACUUM";
                await vacuum.ExecuteNonQueryAsync();
            }

            await File.WriteAllTextAsync(webRemovalSentinel, $"Removed {DateTime.Now:O}");
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DecoSOP.Migration");
            logger.LogError(ex, "Web module removal/cleanup failed.");
        }
    }

    try
    {
      try
      {
        // Add Color and IsPinned columns if missing
        foreach (var table in new[] { "DocumentCategories" })
        {
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = $"PRAGMA table_info('{table}')";
            var columns = new HashSet<string>();
            using (var reader = await pragmaCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    columns.Add(reader.GetString(1));
            }
            if (columns.Count > 0) // table exists
            {
                if (!columns.Contains("Color"))
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN Color TEXT";
                    await alter.ExecuteNonQueryAsync();
                }
                if (!columns.Contains("IsPinned"))
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN IsPinned INTEGER NOT NULL DEFAULT 0";
                    await alter.ExecuteNonQueryAsync();
                }
            }
        }

        // Create DocumentCategories and OfficeDocuments tables if missing
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='DocumentCategories'";
        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists is null)
        {
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE DocumentCategories (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL DEFAULT '',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    IsFavorited INTEGER NOT NULL DEFAULT 0,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    Color TEXT,
                    ParentId INTEGER,
                    FOREIGN KEY (ParentId) REFERENCES DocumentCategories(Id) ON DELETE RESTRICT
                );
                CREATE UNIQUE INDEX IX_DocumentCategories_ParentId_Name ON DocumentCategories(ParentId, Name);

                CREATE TABLE OfficeDocuments (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL DEFAULT '',
                    FileName TEXT NOT NULL DEFAULT '',
                    StoredFileName TEXT NOT NULL DEFAULT '',
                    ContentType TEXT NOT NULL DEFAULT '',
                    FileSize INTEGER NOT NULL DEFAULT 0,
                    IsFavorited INTEGER NOT NULL DEFAULT 0,
                    CategoryId INTEGER NOT NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                    UpdatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                    FOREIGN KEY (CategoryId) REFERENCES DocumentCategories(Id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IX_OfficeDocuments_CategoryId_Title ON OfficeDocuments(CategoryId, Title);
                """;
            await create.ExecuteNonQueryAsync();
        }
        // Create SopCategories and SopFiles tables if missing
        using var checkSopCat = conn.CreateCommand();
        checkSopCat.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='SopCategories'";
        var sopCatExists = await checkSopCat.ExecuteScalarAsync();
        if (sopCatExists is null)
        {
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE SopCategories (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL DEFAULT '',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    IsFavorited INTEGER NOT NULL DEFAULT 0,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    Color TEXT,
                    ParentId INTEGER,
                    FOREIGN KEY (ParentId) REFERENCES SopCategories(Id) ON DELETE RESTRICT
                );
                CREATE UNIQUE INDEX IX_SopCategories_ParentId_Name ON SopCategories(ParentId, Name);

                CREATE TABLE SopFiles (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL DEFAULT '',
                    FileName TEXT NOT NULL DEFAULT '',
                    StoredFileName TEXT NOT NULL DEFAULT '',
                    ContentType TEXT NOT NULL DEFAULT '',
                    FileSize INTEGER NOT NULL DEFAULT 0,
                    IsFavorited INTEGER NOT NULL DEFAULT 0,
                    CategoryId INTEGER NOT NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                    UpdatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                    FOREIGN KEY (CategoryId) REFERENCES SopCategories(Id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IX_SopFiles_CategoryId_Title ON SopFiles(CategoryId, Title);
                """;
            await create.ExecuteNonQueryAsync();
        }

        // Create UserPreferences table if missing (per-machine favorites/pins/colors)
        using var checkUserPrefs = conn.CreateCommand();
        checkUserPrefs.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='UserPreferences'";
        var userPrefsExists = await checkUserPrefs.ExecuteScalarAsync();
        if (userPrefsExists is null)
        {
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE UserPreferences (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ClientId TEXT NOT NULL DEFAULT '',
                    EntityType TEXT NOT NULL DEFAULT '',
                    EntityId INTEGER NOT NULL DEFAULT 0,
                    IsFavorited INTEGER NOT NULL DEFAULT 0,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    Color TEXT
                );
                CREATE UNIQUE INDEX IX_UserPreferences_Client_Entity
                    ON UserPreferences(ClientId, EntityType, EntityId);
                CREATE INDEX IX_UserPreferences_Client_Type_Fav
                    ON UserPreferences(ClientId, EntityType, IsFavorited);
                """;
            await create.ExecuteNonQueryAsync();

            // Migrate existing favorites/pins/colors from entity tables to UserPreferences
            var migrations = new[]
            {
                ("SopCategories", "SopCategory", true),
                ("DocumentCategories", "DocumentCategory", true),
                ("SopFiles", "SopFile", false),
                ("OfficeDocuments", "OfficeDocument", false),
            };

            foreach (var (table, entityType, hasPinColor) in migrations)
            {
                using var migrate = conn.CreateCommand();
                if (hasPinColor)
                {
                    migrate.CommandText = $@"
                        INSERT INTO UserPreferences (ClientId, EntityType, EntityId, IsFavorited, IsPinned, Color)
                        SELECT 'legacy-migrated', '{entityType}', Id, IsFavorited, IsPinned, Color
                        FROM {table}
                        WHERE IsFavorited = 1 OR IsPinned = 1 OR Color IS NOT NULL";
                }
                else
                {
                    migrate.CommandText = $@"
                        INSERT INTO UserPreferences (ClientId, EntityType, EntityId, IsFavorited, IsPinned, Color)
                        SELECT 'legacy-migrated', '{entityType}', Id, IsFavorited, 0, NULL
                        FROM {table}
                        WHERE IsFavorited = 1";
                }
                await migrate.ExecuteNonQueryAsync();
            }
        }

    }
      catch (Exception ex)
      {
          var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DecoSOP.Migration");
          logger.LogError(ex, "Database schema migration failed. The app will continue but some features may not work correctly until the database is updated.");
      }
    }
    finally
    {
        await conn.CloseAsync();
    }

    // Seed demo data if requested via --seed-demo flag, then exit (don't start the web server)
    if (args.Contains("--seed-demo"))
    {
        await DemoDataService.SeedDemoDataAsync(db);
        return;
    }

    // Ensure uploads directories exist
    DocumentService.GetUploadDirectory();
    SopFileService.GetUploadDirectory();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePages(async context =>
{
    // Don't re-execute for API routes — just return the raw status code
    if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        return;

    context.HttpContext.Response.Redirect("/not-found");
});
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Helper: resolve stored filename to actual file on disk.
// Tries the exact StoredFileName first; if not found, strips the "{id}_" prefix
// (handles cases where files were imported without the prefix).
string? ResolveFilePath(string uploadDir, string? storedFileName)
{
    if (string.IsNullOrEmpty(storedFileName)) return null;
    var path = Path.Combine(uploadDir, storedFileName);
    if (File.Exists(path)) return path;

    // Strip "{id}_" prefix and try again
    var underscoreIdx = storedFileName.IndexOf('_');
    if (underscoreIdx > 0)
    {
        var unprefixed = storedFileName[(underscoreIdx + 1)..];
        var fallbackPath = Path.Combine(uploadDir, unprefixed);
        if (File.Exists(fallbackPath)) return fallbackPath;
    }
    return null;
}

// File download endpoint for office documents
app.MapGet("/api/documents/{id:int}/download", async (int id, AppDbContext db) =>
{
    var doc = await db.OfficeDocuments.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(DocumentService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    return Results.File(filePath, doc.ContentType, doc.FileName);
});

// File preview endpoint — serves inline (no Content-Disposition: attachment)
app.MapGet("/api/documents/{id:int}/preview", async (int id, AppDbContext db) =>
{
    var doc = await db.OfficeDocuments.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(DocumentService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    return Results.File(filePath, doc.ContentType, enableRangeProcessing: true);
});

// HTML preview for Office documents (DOCX, XLSX, DOC → converted to HTML)
app.MapGet("/api/documents/{id:int}/preview-html", async (int id, AppDbContext db) =>
{
    var doc = await db.OfficeDocuments.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(DocumentService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    var html = DocumentPreviewService.GenerateHtmlPreview(filePath, doc.Title);
    return Results.Content(html, "text/html");
});

// PDF preview for Office documents — converts via LibreOffice on first access, then caches
app.MapGet("/api/documents/{id:int}/preview-pdf", async (int id, AppDbContext db) =>
{
    var doc = await db.OfficeDocuments.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(DocumentService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    var pdfPath = await PdfConversionService.GetOrCreatePdfAsync(filePath, doc.StoredFileName);
    if (pdfPath is null)
        return Results.Problem($"PDF conversion failed: {PdfConversionService.LastError ?? "Unknown error"}");

    return Results.File(pdfPath, "application/pdf", enableRangeProcessing: true);
});

// File download endpoint for SOP files
app.MapGet("/api/sops/{id:int}/download", async (int id, AppDbContext db) =>
{
    var doc = await db.SopFiles.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(SopFileService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    return Results.File(filePath, doc.ContentType, doc.FileName);
});

app.MapGet("/api/sops/{id:int}/preview", async (int id, AppDbContext db) =>
{
    var doc = await db.SopFiles.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(SopFileService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    return Results.File(filePath, doc.ContentType, enableRangeProcessing: true);
});

app.MapGet("/api/sops/{id:int}/preview-html", async (int id, AppDbContext db) =>
{
    var doc = await db.SopFiles.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(SopFileService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    var html = DocumentPreviewService.GenerateHtmlPreview(filePath, doc.Title);
    return Results.Content(html, "text/html");
});

app.MapGet("/api/sops/{id:int}/preview-pdf", async (int id, AppDbContext db) =>
{
    var doc = await db.SopFiles.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var filePath = ResolveFilePath(SopFileService.GetUploadDirectory(), doc.StoredFileName);
    if (filePath is null) return Results.NotFound();

    var pdfPath = await PdfConversionService.GetOrCreatePdfAsync(filePath, doc.StoredFileName);
    if (pdfPath is null)
        return Results.Problem($"PDF conversion failed: {PdfConversionService.LastError ?? "Unknown error"}");

    return Results.File(pdfPath, "application/pdf", enableRangeProcessing: true);
});

// Database export endpoint
app.MapGet("/api/settings/export-db", () =>
{
    if (!File.Exists(dbPath)) return Results.NotFound();
    return Results.File(dbPath, "application/octet-stream", "decosop-backup.db");
});

app.Run();

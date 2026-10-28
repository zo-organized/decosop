using DecoSOP.Components;
using DecoSOP.Data;
using DecoSOP.Models;
using DecoSOP.Services;
using DecoSOP.Services.Extraction;
using DecoSOP.Services.Online;
using DecoSOP.Services.Search;
using Microsoft.EntityFrameworkCore;

// Each module's local-uploads fallback folder (used when no SyncRoot is configured).
SopFileService.FallbackDirName = "sop-uploads";
DocumentService.FallbackDirName = "doc-uploads";

// Diagnostic mode: run the text-extraction pipeline over a folder and report coverage by file
// type, without starting the web host. Used to validate the search index against a real corpus.
//   DecoSOP.exe extract-report [folder] [maxFilesPerType]
if (args.Length > 0 && args[0].Equals("extract-report", StringComparison.OrdinalIgnoreCase))
{
    SopFileService.DataDirectory = AppContext.BaseDirectory;
    return await ExtractionReport.RunAsync(args);
}

// One-off migration: bring a folder of Word protocols into the ONLINE binder.
//   DecoSOP.exe import-online "<folder>" [--module sop|doc] [--db <path>] [--dry-run]
if (args.Length > 0 && args[0].Equals("import-online", StringComparison.OrdinalIgnoreCase))
    return await WordImporter.RunAsync(args);

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

// Compress dynamic responses (the initial rendered HTML document + file-download/preview API).
// Static assets (blazor JS/framework, wwwroot) are already served pre-compressed by MapStaticAssets.
builder.Services.AddResponseCompression(o =>
{
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

// In development, use the project root; in production (Windows Service), the exe directory.
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

// Shared interceptor enables WAL + a busy_timeout on every SQLite connection so concurrent
// reads/writes (user circuits + the folder-sync background writer) don't hit "database is locked".
var sqlitePragmas = new DecoSOP.Data.SqlitePragmaInterceptor();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}").AddInterceptors(sqlitePragmas));
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}").AddInterceptors(sqlitePragmas), ServiceLifetime.Scoped);

// ONLINE binder: pages written in the app. Its own database file — see OnlineDbContext for why.
var onlineDbPath = Path.Combine(dataDir, "decosop-online.db");
builder.Services.AddDbContextFactory<OnlineDbContext>(options =>
    options.UseSqlite($"Data Source={onlineDbPath}").AddInterceptors(sqlitePragmas));
builder.Services.AddSingleton<OnlineBinderService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientIdentityService>();
builder.Services.AddScoped<UserPreferenceService>();
builder.Services.AddScoped<SopFileService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DataCacheService>();
builder.Services.AddScoped<ContextMenuState>();
builder.Services.AddScoped<DecoSOP.Services.Users.AppUserService>();
builder.Services.AddScoped<DecoSOP.Services.Users.UserListService>();
builder.Services.AddScoped<DecoSOP.Services.DatabaseMaintenanceService>();
builder.Services.AddScoped<DecoSOP.Components.Layout.LayoutState>();

// Content search: text extraction. Extractors are stateless, so they're singletons.
builder.Services.AddSingleton<ITextExtractor, PlainTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, OpenXmlWordExtractor>();
builder.Services.AddSingleton<ITextExtractor, SpreadsheetExtractor>();
builder.Services.AddSingleton<ITextExtractor, LibreOfficeTextExtractor>();
builder.Services.AddSingleton<TextExtractionService>();

// Content search: the index itself lives in its own SQLite file next to decosop.db. It is
// entirely derived data — delete it and it rebuilds — so it stays out of the DB export and
// away from the main database's single writer.
var searchDbPath = Path.Combine(dataDir, "decosop-search.db");
builder.Services.AddSingleton(sp => new SearchDb(searchDbPath, sp.GetRequiredService<ILogger<SearchDb>>()));
builder.Services.AddSingleton<SearchIndexService>();
builder.Services.AddSingleton<SearchIndexBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchIndexBackgroundService>());
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<SyncNotificationService>();
builder.Services.AddSingleton<FolderSyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FolderSyncBackgroundService>());

var app = builder.Build();

// Auto-create the database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SchemaUpgrader.EnsureTablesAsync(db);   // also adds tables new in this version (users, lists)

    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();
    try
    {
        // Enable WAL once — it persists in the DB file header, so every later connection opens in WAL
        // automatically (readers don't block the single writer). Per-connection busy_timeout is handled
        // by SqlitePragmaInterceptor. Setting WAL per-connection instead would be needlessly expensive.
        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        await walCmd.ExecuteNonQueryAsync();
    }
    finally
    {
        await conn.CloseAsync();
    }

    // Ensure uploads directories exist
    DocumentService.GetUploadDirectory();
    SopFileService.GetUploadDirectory();

    // Same treatment for the ONLINE binder's database.
    await using var onlineDb = await scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<OnlineDbContext>>().CreateDbContextAsync();
    await SchemaUpgrader.EnsureTablesAsync(onlineDb);
    await onlineDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

app.UseResponseCompression();

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

// Download / inline-preview / PDF-preview endpoints, mapped once per file module.
void MapFileApi<TFile>(string module, Func<string> uploadDir) where TFile : class, IFileNode
{
    string? Resolve(TFile doc)
    {
        var path = Path.Combine(uploadDir(), doc.StoredFileName);
        return File.Exists(path) ? path : null;
    }

    app.MapGet($"/api/{module}/{{id:int}}/download", async (int id, AppDbContext db) =>
    {
        var doc = await db.Set<TFile>().FindAsync(id);
        var path = doc is null ? null : Resolve(doc);
        return path is null ? Results.NotFound() : Results.File(path, doc!.ContentType, doc.FileName);
    });

    // Inline preview (no Content-Disposition: attachment)
    app.MapGet($"/api/{module}/{{id:int}}/preview", async (int id, AppDbContext db) =>
    {
        var doc = await db.Set<TFile>().FindAsync(id);
        var path = doc is null ? null : Resolve(doc);
        return path is null ? Results.NotFound() : Results.File(path, doc!.ContentType, enableRangeProcessing: true);
    });

    // PDF preview — converts via LibreOffice on first access, then caches
    app.MapGet($"/api/{module}/{{id:int}}/preview-pdf", async (int id, AppDbContext db) =>
    {
        var doc = await db.Set<TFile>().FindAsync(id);
        var path = doc is null ? null : Resolve(doc);
        if (path is null) return Results.NotFound();

        var pdfPath = await PdfConversionService.GetOrCreatePdfAsync(path, doc!.StoredFileName);
        return pdfPath is null
            ? Results.Problem($"PDF conversion failed: {PdfConversionService.LastError ?? "Unknown error"}")
            : Results.File(pdfPath, "application/pdf", enableRangeProcessing: true);
    });
}

MapFileApi<OfficeDocument>("documents", DocumentService.GetUploadDirectory);
MapFileApi<SopFile>("sops", SopFileService.GetUploadDirectory);

// Database export endpoint
app.MapGet("/api/settings/export-db", () =>
{
    if (!File.Exists(dbPath)) return Results.NotFound();
    return Results.File(dbPath, "application/octet-stream", "decosop-backup.db");
});

// ONLINE binder export. Unlike decosop.db this is the only copy of the pages, so it is taken
// with SQLite's online backup API rather than by copying the file: recent saves may still sit
// in the WAL, and a plain file copy would silently leave them out.
app.MapGet("/api/settings/export-online-db", async () =>
{
    if (!File.Exists(onlineDbPath)) return Results.NotFound();
    var tmp = Path.Combine(Path.GetTempPath(), $"decosop-online-{Guid.NewGuid():N}.db");
    await using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={onlineDbPath}"))
    await using (var target = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp};Pooling=False"))
    {
        await source.OpenAsync();
        await target.OpenAsync();
        source.BackupDatabase(target);
    }
    var bytes = await File.ReadAllBytesAsync(tmp);
    File.Delete(tmp);
    return Results.File(bytes, "application/octet-stream", $"decosop-online-backup-{DateTime.Now:yyyy-MM-dd}.db");
});

// Printable ONLINE page: a bare document with just the title and content, which the viewer
// opens in a new tab and prints.
app.MapGet("/api/online/pages/{id:int}/print", async (int id, OnlineBinderService online) =>
{
    var page = await online.GetPageAsync(id);
    if (page is null) return Results.NotFound();
    var title = System.Net.WebUtility.HtmlEncode(page.Title);
    // The house style (margins, 14pt centered title, 10pt body) comes from the same stylesheet
    // the page view and editor use; @page in it sets the printed margins.
    var html = $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>{{title}}</title>
        <link rel="stylesheet" href="/css/online-doc.css">
        <style> body { margin: 0; } </style></head>
        <body class="online-doc"><h1 class="online-doc-title">{{title}}</h1>{{OnlineHtml.Sanitize(page.Html)}}</body></html>
        """;
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();

// Reached on graceful shutdown. Explicit because the extract-report branch above returns an
// exit code, which makes the entry point int-returning.
return 0;

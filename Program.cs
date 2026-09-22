using DecoSOP.Components;
using DecoSOP.Data;
using DecoSOP.Models;
using DecoSOP.Services;
using DecoSOP.Services.Extraction;
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

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientIdentityService>();
builder.Services.AddScoped<UserPreferenceService>();
builder.Services.AddScoped<SopFileService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DataCacheService>();
builder.Services.AddScoped<ContextMenuState>();

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
    db.Database.EnsureCreated();

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

app.Run();

// Reached on graceful shutdown. Explicit because the extract-report branch above returns an
// exit code, which makes the entry point int-returning.
return 0;

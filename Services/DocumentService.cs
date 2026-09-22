using DecoSOP.Data;
using DecoSOP.Models;

namespace DecoSOP.Services;

/// <summary>Document module's folder-backed service — all behavior lives in FolderFileService.</summary>
public sealed class DocumentService(AppDbContext db) : FolderFileService<DocumentCategory, OfficeDocument>(db);

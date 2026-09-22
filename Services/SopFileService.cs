using DecoSOP.Data;
using DecoSOP.Models;

namespace DecoSOP.Services;

/// <summary>SOP module's folder-backed service — all behavior lives in FolderFileService.</summary>
public sealed class SopFileService(AppDbContext db) : FolderFileService<SopCategory, SopFile>(db);

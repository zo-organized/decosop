using DecoSOP.Data;
using DecoSOP.Models;
using Microsoft.EntityFrameworkCore;

namespace DecoSOP.Services;

/// <summary>
/// Reconciles a module's DB rows (categories + files) with the files actually on disk
/// under a root folder. Non-destructive: matches by relative path and preserves row IDs
/// (and therefore the per-user preferences keyed to them). Idempotent — re-running with
/// no disk changes performs no writes. Reads file metadata only (never content), so it
/// does not force OneDrive "Files On-Demand" hydration.
/// </summary>
public static class FolderReconciler
{
    public readonly record struct Result(int Added, int Updated, int Removed)
    {
        public bool Changed => Added > 0 || Updated > 0 || Removed > 0;
    }

    public static async Task<Result> ReconcileAsync<TCat, TFile>(
        AppDbContext db, string root, string catEntityType, string fileEntityType,
        CancellationToken ct = default)
        where TCat : class, ICategoryNode, new()
        where TFile : class, IFileNode, new()
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return default;

        // --- 1. Scan disk (metadata only) ---
        var disk = new List<DiskFile>();
        foreach (var (relPath, fullPath) in FileScanUtil.WalkFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            long size; DateTime mtime;
            try
            {
                var fi = new FileInfo(fullPath);
                if (!fi.Exists) continue;
                size = fi.Length;
                mtime = fi.LastWriteTimeUtc;
            }
            catch { continue; } // locked / placeholder — skip this pass, next run picks it up

            disk.Add(new DiskFile(
                StoredName: relPath,
                Title: Trim200(FileScanUtil.CleanTitle(Path.GetFileName(fullPath))),
                FileName: Path.GetFileName(fullPath),
                ContentType: FileScanUtil.GetContentType(fullPath),
                Size: size,
                Mtime: mtime,
                Chain: FileScanUtil.CategoryChainForRelPath(relPath)));
        }
        var diskByStored = disk.ToDictionary(d => d.StoredName, StringComparer.Ordinal);

        // --- 2. Load DB state ---
        var cats = await db.Set<TCat>().ToListAsync(ct);
        var files = await db.Set<TFile>().ToListAsync(ct);
        var fileByStored = new Dictionary<string, TFile>(StringComparer.Ordinal);
        foreach (var f in files) fileByStored[f.StoredFileName] = f;

        var catByKey = new Dictionary<(int Parent, string Name), TCat>();
        foreach (var c in cats) catByKey[(c.ParentId ?? -1, c.Name)] = c;

        var catMaxSort = new Dictionary<int, int>();
        foreach (var c in cats)
            catMaxSort[c.ParentId ?? -1] = Math.Max(catMaxSort.GetValueOrDefault(c.ParentId ?? -1, -1), c.SortOrder);

        var fileMaxSort = new Dictionary<int, int>();
        var usedTitles = new Dictionary<int, HashSet<string>>();
        foreach (var f in files)
        {
            fileMaxSort[f.CategoryId] = Math.Max(fileMaxSort.GetValueOrDefault(f.CategoryId, -1), f.SortOrder);
            (usedTitles.TryGetValue(f.CategoryId, out var s) ? s : usedTitles[f.CategoryId] = new(StringComparer.OrdinalIgnoreCase)).Add(f.Title);
        }

        var catSet = db.Set<TCat>();
        var fileSet = db.Set<TFile>();
        int added = 0, updated = 0, removed = 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Resolve (creating if needed) the leaf category Id for a chain of folder names.
        async Task<int> EnsureChainAsync(IReadOnlyList<string> chain)
        {
            int? parentId = null;
            foreach (var name in chain)
            {
                var key = (parentId ?? -1, name);
                if (catByKey.TryGetValue(key, out var existing)) { parentId = existing.Id; continue; }

                var sort = catMaxSort.GetValueOrDefault(parentId ?? -1, -1) + 1;
                catMaxSort[parentId ?? -1] = sort;
                var cat = new TCat { Name = name, SortOrder = sort, ParentId = parentId };
                catSet.Add(cat);
                await db.SaveChangesAsync(ct); // need the generated Id for the next level
                catByKey[key] = cat;
                parentId = cat.Id;
            }
            return parentId!.Value;
        }

        // --- 3. Partition into matched / new / vanished by stored path ---
        var vanished = files.Where(f => !diskByStored.ContainsKey(f.StoredFileName)).ToList();
        var newOnDisk = disk.Where(d => !fileByStored.ContainsKey(d.StoredName)).ToList();

        // --- 4. Move/rename heuristic: a vanished file + exactly one new file with the same
        //         size and title is treated as a move (preserve Id + preferences). ---
        var movedNew = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gone in vanished.ToList())
        {
            var candidates = newOnDisk
                .Where(d => !movedNew.Contains(d.StoredName)
                         && d.Size == gone.FileSize
                         && string.Equals(d.Title, gone.Title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count != 1) continue;

            var moved = candidates[0];
            var catId = await EnsureChainAsync(moved.Chain);
            gone.StoredFileName = moved.StoredName;
            gone.CategoryId = catId;
            gone.FileName = moved.FileName;
            gone.ContentType = moved.ContentType;
            gone.FileSize = moved.Size;
            gone.UpdatedAt = moved.Mtime;
            movedNew.Add(moved.StoredName);
            vanished.Remove(gone);
            updated++;
        }
        newOnDisk = newOnDisk.Where(d => !movedNew.Contains(d.StoredName)).ToList();

        // --- 5. Update matched files in place (keep Id, StoredFileName, Title). Re-home to the
        //         disk-derived category so the folder stays authoritative — this auto-migrates when
        //         the naming scheme changes. Skip a re-home that would collide with an existing
        //         title in the target (unique CategoryId+Title). ---
        foreach (var d in disk)
        {
            if (!fileByStored.TryGetValue(d.StoredName, out var row)) continue;
            bool changed = false;

            var catId = await EnsureChainAsync(d.Chain);
            if (row.CategoryId != catId)
            {
                var targetTitles = usedTitles.TryGetValue(catId, out var ts) ? ts : usedTitles[catId] = new(StringComparer.OrdinalIgnoreCase);
                if (!targetTitles.Contains(row.Title))
                {
                    if (usedTitles.TryGetValue(row.CategoryId, out var oldSet)) oldSet.Remove(row.Title);
                    targetTitles.Add(row.Title);
                    row.CategoryId = catId;
                    changed = true;
                }
            }

            if (row.FileSize != d.Size) { row.FileSize = d.Size; changed = true; }
            if (row.ContentType != d.ContentType) { row.ContentType = d.ContentType; changed = true; }
            if (changed) { row.UpdatedAt = d.Mtime; updated++; }
        }

        // --- 6. Insert genuinely new files ---
        foreach (var d in newOnDisk)
        {
            var catId = await EnsureChainAsync(d.Chain);
            var titleSet = usedTitles.TryGetValue(catId, out var s) ? s : usedTitles[catId] = new(StringComparer.OrdinalIgnoreCase);
            var title = d.Title;
            for (int n = 2; titleSet.Contains(title); n++) title = $"{d.Title} ({n})";
            titleSet.Add(title);

            var sort = fileMaxSort.GetValueOrDefault(catId, -1) + 1;
            fileMaxSort[catId] = sort;

            fileSet.Add(new TFile
            {
                Title = title,
                FileName = d.FileName,
                StoredFileName = d.StoredName,
                ContentType = d.ContentType,
                FileSize = d.Size,
                CategoryId = catId,
                SortOrder = sort,
                CreatedAt = d.Mtime,
                UpdatedAt = d.Mtime
            });
            added++;
        }

        // --- 7. Delete vanished files + clean their preferences ---
        var removedFileIds = vanished.Select(f => f.Id).ToList();
        if (vanished.Count > 0)
        {
            fileSet.RemoveRange(vanished);
            removed += vanished.Count;
        }

        await db.SaveChangesAsync(ct);

        // --- 8. Prune categories that have no files, no children, and no matching disk folder.
        //         Iterative (bottom-up): removing a leaf can make its parent prunable. ---
        var removedCatIds = await PruneEmptyCategoriesAsync<TCat, TFile>(db, root, ct);

        await db.SaveChangesAsync(ct);

        // --- 9. Clean orphaned preferences for removed files + categories ---
        if (removedFileIds.Count > 0 || removedCatIds.Count > 0)
        {
            var stalePrefs = await db.UserPreferences
                .Where(p => (p.EntityType == fileEntityType && removedFileIds.Contains(p.EntityId))
                         || (p.EntityType == catEntityType && removedCatIds.Contains(p.EntityId)))
                .ToListAsync(ct);
            if (stalePrefs.Count > 0)
            {
                db.UserPreferences.RemoveRange(stalePrefs);
                await db.SaveChangesAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        return new Result(added, updated, removed);
    }

    private static async Task<List<int>> PruneEmptyCategoriesAsync<TCat, TFile>(
        AppDbContext db, string root, CancellationToken ct)
        where TCat : class, ICategoryNode, new()
        where TFile : class, IFileNode, new()
    {
        var cats = await db.Set<TCat>().ToListAsync(ct);
        var catById = cats.ToDictionary(c => c.Id);

        // Folder chains that exist on disk ("a/b/c" of folder names).
        var diskChains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in FileScanUtil.WalkDirectoryChains(root))
            diskChains.Add(string.Join("/", chain));

        string ChainOf(TCat c)
        {
            var parts = new List<string>();
            var cur = c;
            while (cur is not null)
            {
                parts.Insert(0, cur.Name);
                cur = cur.ParentId is int pid && catById.TryGetValue(pid, out var p) ? p : null;
            }
            return string.Join("/", parts);
        }

        var fileCatIds = await db.Set<TFile>().Select(f => f.CategoryId).Distinct().ToListAsync(ct);
        var hasFiles = new HashSet<int>(fileCatIds);

        var removedIds = new List<int>();
        var alive = cats.ToList();
        bool any;
        do
        {
            any = false;
            var childParents = alive.Where(c => c.ParentId.HasValue).Select(c => c.ParentId!.Value).ToHashSet();
            foreach (var c in alive.ToList())
            {
                if (hasFiles.Contains(c.Id)) continue;          // has files
                if (childParents.Contains(c.Id)) continue;      // has children
                if (diskChains.Contains(ChainOf(c))) continue;  // still a real folder on disk
                db.Set<TCat>().Remove(c);
                alive.Remove(c);
                removedIds.Add(c.Id);
                any = true;
            }
        } while (any);

        return removedIds;
    }

    private static string Trim200(string s) => s.Length > 200 ? s[..200] : s;

    private readonly record struct DiskFile(
        string StoredName, string Title, string FileName, string ContentType,
        long Size, DateTime Mtime, IReadOnlyList<string> Chain);
}

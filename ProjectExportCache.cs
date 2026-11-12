using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DecompilerMCPServer;

/// <summary>An assembly's exported project on disk. <see cref="Reused"/> is true when a previous export was still valid.</summary>
public sealed record ProjectExport(
    string RelativePath,
    string ProjectDirectory,
    string ProjectFilePath,
    int FileCount,
    int ErrorCount,
    bool Reused);

/// <summary>
/// Exports assemblies to <c>&lt;OutputDirectory&gt;/&lt;relative path incl. .dll&gt;/</c> and reuses an export while the
/// assembly is unchanged. Freshness is decided by a manifest written into the project folder (assembly size,
/// timestamp, MVID and engine version), so exports survive restarts. A given DLL is exported at most once at a time
/// in this process (Lazy) and across processes sharing the output directory (a lock file beside the project folder).
/// </summary>
public sealed class ProjectExportCache(ExportOptions options, DecompilerService decompiler, ILogger<ProjectExportCache> logger)
{
    private const int FormatVersion = 1;
    private const string ManifestFileName = ".decompiled.json";

    private sealed record Stamp(long LastWriteUtcTicks, long Length);
    private sealed record Entry(Stamp Stamp, Lazy<ProjectExport> Export);

    private sealed record Manifest(
        string AssemblyPath,
        long AssemblyLastWriteUtcTicks,
        long AssemblyLength,
        Guid Mvid,
        string DecompilerVersion,
        int FormatVersion,
        int FileCount,
        int ErrorCount,
        DateTime ExportedUtc);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Ordinal: the key is a literal filesystem path from directory enumeration; a
    // case-insensitive comparer would conflate distinct files on case-sensitive filesystems.
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>Name of the folder at the output root holding generated language-server build state; never an export.</summary>
    public const string LanguageServerFolderName = ".lsp";

    public string OutputDirectory => options.OutputDirectory;

    /// <summary>
    /// Raised with the output-relative path after an export was written or removed on disk (not when one is reused).
    /// Handlers run synchronously on the exporting thread and must be quick.
    /// </summary>
    public event Action<string>? ExportsChanged;

    /// <summary>Where the given assembly's project is (or would be) written.</summary>
    public string ProjectDirectoryFor(string relativePath) => Path.Combine(options.OutputDirectory, relativePath);

    /// <summary>Ensures the assembly is exported and returns where. Throws if it can't be exported.</summary>
    public ProjectExport GetExport(string fullPath, string relativePath, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(fullPath);
        var stamp = new Stamp(info.LastWriteTimeUtc.Ticks, info.Length);
        while (true)
        {
            Entry? created = null;
            var entry = _cache.AddOrUpdate(fullPath,
                _ => created = Create(fullPath, relativePath, stamp, cancellationToken),
                (_, existing) => existing.Stamp == stamp ? existing : created = Create(fullPath, relativePath, stamp, cancellationToken));
            try
            {
                var export = entry.Export.Value;
                // Someone else's export (an earlier call, or the background monitor): from this caller's view it was reused
                if (!ReferenceEquals(entry, created))
                    return export with { Reused = true };
                if (!export.Reused)
                    RaiseExportsChanged(relativePath);
                return export;
            }
            catch (OperationCanceledException)
            {
                // A cancelled export must not poison the entry for future callers
                _cache.TryRemove(new KeyValuePair<string, Entry>(fullPath, entry));
                if (cancellationToken.IsCancellationRequested)
                    throw;
                // The shared export ran under another caller's token and that caller went away; run it again under ours
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Filesystem trouble (lock, swap, disk) is usually transient: let the next call retry instead of
                // failing fast until the DLL changes, which is only right for a DLL that itself can't be decompiled
                _cache.TryRemove(new KeyValuePair<string, Entry>(fullPath, entry));
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes the assembly's exported project (and any leftover temp folders) and forgets it in memory.
    /// Used when the assembly has disappeared from the scan directory. Returns true if a folder was deleted.
    /// </summary>
    public bool RemoveExport(string fullPath, string relativePath, CancellationToken cancellationToken = default)
    {
        _cache.TryRemove(fullPath, out _);

        var projectDir = ProjectDirectoryFor(relativePath);
        var parent = Path.GetDirectoryName(projectDir)!;
        if (!Directory.Exists(parent))
            return false;

        using (AcquireLock(projectDir + ".lock", cancellationToken))
        {
            foreach (var stale in Directory.EnumerateDirectories(parent, Path.GetFileName(projectDir) + ".tmp-*"))
                DeleteOwnedDirectory(stale);
            if (!Directory.Exists(projectDir))
                return false;
            DeleteOwnedDirectory(projectDir);
        }
        logger.LogInformation("Removed export of {Assembly}: the assembly no longer exists", relativePath);
        RaiseExportsChanged(relativePath);
        return true;
    }

    /// <summary>
    /// Output-relative paths (same form as scan-relative paths, e.g. "sub/Foo.dll") of every exported project on disk,
    /// identified by a folder named *.dll that holds a manifest. Project folders are not descended into.
    /// </summary>
    public IReadOnlyList<string> FindExportedAssemblies()
    {
        var results = new List<string>();
        if (!Directory.Exists(options.OutputDirectory))
            return results;

        var pending = new Stack<string>();
        pending.Push(options.OutputDirectory);
        while (pending.Count > 0)
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(pending.Pop()).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var dir in children)
            {
                var name = Path.GetFileName(dir);
                if (name.Contains(".dll.tmp-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name == LanguageServerFolderName && Path.GetDirectoryName(dir) == options.OutputDirectory)
                    continue;
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, ManifestFileName)))
                    results.Add(Path.GetRelativePath(options.OutputDirectory, dir));
                else
                    pending.Push(dir);
            }
        }
        return results;
    }

    private Entry Create(string fullPath, string relativePath, Stamp stamp, CancellationToken cancellationToken) =>
        // ExecutionAndPublication also caches a thrown exception: a corrupt DLL
        // fails fast on every access until its timestamp or size changes.
        new(stamp, new Lazy<ProjectExport>(
            () => Export(fullPath, relativePath, stamp, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));

    private ProjectExport Export(string fullPath, string relativePath, Stamp stamp, CancellationToken cancellationToken)
    {
        var projectDir = ProjectDirectoryFor(relativePath);
        var mvid = decompiler.ReadMvid(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectDir)!);

        using var _ = AcquireLock(projectDir + ".lock", cancellationToken);

        var manifest = ReadManifest(projectDir);
        if (manifest is not null)
        {
            if (IsCurrent(manifest, stamp, mvid))
                return Describe(relativePath, projectDir, manifest.FileCount, manifest.ErrorCount, reused: true);

            if (manifest.DecompilerVersion != DecompilerService.EngineVersion || manifest.FormatVersion != FormatVersion)
                logger.LogInformation("Re-exporting {Assembly}: existing export was made by engine {Old}, now {New}",
                    relativePath, manifest.DecompilerVersion, DecompilerService.EngineVersion);
        }

        // Export into a sibling temp folder and swap it in, so a crash never leaves a half-written project
        // under the final name. Leftovers from earlier crashed exports (other pids) are cleared first.
        foreach (var stale in Directory.EnumerateDirectories(Path.GetDirectoryName(projectDir)!, Path.GetFileName(projectDir) + ".tmp-*"))
            DeleteOwnedDirectory(stale);

        var tempDir = $"{projectDir}.tmp-{Environment.ProcessId}";
        Directory.CreateDirectory(tempDir);
        try
        {
            logger.LogInformation("Exporting {Assembly} to {Directory}", relativePath, projectDir);
            var result = decompiler.ExportProject(fullPath, tempDir, cancellationToken);
            WriteManifest(tempDir, new Manifest(fullPath, stamp.LastWriteUtcTicks, stamp.Length, mvid,
                DecompilerService.EngineVersion, FormatVersion, result.FileCount, result.Errors.Count, DateTime.UtcNow));

            DeleteOwnedDirectory(projectDir);
            Directory.Move(tempDir, projectDir);
            return Describe(relativePath, projectDir, result.FileCount, result.Errors.Count, reused: false);
        }
        catch
        {
            TryDeleteOwnedDirectory(tempDir);
            throw;
        }
    }

    private static bool IsCurrent(Manifest manifest, Stamp stamp, Guid mvid) =>
        manifest.FormatVersion == FormatVersion &&
        manifest.DecompilerVersion == DecompilerService.EngineVersion &&
        manifest.AssemblyLastWriteUtcTicks == stamp.LastWriteUtcTicks &&
        manifest.AssemblyLength == stamp.Length &&
        manifest.Mvid == mvid;

    private static ProjectExport Describe(string relativePath, string projectDir, int fileCount, int errorCount, bool reused)
    {
        var projectFile = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault()
                          ?? Path.Combine(projectDir, Path.GetFileNameWithoutExtension(relativePath) + ".csproj");
        return new ProjectExport(relativePath, projectDir, projectFile, fileCount, errorCount, reused);
    }

    private static Manifest? ReadManifest(string projectDir)
    {
        var path = Path.Combine(projectDir, ManifestFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // unreadable manifest: treat the export as stale
        }
    }

    private static void WriteManifest(string projectDir, Manifest manifest) =>
        File.WriteAllText(Path.Combine(projectDir, ManifestFileName), JsonSerializer.Serialize(manifest, JsonOptions));

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cross-process mutual exclusion per project. Two MCP clients registered on the same scan directory share the
    /// default output directory, so this is the normal case.
    /// </summary>
    private static FileStream AcquireLock(string lockPath, CancellationToken cancellationToken) =>
        FileLock.Acquire(lockPath, LockTimeout, cancellationToken);

    private void RaiseExportsChanged(string relativePath)
    {
        try
        {
            ExportsChanged?.Invoke(relativePath);
        }
        catch (Exception ex)
        {
            // A subscriber's failure must never fail an export
            logger.LogWarning(ex, "An ExportsChanged handler failed for {Assembly}", relativePath);
        }
    }

    private void TryDeleteOwnedDirectory(string dir)
    {
        try
        {
            DeleteOwnedDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove temporary export folder {Directory}", dir);
        }
    }

    /// <summary>
    /// Recursive delete, restricted to folders this server owns: under the output directory and named like an
    /// exported project (*.dll) or its temp folder (*.dll.tmp-*). Retries briefly for transient sharing violations.
    /// </summary>
    private void DeleteOwnedDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.OutputDirectory)) + Path.DirectorySeparatorChar;
        var name = Path.GetFileName(full);
        bool owned = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                     (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains(".dll.tmp-", StringComparison.OrdinalIgnoreCase));
        if (!owned)
            throw new InvalidOperationException(
                $"Refusing to delete '{full}': not a server-owned export folder under '{options.OutputDirectory}'.");

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(full, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}

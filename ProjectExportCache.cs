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

    public string OutputDirectory => options.OutputDirectory;

    /// <summary>Where the given assembly's project is (or would be) written.</summary>
    public string ProjectDirectoryFor(string relativePath) => Path.Combine(options.OutputDirectory, relativePath);

    /// <summary>Ensures the assembly is exported and returns where. Throws if it can't be exported.</summary>
    public ProjectExport GetExport(string fullPath, string relativePath, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(fullPath);
        var stamp = new Stamp(info.LastWriteTimeUtc.Ticks, info.Length);
        while (true)
        {
            var entry = _cache.AddOrUpdate(fullPath,
                _ => Create(fullPath, relativePath, stamp, cancellationToken),
                (_, existing) => existing.Stamp == stamp ? existing : Create(fullPath, relativePath, stamp, cancellationToken));
            try
            {
                return entry.Export.Value;
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
    /// default output directory, so this is the normal case. FileShare.None maps to an advisory lock on Unix.
    /// </summary>
    private static FileStream AcquireLock(string lockPath, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex)
            {
                // Only a lock held by another process is worth waiting for; if the file could not even be created
                // (path too long, folder gone, disk full) the error is real and must surface
                if (!File.Exists(lockPath))
                    throw;
                if (DateTime.UtcNow - started > LockTimeout)
                    throw new IOException($"Timed out after {LockTimeout.TotalMinutes:N0} minutes waiting for '{lockPath}'; " +
                                          $"another process seems stuck exporting this assembly. ({ex.Message})", ex);
                cancellationToken.WaitHandle.WaitOne(250);
            }
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

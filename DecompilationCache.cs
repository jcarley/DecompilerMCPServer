using System.Collections.Concurrent;

namespace DecompilerMCPServer;

/// <summary>
/// Caches whole-module decompilations keyed by file path, invalidated by last-write-time.
/// A given DLL is decompiled at most once even under concurrent access.
/// </summary>
public sealed class DecompilationCache(DecompilerService decompiler)
{
    private sealed record Entry(DateTime LastWriteUtc, Lazy<string> Source);

    // Ordinal: the key is a literal filesystem path from directory enumeration; a
    // case-insensitive comparer would conflate distinct files on case-sensitive filesystems.
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>Whole-module decompiled C#. Throws if the file can't be decompiled.</summary>
    public string GetSource(string fullPath, CancellationToken cancellationToken = default)
    {
        var stamp = File.GetLastWriteTimeUtc(fullPath);
        var entry = _cache.AddOrUpdate(fullPath,
            _ => Create(fullPath, stamp, cancellationToken),
            (_, existing) => existing.LastWriteUtc == stamp ? existing : Create(fullPath, stamp, cancellationToken));
        try
        {
            return entry.Source.Value;
        }
        catch (OperationCanceledException)
        {
            // A cancelled decompile must not poison the entry for future callers
            _cache.TryRemove(new KeyValuePair<string, Entry>(fullPath, entry));
            throw;
        }
    }

    private Entry Create(string path, DateTime stamp, CancellationToken cancellationToken) =>
        // ExecutionAndPublication also caches a thrown exception: a corrupt DLL
        // fails fast on every access until its timestamp changes.
        new(stamp, new Lazy<string>(
            () => decompiler.DecompileAssembly(path, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
}

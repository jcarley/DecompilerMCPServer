using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DecompilerMCPServer;

/// <summary>
/// Keeps the exports in step with the scan directory: at startup it exports every assembly and removes exports whose
/// assembly is gone, then it watches the scan directory recursively and re-exports DLLs as they are added or changed.
/// Work runs one item at a time on a background thread; tools that need a DLL being exported join the same export
/// through <see cref="ProjectExportCache"/>. Failures are logged and never stop the server.
/// </summary>
public sealed class ScanDirectoryMonitor(
    AssemblyDiscoveryService discovery,
    ProjectExportCache cache,
    ILogger<ScanDirectoryMonitor> logger) : BackgroundService
{
    // Builds and copies write a DLL in several steps; wait for the events to settle before exporting
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 5;

    private readonly record struct Pending(DateTime DueUtc, int Attempt);

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal); // full path -> when to export
    private DateTime? _reconcileDueUtc;
    private readonly SemaphoreSlim _signal = new(0);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Exports are long synchronous CPU work: keep them off the host's startup path
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Watch before the first pass so nothing changed during it is missed
        using var watcher = StartWatcher();
        ScheduleReconcile(TimeSpan.Zero);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan directory monitor error; continuing");
            }
        }
    }

    /// <summary>Runs whatever is due, or waits for the next due time or a new event.</summary>
    private async Task RunDueWorkAsync(CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        bool reconcile = false;
        List<(string Path, int Attempt)> due = [];
        TimeSpan wait = Timeout.InfiniteTimeSpan;

        lock (_gate)
        {
            if (_reconcileDueUtc is { } r && r <= now)
            {
                reconcile = true;
                _reconcileDueUtc = null;
            }
            else
            {
                foreach (var (path, pending) in _pending)
                    if (pending.DueUtc <= now)
                        due.Add((path, pending.Attempt));
                foreach (var (path, _) in due)
                    _pending.Remove(path);

                if (due.Count == 0)
                {
                    var next = _pending.Values.Select(p => (DateTime?)p.DueUtc).Min();
                    if (_reconcileDueUtc is { } rd && (next is null || rd < next))
                        next = rd;
                    if (next is { } n)
                        wait = n - now;
                }
            }
        }

        if (reconcile)
        {
            Reconcile(stoppingToken);
            return;
        }
        if (due.Count > 0)
        {
            foreach (var (path, attempt) in due)
                ExportOne(path, attempt, stoppingToken);
            return;
        }

        await _signal.WaitAsync(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, stoppingToken);
    }

    /// <summary>Exports every assembly (unchanged ones are reused) and deletes exports whose assembly is gone.</summary>
    private void Reconcile(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> assemblies;
        try
        {
            assemblies = discovery.FindAssemblies();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not scan '{Directory}'", discovery.Root);
            return;
        }

        int exported = 0, reused = 0, failed = 0, removed = 0;
        foreach (var rel in assemblies)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var fullPath = discovery.FullPath(rel);
            lock (_gate)
            {
                // Still being written: its own debounced export will handle it
                if (_pending.ContainsKey(fullPath))
                    continue;
            }
            switch (TryExport(fullPath, rel, attempt: 0, stoppingToken))
            {
                case true: exported++; break;
                case false: reused++; break;
                default: failed++; break;
            }
        }

        foreach (var rel in cache.FindExportedAssemblies())
        {
            stoppingToken.ThrowIfCancellationRequested();
            var fullPath = discovery.FullPath(rel);
            if (File.Exists(fullPath))
                continue;
            try
            {
                if (cache.RemoveExport(fullPath, rel, stoppingToken))
                    removed++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the export of deleted assembly {Assembly}", rel);
            }
        }

        logger.LogInformation(
            "Synchronized {Count} assemblies in {Seconds:N1}s: {Exported} exported, {Reused} up to date, {Failed} failed, {Removed} stale exports removed",
            assemblies.Count, stopwatch.Elapsed.TotalSeconds, exported, reused, failed, removed);
    }

    private void ExportOne(string fullPath, int attempt, CancellationToken stoppingToken)
    {
        if (!File.Exists(fullPath))
            return; // deleted again before it settled; the delete event schedules the cleanup
        TryExport(fullPath, Path.GetRelativePath(discovery.Root, fullPath), attempt, stoppingToken);
    }

    /// <summary>true = exported now, false = existing export reused, null = failed (retry scheduled if transient).</summary>
    private bool? TryExport(string fullPath, string relativePath, int attempt, CancellationToken stoppingToken)
    {
        try
        {
            return !cache.GetExport(fullPath, relativePath, stoppingToken).Reused;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usually the DLL is still locked by whatever is writing it
            if (attempt + 1 < MaxAttempts && File.Exists(fullPath))
            {
                var delay = Debounce * Math.Pow(2, attempt);
                logger.LogDebug("Export of {Assembly} failed ({Message}); retrying in {Seconds:N0}s", relativePath, ex.Message, delay.TotalSeconds);
                Schedule(fullPath, delay, attempt + 1);
            }
            else
            {
                logger.LogWarning(ex, "Could not export {Assembly}", relativePath);
            }
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not export {Assembly}: {Message}", relativePath, ex.Message);
            return null;
        }
    }

    private FileSystemWatcher? StartWatcher()
    {
        try
        {
            // No filter: directory events matter too (a folder of DLLs moved in or deleted raises a single event)
            var watcher = new FileSystemWatcher(discovery.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => OnCreated(e.FullPath);
            watcher.Changed += (_, e) => OnChanged(e.FullPath);
            watcher.Deleted += (_, _) => ScheduleReconcile(Debounce);
            watcher.Renamed += (_, _) => ScheduleReconcile(Debounce);
            watcher.Error += (_, e) =>
            {
                logger.LogWarning(e.GetException(), "File watcher error; rescanning '{Directory}'", discovery.Root);
                ScheduleReconcile(Debounce);
            };
            watcher.EnableRaisingEvents = true;
            logger.LogInformation("Watching '{Directory}' for DLL changes", discovery.Root);
            return watcher;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not watch '{Directory}'; only the startup export will run", discovery.Root);
            return null;
        }
    }

    private void OnCreated(string path)
    {
        if (IsDll(path))
            Schedule(path, Debounce, attempt: 0);
        else if (Directory.Exists(path))
            ScheduleReconcile(Debounce); // a folder moved in can arrive with DLLs already inside and no per-file events
    }

    // Changed also fires for folders whenever something inside them changes; those files raise their own events
    private void OnChanged(string path)
    {
        if (IsDll(path))
            Schedule(path, Debounce, attempt: 0);
    }

    private static bool IsDll(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private void Schedule(string fullPath, TimeSpan delay, int attempt)
    {
        lock (_gate)
            _pending[fullPath] = new Pending(DateTime.UtcNow + delay, attempt);
        _signal.Release();
    }

    private void ScheduleReconcile(TimeSpan delay)
    {
        lock (_gate)
            _reconcileDueUtc = DateTime.UtcNow + delay;
        _signal.Release();
    }
}

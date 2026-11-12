using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>The language server can't serve requests; the message says why and how to fix it.</summary>
public sealed class LanguageServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Owns the Roslyn language server process. It is started lazily on the first tool call: find dotnet, find a
/// compatible installed server (or download the pinned one), export every scanned DLL, generate the workspace,
/// start and initialize the server, open the solution and wait for the load. When exports change afterwards the
/// workspace is regenerated and the solution reopened (debounced). A crashed server is restarted on the next call,
/// at most <see cref="MaxRestarts"/> times per <see cref="RestartWindow"/>.
/// </summary>
public sealed class RoslynLanguageServerHost : IHostedService, IAsyncDisposable
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(90);
    private const int MaxOpenDocuments = 32;

    private readonly LanguageServerOptions _options;
    private readonly DotnetLocator _dotnetLocator;
    private readonly RoslynServerLocator _serverLocator;
    private readonly RoslynServerInstaller _installer;
    private readonly WorkspaceGenerator _workspace;
    private readonly AssemblyDiscoveryService _discovery;
    private readonly ProjectExportCache _cache;
    private readonly ILogger<RoslynLanguageServerHost> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _badServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> _crashes = new();
    private readonly LinkedList<string> _openDocuments = new();
    private readonly Dictionary<string, IReadOnlyList<LspDocumentSymbol>> _symbolCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _documentLock = new();
    private readonly SemaphoreSlim _documentSync = new(1, 1);
    private long _documentGeneration;
    private readonly Timer _reloadTimer;
    private readonly CancellationTokenSource _shutdown = new();

    private LspClient? _client;
    private Task _loaded = Task.CompletedTask;
    private DateTime _workspaceGeneratedUtc = DateTime.MinValue;
    private long _exportsChangedTicks;

    public RoslynLanguageServerHost(
        LanguageServerOptions options,
        DotnetLocator dotnetLocator,
        RoslynServerLocator serverLocator,
        RoslynServerInstaller installer,
        WorkspaceGenerator workspace,
        AssemblyDiscoveryService discovery,
        ProjectExportCache cache,
        ILogger<RoslynLanguageServerHost> logger)
    {
        _options = options;
        _dotnetLocator = dotnetLocator;
        _serverLocator = serverLocator;
        _installer = installer;
        _workspace = workspace;
        _discovery = discovery;
        _cache = cache;
        _logger = logger;
        _reloadTimer = new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _cache.ExportsChanged += OnExportsChanged;
    }

    public string OutputDirectory => _cache.OutputDirectory;

    /// <summary>Starts the server if needed and waits for any pending (re)load; throws <see cref="LanguageServerUnavailableException"/>.</summary>
    public async Task<LspClient> GetClientAsync(CancellationToken cancellationToken)
    {
        LspClient client;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is null || _client.HasExited)
                await StartAsync(cancellationToken);
            client = _client!;
        }
        finally
        {
            _gate.Release();
        }

        await _loaded.WaitAsync(cancellationToken);
        return client;
    }

    /// <summary>
    /// Sends didOpen for a file before position-based requests; keeps at most <see cref="MaxOpenDocuments"/> open.
    /// All didOpen/didClose traffic is serialized and a path is recorded as open only after its didOpen was sent:
    /// Roslyn treats a duplicate didOpen or a didClose for an unopened document as fatal and shuts down.
    /// </summary>
    public async Task EnsureOpenAsync(LspClient client, string path, CancellationToken cancellationToken = default)
    {
        await _documentSync.WaitAsync(cancellationToken);
        try
        {
            lock (_documentLock)
            {
                var node = _openDocuments.Find(path);
                if (node is not null)
                {
                    _openDocuments.Remove(node);
                    _openDocuments.AddFirst(node);
                    return;
                }
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken);
            await client.NotifyAsync("textDocument/didOpen", new
            {
                textDocument = new { uri = Lsp.PathToUri(path), languageId = "csharp", version = 1, text },
            });

            string? evicted = null;
            lock (_documentLock)
            {
                _openDocuments.AddFirst(path);
                if (_openDocuments.Count > MaxOpenDocuments)
                {
                    evicted = _openDocuments.Last!.Value;
                    _openDocuments.RemoveLast();
                }
            }
            if (evicted is not null)
                await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = Lsp.PathToUri(evicted) } });
        }
        finally
        {
            _documentSync.Release();
        }
    }

    /// <summary>Hierarchical symbols of a file, cached until the next workspace reload.</summary>
    public async Task<IReadOnlyList<LspDocumentSymbol>> GetDocumentSymbolsAsync(LspClient client, string path, CancellationToken cancellationToken)
    {
        long generation;
        lock (_documentLock)
        {
            if (_symbolCache.TryGetValue(path, out var cached))
                return cached;
            generation = _documentGeneration;
        }

        await EnsureOpenAsync(client, path, cancellationToken);
        var result = await client.RequestAsync("textDocument/documentSymbol",
            new { textDocument = new { uri = Lsp.PathToUri(path) } }, cancellationToken);
        IReadOnlyList<LspDocumentSymbol> symbols = result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray()
                .Where(e => e.TryGetProperty("selectionRange", out _))
                .Select(e => e.Deserialize<LspDocumentSymbol>(Lsp.JsonOptions)!)
                .ToList()
            : [];

        // A reload in the meantime makes this answer stale; return it to this caller but don't cache it
        lock (_documentLock)
        {
            if (generation == _documentGeneration)
                _symbolCache[path] = symbols;
        }
        return symbols;
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            var now = DateTime.UtcNow;
            while (_crashes.Count > 0 && now - _crashes.Peek() > RestartWindow)
                _crashes.Dequeue();
            if (_crashes.Count >= MaxRestarts)
                throw new LanguageServerUnavailableException(
                    $"the language server crashed {_crashes.Count + 1} times within {RestartWindow.TotalMinutes:N0} minutes; not restarting it " +
                    $"for now. See the logs in '{_options.LogDirectory}'.");
            _crashes.Enqueue(now);
            _logger.LogWarning("The Roslyn language server exited; restarting it");
            await _client.DisposeAsync();
            _client = null;
        }

        var located = _dotnetLocator.Locate(cancellationToken);
        if (located.Install is null)
            throw new LanguageServerUnavailableException(located.Error!);
        var dotnet = located.Install;

        PrepareWorkspace(cancellationToken);

        // Installed servers first; the download is the fallback, also when every discovered one fails to start
        var failures = new List<string>();
        foreach (var candidate in _serverLocator.FindCompatible(dotnet).Where(c => !_badServers.Contains(c.DllPath)))
        {
            if (await TryStartAsync(dotnet, candidate, failures, cancellationToken))
                return;
        }

        if (!string.IsNullOrWhiteSpace(_options.LanguageServerPath))
            throw new LanguageServerUnavailableException(
                $"the language server at --LanguageServerPath could not be started ({string.Join("; ", failures)}). " +
                "Fix or remove the setting to let the server find or download one.");

        string downloaded;
        try
        {
            downloaded = await _installer.EnsureInstalledAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add($"download of roslyn-language-server {_installer.Version}: {ex.Message}");
            throw new LanguageServerUnavailableException(
                $"no usable Roslyn language server was found and the download failed ({string.Join("; ", failures)}). " +
                "Install the VS Code C# extension, check network access to nuget.org, or pass --LanguageServerPath.", ex);
        }

        var reason = RoslynServerLocator.CheckCompatibility(downloaded, dotnet, out var version);
        if (reason is not null)
            throw new LanguageServerUnavailableException($"the downloaded language server can't run on {dotnet.DotnetPath}: {reason}.");
        if (_badServers.Contains(downloaded) ||
            !await TryStartAsync(dotnet, new LanguageServerCandidate(downloaded, "download", version), failures, cancellationToken))
            throw new LanguageServerUnavailableException(
                $"the Roslyn language server could not be started ({string.Join("; ", failures)}). See the logs in '{_options.LogDirectory}'.");
    }

    /// <summary>Exports every scanned DLL (joining exports already running) and writes the workspace files.</summary>
    private void PrepareWorkspace(CancellationToken cancellationToken)
    {
        foreach (var relative in _discovery.FindAssemblies())
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _cache.GetExport(_discovery.FullPath(relative), relative, cancellationToken);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    // Usually a transient lock (antivirus, indexer, the background monitor's swap) on the export folder
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(attempt));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Language server workspace will not include {Assembly}: {Message}", relative, ex.Message);
                    break;
                }
            }
        }
        _workspaceGeneratedUtc = DateTime.UtcNow;
        _workspace.Generate();
    }

    private async Task<bool> TryStartAsync(DotnetInstall dotnet, LanguageServerCandidate candidate, List<string> failures, CancellationToken cancellationToken)
    {
        LspClient? client = null;
        try
        {
            _logger.LogInformation("Using Roslyn language server {Version} from {Source}: {Path}",
                candidate.Version?.ToString() ?? "(unknown version)", candidate.Source, candidate.DllPath);
            client = LspClient.Start(dotnet.DotnetPath, candidate.DllPath, _options.LogDirectory, _logger);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(InitializeTimeout);
                var initialize = client.InitializeAsync(_cache.OutputDirectory, timeout.Token);
                if (await Task.WhenAny(initialize, client.Exited) != initialize)
                    throw new InvalidOperationException("the process exited during initialize");
                await initialize;
            }

            var generation = client.LoadGeneration;
            var opened = DateTime.UtcNow;
            await client.OpenSolutionAsync(_workspace.SolutionPath);
            ResetDocuments();
            _client = client;
            _loaded = WaitForLoadAsync(client, generation, opened);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
                await client.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Roslyn language server {Path} ({Source}) failed to start: {Message}", candidate.DllPath, candidate.Source, ex.Message);
            failures.Add($"{candidate.Source}: {ex.Message}");
            _badServers.Add(candidate.DllPath);
            if (client is not null)
                await client.DisposeAsync();
            return false;
        }
    }

    /// <summary>Never faults: on timeout or a crash the callers proceed and the request itself reports any failure.</summary>
    private async Task WaitForLoadAsync(LspClient client, long afterGeneration, DateTime openedUtc)
    {
        try
        {
            if (!await client.WaitUntilLoadedAsync(afterGeneration, openedUtc, _options.LoadTimeout, _shutdown.Token))
                _logger.LogWarning("The language server had not finished loading the workspace after {Seconds:N0}s; answering with what is loaded",
                    _options.LoadTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Waiting for the language server to load the workspace failed: {Message}", ex.Message);
        }
    }

    private void OnExportsChanged(string relativePath)
    {
        Interlocked.Exchange(ref _exportsChangedTicks, DateTime.UtcNow.Ticks);
        // Armed even before the first start: a change landing between workspace generation and the server coming up
        // still triggers a reload; ReloadAsync ignores changes the generated workspace already covers
        if (!_shutdown.IsCancellationRequested)
            _reloadTimer.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
    }

    private async Task ReloadAsync()
    {
        try
        {
            await _gate.WaitAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var client = _client;
            if (client is null || client.HasExited)
                return; // the next call restarts it with a fresh workspace
            if (new DateTime(Interlocked.Read(ref _exportsChangedTicks), DateTimeKind.Utc) <= _workspaceGeneratedUtc)
                return; // the change was already part of the last generated workspace

            _workspaceGeneratedUtc = DateTime.UtcNow;
            _workspace.Generate();

            // Open buffers would shadow the re-exported files; close exactly what the server has open
            await _documentSync.WaitAsync(_shutdown.Token);
            try
            {
                string[] open;
                lock (_documentLock)
                    open = _openDocuments.ToArray();
                ResetDocuments();
                foreach (var path in open)
                    await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = Lsp.PathToUri(path) } });
            }
            finally
            {
                _documentSync.Release();
            }

            var generation = client.LoadGeneration;
            var opened = DateTime.UtcNow;
            await client.OpenSolutionAsync(_workspace.SolutionPath);
            _loaded = WaitForLoadAsync(client, generation, opened);
            _logger.LogInformation("Exports changed; the language server is reloading the workspace");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reloading the language server workspace failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetDocuments()
    {
        lock (_documentLock)
        {
            _openDocuments.Clear();
            _symbolCache.Clear();
            _documentGeneration++;
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
            return;
        _cache.ExportsChanged -= OnExportsChanged;
        _shutdown.Cancel();
        await _reloadTimer.DisposeAsync();
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.DisposeAsync();
    }
}

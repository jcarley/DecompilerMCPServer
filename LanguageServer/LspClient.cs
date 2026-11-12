using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>
/// A running Roslyn language server process and the JSON-RPC (LSP, Content-Length framed) connection to it over its
/// stdin/stdout. The child's stdout is never forwarded to ours: it is the LSP stream. Its stderr goes to the logger.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettlePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StuckRestore = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly JsonRpc _rpc;
    private readonly ServerCallbacks _callbacks;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public string ServerPath { get; }

    /// <summary>Completes when the server process exits or the connection drops.</summary>
    public Task Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Incremented each time the server reports a completed (re)load of the workspace projects.</summary>
    public long LoadGeneration => Interlocked.Read(ref _callbacks.Generation);

    private LspClient(Process process, string serverPath, ILogger logger)
    {
        _process = process;
        _logger = logger;
        ServerPath = serverPath;
        _callbacks = new ServerCallbacks(logger);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _exited.TrySetResult();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                logger.LogDebug("[roslyn stderr] {Line}", e.Data);
        };
        process.BeginErrorReadLine();

        var handler = new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            new SystemTextJsonFormatter());
        _rpc = new JsonRpc(handler);
        _rpc.AddLocalRpcTarget(_callbacks);
        _rpc.Disconnected += (_, e) =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                logger.LogWarning("Roslyn language server disconnected: {Reason} {Description}", e.Reason, e.Description);
            _exited.TrySetResult();
        };
        _rpc.StartListening();
        if (process.HasExited)
            _exited.TrySetResult();
    }

    /// <summary>Starts <c>dotnet &lt;serverDll&gt; --stdio</c>. The server exits by itself if this process dies (--clientProcessId).</summary>
    public static LspClient Start(string dotnetPath, string serverDll, string logDirectory, ILogger logger)
    {
        Directory.CreateDirectory(logDirectory);
        var psi = new ProcessStartInfo(dotnetPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!,
        };
        psi.ArgumentList.Add(serverDll);
        psi.ArgumentList.Add("--stdio");
        psi.ArgumentList.Add("--logLevel=Information");
        psi.ArgumentList.Add($"--extensionLogDirectory={logDirectory}");
        psi.ArgumentList.Add($"--clientProcessId={Environment.ProcessId}");

        // The server's MSBuild build host finds the SDK through the dotnet it runs on; make that the one we picked
        var dotnetDir = Path.GetDirectoryName(dotnetPath)!;
        psi.Environment["DOTNET_ROOT"] = dotnetDir;
        psi.Environment["PATH"] = dotnetDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        logger.LogInformation("Starting Roslyn language server: {Dotnet} {Server}", dotnetPath, serverDll);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"'{dotnetPath}' did not start.");
        return new LspClient(process, serverDll, logger);
    }

    /// <summary>LSP initialize/initialized handshake; throws if the server lacks a capability the tools need.</summary>
    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken)
    {
        var rootUri = Lsp.PathToUri(rootPath);
        var result = await RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "DecompilerMCPServer" },
            rootUri,
            workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(rootPath) } },
            capabilities = new
            {
                workspace = new
                {
                    configuration = true,
                    workspaceFolders = true,
                    symbol = new { symbolKind = new { valueSet = Enumerable.Range(1, 26).ToArray() } },
                },
                textDocument = new
                {
                    synchronization = new { didSave = false },
                    hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                    definition = new { linkSupport = false },
                    implementation = new { linkSupport = false },
                    references = new { },
                    documentSymbol = new
                    {
                        hierarchicalDocumentSymbolSupport = true,
                        symbolKind = new { valueSet = Enumerable.Range(1, 26).ToArray() },
                    },
                },
                window = new { workDoneProgress = true },
            },
        }, cancellationToken);

        var capabilities = result.TryGetProperty("capabilities", out var caps) ? caps : default;
        string[] required = ["definitionProvider", "referencesProvider", "documentSymbolProvider", "hoverProvider",
            "implementationProvider", "workspaceSymbolProvider"];
        var missing = required.Where(r => capabilities.ValueKind != JsonValueKind.Object || !capabilities.TryGetProperty(r, out _)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"The language server lacks required capabilities: {string.Join(", ", missing)}.");

        await NotifyAsync("initialized", new { });
    }

    /// <summary>Roslyn-specific: load (or reload) a solution. Completion is reported through the log; see <see cref="WaitUntilLoadedAsync"/>.</summary>
    public Task OpenSolutionAsync(string solutionPath) =>
        NotifyAsync("solution/open", new { solution = Lsp.PathToUri(solutionPath) });

    /// <summary>
    /// Waits until the workspace settled after a solution/open sent at <paramref name="openedUtc"/>: no restore running,
    /// and either a load newer than <paramref name="afterGeneration"/> completed followed by a short quiet period, or
    /// the project system stayed quiet for <see cref="SettlePeriod"/> (the server may have reloaded on its own before
    /// the reopen, in which case no new completion is logged). Returns false on timeout.
    /// </summary>
    public async Task<bool> WaitUntilLoadedAsync(long afterGeneration, DateTime openedUtc, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (HasExited)
                throw new InvalidOperationException("The language server exited while loading the workspace.");

            var lastActivity = new DateTime(Math.Max(Interlocked.Read(ref _callbacks.LastActivityTicks), openedUtc.Ticks), DateTimeKind.Utc);
            var idle = DateTime.UtcNow - lastActivity;
            bool restoring = Volatile.Read(ref _callbacks.Restoring) && idle < StuckRestore;
            if (!restoring && (idle >= SettlePeriod || (LoadGeneration > afterGeneration && idle >= QuietPeriod)))
                return true;
            await Task.Delay(200, cancellationToken);
        }
        return false;
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (HasExited)
            throw new InvalidOperationException("The language server is not running.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The language server did not answer '{method}' within {RequestTimeout.TotalSeconds:N0}s.");
        }
    }

    public Task NotifyAsync(string method, object parameters) => _rpc.NotifyWithParameterObjectAsync(method, parameters);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            if (!HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _rpc.InvokeWithCancellationAsync<object>("shutdown", [], cts.Token);
                await _rpc.NotifyAsync("exit");
                await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Language server did not shut down cleanly");
        }
        finally
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            _rpc.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>Requests and notifications the server sends us.</summary>
    private sealed class ServerCallbacks(ILogger logger)
    {
        public long Generation;
        public long LastActivityTicks = DateTime.UtcNow.Ticks;
        public bool Restoring;

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void LogMessage(JsonElement message)
        {
            var text = message.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var type = message.TryGetProperty("type", out var t) && t.TryGetInt32(out var parsed) ? parsed : 4;

            if (text.Contains("LanguageServerProjectSystem", StringComparison.Ordinal) || text.Contains("Restor", StringComparison.Ordinal))
                Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
            if (text.Contains("Running dotnet restore", StringComparison.Ordinal))
                Volatile.Write(ref Restoring, true);
            if (text.Contains("Restore complete", StringComparison.Ordinal) ||
                text.Contains("Restore failed", StringComparison.OrdinalIgnoreCase))
                Volatile.Write(ref Restoring, false);

            if (text.Contains("Completed (re)load of", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Generation);
                logger.LogInformation("[roslyn] {Message}", text);
            }
            else if (type == 1)
                logger.LogWarning("[roslyn] {Message}", text);
            else
                logger.LogDebug("[roslyn] {Message}", text);
        }

        [JsonRpcMethod("workspace/projectInitializationComplete")]
        public void ProjectInitializationComplete() => Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);

        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public object?[] Configuration(JsonElement parameters) =>
            parameters.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(_ => (object?)null).ToArray() : [];

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public void WorkDoneProgressCreate(JsonElement parameters) { }

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public void RegisterCapability(JsonElement parameters) { }

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public void UnregisterCapability(JsonElement parameters) { }

        [JsonRpcMethod("window/showMessageRequest", UseSingleObjectParameterDeserialization = true)]
        public object? ShowMessageRequest(JsonElement parameters) => null;

        // The server restores on its own after loading; nothing for the client to do
        [JsonRpcMethod("workspace/_roslyn_projectNeedsRestore", UseSingleObjectParameterDeserialization = true)]
        public void ProjectNeedsRestore(JsonElement parameters) { }

        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void ShowMessage(JsonElement parameters) =>
            logger.LogDebug("[roslyn] {Message}", parameters.TryGetProperty("message", out var m) ? m.GetString() : null);

        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void Progress(JsonElement parameters) { }

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JsonElement parameters) { }
    }
}

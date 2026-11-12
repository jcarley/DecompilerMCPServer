using System.Security.Cryptography;
using System.Text;
using DecompilerMCPServer;
using DecompilerMCPServer.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Fail fast on missing/invalid ScanDirectory, before starting the transport.
// This check stays first: the install scripts' smoke test expects exit 1 and a "ScanDirectory" message on stderr.
var scanDirectory = builder.Configuration["ScanDirectory"];
if (string.IsNullOrWhiteSpace(scanDirectory))
{
    Console.Error.WriteLine("Error: ScanDirectory is not configured. " +
        "Pass --ScanDirectory /path/to/dlls or set the ScanDirectory environment variable.");
    return 1;
}
scanDirectory = Normalize(scanDirectory);
if (!Directory.Exists(scanDirectory))
{
    Console.Error.WriteLine($"Error: ScanDirectory '{scanDirectory}' does not exist or is not a directory.");
    return 1;
}

// OutputDirectory is optional; the default is a per-user folder keyed by the scan directory.
var outputDirectory = builder.Configuration["OutputDirectory"];
outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
    ? DefaultOutputDirectory(scanDirectory)
    : Normalize(outputDirectory);

// ILSpy writes embedded resources out as files, which can include *.dll: the two trees must not overlap
if (IsSameOrNested(scanDirectory, outputDirectory) || IsSameOrNested(outputDirectory, scanDirectory))
{
    Console.Error.WriteLine($"Error: OutputDirectory '{outputDirectory}' and ScanDirectory '{scanDirectory}' " +
        "must not be the same or nested inside each other.");
    return 1;
}
try
{
    Directory.CreateDirectory(outputDirectory);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"Error: OutputDirectory '{outputDirectory}' could not be created: {ex.Message}");
    return 1;
}

// WatchScanDirectory (default true): export everything in the background at startup and keep exports in sync
var watchSetting = builder.Configuration["WatchScanDirectory"];
bool watchScanDirectory = true;
if (!string.IsNullOrWhiteSpace(watchSetting) && !bool.TryParse(watchSetting, out watchScanDirectory))
{
    Console.Error.WriteLine($"Error: WatchScanDirectory must be 'true' or 'false', got '{watchSetting}'.");
    return 1;
}

// EnableLanguageServer (default false): semantic tools backed by a Roslyn language server started on first use
var languageServerSetting = builder.Configuration["EnableLanguageServer"];
bool enableLanguageServer = false;
if (!string.IsNullOrWhiteSpace(languageServerSetting) && !bool.TryParse(languageServerSetting, out enableLanguageServer))
{
    Console.Error.WriteLine($"Error: EnableLanguageServer must be 'true' or 'false', got '{languageServerSetting}'.");
    return 1;
}

LanguageServerOptions? languageServerOptions = null;
if (enableLanguageServer)
{
    var serverPath = NullIfBlank(builder.Configuration["LanguageServerPath"]);
    if (serverPath is not null && !File.Exists(serverPath = Path.GetFullPath(serverPath)))
    {
        Console.Error.WriteLine($"Error: LanguageServerPath '{serverPath}' does not exist. " +
            "It must point at Microsoft.CodeAnalysis.LanguageServer.dll.");
        return 1;
    }
    var dotnetPath = NullIfBlank(builder.Configuration["DotnetPath"]);
    if (dotnetPath is not null && !File.Exists(dotnetPath = Path.GetFullPath(dotnetPath)))
    {
        Console.Error.WriteLine($"Error: DotnetPath '{dotnetPath}' does not exist. It must point at the dotnet executable.");
        return 1;
    }
    var timeoutSetting = NullIfBlank(builder.Configuration["LanguageServerLoadTimeoutSeconds"]);
    int loadTimeoutSeconds = 300;
    if (timeoutSetting is not null && (!int.TryParse(timeoutSetting, out loadTimeoutSeconds) || loadTimeoutSeconds < 1))
    {
        Console.Error.WriteLine($"Error: LanguageServerLoadTimeoutSeconds must be a positive integer, got '{timeoutSetting}'.");
        return 1;
    }
    languageServerOptions = new LanguageServerOptions(
        serverPath,
        NullIfBlank(builder.Configuration["LanguageServerVersion"]),
        dotnetPath,
        LanguageServerOptions.DefaultInstallRoot,
        TimeSpan.FromSeconds(loadTimeoutSeconds));
}

// stdout carries JSON-RPC; all logging goes to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new ScanOptions(scanDirectory));
builder.Services.AddSingleton(new ExportOptions(outputDirectory));
builder.Services.AddSingleton<DecompilerService>();
builder.Services.AddSingleton<AssemblyDiscoveryService>();
builder.Services.AddSingleton<ProjectExportCache>();
if (watchScanDirectory)
    builder.Services.AddHostedService<ScanDirectoryMonitor>();

var mcp = builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DecompilerTools>();

if (languageServerOptions is not null)
{
    builder.Services.AddSingleton(languageServerOptions);
    builder.Services.AddSingleton<DotnetLocator>();
    builder.Services.AddSingleton<RoslynServerLocator>();
    builder.Services.AddSingleton<RoslynServerInstaller>();
    builder.Services.AddSingleton<WorkspaceGenerator>();
    builder.Services.AddSingleton<RoslynLanguageServerHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RoslynLanguageServerHost>());
    builder.Services.AddSingleton<SymbolResolver>();
    mcp.WithTools<LanguageServerTools>();
}

await builder.Build().RunAsync();
return 0;

static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

static bool IsSameOrNested(string outer, string inner) =>
    string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase) ||
    inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

// <LocalAppData>/DecompilerMCPServer/sources/<leaf>-<8 hex of sha256(path)>: readable, unique per scan folder,
// and short enough to leave room for namespace/type paths on Windows.
static string DefaultOutputDirectory(string scanDirectory)
{
    var leaf = Path.GetFileName(scanDirectory);
    if (string.IsNullOrEmpty(leaf))
        leaf = "root";
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scanDirectory.ToLowerInvariant())))[..8];
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DecompilerMCPServer", "sources", $"{leaf}-{hash}");
}

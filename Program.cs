using DecompilerMCPServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Fail fast on missing/invalid ScanDirectory, before starting the transport
var scanDirectory = builder.Configuration["ScanDirectory"];
if (string.IsNullOrWhiteSpace(scanDirectory))
{
    Console.Error.WriteLine("Error: ScanDirectory is not configured. " +
        "Pass --ScanDirectory /path/to/dlls or set the ScanDirectory environment variable.");
    return 1;
}
scanDirectory = Path.GetFullPath(scanDirectory);
if (!Directory.Exists(scanDirectory))
{
    Console.Error.WriteLine($"Error: ScanDirectory '{scanDirectory}' does not exist or is not a directory.");
    return 1;
}

// stdout carries JSON-RPC; all logging goes to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new ScanOptions(scanDirectory));
builder.Services.AddSingleton<DecompilerService>();
builder.Services.AddSingleton<AssemblyDiscoveryService>();
builder.Services.AddSingleton<DecompilationCache>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DecompilerTools>();

await builder.Build().RunAsync();
return 0;

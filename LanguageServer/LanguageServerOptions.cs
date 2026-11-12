using System.Runtime.InteropServices;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>Settings for the optional Roslyn language server integration (<c>--EnableLanguageServer true</c>).</summary>
/// <param name="LanguageServerPath">Explicit Microsoft.CodeAnalysis.LanguageServer.dll; disables discovery and download.</param>
/// <param name="LanguageServerVersion">Explicit package version to use from the cache or download; disables editor discovery.</param>
/// <param name="DotnetPath">Explicit dotnet host; disables dotnet discovery.</param>
/// <param name="InstallRoot">Where downloaded language servers are cached, one folder per version and RID.</param>
/// <param name="LoadTimeout">How long to wait for the language server to load the workspace before answering anyway.</param>
public sealed record LanguageServerOptions(
    string? LanguageServerPath,
    string? LanguageServerVersion,
    string? DotnetPath,
    string InstallRoot,
    TimeSpan LoadTimeout)
{
    public static string DefaultInstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DecompilerMCPServer", "roslyn-ls");

    public string LogDirectory => Path.Combine(InstallRoot, "logs");
}

/// <summary>The bits of the host environment discovery depends on, injectable for tests.</summary>
public sealed record PlatformInfo(OSPlatform OS, Architecture Architecture, string HomeDirectory, Func<string, string?> GetEnvironmentVariable)
{
    public static PlatformInfo Current { get; } = new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
            : OSPlatform.Linux,
        RuntimeInformation.OSArchitecture,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable);

    public bool IsWindows => OS == OSPlatform.Windows;

    /// <summary>.NET runtime identifier for the language server package, e.g. win-x64, osx-arm64, linux-x64.</summary>
    public string Rid => $"{(IsWindows ? "win" : OS == OSPlatform.OSX ? "osx" : "linux")}-{ArchName}";

    /// <summary>VS Code's platform suffix for platform-specific extensions, e.g. win32-x64, darwin-arm64.</summary>
    public string VsCodePlatform => $"{(IsWindows ? "win32" : OS == OSPlatform.OSX ? "darwin" : "linux")}-{ArchName}";

    public string DotnetExecutableName => IsWindows ? "dotnet.exe" : "dotnet";

    /// <summary>Path comparison matching the platform's default filesystem case sensitivity.</summary>
    public StringComparison PathComparison => OS == OSPlatform.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private string ArchName => Architecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => "x64",
    };
}

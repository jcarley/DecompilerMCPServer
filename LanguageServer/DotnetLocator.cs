using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>A dotnet host and the shared runtimes / SDKs it reports.</summary>
/// <param name="Runtimes">Shared framework name (e.g. Microsoft.NETCore.App) to installed versions.</param>
public sealed record DotnetInstall(string DotnetPath, IReadOnlyDictionary<string, IReadOnlyList<Version>> Runtimes, IReadOnlyList<Version> Sdks)
{
    public const string NetCoreApp = "Microsoft.NETCore.App";

    public bool HasRuntime(int minimumMajor) =>
        Runtimes.TryGetValue(NetCoreApp, out var versions) && versions.Any(v => v.Major >= minimumMajor);

    public bool HasSdk(int minimumMajor) => Sdks.Any(v => v.Major >= minimumMajor);
}

/// <summary>Result of looking for dotnet: an install, or an error message with a remediation hint.</summary>
public sealed record DotnetLocateResult(DotnetInstall? Install, string? Error);

/// <summary>
/// Finds an installed dotnet host that can run the Roslyn language server (a .NET 10+ runtime) and load projects
/// (a .NET 10+ SDK). Order: --DotnetPath, DOTNET_ROOT, PATH, then the default install locations.
/// </summary>
public sealed class DotnetLocator(LanguageServerOptions options, ILogger<DotnetLocator> logger)
{
    public const int RequiredMajor = 10;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    internal PlatformInfo Platform { get; init; } = PlatformInfo.Current;

    public DotnetLocateResult Locate(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(options.DotnetPath))
        {
            var install = Probe(options.DotnetPath, cancellationToken);
            if (install is null)
                return new(null, $"DotnetPath '{options.DotnetPath}' could not be run.");
            if (!install.HasRuntime(RequiredMajor))
                return new(null, $"DotnetPath '{options.DotnetPath}' has no .NET {RequiredMajor}+ runtime. " +
                                 $"Install the .NET {RequiredMajor} SDK from https://dot.net/download.");
            WarnIfNoSdk(install);
            return new(install, null);
        }

        DotnetInstall? runtimeOnly = null;
        foreach (var candidate in EnumerateCandidates(Platform))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var install = Probe(candidate, cancellationToken);
            if (install is null || !install.HasRuntime(RequiredMajor))
            {
                logger.LogDebug("dotnet candidate {Path} rejected: no .NET {Major}+ runtime", candidate, RequiredMajor);
                continue;
            }
            if (install.HasSdk(RequiredMajor))
            {
                logger.LogInformation("Using dotnet at {Path}", install.DotnetPath);
                return new(install, null);
            }
            runtimeOnly ??= install;
        }

        if (runtimeOnly is not null)
        {
            WarnIfNoSdk(runtimeOnly);
            return new(runtimeOnly, null);
        }

        return new(null, $"No .NET {RequiredMajor}+ installation was found (checked DOTNET_ROOT, PATH and the default install " +
                         $"locations). Install the .NET {RequiredMajor} SDK from https://dot.net/download, or pass --DotnetPath.");
    }

    private void WarnIfNoSdk(DotnetInstall install)
    {
        if (!install.HasSdk(RequiredMajor))
            logger.LogWarning("dotnet at {Path} has a .NET {Major} runtime but no .NET {Major} SDK; the language server needs " +
                              "the SDK to load projects, so results may be incomplete. Install the .NET {Major} SDK.",
                install.DotnetPath, RequiredMajor, RequiredMajor, RequiredMajor);
    }

    /// <summary>Candidate dotnet host paths in priority order, de-duplicated, existing files only.</summary>
    public static IReadOnlyList<string> EnumerateCandidates(PlatformInfo platform, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var exe = platform.DotnetExecutableName;
        var raw = new List<string>();

        foreach (var variable in new[] { $"DOTNET_ROOT_{platform.Architecture.ToString().ToUpperInvariant()}", "DOTNET_ROOT" })
        {
            var root = platform.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
                raw.Add(Path.Combine(root, exe));
        }

        var path = platform.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(platform.IsWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
                raw.Add(Path.Combine(dir.Trim().Trim('"'), exe));
        }

        if (platform.IsWindows)
        {
            var programFiles = platform.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
                raw.Add(Path.Combine(programFiles, "dotnet", exe));
        }
        else if (platform.OS == System.Runtime.InteropServices.OSPlatform.OSX)
        {
            raw.Add("/usr/local/share/dotnet/" + exe);
            raw.Add("/opt/homebrew/bin/" + exe);
        }
        else
        {
            raw.Add("/usr/share/dotnet/" + exe);
            raw.Add("/usr/lib/dotnet/" + exe);
            raw.Add("/usr/lib64/dotnet/" + exe);
        }
        raw.Add(Path.Combine(platform.HomeDirectory, ".dotnet", exe));

        var seen = new HashSet<string>(platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var results = new List<string>();
        foreach (var candidate in raw)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (fileExists(full) && seen.Add(full))
                results.Add(full);
        }
        return results;
    }

    /// <summary>Runs <c>dotnet --list-runtimes</c> and <c>--list-sdks</c>; null when the host can't be run.</summary>
    private DotnetInstall? Probe(string dotnetPath, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = ResolveLink(dotnetPath);
            var runtimes = ParseRuntimes(Run(resolved, "--list-runtimes", cancellationToken));
            var sdks = ParseSdks(Run(resolved, "--list-sdks", cancellationToken));
            return new DotnetInstall(resolved, runtimes, sdks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not probe dotnet at {Path}", dotnetPath);
            return null;
        }
    }

    private static string ResolveLink(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static string Run(string dotnetPath, string argument, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(dotnetPath, argument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        if (!process.WaitForExit(ProbeTimeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"'{dotnetPath} {argument}' did not finish within {ProbeTimeout.TotalSeconds:N0}s");
        }
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>Parses lines like <c>Microsoft.NETCore.App 10.0.11 [C:\Program Files\dotnet\shared\...]</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Version>> ParseRuntimes(string output)
    {
        var result = new Dictionary<string, List<Version>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !TryParseVersion(parts[1], out var version))
                continue;
            if (!result.TryGetValue(parts[0], out var list))
                result[parts[0]] = list = [];
            list.Add(version);
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Version>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Parses lines like <c>10.0.400 [C:\Program Files\dotnet\sdk]</c>.</summary>
    public static IReadOnlyList<Version> ParseSdks(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2)[0])
            .Select(text => TryParseVersion(text, out var v) ? v : null)
            .OfType<Version>()
            .ToList();

    /// <summary>Parses the numeric part of a version, ignoring any prerelease/build suffix ("10.0.0-rc.1" -> 10.0.0).</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var numeric = text.Split('-', '+')[0];
        if (Version.TryParse(numeric, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version();
        return false;
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>A Roslyn language server found on disk, and where it came from (for logs and error messages).</summary>
public sealed record LanguageServerCandidate(string DllPath, string Source, Version? Version = null);

/// <summary>
/// Finds an existing Roslyn language server (Microsoft.CodeAnalysis.LanguageServer.dll) before falling back to a
/// download. Order: --LanguageServerPath, this server's own download cache, editor extensions that ship one
/// (VS Code C# and its forks), then the roslyn-language-server dotnet tool store.
/// </summary>
public sealed partial class RoslynServerLocator(LanguageServerOptions options, ILogger<RoslynServerLocator> logger)
{
    public const string ServerDllName = "Microsoft.CodeAnalysis.LanguageServer.dll";
    public const string RuntimeConfigName = "Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json";

    /// <summary>Oldest server known to support solution/open and the name formats the tools parse.</summary>
    public static readonly Version MinimumVersion = new(5, 0);

    private static readonly string[] EditorFolders =
        [".vscode", ".vscode-insiders", ".vscode-server", ".vscode-server-insiders", ".vscode-oss", ".cursor", ".cursor-server", ".windsurf", ".windsurf-server"];

    internal PlatformInfo Platform { get; init; } = PlatformInfo.Current;

    /// <summary>Compatible candidates in priority order. An explicit --LanguageServerPath is returned as is (checked at start).</summary>
    public IReadOnlyList<LanguageServerCandidate> FindCompatible(DotnetInstall dotnet)
    {
        if (!string.IsNullOrWhiteSpace(options.LanguageServerPath))
        {
            // Used even if the check fails (it's what the user asked for); the warning explains a failed start
            var explicitPath = Path.GetFullPath(options.LanguageServerPath);
            var reason = CheckCompatibility(explicitPath, dotnet, out var explicitVersion);
            if (reason is not null)
                logger.LogWarning("--LanguageServerPath {Path} may not work: {Reason}", explicitPath, reason);
            return [new LanguageServerCandidate(explicitPath, "--LanguageServerPath", explicitVersion)];
        }

        var results = new List<LanguageServerCandidate>();
        foreach (var candidate in EnumerateCandidates(options, Platform))
        {
            var reason = CheckCompatibility(candidate.DllPath, dotnet, out var version);
            if (reason is null)
                results.Add(candidate with { Version = version });
            else
                logger.LogDebug("Language server candidate {Path} ({Source}) rejected: {Reason}", candidate.DllPath, candidate.Source, reason);
        }
        return results;
    }

    /// <summary>Candidate locations that exist on disk, in priority order (no compatibility check).</summary>
    public static IReadOnlyList<LanguageServerCandidate> EnumerateCandidates(LanguageServerOptions options, PlatformInfo platform)
    {
        var results = new List<LanguageServerCandidate>();

        // Our own cache: <InstallRoot>/<version>/<rid>/, newest version first; a pinned version restricts it
        if (Directory.Exists(options.InstallRoot))
        {
            var versions = Directory.EnumerateDirectories(options.InstallRoot)
                .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
                .Where(v => options.LanguageServerVersion is null ||
                            string.Equals(v.Name, options.LanguageServerVersion, StringComparison.OrdinalIgnoreCase))
                .Select(v => (v.Dir, Version: DotnetLocator.TryParseVersion(v.Name, out var parsed) ? parsed : null))
                .Where(v => v.Version is not null)
                .OrderByDescending(v => v.Version);
            foreach (var (dir, _) in versions)
            {
                var dll = Path.Combine(dir, platform.Rid, ServerDllName);
                if (File.Exists(dll))
                    results.Add(new LanguageServerCandidate(dll, "download cache"));
            }
        }

        // A pinned version means "use exactly this package"; don't substitute an editor's build
        if (options.LanguageServerVersion is not null)
            return results;

        // Editors: <home>/<editor>/extensions/ms-dotnettools.csharp-<version>[-<platform>]/.roslyn/
        var editorHits = new List<(LanguageServerCandidate Candidate, Version Version)>();
        foreach (var editor in EditorFolders)
        {
            var extensions = Path.Combine(platform.HomeDirectory, editor, "extensions");
            if (!Directory.Exists(extensions))
                continue;
            foreach (var dir in SafeEnumerateDirectories(extensions, "ms-dotnettools.csharp-*"))
            {
                var match = CSharpExtensionFolder().Match(Path.GetFileName(dir));
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
                    continue;
                var extensionPlatform = match.Groups["platform"].Value;
                if (extensionPlatform.Length > 0 && !string.Equals(extensionPlatform, platform.VsCodePlatform, StringComparison.OrdinalIgnoreCase))
                    continue;
                var dll = Path.Combine(dir, ".roslyn", ServerDllName);
                if (File.Exists(dll))
                    editorHits.Add((new LanguageServerCandidate(dll, $"{editor} C# extension {version}"), version));
            }
        }
        results.AddRange(editorHits.OrderByDescending(h => h.Version).Select(h => h.Candidate));

        // dotnet tool store: <home>/.dotnet/tools/.store/roslyn-language-server*/<ver>/.../tools/<tfm>/<rid|any>/
        var store = Path.Combine(platform.HomeDirectory, ".dotnet", "tools", ".store");
        if (Directory.Exists(store))
        {
            foreach (var package in SafeEnumerateDirectories(store, "roslyn-language-server*"))
            {
                IEnumerable<string> dlls;
                try
                {
                    dlls = Directory.EnumerateFiles(package, ServerDllName, SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var dll in dlls.OrderByDescending(d => d, StringComparer.Ordinal))
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(dll));
                    if (string.Equals(parent, platform.Rid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(parent, "any", StringComparison.OrdinalIgnoreCase))
                        results.Add(new LanguageServerCandidate(dll, "dotnet tool " + Path.GetFileName(package)));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Cheap, process-free check that the given server can run on the given dotnet. Returns null when compatible,
    /// otherwise the reason it isn't.
    /// </summary>
    public static string? CheckCompatibility(string dllPath, DotnetInstall dotnet, out Version? version)
    {
        version = null;
        if (!File.Exists(dllPath))
            return "file not found";

        var runtimeConfig = Path.Combine(Path.GetDirectoryName(dllPath)!, RuntimeConfigName);
        if (!File.Exists(runtimeConfig))
            return $"{RuntimeConfigName} not found next to it";

        IReadOnlyList<(string Name, Version Version)> frameworks;
        string rollForward;
        try
        {
            (frameworks, rollForward) = ParseRuntimeConfig(File.ReadAllText(runtimeConfig));
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return $"unreadable runtimeconfig: {ex.Message}";
        }

        foreach (var (name, required) in frameworks)
        {
            var available = dotnet.Runtimes.TryGetValue(name, out var list) ? list : [];
            if (!available.Any(v => IsRuntimeSatisfied(required, v, rollForward)))
                return $"needs {name} {required} (rollForward {rollForward}), which {dotnet.DotnetPath} doesn't have";
        }

        try
        {
            version = AssemblyName.GetAssemblyName(dllPath).Version;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or ArgumentException)
        {
            return $"not a loadable assembly: {ex.Message}";
        }
        if (version is null || version < MinimumVersion)
            return $"version {version} is older than the minimum supported {MinimumVersion}";

        return null;
    }

    /// <summary>Framework references and roll-forward policy from a runtimeconfig.json.</summary>
    public static (IReadOnlyList<(string Name, Version Version)> Frameworks, string RollForward) ParseRuntimeConfig(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var runtimeOptions = doc.RootElement.GetProperty("runtimeOptions");
        var frameworks = new List<(string, Version)>();

        void Add(JsonElement framework)
        {
            var name = framework.GetProperty("name").GetString() ?? throw new FormatException("framework name missing");
            var text = framework.GetProperty("version").GetString() ?? throw new FormatException("framework version missing");
            if (!DotnetLocator.TryParseVersion(text, out var parsed))
                throw new FormatException($"bad framework version '{text}'");
            frameworks.Add((name, parsed));
        }

        if (runtimeOptions.TryGetProperty("framework", out var single))
            Add(single);
        if (runtimeOptions.TryGetProperty("frameworks", out var many))
            foreach (var framework in many.EnumerateArray())
                Add(framework);
        if (frameworks.Count == 0)
            throw new FormatException("no framework reference (self-contained servers are not supported)");

        var rollForward = runtimeOptions.TryGetProperty("rollForward", out var rf) ? rf.GetString() ?? "Minor" : "Minor";
        return (frameworks, rollForward);
    }

    /// <summary>Whether an installed runtime satisfies a framework reference under the host's roll-forward policy.</summary>
    public static bool IsRuntimeSatisfied(Version required, Version available, string rollForward)
    {
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
        var req = Norm(required);
        var have = Norm(available);
        return rollForward.ToLowerInvariant() switch
        {
            "disable" => have == req,
            "latestpatch" => have.Major == req.Major && have.Minor == req.Minor && have >= req,
            "major" or "latestmajor" => have >= req,
            _ => have.Major == req.Major && have >= req, // Minor / LatestMinor (the default)
        };
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(path, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^ms-dotnettools\.csharp-(?<version>\d+\.\d+\.\d+)(?:-(?<platform>[a-z0-9]+-[a-z0-9]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CSharpExtensionFolder();
}

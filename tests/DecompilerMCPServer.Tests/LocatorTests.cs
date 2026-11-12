using System.Reflection;
using System.Runtime.InteropServices;
using DecompilerMCPServer.LanguageServer;

namespace DecompilerMCPServer.Tests;

public sealed class LocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmcp-tests-" + Guid.NewGuid().ToString("N"));

    public LocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string RuntimesOutput = """
        Microsoft.AspNetCore.App 8.0.29 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 8.0.29 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.NETCore.App 10.0.11 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.NETCore.App 11.0.0-preview.1.25500.1 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        garbage line
        """;

    [Fact]
    public void ParseRuntimes_groups_by_framework_and_ignores_prerelease_suffixes()
    {
        var runtimes = DotnetLocator.ParseRuntimes(RuntimesOutput.Replace("\n", "\r\n"));

        Assert.Equal([new Version(8, 0, 29), new Version(10, 0, 11), new Version(11, 0, 0)], runtimes[DotnetInstall.NetCoreApp]);
        Assert.Single(runtimes["Microsoft.AspNetCore.App"]);
    }

    [Fact]
    public void ParseSdks() =>
        Assert.Equal([new Version(9, 0, 316), new Version(10, 0, 400)],
            DotnetLocator.ParseSdks("9.0.316 [C:\\Program Files\\dotnet\\sdk]\n10.0.400 [C:\\Program Files\\dotnet\\sdk]\n"));

    [Fact]
    public void DotnetInstall_requirements()
    {
        var install = new DotnetInstall("dotnet", DotnetLocator.ParseRuntimes(RuntimesOutput), [new Version(9, 0, 100)]);

        Assert.True(install.HasRuntime(10));
        Assert.False(install.HasSdk(10));
    }

    [Fact]
    public void Dotnet_candidates_follow_priority_and_deduplicate()
    {
        var env = new Dictionary<string, string?>
        {
            ["DOTNET_ROOT_X64"] = "/opt/dotnet-x64",
            ["DOTNET_ROOT"] = "/opt/dotnet",
            ["PATH"] = "/usr/bin:/opt/dotnet:/missing",
        };
        var platform = new PlatformInfo(OSPlatform.Linux, Architecture.X64, "/home/me", k => env.GetValueOrDefault(k));
        var existing = new HashSet<string> { "/opt/dotnet-x64/dotnet", "/opt/dotnet/dotnet", "/usr/bin/dotnet", "/usr/share/dotnet/dotnet", "/home/me/.dotnet/dotnet" }
            .Select(Path.GetFullPath).ToHashSet();

        var candidates = DotnetLocator.EnumerateCandidates(platform, existing.Contains);

        Assert.Equal(new[] { "/opt/dotnet-x64/dotnet", "/opt/dotnet/dotnet", "/usr/bin/dotnet", "/usr/share/dotnet/dotnet", "/home/me/.dotnet/dotnet" }
            .Select(Path.GetFullPath), candidates);
    }

    [Theory]
    [InlineData("10.0.0", "10.0.11", "Minor", true)]
    [InlineData("10.0.0", "10.1.0", "Minor", true)]
    [InlineData("10.0.0", "11.0.0", "Minor", false)]
    [InlineData("10.0.0", "11.0.0", "Major", true)]
    [InlineData("10.0.0", "11.0.0", "LatestMajor", true)]
    [InlineData("10.0.5", "10.0.3", "Major", false)]
    [InlineData("10.0.0", "10.1.0", "LatestPatch", false)]
    [InlineData("10.0.0", "10.0.7", "LatestPatch", true)]
    [InlineData("10.0.0", "10.0.1", "Disable", false)]
    [InlineData("10.0.0", "10.0.0", "Disable", true)]
    [InlineData("9.0.0", "10.0.11", "Minor", false)]
    public void IsRuntimeSatisfied(string required, string available, string rollForward, bool expected) =>
        Assert.Equal(expected, RoslynServerLocator.IsRuntimeSatisfied(Version.Parse(required), Version.Parse(available), rollForward));

    [Fact]
    public void ParseRuntimeConfig_single_and_multiple_frameworks()
    {
        var (single, rollForward) = RoslynServerLocator.ParseRuntimeConfig("""
            { "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, "rollForward": "Major" } }
            """);
        Assert.Equal([("Microsoft.NETCore.App", new Version(10, 0, 0))], single);
        Assert.Equal("Major", rollForward);

        var (many, defaultRollForward) = RoslynServerLocator.ParseRuntimeConfig("""
            { "runtimeOptions": { "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" } ] } }
            """);
        Assert.Equal(2, many.Count);
        Assert.Equal("Minor", defaultRollForward);
    }

    [Fact]
    public void ParseRuntimeConfig_rejects_self_contained() =>
        Assert.Throws<FormatException>(() => RoslynServerLocator.ParseRuntimeConfig("""{ "runtimeOptions": { "tfm": "net10.0" } }"""));

    /// <summary>A fake server folder: any real managed DLL with version >= 5 stands in for the server assembly.</summary>
    private string FakeServer(string dir, string frameworkVersion = "10.0.0", string rollForward = "Major")
    {
        Directory.CreateDirectory(dir);
        var standIn = typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location; // 10.x
        var dll = Path.Combine(dir, RoslynServerLocator.ServerDllName);
        File.Copy(standIn, dll, overwrite: true);
        File.WriteAllText(Path.Combine(dir, RoslynServerLocator.RuntimeConfigName),
            $$"""{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "{{frameworkVersion}}" }, "rollForward": "{{rollForward}}" } }""");
        return dll;
    }

    private static DotnetInstall Dotnet(params string[] runtimes) =>
        new("dotnet", new Dictionary<string, IReadOnlyList<Version>> { [DotnetInstall.NetCoreApp] = runtimes.Select(Version.Parse).ToList() },
            [new Version(10, 0, 100)]);

    [Fact]
    public void CheckCompatibility_accepts_a_matching_server()
    {
        var dll = FakeServer(Path.Combine(_root, "ok"));

        Assert.Null(RoslynServerLocator.CheckCompatibility(dll, Dotnet("10.0.11"), out var version));
        Assert.True(version >= RoslynServerLocator.MinimumVersion);
    }

    [Fact]
    public void CheckCompatibility_reports_missing_runtime()
    {
        var dll = FakeServer(Path.Combine(_root, "needs11"), "11.0.0", "Minor");

        var reason = RoslynServerLocator.CheckCompatibility(dll, Dotnet("10.0.11"), out _);

        Assert.Contains("Microsoft.NETCore.App 11.0.0", reason);
    }

    [Fact]
    public void CheckCompatibility_reports_missing_files()
    {
        Assert.Equal("file not found", RoslynServerLocator.CheckCompatibility(Path.Combine(_root, "none.dll"), Dotnet("10.0.0"), out _));

        var dir = Path.Combine(_root, "noconfig");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, RoslynServerLocator.ServerDllName);
        File.WriteAllText(dll, "x");
        Assert.Contains("runtimeconfig", RoslynServerLocator.CheckCompatibility(dll, Dotnet("10.0.0"), out _));
    }

    [Fact]
    public void CheckCompatibility_rejects_non_assemblies()
    {
        var dll = FakeServer(Path.Combine(_root, "bad"));
        File.WriteAllText(dll, "not a PE file");

        Assert.Contains("not a loadable assembly", RoslynServerLocator.CheckCompatibility(dll, Dotnet("10.0.11"), out _));
    }

    [Fact]
    public void Server_candidates_cache_then_editors_by_version_then_tool_store()
    {
        var home = Path.Combine(_root, "home");
        var installRoot = Path.Combine(_root, "roslyn-ls");
        var platform = new PlatformInfo(OSPlatform.Windows, Architecture.X64, home, _ => null);

        FakeServer(Path.Combine(installRoot, "5.10.0", "win-x64"));
        FakeServer(Path.Combine(installRoot, "5.11.0", "win-x64"));
        FakeServer(Path.Combine(installRoot, "5.12.0", "linux-x64"));                     // other RID: ignored
        FakeServer(Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csharp-2.90.1-win32-x64", ".roslyn"));
        FakeServer(Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csharp-2.160.4-win32-x64", ".roslyn"));
        FakeServer(Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csharp-2.170.0-darwin-arm64", ".roslyn")); // other platform
        FakeServer(Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csdevkit-1.0.0-win32-x64", ".roslyn"));  // not the C# extension
        FakeServer(Path.Combine(home, ".cursor", "extensions", "ms-dotnettools.csharp-2.100.0", ".roslyn"));            // universal build
        FakeServer(Path.Combine(home, ".dotnet", "tools", ".store", "roslyn-language-server", "5.11.0",
            "roslyn-language-server", "5.11.0", "tools", "net10.0", "any"));

        var options = new LanguageServerOptions(null, null, null, installRoot, TimeSpan.FromSeconds(1));
        var candidates = RoslynServerLocator.EnumerateCandidates(options, platform);

        Assert.Equal(
        [
            "download cache",
            "download cache",
            ".vscode C# extension 2.160.4",
            ".cursor C# extension 2.100.0",
            ".vscode C# extension 2.90.1",
            "dotnet tool roslyn-language-server",
        ], candidates.Select(c => c.Source));
        Assert.Contains(Path.Combine("5.11.0", "win-x64"), candidates[0].DllPath);
    }

    [Fact]
    public void Server_candidates_with_pinned_version_only_use_the_cache()
    {
        var home = Path.Combine(_root, "home");
        var installRoot = Path.Combine(_root, "roslyn-ls");
        var platform = new PlatformInfo(OSPlatform.Windows, Architecture.X64, home, _ => null);
        FakeServer(Path.Combine(installRoot, "5.10.0", "win-x64"));
        FakeServer(Path.Combine(installRoot, "5.11.0", "win-x64"));
        FakeServer(Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csharp-2.160.4-win32-x64", ".roslyn"));

        var options = new LanguageServerOptions(null, "5.10.0", null, installRoot, TimeSpan.FromSeconds(1));
        var candidate = Assert.Single(RoslynServerLocator.EnumerateCandidates(options, platform));

        Assert.Contains(Path.Combine("5.10.0", "win-x64"), candidate.DllPath);
    }

    [Theory]
    [InlineData(PlatformKind.Windows, Architecture.X64, "win-x64", "win32-x64")]
    [InlineData(PlatformKind.Windows, Architecture.Arm64, "win-arm64", "win32-arm64")]
    [InlineData(PlatformKind.OSX, Architecture.Arm64, "osx-arm64", "darwin-arm64")]
    [InlineData(PlatformKind.Linux, Architecture.X64, "linux-x64", "linux-x64")]
    public void Platform_identifiers(PlatformKind kind, Architecture arch, string rid, string vsCode)
    {
        var os = kind switch { PlatformKind.Windows => OSPlatform.Windows, PlatformKind.OSX => OSPlatform.OSX, _ => OSPlatform.Linux };
        var platform = new PlatformInfo(os, arch, "/home", _ => null);

        Assert.Equal(rid, platform.Rid);
        Assert.Equal(vsCode, platform.VsCodePlatform);
    }

    public enum PlatformKind { Windows, OSX, Linux }
}

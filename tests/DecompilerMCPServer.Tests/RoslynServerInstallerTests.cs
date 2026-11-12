using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DecompilerMCPServer.LanguageServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace DecompilerMCPServer.Tests;

public sealed class RoslynServerInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmcp-tests-" + Guid.NewGuid().ToString("N"));

    public RoslynServerInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string FakePackage(string rid, params (string Name, string Content)[] extra)
    {
        var path = Path.Combine(_root, $"fake-{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        Add("roslyn-language-server.nuspec", "<package />");
        Add($"tools/net10.0/{rid}/{RoslynServerLocator.ServerDllName}", "server");
        Add($"tools/net10.0/{rid}/{RoslynServerLocator.RuntimeConfigName}", "{}");
        Add($"tools/net10.0/{rid}/BuildHost-netcore/Host.dll", "host");
        Add("tools/net10.0/other-rid/Other.dll", "other");
        foreach (var (name, content) in extra)
            Add(name, content);
        return path;
    }

    private static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    [Fact]
    public void InstallFromPackage_extracts_only_the_rid_folder()
    {
        var package = FakePackage("win-x64");
        var target = Path.Combine(_root, "install", "win-x64");

        RoslynServerInstaller.InstallFromPackage(package, "win-x64", target, Sha(package));

        Assert.True(File.Exists(Path.Combine(target, RoslynServerLocator.ServerDllName)));
        Assert.True(File.Exists(Path.Combine(target, "BuildHost-netcore", "Host.dll")));
        Assert.False(File.Exists(Path.Combine(target, "Other.dll")));
        Assert.False(File.Exists(Path.Combine(target, "roslyn-language-server.nuspec")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, "*.tmp-*"));
    }

    [Fact]
    public void InstallFromPackage_rejects_checksum_mismatch()
    {
        var package = FakePackage("win-x64");
        var target = Path.Combine(_root, "install", "win-x64");

        Assert.Throws<InvalidDataException>(() =>
            RoslynServerInstaller.InstallFromPackage(package, "win-x64", target, new string('0', 64)));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void InstallFromPackage_rejects_missing_rid()
    {
        var package = FakePackage("win-x64");

        Assert.Throws<InvalidDataException>(() =>
            RoslynServerInstaller.InstallFromPackage(package, "linux-arm64", Path.Combine(_root, "x"), null));
    }

    [Fact]
    public void ExtractServerFiles_rejects_zip_slip()
    {
        var package = FakePackage("win-x64", ("tools/net10.0/win-x64/../../../../evil.txt", "boom"));
        var target = Path.Combine(_root, "slip");
        Directory.CreateDirectory(target);

        using var archive = ZipFile.OpenRead(package);
        Assert.Throws<InvalidDataException>(() => RoslynServerInstaller.ExtractServerFiles(archive, "win-x64", target));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3-flatcontainer/x/1.0/x.1.0.nupkg", true)]
    [InlineData("https://globalcdn.nuget.org/packages/x.1.0.nupkg", true)]
    [InlineData("https://www.nuget.org/api/v2/package/x/1.0", true)]
    [InlineData("http://api.nuget.org/x.nupkg", false)]
    [InlineData("https://evil.example.com/x.nupkg", false)]
    [InlineData("https://api.nuget.org.evil.example.com/x.nupkg", false)]
    public void IsAllowedHost(string url, bool expected) => Assert.Equal(expected, RoslynServerInstaller.IsAllowedHost(new Uri(url)));

    [Fact]
    public void Pinned_checksums_cover_every_published_rid()
    {
        Assert.Equal(["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"],
            RoslynServerInstaller.DefaultSha256.Keys.Order());
        Assert.All(RoslynServerInstaller.DefaultSha256.Values, sha => Assert.Matches("^[0-9a-f]{64}$", sha));
    }

    [Fact]
    public async Task EnsureInstalledAsync_downloads_once_then_uses_the_cache()
    {
        var platform = new PlatformInfo(OSPlatform.Linux, Architecture.X64, _root, _ => null);
        var package = FakePackage(platform.Rid);
        var options = new LanguageServerOptions(null, "9.9.9", null, Path.Combine(_root, "roslyn-ls"), TimeSpan.FromSeconds(1));
        var downloads = new List<Uri>();
        var installer = new RoslynServerInstaller(options, NullLogger<RoslynServerInstaller>.Instance)
        {
            Platform = platform,
            OpenDownload = (uri, _) =>
            {
                downloads.Add(uri);
                return Task.FromResult<Stream>(File.OpenRead(package));
            },
        };

        var first = await installer.EnsureInstalledAsync(CancellationToken.None);
        var second = await installer.EnsureInstalledAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "roslyn-ls", "9.9.9", "linux-x64", RoslynServerLocator.ServerDllName), first);
        Assert.Equal(first, second);
        var uri = Assert.Single(downloads);
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/roslyn-language-server.linux-x64/9.9.9/roslyn-language-server.linux-x64.9.9.9.nupkg",
            uri.ToString());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "roslyn-ls", "9.9.9"), "*.nupkg.tmp-*"));
    }

    [Fact]
    public async Task EnsureInstalledAsync_pinned_version_verifies_checksum()
    {
        var platform = new PlatformInfo(OSPlatform.Linux, Architecture.X64, _root, _ => null);
        var package = FakePackage(platform.Rid);
        var options = new LanguageServerOptions(null, null, null, Path.Combine(_root, "roslyn-ls"), TimeSpan.FromSeconds(1));
        var installer = new RoslynServerInstaller(options, NullLogger<RoslynServerInstaller>.Instance)
        {
            Platform = platform,
            OpenDownload = (_, _) => Task.FromResult<Stream>(File.OpenRead(package)),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.EnsureInstalledAsync(CancellationToken.None));
    }
}

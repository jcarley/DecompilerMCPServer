using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>
/// Downloads and caches the Roslyn language server NuGet package (roslyn-language-server.&lt;rid&gt;) when no compatible
/// server was discovered. The default version is pinned with a SHA256 per RID; bump <see cref="DefaultVersion"/> and
/// <see cref="DefaultSha256"/> together.
/// </summary>
public sealed class RoslynServerInstaller(LanguageServerOptions options, ILogger<RoslynServerInstaller> logger)
{
    public const string DefaultVersion = "5.11.0";

    /// <summary>SHA256 of roslyn-language-server.&lt;rid&gt;.<see cref="DefaultVersion"/>.nupkg from nuget.org.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultSha256 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["win-x64"] = "c06d68dda27dfb5ec314b49e59c3daf5140ee6b7f168199a86e99238e5d899d4",
        ["win-arm64"] = "9577899ea57bd4ef8411a6347ad3dc09d31889ad295671bcd57fd19de1e1a26a",
        ["osx-x64"] = "9d0d4c4c6e91447aee19060d505c30a1fb8e9052237af299505bdd03beba5bbd",
        ["osx-arm64"] = "db3de4312b1ccaa3a1645e5821b18af1748c548ba549cfd078414c3d20e498a5",
        ["linux-x64"] = "9680fd63baf9512bf8ff937260fda3e8c402903310baa11bb5f24d6afdf6eac6",
        ["linux-arm64"] = "8a7d1c94e9a25091e6049e690ae93bb8fb5d1211e7c9eb21b34c16400ca8be8b",
    };

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "api.nuget.org", "www.nuget.org", "nuget.org", "globalcdn.nuget.org" };

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(15);
    private const int MaxRedirects = 5;

    internal PlatformInfo Platform { get; init; } = PlatformInfo.Current;

    /// <summary>Overridable for tests; production downloads from nuget.org's flat container.</summary>
    internal Func<Uri, CancellationToken, Task<Stream>>? OpenDownload { get; init; }

    public string Version => options.LanguageServerVersion ?? DefaultVersion;

    public string InstallDirectory(string version, string rid) => Path.Combine(options.InstallRoot, version, rid);

    /// <summary>Returns the server DLL, downloading and extracting the package first if it isn't cached.</summary>
    public async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        var version = Version;
        var rid = Platform.Rid;
        var targetDir = InstallDirectory(version, rid);
        var dll = Path.Combine(targetDir, RoslynServerLocator.ServerDllName);
        if (File.Exists(dll))
            return dll;

        string? expectedSha;
        if (string.Equals(version, DefaultVersion, StringComparison.OrdinalIgnoreCase))
        {
            if (!DefaultSha256.TryGetValue(rid, out expectedSha))
                throw new PlatformNotSupportedException($"No Roslyn language server package is published for '{rid}'. " +
                                                        "Pass --LanguageServerPath to use one you installed yourself.");
        }
        else
        {
            expectedSha = null;
            logger.LogWarning("LanguageServerVersion {Version} is not the pinned {Pinned}; the download is not checksum-verified",
                version, DefaultVersion);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        using var _ = FileLock.Acquire(targetDir + ".lock", LockTimeout, cancellationToken);
        if (File.Exists(dll))
            return dll; // another process installed it while we waited

        var packageId = $"roslyn-language-server.{rid}".ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var url = new Uri($"https://api.nuget.org/v3-flatcontainer/{packageId}/{lowerVersion}/{packageId}.{lowerVersion}.nupkg");
        var downloadPath = $"{targetDir}.nupkg.tmp-{Environment.ProcessId}";
        try
        {
            logger.LogInformation("Downloading Roslyn language server {Package} {Version} from {Url}", packageId, version, url);
            await DownloadAsync(url, downloadPath, cancellationToken);
            InstallFromPackage(downloadPath, rid, targetDir, expectedSha);
            logger.LogInformation("Installed Roslyn language server to {Directory}", targetDir);
        }
        finally
        {
            TryDelete(downloadPath);
        }

        return File.Exists(dll)
            ? dll
            : throw new InvalidOperationException($"{RoslynServerLocator.ServerDllName} missing after installing to '{targetDir}'.");
    }

    /// <summary>
    /// Verifies (when <paramref name="expectedSha256"/> is given) and extracts <c>tools/&lt;tfm&gt;/&lt;rid&gt;/**</c> from a
    /// downloaded package into <paramref name="targetDir"/>, via a temp folder that is moved into place.
    /// </summary>
    public static void InstallFromPackage(string packagePath, string rid, string targetDir, string? expectedSha256)
    {
        if (expectedSha256 is not null)
        {
            string actual;
            using (var stream = File.OpenRead(packagePath))
                actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch for '{Path.GetFileName(packagePath)}': expected {expectedSha256}, got {actual}.");
        }

        var tempDir = $"{targetDir}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
        Directory.CreateDirectory(tempDir);
        try
        {
            using (var archive = ZipFile.OpenRead(packagePath))
                ExtractServerFiles(archive, rid, tempDir);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            MoveDirectoryWithRetry(tempDir, targetDir);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Antivirus scanners briefly hold freshly written DLLs open on Windows, which makes moving their folder fail with
    /// "access denied"; retry for a while before giving up.
    /// </summary>
    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(Math.Min(250 * attempt, 2000));
            }
        }
    }

    /// <summary>Extracts the folder holding the server DLL for the RID, refusing entries that escape the target (zip-slip).</summary>
    public static int ExtractServerFiles(ZipArchive archive, string rid, string targetDir)
    {
        var serverEntry = archive.Entries.FirstOrDefault(e =>
        {
            var parts = e.FullName.Split('/');
            return parts.Length == 4 &&
                   parts[0].Equals("tools", StringComparison.OrdinalIgnoreCase) &&
                   parts[2].Equals(rid, StringComparison.OrdinalIgnoreCase) &&
                   parts[3].Equals(RoslynServerLocator.ServerDllName, StringComparison.OrdinalIgnoreCase);
        }) ?? throw new InvalidDataException($"The package has no tools/<tfm>/{rid}/{RoslynServerLocator.ServerDllName}.");

        var prefix = serverEntry.FullName[..(serverEntry.FullName.LastIndexOf('/') + 1)];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir)) + Path.DirectorySeparatorChar;
        int count = 0;
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith('/'))
                continue;
            var relative = entry.FullName[prefix.Length..];
            var destination = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to extract '{entry.FullName}': it resolves outside '{targetDir}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }
        return count;
    }

    public static bool IsAllowedHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host);

    private async Task DownloadAsync(Uri url, string destination, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        await using var source = OpenDownload is not null
            ? await OpenDownload(url, timeout.Token)
            : await OpenNuGetDownloadAsync(url, timeout.Token);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, timeout.Token);
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>GET with redirects followed by hand, so every hop is checked against the nuget.org host allowlist.</summary>
    private static async Task<Stream> OpenNuGetDownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (int hop = 0; ; hop++)
        {
            if (!IsAllowedHost(current))
                throw new InvalidOperationException($"Refusing to download from '{current}': only https nuget.org hosts are allowed.");

            var response = await Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects)
                    throw new InvalidOperationException($"Too many redirects downloading '{url}'.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}

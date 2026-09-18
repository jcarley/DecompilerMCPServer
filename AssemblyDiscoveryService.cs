namespace DecompilerMCPServer;

public sealed record ScanOptions(string ScanDirectory);

/// <summary>Root under which decompiled projects are written, mirroring the scan directory's layout.</summary>
public sealed record ExportOptions(string OutputDirectory);

public sealed class AssemblyDiscoveryService(ScanOptions options)
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public string Root => options.ScanDirectory;

    /// <summary>Full path of a scan-relative path as returned by FindAssemblies.</summary>
    public string FullPath(string relativePath) => Path.Combine(Root, relativePath);

    /// <summary>Relative paths of all *.dll files under the scan directory, sorted. Fresh scan per call.</summary>
    public IReadOnlyList<string> FindAssemblies() =>
        Directory.EnumerateFiles(Root, "*.dll", Enumeration)
            .Select(p => Path.GetRelativePath(Root, p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Resolves an assembly identifier (relative path from FindAssemblies, or a simple name
    /// like "MyLib" / "MyLib.dll") to its relative and full paths. Never picks silently among duplicates.
    /// </summary>
    public bool TryResolve(string identifier, out string relativePath, out string fullPath, out string error)
    {
        relativePath = string.Empty;
        fullPath = string.Empty;
        error = string.Empty;

        var assemblies = FindAssemblies();

        var candidates = assemblies
            .Where(rel => string.Equals(rel, identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            var simpleName = identifier.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? identifier
                : identifier + ".dll";
            candidates = assemblies
                .Where(rel => string.Equals(Path.GetFileName(rel), simpleName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        switch (candidates.Count)
        {
            case 0:
                error = $"Assembly '{identifier}' not found under '{Root}'. Use list_assemblies to see what is available.";
                return false;
            case > 1:
                error = $"Ambiguous assembly '{identifier}'. Candidates:\n  " +
                        string.Join("\n  ", candidates) +
                        "\nUse the relative path to disambiguate.";
                return false;
        }

        // candidates come from FindAssemblies (relative paths enumerated under Root),
        // so the combined path cannot escape the scan directory
        relativePath = candidates[0];
        fullPath = Path.GetFullPath(FullPath(relativePath));
        return true;
    }
}

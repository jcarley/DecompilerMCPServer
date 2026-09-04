namespace DecompilerMCPServer;

public sealed record ScanOptions(string ScanDirectory);

public sealed class AssemblyDiscoveryService(ScanOptions options)
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public string Root => options.ScanDirectory;

    /// <summary>Relative paths of all *.dll files under the scan directory, sorted. Fresh scan per call.</summary>
    public IReadOnlyList<string> FindAssemblies() =>
        Directory.EnumerateFiles(Root, "*.dll", Enumeration)
            .Select(p => Path.GetRelativePath(Root, p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Resolves an assembly identifier (relative path from FindAssemblies, or a simple name
    /// like "MyLib" / "MyLib.dll") to a full path. Never picks silently among duplicates.
    /// </summary>
    public bool TryResolve(string identifier, out string fullPath, out string error)
    {
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
        fullPath = Path.GetFullPath(Path.Combine(Root, candidates[0]));
        return true;
    }
}

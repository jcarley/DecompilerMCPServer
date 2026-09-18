using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DecompilerMCPServer;

[McpServerToolType]
public sealed class DecompilerTools(
    AssemblyDiscoveryService discovery,
    ProjectExportCache cache,
    DecompilerService decompiler)
{
    private const int MaxNamespacesListed = 50;

    [McpServerTool(Name = "list_assemblies", ReadOnly = true, Idempotent = true, Destructive = false),
     Description("Lists all .NET assemblies (*.dll) found recursively under the configured scan directory. " +
                 "The returned relative paths are the identifiers accepted by the other tools.")]
    public string ListAssemblies()
    {
        try
        {
            var assemblies = discovery.FindAssemblies();
            if (assemblies.Count == 0)
                return $"No assemblies found under '{discovery.Root}'.";

            return $"Found {assemblies.Count} assemblies under '{discovery.Root}':\n" +
                   string.Join("\n", assemblies) +
                   $"\n\nSources are exported under '{cache.OutputDirectory}'.";
        }
        catch (Exception ex)
        {
            return $"Error: could not scan '{discovery.Root}': {ex.Message}";
        }
    }

    [McpServerTool(Name = "decompile_assembly", Idempotent = true, Destructive = false),
     Description("Decompiles an entire assembly to C# on disk as an ILSpy project (one .cs file per type, grouped in " +
                 "namespace folders, plus a .csproj) and returns the folder path. Nothing is written to the response " +
                 "except the summary; read the files from the folder. The export is reused while the DLL is unchanged.")]
    public string DecompileAssembly(
        [Description("Assembly identifier: a relative path from list_assemblies, or a simple name like 'MyLib' or 'MyLib.dll'.")]
        string assembly,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOrError(assembly, out var fullPath, out var relativePath, out var error))
            return error;

        try
        {
            var export = cache.GetExport(fullPath, relativePath, cancellationToken);
            return DescribeExport(export) +
                   "\nRead files from that folder directly; use decompile_type to find a type's file or search_code to search.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: could not decompile '{assembly}': {ex.Message}";
        }
    }

    [McpServerTool(Name = "decompile_type", Idempotent = true, Destructive = false),
     Description("Locates the C# file for a single type, exporting its assembly to disk first if needed, and returns " +
                 "the file path. Use the full type name, e.g. 'MyNs.MyClass'. Nested types use '+' (MyNs.Outer+Inner) " +
                 "and are found in their outer type's file; generic types include arity (MyNs.Widget`1).")]
    public string DecompileType(
        [Description("Full type name, e.g. 'MyNamespace.MyClass'")] string typeName,
        [Description("Optional assembly identifier to look in. If omitted, all scanned assemblies are searched.")]
        string? assembly = null,
        CancellationToken cancellationToken = default)
    {
        if (assembly is not null)
        {
            if (!TryResolveOrError(assembly, out var fullPath, out var relativePath, out var error))
                return error;
            return LocateTypeFile(fullPath, relativePath, typeName, cancellationToken);
        }

        var hits = new List<string>();
        foreach (var rel in discovery.FindAssemblies())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (decompiler.ContainsType(discovery.FullPath(rel), typeName))
                    hits.Add(rel);
            }
            catch
            {
                // Unreadable/corrupt DLL — skip it for the probe
            }
        }

        return hits.Count switch
        {
            0 => $"Error: type '{typeName}' not found in any scanned assembly. " +
                 "Use the full namespace-qualified name; search_code can help locate it.",
            1 => LocateTypeFile(discovery.FullPath(hits[0]), hits[0], typeName, cancellationToken),
            _ => $"Error: type '{typeName}' found in multiple assemblies:\n  " +
                 string.Join("\n  ", hits) +
                 "\nSpecify the 'assembly' parameter.",
        };
    }

    private string LocateTypeFile(string fullPath, string relativePath, string typeName, CancellationToken cancellationToken)
    {
        try
        {
            var export = cache.GetExport(fullPath, relativePath, cancellationToken);
            var file = Path.Combine(export.ProjectDirectory, DecompilerService.GetTypeFileRelativePath(typeName));

            if (!File.Exists(file))
            {
                // ILSpy names resource-backed types after their companion file (MainWindow.xaml.cs)
                var dir = Path.GetDirectoryName(file)!;
                var companions = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(file) + ".*.cs")
                    : [];
                if (companions.Length != 1)
                    return $"Error: type '{typeName}' exists in '{relativePath}' but the export did not emit a file for it " +
                           $"(compiler-generated and hidden types are skipped); browse '{export.ProjectDirectory}' directly.";
                file = companions[0];
            }

            int lines = File.ReadLines(file).Count();
            return $"Type '{typeName}' from '{relativePath}' is in:\n  {file}\n" +
                   $"({lines:N0} lines; this file may hold several same-named types and all nested types)";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: could not decompile type '{typeName}' in '{relativePath}': {ex.Message}";
        }
    }

    [McpServerTool(Name = "search_code", Idempotent = true, Destructive = false),
     Description("Regex search across the decompiled C# source of all scanned assemblies, exporting any assembly " +
                 "not yet on disk first. Returns matching lines with surrounding context, grouped by source file; " +
                 "line numbers refer to the listed file.")]
    public string SearchCode(
        [Description(".NET regular expression to search for")] string pattern,
        [Description("Case-insensitive matching (default true)")] bool ignoreCase = true,
        [Description("Context lines before/after each match (default 2, max 10)")] int contextLines = 2,
        [Description("Maximum total matching lines to return (default 100, max 500)")] int maxResults = 100,
        [Description("Optional assembly identifier to restrict the search to one assembly")] string? assembly = null,
        CancellationToken cancellationToken = default)
    {
        contextLines = Math.Clamp(contextLines, 0, 10);
        maxResults = Math.Clamp(maxResults, 1, 500);

        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return $"Error: invalid regex: {ex.Message}";
        }

        IReadOnlyList<string> targets;
        if (assembly is not null)
        {
            if (!TryResolveOrError(assembly, out _, out var relativePath, out var error))
                return error;
            targets = [relativePath];
        }
        else
        {
            targets = discovery.FindAssemblies();
        }

        if (targets.Count == 0)
            return $"No assemblies found under '{discovery.Root}'.";

        var sb = new StringBuilder();
        int totalMatches = 0, filesWithMatches = 0, assembliesWithMatches = 0, searched = 0;
        bool truncated = false, timedOut = false;

        foreach (var rel in targets)
        {
            if (truncated || timedOut)
                break;
            cancellationToken.ThrowIfCancellationRequested();

            ProjectExport export;
            try
            {
                export = cache.GetExport(discovery.FullPath(rel), rel, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"== {rel} == (skipped: {ex.Message})");
                sb.AppendLine();
                continue;
            }
            searched++;

            bool assemblyCounted = false;
            var files = Directory.EnumerateFiles(export.ProjectDirectory, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal);
            try
            {
                foreach (var file in files)
                {
                    if (truncated)
                        break;
                    cancellationToken.ThrowIfCancellationRequested();

                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Another process may be swapping this project folder right now
                        sb.AppendLine($"== {file} == (skipped: {ex.Message})");
                        sb.AppendLine();
                        continue;
                    }
                    bool headerWritten = false;
                    for (int i = 0; i < lines.Length && !truncated; i++)
                    {
                        if (!regex.IsMatch(lines[i]))
                            continue;

                        if (!headerWritten)
                        {
                            sb.AppendLine($"== {file} ==");
                            headerWritten = true;
                            filesWithMatches++;
                            if (!assemblyCounted)
                            {
                                assemblyCounted = true;
                                assembliesWithMatches++;
                            }
                        }

                        int start = Math.Max(0, i - contextLines);
                        int end = Math.Min(lines.Length - 1, i + contextLines);
                        for (int j = start; j <= end; j++)
                            sb.AppendLine($"{(j == i ? ">" : " ")}{j + 1,5} | {lines[j]}");
                        sb.AppendLine();

                        if (++totalMatches >= maxResults)
                            truncated = true;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sb.AppendLine($"== {rel} == (skipped: {ex.Message})");
                sb.AppendLine();
            }
        }

        if (totalMatches == 0 && sb.Length == 0)
            return $"No matches for pattern '{pattern}' (searched {searched} of {targets.Count} assemblies).";

        if (timedOut)
            sb.AppendLine("Error: regex timed out (catastrophic backtracking?); results above are partial. Simplify the pattern.");
        if (truncated)
            sb.AppendLine($"[truncated: maxResults={maxResults} reached; refine the pattern]");

        sb.Append($"{totalMatches} match(es) in {filesWithMatches} file(s) across {assembliesWithMatches} of {searched} searched assemblies.");
        return sb.ToString();
    }

    [McpServerTool(Name = "decompile_all", Idempotent = true, Destructive = false),
     Description("Exports every scanned assembly to disk as an ILSpy project (skipping ones already exported and " +
                 "unchanged) and returns a summary with the output directory. Can take a long time on large folders; " +
                 "progress notifications are sent per assembly.")]
    public string DecompileAll(
        [Description("Bound by the server to forward per-assembly progress notifications; not a client argument.")]
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> assemblies;
        try
        {
            assemblies = discovery.FindAssemblies();
        }
        catch (Exception ex)
        {
            return $"Error: could not scan '{discovery.Root}': {ex.Message}";
        }

        if (assemblies.Count == 0)
            return $"No assemblies found under '{discovery.Root}'.";

        var stopwatch = Stopwatch.StartNew();
        int exported = 0, reused = 0, files = 0, errors = 0;
        var failures = new List<string>();

        for (int i = 0; i < assemblies.Count; i++)
        {
            var rel = assemblies[i];
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new ProgressNotificationValue { Progress = i, Total = assemblies.Count, Message = rel });

            try
            {
                var export = cache.GetExport(discovery.FullPath(rel), rel, cancellationToken);
                if (export.Reused)
                    reused++;
                else
                    exported++;
                files += export.FileCount;
                errors += export.ErrorCount;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"  {rel}: {ex.Message}");
            }
        }
        progress.Report(new ProgressNotificationValue { Progress = assemblies.Count, Total = assemblies.Count, Message = "done" });

        var sb = new StringBuilder();
        sb.AppendLine($"Exported {assemblies.Count} assemblies to '{cache.OutputDirectory}' in {stopwatch.Elapsed.TotalSeconds:N1}s:");
        sb.AppendLine($"  freshly exported: {exported}   reused (unchanged): {reused}   failed: {failures.Count}");
        sb.AppendLine($"  C# files: {files:N0}   decompiler errors written as comments: {errors:N0}");
        if (failures.Count > 0)
        {
            sb.AppendLine("Failed assemblies:");
            foreach (var failure in failures)
                sb.AppendLine(failure);
        }
        sb.Append("Each assembly lives at <OutputDirectory>/<its relative path>/ (the folder keeps the .dll suffix).");
        return sb.ToString();
    }

    private static string DescribeExport(ProjectExport export)
    {
        var namespaces = Directory.EnumerateDirectories(export.ProjectDirectory)
            .Select(Path.GetFileName)
            .Where(n => n != "Properties")
            .Order(StringComparer.Ordinal)
            .ToList();
        var listed = string.Join(", ", namespaces.Take(MaxNamespacesListed));
        if (namespaces.Count > MaxNamespacesListed)
            listed += $", +{namespaces.Count - MaxNamespacesListed} more";

        var status = export.Reused ? "reused, assembly unchanged" : "freshly exported";
        return $"Decompiled '{export.RelativePath}' ({status}) to:\n  {export.ProjectDirectory}\n" +
               $"Project file: {export.ProjectFilePath}\n" +
               $"C# files: {export.FileCount:N0}   Decompiler errors (written as comments in the files): {export.ErrorCount:N0}\n" +
               $"Namespace folders: {(namespaces.Count == 0 ? "(none; types are in the project root)" : listed)}";
    }

    private bool TryResolveOrError(string identifier, out string fullPath, out string relativePath, out string errorMessage)
    {
        if (discovery.TryResolve(identifier, out relativePath, out fullPath, out var error))
        {
            errorMessage = string.Empty;
            return true;
        }
        errorMessage = $"Error: {error}";
        return false;
    }
}

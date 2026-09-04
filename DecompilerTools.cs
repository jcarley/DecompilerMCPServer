using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace DecompilerMCPServer;

[McpServerToolType]
public sealed class DecompilerTools(
    AssemblyDiscoveryService discovery,
    DecompilationCache cache,
    DecompilerService decompiler)
{
    private const int MaxDecompileOutputChars = 200_000;

    [McpServerTool(Name = "list_assemblies"),
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
                   string.Join("\n", assemblies);
        }
        catch (Exception ex)
        {
            return $"Error: could not scan '{discovery.Root}': {ex.Message}";
        }
    }

    [McpServerTool(Name = "decompile_assembly"),
     Description("Decompiles an entire assembly to C# source code. Can return very large output; " +
                 "prefer decompile_type or search_code when possible.")]
    public string DecompileAssembly(
        [Description("Assembly identifier: a relative path from list_assemblies, or a simple name like 'MyLib' or 'MyLib.dll'.")]
        string assembly,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOrError(assembly, out var fullPath, out var error))
            return error;

        try
        {
            var source = cache.GetSource(fullPath, cancellationToken);
            if (source.Length <= MaxDecompileOutputChars)
                return source;

            int cut = source.LastIndexOf('\n', MaxDecompileOutputChars);
            if (cut < 0)
                cut = MaxDecompileOutputChars;
            return source[..cut] +
                   $"\n\n[truncated: output exceeded {MaxDecompileOutputChars:N0} characters ({source.Length:N0} total). " +
                   "Use decompile_type for a specific type or search_code to locate code.]";
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

    [McpServerTool(Name = "decompile_type"),
     Description("Decompiles a single type to C#. Use the full type name, e.g. 'MyNs.MyClass'. " +
                 "Nested types use '+' (MyNs.Outer+Inner); generic types include arity (MyNs.Widget`1).")]
    public string DecompileType(
        [Description("Full type name, e.g. 'MyNamespace.MyClass'")] string typeName,
        [Description("Optional assembly identifier to look in. If omitted, all scanned assemblies are searched.")]
        string? assembly = null,
        CancellationToken cancellationToken = default)
    {
        if (assembly is not null)
        {
            if (!TryResolveOrError(assembly, out var fullPath, out var error))
                return error;
            return DecompileTypeIn(fullPath, Path.GetRelativePath(discovery.Root, fullPath), typeName, cancellationToken);
        }

        var hits = new List<string>();
        foreach (var rel in discovery.FindAssemblies())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (decompiler.ContainsType(Path.Combine(discovery.Root, rel), typeName))
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
            1 => DecompileTypeIn(Path.Combine(discovery.Root, hits[0]), hits[0], typeName, cancellationToken),
            _ => $"Error: type '{typeName}' found in multiple assemblies:\n  " +
                 string.Join("\n  ", hits) +
                 "\nSpecify the 'assembly' parameter.",
        };
    }

    private string DecompileTypeIn(string fullPath, string relativePath, string typeName, CancellationToken cancellationToken)
    {
        try
        {
            return $"// From: {relativePath}\n" + decompiler.DecompileType(fullPath, typeName, cancellationToken);
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

    [McpServerTool(Name = "search_code"),
     Description("Regex search across the decompiled C# source of all scanned assemblies. " +
                 "Returns matching lines with line numbers and surrounding context, grouped by assembly. " +
                 "Line numbers align with decompile_assembly output for the same assembly.")]
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
            if (!TryResolveOrError(assembly, out var fullPath, out var error))
                return error;
            targets = [Path.GetRelativePath(discovery.Root, fullPath)];
        }
        else
        {
            targets = discovery.FindAssemblies();
        }

        if (targets.Count == 0)
            return $"No assemblies found under '{discovery.Root}'.";

        var sb = new StringBuilder();
        int totalMatches = 0, assembliesWithMatches = 0, searched = 0;
        bool truncated = false, timedOut = false;

        foreach (var rel in targets)
        {
            if (truncated || timedOut)
                break;
            cancellationToken.ThrowIfCancellationRequested();

            string source;
            try
            {
                source = cache.GetSource(Path.Combine(discovery.Root, rel), cancellationToken);
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

            var lines = source.Replace("\r\n", "\n").Split('\n');
            bool headerWritten = false;
            try
            {
                for (int i = 0; i < lines.Length && !truncated; i++)
                {
                    if (!regex.IsMatch(lines[i]))
                        continue;

                    if (!headerWritten)
                    {
                        sb.AppendLine($"== {rel} ==");
                        headerWritten = true;
                        assembliesWithMatches++;
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
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
            }
        }

        if (totalMatches == 0 && sb.Length == 0)
            return $"No matches for pattern '{pattern}' (searched {searched} of {targets.Count} assemblies).";

        if (timedOut)
            sb.AppendLine("Error: regex timed out (catastrophic backtracking?); results above are partial. Simplify the pattern.");
        if (truncated)
            sb.AppendLine($"[truncated: maxResults={maxResults} reached; refine the pattern]");

        sb.Append($"{totalMatches} match(es) in {assembliesWithMatches} of {searched} searched assemblies.");
        return sb.ToString();
    }

    private bool TryResolveOrError(string identifier, out string fullPath, out string errorMessage)
    {
        if (discovery.TryResolve(identifier, out fullPath, out var error))
        {
            errorMessage = string.Empty;
            return true;
        }
        errorMessage = $"Error: {error}";
        return false;
    }
}

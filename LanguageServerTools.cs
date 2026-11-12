using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DecompilerMCPServer.LanguageServer;
using ModelContextProtocol.Server;

namespace DecompilerMCPServer;

/// <summary>
/// Semantic navigation over the decompiled sources through the Roslyn language server (registered only with
/// --EnableLanguageServer true). Positions are 1-based at this boundary and 0-based on the LSP wire.
/// </summary>
[McpServerToolType]
public sealed class LanguageServerTools(
    RoslynLanguageServerHost host,
    SymbolResolver resolver,
    AssemblyDiscoveryService discovery,
    ProjectExportCache cache)
{
    private const int MaxAmbiguous = 20;
    private const int MaxOutlineEntries = 2000;

    private const string SymbolDescription =
        "Symbol name: 'Ns.Type', 'Type.Member', 'Ns.Type.Method(int, string)' for an overload, 'Widget`1' or 'Widget<T>' " +
        "for generics, 'Ns.Type..ctor' for constructors. Optional when file+line are given.";
    private const string FileDescription =
        "Optional .cs file under the output directory (absolute, or relative to it), as returned by other tools. Requires 'line'.";
    private const string LineDescription = "1-based line in 'file'.";
    private const string ColumnDescription =
        "1-based column in 'line'. If omitted, the last segment of 'symbol' is located on the line.";

    [McpServerTool(Name = "find_symbol", Idempotent = true, Destructive = false),
     Description("Searches declarations across all decompiled assemblies by name through the Roslyn language server " +
                 "(fuzzy, case-insensitive). Returns kind, name, assembly and file:line for each hit. The first call " +
                 "starts the language server and exports every scanned assembly, which can take a while.")]
    public async Task<string> FindSymbol(
        [Description("Name or partial name to search for, e.g. 'Widget' or 'GetWid'.")] string query,
        [Description("Optional kind filter: class, interface, struct, enum, type, method, property, field, constant, event, enummember, operator, namespace. Constructors are reported as methods named after their type.")]
        string? kind = null,
        [Description("Optional assembly identifier (relative path from list_assemblies, or simple name) to restrict results to.")]
        string? assembly = null,
        [Description("Maximum results to return (default 50, max 200).")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 200);
        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' is required.";

        int[]? kinds = null;
        if (kind is not null && (kinds = Lsp.KindsFor(kind)) is null)
            return $"Error: unknown kind '{kind}'. Use one of: class, interface, struct, enum, type, method, " +
                   "property, field, constant, event, enummember, operator, namespace.";

        string? assemblyDir = null;
        if (assembly is not null)
        {
            if (!discovery.TryResolve(assembly, out var relativePath, out _, out var error))
                return $"Error: {error}";
            assemblyDir = Path.TrimEndingDirectorySeparator(cache.ProjectDirectoryFor(relativePath)) + Path.DirectorySeparatorChar;
        }

        return await RunAsync(async client =>
        {
            var result = await client.RequestAsync("workspace/symbol", new { query }, cancellationToken);
            var symbols = result.ValueKind == JsonValueKind.Array
                ? result.EnumerateArray().Select(e => e.Deserialize<LspSymbolInformation>(Lsp.JsonOptions)!).ToList()
                : [];

            var hits = symbols
                .Where(s => s.Location is not null)
                .Select(s => (Symbol: s, Path: Lsp.UriToPath(s.Location!.Uri)))
                .Where(h => IsUnderOutput(h.Path))
                .Where(h => kinds is null || kinds.Contains(h.Symbol.Kind))
                .Where(h => assemblyDir is null || h.Path.StartsWith(assemblyDir, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 0)
                return $"No symbols matching '{query}'{(kind is null ? "" : $" of kind {kind}")}" +
                       $"{(assembly is null ? "" : $" in '{assembly}'")}.";

            var sb = new StringBuilder();
            foreach (var (symbol, path) in hits.Take(maxResults))
                sb.AppendLine($"{Lsp.KindName(symbol.Kind),-12} {symbol.Name}  [{AssemblyOf(path)}]\n  {path}:{symbol.Location!.Range.Start.Line + 1}");
            if (hits.Count > maxResults)
                sb.AppendLine($"[truncated: {hits.Count} hits, maxResults={maxResults}; refine the query or add kind/assembly]");
            sb.Append($"{Math.Min(hits.Count, maxResults)} of {hits.Count} symbol(s). Use go_to_definition, find_references or hover with a qualified name for more.");
            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "get_document_symbols", Idempotent = true, Destructive = false),
     Description("Outline of a decompiled C# file through the Roslyn language server: namespaces, types and members " +
                 "with signatures and 1-based line numbers. Give a file path, or a type name to outline its file.")]
    public async Task<string> GetDocumentSymbols(
        [Description("A .cs file under the output directory (absolute, or relative to it).")] string? file = null,
        [Description("Alternatively, a type name like 'Ns.Type' (or 'Type'); its file is outlined.")] string? type = null,
        CancellationToken cancellationToken = default)
    {
        if (file is null == type is null)
            return "Error: pass exactly one of 'file' or 'type'.";

        string? path = null;
        if (file is not null && !TryResolveSourceFile(file, out path, out var fileError))
            return fileError;

        return await RunAsync(async client =>
        {
            if (type is not null)
            {
                var resolved = await ResolveSingleAsync(client, type, Lsp.TypeKinds, cancellationToken);
                if (resolved.Error is not null)
                    return resolved.Error;
                path = resolved.Match!.FilePath;
            }

            var symbols = await host.GetDocumentSymbolsAsync(client, path!, cancellationToken);
            if (symbols.Count == 0)
                return $"No symbols in '{path}' (the language server may still be loading; try again shortly).";

            var sb = new StringBuilder($"Symbols in {path}:\n");
            int count = 0;
            void Write(IReadOnlyList<LspDocumentSymbol> items, int depth)
            {
                foreach (var s in items)
                {
                    if (++count > MaxOutlineEntries)
                        return;
                    sb.AppendLine($"{new string(' ', depth * 2)}{Lsp.KindName(s.Kind)} {s.Name}  (line {s.SelectionRange.Start.Line + 1})");
                    if (s.Children is { Count: > 0 })
                        Write(s.Children, depth + 1);
                }
            }
            Write(symbols, 0);
            if (count > MaxOutlineEntries)
                sb.AppendLine($"[truncated at {MaxOutlineEntries} entries]");
            return sb.ToString().TrimEnd();
        }, cancellationToken);
    }

    [McpServerTool(Name = "go_to_definition", Idempotent = true, Destructive = false),
     Description("Finds where a symbol is declared, across assemblies, through the Roslyn language server. Identify " +
                 "the symbol by name, or by a usage position (file + line [+ column]). Returns file:line with a snippet; " +
                 "definitions outside the scanned assemblies are reported as external.")]
    public Task<string> GoToDefinition(
        [Description(SymbolDescription)] string? symbol = null,
        [Description(FileDescription)] string? file = null,
        [Description(LineDescription)] int? line = null,
        [Description(ColumnDescription)] int? column = null,
        [Description("Context lines around each result (default 2, max 10).")] int contextLines = 2,
        CancellationToken cancellationToken = default) =>
        LocationsAsync("textDocument/definition", "Definition", symbol, file, line, column, contextLines, 20, null, cancellationToken);

    [McpServerTool(Name = "find_references", Idempotent = true, Destructive = false),
     Description("Finds all references to a symbol across the decompiled assemblies through the Roslyn language server. " +
                 "Identify the symbol by name, or by a position (file + line [+ column]). Results are grouped by file " +
                 "with 1-based line numbers.")]
    public Task<string> FindReferences(
        [Description(SymbolDescription)] string? symbol = null,
        [Description(FileDescription)] string? file = null,
        [Description(LineDescription)] int? line = null,
        [Description(ColumnDescription)] int? column = null,
        [Description("Include the declaration itself (default false).")] bool includeDeclaration = false,
        [Description("Context lines around each result (default 0, max 10).")] int contextLines = 0,
        [Description("Maximum references to return (default 100, max 500).")] int maxResults = 100,
        CancellationToken cancellationToken = default) =>
        LocationsAsync("textDocument/references", "Reference", symbol, file, line, column, contextLines,
            Math.Clamp(maxResults, 1, 500), new { includeDeclaration }, cancellationToken);

    [McpServerTool(Name = "find_implementations", Idempotent = true, Destructive = false),
     Description("Finds implementations of an interface or interface member, and overrides of an abstract/virtual " +
                 "member, across the decompiled assemblies through the Roslyn language server. Identify the symbol by " +
                 "name or by a position (file + line [+ column]).")]
    public Task<string> FindImplementations(
        [Description(SymbolDescription)] string? symbol = null,
        [Description(FileDescription)] string? file = null,
        [Description(LineDescription)] int? line = null,
        [Description(ColumnDescription)] int? column = null,
        [Description("Context lines around each result (default 0, max 10).")] int contextLines = 0,
        [Description("Maximum results to return (default 100, max 500).")] int maxResults = 100,
        CancellationToken cancellationToken = default) =>
        LocationsAsync("textDocument/implementation", "Implementation", symbol, file, line, column, contextLines,
            Math.Clamp(maxResults, 1, 500), null, cancellationToken);

    [McpServerTool(Name = "hover", Idempotent = true, Destructive = false),
     Description("Shows a symbol's full signature and XML documentation (when the assembly carried any) through the " +
                 "Roslyn language server. Identify the symbol by name or by a position (file + line [+ column]).")]
    public async Task<string> Hover(
        [Description(SymbolDescription)] string? symbol = null,
        [Description(FileDescription)] string? file = null,
        [Description(LineDescription)] int? line = null,
        [Description(ColumnDescription)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(async client =>
        {
            var target = await ResolveTargetAsync(client, symbol, file, line, column, cancellationToken);
            if (target.Error is not null)
                return target.Error;

            await host.EnsureOpenAsync(client, target.Path!, cancellationToken);
            var result = await client.RequestAsync("textDocument/hover", new
            {
                textDocument = new { uri = Lsp.PathToUri(target.Path!) },
                position = target.Position,
            }, cancellationToken);
            var text = Lsp.HoverText(result);
            return text is null
                ? $"No hover information at {target.Label}."
                : $"{target.Label}:\n{text}";
        }, cancellationToken);
    }

    private async Task<string> LocationsAsync(
        string method, string noun, string? symbol, string? file, int? line, int? column,
        int contextLines, int maxResults, object? context, CancellationToken cancellationToken)
    {
        contextLines = Math.Clamp(contextLines, 0, 10);
        return await RunAsync(async client =>
        {
            var target = await ResolveTargetAsync(client, symbol, file, line, column, cancellationToken);
            if (target.Error is not null)
                return target.Error;

            await host.EnsureOpenAsync(client, target.Path!, cancellationToken);
            var textDocument = new { uri = Lsp.PathToUri(target.Path!) };
            object parameters = context is null
                ? new { textDocument, position = target.Position }
                : new { textDocument, position = target.Position, context };
            var result = await client.RequestAsync(method, parameters, cancellationToken);
            var locations = Lsp.ParseLocations(result)
                .OrderBy(l => Lsp.UriToPath(l.Uri), StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Range.Start.Line)
                .ThenBy(l => l.Range.Start.Character)
                .ToList();
            if (locations.Count == 0)
                return $"No {noun.ToLowerInvariant()}s found for {target.Label}.";

            var sb = new StringBuilder($"{noun}s of {target.Label}:\n");
            var external = new List<string>();
            string? currentFile = null;
            string[]? lines = null;
            int shown = 0;
            foreach (var location in locations)
            {
                if (shown >= maxResults)
                    break;
                var path = Lsp.UriToPath(location.Uri);
                if (!IsUnderOutput(path))
                {
                    external.Add(DescribeExternal(path, location.Range.Start.Line));
                    shown++;
                    continue;
                }
                if (!string.Equals(path, currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    currentFile = path;
                    lines = TryReadLines(path);
                    sb.AppendLine($"== {path} ==");
                }
                AppendSnippet(sb, lines, location.Range.Start.Line, contextLines);
                shown++;
            }
            foreach (var item in external)
                sb.AppendLine($"(external) {item}");
            if (locations.Count > shown)
                sb.AppendLine($"[truncated: {locations.Count} results, maxResults={maxResults}]");
            sb.Append($"{shown} of {locations.Count} {noun.ToLowerInvariant()}(s).");
            return sb.ToString();
        }, cancellationToken);
    }

    private sealed record Target(string? Path, LspPosition? Position, string? Label, string? Error);

    /// <summary>A position from file+line[+column] (column located from 'symbol' if omitted), or from a resolved symbol name.</summary>
    private async Task<Target> ResolveTargetAsync(LspClient client, string? symbol, string? file, int? line, int? column, CancellationToken cancellationToken)
    {
        if (file is not null)
        {
            if (line is null)
                return new(null, null, null, "Error: 'line' is required with 'file'.");
            if (!TryResolveSourceFile(file, out var path, out var fileError))
                return new(null, null, null, fileError);

            var lines = TryReadLines(path) ?? [];
            if (line < 1 || line > lines.Length)
                return new(null, null, null, $"Error: line {line} is outside '{path}' ({lines.Length} lines).");
            var text = lines[line.Value - 1];

            int character;
            if (column is not null)
            {
                if (column < 1 || column > text.Length + 1)
                    return new(null, null, null, $"Error: column {column} is outside line {line} ({text.Length} characters).");
                character = column.Value - 1;
            }
            else if (symbol is not null && SymbolResolver.ParseQuery(symbol) is { } query)
            {
                character = FindIdentifier(text, query.Name);
                if (character < 0)
                    return new(null, null, null, $"Error: '{query.Name}' does not occur on line {line} of '{path}'. Pass 'column'.");
            }
            else
            {
                return new(null, null, null, "Error: pass 'column', or 'symbol' to locate on the line.");
            }
            return new(path, new LspPosition(line.Value - 1, character), $"{path}:{line}:{character + 1}", null);
        }

        if (string.IsNullOrWhiteSpace(symbol))
            return new(null, null, null, "Error: pass 'symbol', or 'file' + 'line' [+ 'column'].");

        var resolved = await ResolveSingleAsync(client, symbol, null, cancellationToken);
        if (resolved.Error is not null)
            return new(null, null, null, resolved.Error);
        var match = resolved.Match!;
        return new(match.FilePath, match.Position,
            $"{Lsp.KindName(match.Entry.Kind)} {match.Entry.QualifiedDisplay} ({match.FilePath}:{match.Position.Line + 1})", null);
    }

    private async Task<(SymbolMatch? Match, string? Error)> ResolveSingleAsync(LspClient client, string symbol, int[]? kinds, CancellationToken cancellationToken)
    {
        var query = SymbolResolver.ParseQuery(symbol);
        if (query is null)
            return (null, $"Error: '{symbol}' is not a valid symbol name. Use e.g. 'Ns.Type', 'Type.Member' or 'Type.Method(int)'.");

        var matches = await resolver.ResolveAsync(client, query, kinds, cancellationToken);
        if (matches.Count == 0)
            return (null, $"Error: no declaration named '{symbol}' found in the decompiled assemblies. " +
                          "Check the spelling, try find_symbol with a partial name, or the language server may still be loading.");

        // A type and its constructors share a name; prefer the type unless constructors were asked for
        if (matches.Count > 1 && !query.IsConstructor)
        {
            var types = matches.Where(m => Lsp.TypeKinds.Contains(m.Entry.Kind)).ToList();
            if (types.Count == 1)
                return (types[0], null);
            if (types.Count > 1)
                matches = types;
        }
        if (matches.Count == 1)
            return (matches[0], null);

        var sb = new StringBuilder($"Error: ambiguous symbol '{symbol}' ({matches.Count} matches):\n");
        foreach (var m in matches.Take(MaxAmbiguous))
            sb.AppendLine($"  {Lsp.KindName(m.Entry.Kind)} {m.Entry.QualifiedDisplay}  {m.FilePath}:{m.Position.Line + 1}");
        if (matches.Count > MaxAmbiguous)
            sb.AppendLine($"  ... +{matches.Count - MaxAmbiguous} more");
        sb.Append("Qualify the name with its namespace/type, add generic arity (Name`2) or a parameter list for overloads, or pass file + line.");
        return (null, sb.ToString());
    }

    private async Task<string> RunAsync(Func<LspClient, Task<string>> action, CancellationToken cancellationToken)
    {
        try
        {
            var client = await host.GetClientAsync(cancellationToken);
            return await action(client);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LanguageServerUnavailableException ex)
        {
            return $"Error: language server unavailable: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: language server request failed: {ex.Message}. If it keeps failing, check the server's stderr log.";
        }
    }

    private bool TryResolveSourceFile(string file, out string path, out string error)
    {
        path = "";
        error = "";
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(cache.OutputDirectory, file));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Error: invalid file path '{file}': {ex.Message}";
            return false;
        }
        if (!IsUnderOutput(full) || !full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Error: '{file}' is not a decompiled .cs file under '{cache.OutputDirectory}'.";
            return false;
        }
        if (!File.Exists(full))
        {
            error = $"Error: '{full}' does not exist. Use decompile_type or find_symbol to get a current path.";
            return false;
        }
        path = full;
        return true;
    }

    private bool IsUnderOutput(string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(cache.OutputDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The assembly folder part of an output path, e.g. "sub/Foo.dll".</summary>
    private string AssemblyOf(string path)
    {
        var relative = Path.GetRelativePath(cache.OutputDirectory, path);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        int index = Array.FindIndex(parts, p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        return index < 0 ? relative : string.Join(Path.DirectorySeparatorChar, parts[..(index + 1)]);
    }

    /// <summary>
    /// A location outside the output directory is the server's metadata-as-source view of a referenced assembly
    /// (framework or unresolved). Its header names the assembly; the target line names the symbol.
    /// </summary>
    private static string DescribeExternal(string path, int line)
    {
        var lines = TryReadLines(path);
        if (lines is null)
            return path;
        var assembly = lines.Take(10)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("#region Assembly ", StringComparison.Ordinal))?["#region Assembly ".Length..]
            .Split(',')[0];
        var text = line >= 0 && line < lines.Length ? lines[line].Trim() : "";
        return $"{(assembly is null ? "" : $"[{assembly}] ")}{text}  (not in the scan directory; decompiled view at {path})";
    }

    private static int FindIdentifier(string line, string name)
    {
        int start = 0;
        while (true)
        {
            int index = line.IndexOf(name, start, StringComparison.Ordinal);
            if (index < 0)
                return -1;
            bool before = index == 0 || !IsIdentifierChar(line[index - 1]);
            bool after = index + name.Length >= line.Length || !IsIdentifierChar(line[index + name.Length]);
            if (before && after)
                return index;
            start = index + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string[]? TryReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AppendSnippet(StringBuilder sb, string[]? lines, int line, int contextLines)
    {
        if (lines is null || line < 0 || line >= lines.Length)
        {
            sb.AppendLine($">{line + 1,5} | (file unreadable)");
            return;
        }
        int start = Math.Max(0, line - contextLines);
        int end = Math.Min(lines.Length - 1, line + contextLines);
        for (int j = start; j <= end; j++)
            sb.AppendLine($"{(j == line ? ">" : " ")}{j + 1,5} | {lines[j]}");
        if (contextLines > 0)
            sb.AppendLine();
    }
}

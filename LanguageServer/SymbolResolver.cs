using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>A parsed symbol name: <c>Ns.Type</c>, <c>Type.Member</c>, <c>Ns.Type.Method(int, string)</c>, <c>Widget`1</c>, <c>Type..ctor</c>.</summary>
/// <param name="Segments">Name segments without generic arguments; namespaces split on '.', nested types on '+' or '.'.</param>
/// <param name="Arities">Generic arity per segment when given (<c>`1</c> or <c>&lt;T&gt;</c>), else null.</param>
/// <param name="Parameters">Parameter list text between the parentheses when given (possibly empty).</param>
public sealed record SymbolQuery(IReadOnlyList<string> Segments, IReadOnlyList<int?> Arities, string? Parameters, bool IsConstructor)
{
    public string Name => Segments[^1];
    public int? Arity => Arities[^1];
    public IReadOnlyList<string> Container => Segments.Take(Segments.Count - 1).ToList();
    public IReadOnlyList<int?> ContainerArities => Arities.Take(Arities.Count - 1).ToList();
}

/// <summary>Roslyn's display name split into the parts the resolver matches on.</summary>
/// <param name="Detail">Signature/type part, e.g. <c>(int, int) : int</c> or <c>: string</c>.</param>
public sealed record RoslynName(string Name, int Arity, string? Parameters, string Detail);

/// <summary>A declaration found in a file's symbol tree, with its fully qualified container.</summary>
/// <param name="Container">Container names as matched (no generic arguments).</param>
/// <param name="ContainerArities">Generic arity of each container (0 for namespaces and non-generic types).</param>
/// <param name="ContainerDisplay">Container names as Roslyn shows them (generic types keep their type parameters).</param>
public sealed record SymbolEntry(
    IReadOnlyList<string> Container,
    IReadOnlyList<int> ContainerArities,
    IReadOnlyList<string> ContainerDisplay,
    RoslynName Parsed,
    string DisplayName,
    int Kind,
    LspRange SelectionRange)
{
    public string QualifiedName => string.Join('.', Container.Append(Parsed.Name));

    /// <summary>E.g. <c>Ns.Widget&lt;T&gt;.Add(int, int) : int</c>.</summary>
    public string QualifiedDisplay => string.Join('.', ContainerDisplay.Append(DisplayName));
}

public sealed record SymbolMatch(SymbolEntry Entry, string FilePath)
{
    public LspPosition Position => Entry.SelectionRange.Start;
}

/// <summary>
/// Resolves user-facing symbol names to declarations in the decompiled workspace. workspace/symbol finds candidates
/// by simple name (it is fuzzy and its container is the project, not the namespace); each candidate's file is then
/// read with documentSymbol to get the qualified chain (namespace, types, member) that the query must end with.
/// </summary>
public sealed partial class SymbolResolver(RoslynLanguageServerHost host)
{
    private const int MaxCandidateFiles = 50;

    /// <summary>Roslyn reports constructors as methods named after their type.</summary>
    private static readonly int[] ConstructorKinds = [6, 9];

    /// <summary>Matches for the query, optionally restricted to LSP kinds. Empty when nothing matches.</summary>
    public async Task<IReadOnlyList<SymbolMatch>> ResolveAsync(LspClient client, SymbolQuery query, int[]? kinds, CancellationToken cancellationToken)
    {
        if (query.IsConstructor)
            kinds = ConstructorKinds;

        var files = await CandidateFilesAsync(client, query.Name, cancellationToken);
        var matches = await MatchInFilesAsync(client, files, query, kinds, cancellationToken);

        // workspace/symbol caps and ranks its results, so a common member name may miss; find the container type instead
        if (matches.Count == 0 && query.Segments.Count >= 2)
        {
            var containerQuery = new SymbolQuery(query.Container, query.ContainerArities, null, false);
            var containers = await ResolveAsync(client, containerQuery, Lsp.TypeKinds, cancellationToken);
            matches = await MatchInFilesAsync(client, containers.Select(c => c.FilePath).Distinct().ToList(), query, kinds, cancellationToken);
        }
        return matches;
    }

    private async Task<IReadOnlyList<string>> CandidateFilesAsync(LspClient client, string name, CancellationToken cancellationToken)
    {
        var result = await client.RequestAsync("workspace/symbol", new { query = name }, cancellationToken);
        if (result.ValueKind != JsonValueKind.Array)
            return [];

        return result.EnumerateArray()
            .Select(e => e.Deserialize<LspSymbolInformation>(Lsp.JsonOptions))
            .Where(s => s?.Location is not null && string.Equals(ParseRoslynName(s.Name).Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(s => Lsp.UriToPath(s!.Location!.Uri))
            .Where(IsInWorkspace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidateFiles)
            .ToList();
    }

    private async Task<IReadOnlyList<SymbolMatch>> MatchInFilesAsync(
        LspClient client, IReadOnlyList<string> files, SymbolQuery query, int[]? kinds, CancellationToken cancellationToken)
    {
        var all = new List<SymbolMatch>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbols = await host.GetDocumentSymbolsAsync(client, file, cancellationToken);
            all.AddRange(Flatten(symbols).Select(entry => new SymbolMatch(entry, file)));
        }

        var exact = Filter(all, query, kinds, StringComparison.Ordinal);
        var chosen = exact.Count > 0 ? exact : Filter(all, query, kinds, StringComparison.OrdinalIgnoreCase);
        return chosen
            .DistinctBy(m => (m.FilePath.ToUpperInvariant(), m.Position.Line, m.Position.Character))
            .ToList();
    }

    private static List<SymbolMatch> Filter(List<SymbolMatch> all, SymbolQuery query, int[]? kinds, StringComparison comparison) =>
        all.Where(m => Matches(m.Entry, query, comparison) && (kinds is null || kinds.Contains(m.Entry.Kind))).ToList();

    private bool IsInWorkspace(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(host.OutputDirectory) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a declaration matches the query: same name (and arity/parameters if given), container ends with the query's.</summary>
    public static bool Matches(SymbolEntry entry, SymbolQuery query, StringComparison comparison)
    {
        if (!string.Equals(entry.Parsed.Name, query.Name, comparison))
            return false;
        if (query.Arity is { } arity && entry.Parsed.Arity != arity)
            return false;
        if (query.Parameters is not null &&
            !string.Equals(NormalizeParameters(entry.Parsed.Parameters ?? ""), NormalizeParameters(query.Parameters), StringComparison.OrdinalIgnoreCase))
            return false;

        var container = query.Container;
        var arities = query.ContainerArities;
        if (container.Count > entry.Container.Count)
            return false;
        int offset = entry.Container.Count - container.Count;
        for (int i = 0; i < container.Count; i++)
        {
            if (!string.Equals(entry.Container[offset + i], container[i], comparison))
                return false;
            if (arities[i] is { } containerArity && entry.ContainerArities[offset + i] != containerArity)
                return false;
        }
        return true;
    }

    /// <summary>Depth-first list of declarations with their qualified containers; namespace names are split on '.'.</summary>
    public static IEnumerable<SymbolEntry> Flatten(IReadOnlyList<LspDocumentSymbol> symbols)
    {
        var stack = new Stack<(LspDocumentSymbol Symbol, IReadOnlyList<string> Container, IReadOnlyList<int> Arities, IReadOnlyList<string> Display)>();
        foreach (var symbol in symbols.Reverse())
            stack.Push((symbol, [], [], []));

        while (stack.Count > 0)
        {
            var (symbol, container, arities, display) = stack.Pop();
            var parsed = ParseRoslynName(symbol.Name);
            SymbolEntry entry;
            if (symbol.Kind == 3 && parsed.Name.Contains('.'))
            {
                var parts = parsed.Name.Split('.');
                entry = new SymbolEntry([.. container, .. parts[..^1]], [.. arities, .. parts[..^1].Select(_ => 0)],
                    [.. display, .. parts[..^1]], parsed with { Name = parts[^1] }, parts[^1], symbol.Kind, symbol.SelectionRange);
            }
            else
            {
                entry = new SymbolEntry(container, arities, display, parsed, symbol.Name, symbol.Kind, symbol.SelectionRange);
            }
            yield return entry;

            if (symbol.Children is { Count: > 0 } children)
            {
                IReadOnlyList<string> childContainer = [.. entry.Container, entry.Parsed.Name];
                IReadOnlyList<int> childArities = [.. entry.ContainerArities, entry.Parsed.Arity];
                // Members show signatures ("Add(int) : int"); as a container only the name (with type parameters) is useful
                IReadOnlyList<string> childDisplay = [.. entry.ContainerDisplay, entry.DisplayName.Split(" : ")[0]];
                foreach (var child in children.Reverse())
                    stack.Push((child, childContainer, childArities, childDisplay));
            }
        }
    }

    /// <summary>
    /// Parses a user query. Accepts '.' or '+' between segments, generic arity on any segment as <c>`1</c> or
    /// <c>&lt;T, U&gt;</c>, an optional parameter list, and <c>..ctor</c> / <c>.#ctor</c> for constructors.
    /// </summary>
    public static SymbolQuery? ParseQuery(string text)
    {
        text = text.Trim();
        string? parameters = null;
        int paren = IndexOfTopLevel(text, '(');
        if (paren >= 0)
        {
            int close = text.LastIndexOf(')');
            if (close < paren)
                return null;
            parameters = text[(paren + 1)..close].Trim();
            text = text[..paren].Trim();
        }

        bool isConstructor = false;
        foreach (var suffix in new[] { "..ctor", ".#ctor", ".ctor" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length)
            {
                text = text[..^suffix.Length];
                isConstructor = true;
                break;
            }
        }

        var segments = new List<string>();
        var arities = new List<int?>();
        int? arity = null;
        var current = new StringBuilder();
        int depth = 0, commas = 0;

        bool EndSegment()
        {
            var segment = current.ToString();
            current.Clear();
            int tick = segment.IndexOf('`');
            if (tick >= 0)
            {
                if (tick == 0 || !int.TryParse(segment[(tick + 1)..], out var parsedArity))
                    return false;
                arity = parsedArity;
                segment = segment[..tick];
            }
            if (segment.Length == 0)
                return false;
            segments.Add(segment);
            arities.Add(arity);
            arity = null;
            return true;
        }

        foreach (var c in text)
        {
            if (c == '<')
            {
                if (depth++ == 0)
                    commas = 0;
                continue;
            }
            if (c == '>')
            {
                if (--depth < 0)
                    return null;
                if (depth == 0)
                    arity = commas + 1;
                continue;
            }
            if (depth > 0)
            {
                if (c == ',' && depth == 1)
                    commas++;
                continue;
            }
            if (c is '.' or '+' or '/')
            {
                if (!EndSegment())
                    return null;
                continue;
            }
            if (!char.IsWhiteSpace(c))
                current.Append(c);
        }
        if (depth != 0 || !EndSegment())
            return null;

        if (isConstructor)
        {
            segments.Add(segments[^1]);
            arities.Add(null);
        }
        return new SymbolQuery(segments, arities, parameters, isConstructor);
    }

    /// <summary>
    /// Splits Roslyn 5.x display names: <c>Name : string</c> -> (Name, ": string"); <c>Add(int, int) : int</c> -> (Add,
    /// params "int, int"); <c>Widget&lt;T&gt;</c> -> (Widget, arity 1); <c>Position : (int X, string Y)</c> keeps the tuple as detail.
    /// </summary>
    public static RoslynName ParseRoslynName(string roslynName)
    {
        string baseName, detail = "";
        string? parameters = null;

        int colon = roslynName.IndexOf(" : ", StringComparison.Ordinal);
        int paren = IndexOfTopLevel(roslynName, '(');
        if (colon >= 0 && (paren < 0 || paren > colon))
        {
            baseName = roslynName[..colon];
            detail = ": " + roslynName[(colon + 3)..].Trim();
        }
        else if (paren >= 0)
        {
            baseName = roslynName[..paren];
            detail = roslynName[paren..].Trim();
            int close = MatchingParen(roslynName, paren);
            parameters = close > paren ? roslynName[(paren + 1)..close] : roslynName[(paren + 1)..];
        }
        else
        {
            baseName = roslynName;
        }

        baseName = baseName.Trim();
        int arity = 0;
        int lt = baseName.IndexOf('<');
        if (lt > 0 && baseName.EndsWith('>'))
        {
            var args = baseName[(lt + 1)..^1];
            int depth = 0;
            arity = 1;
            foreach (var c in args)
            {
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0) arity++;
            }
            baseName = baseName[..lt];
        }
        return new RoslynName(baseName, arity, parameters, detail);
    }

    /// <summary>Comparable form of a parameter list: whitespace and namespace qualifiers removed.</summary>
    public static string NormalizeParameters(string parameters) =>
        NamespaceQualifier().Replace(new string(parameters.Where(c => !char.IsWhiteSpace(c)).ToArray()), "");

    private static int IndexOfTopLevel(string text, char target)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<') depth++;
            else if (c == '>') depth = Math.Max(0, depth - 1);
            else if (c == target && depth == 0) return i;
        }
        return -1;
    }

    private static int MatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)+(?=[A-Za-z_])")]
    private static partial Regex NamespaceQualifier();
}

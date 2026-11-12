using System.Text.Json;
using System.Text.Json.Serialization;

namespace DecompilerMCPServer.LanguageServer;

// Minimal LSP shapes for the requests the tools use. Positions are 0-based, as on the wire.

public sealed record LspPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record LspRange(
    [property: JsonPropertyName("start")] LspPosition Start,
    [property: JsonPropertyName("end")] LspPosition End);

public sealed record LspLocation(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] LspRange Range);

/// <summary>workspace/symbol result item (SymbolInformation; WorkspaceSymbol's location may lack a range).</summary>
public sealed record LspSymbolInformation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("location")] LspLocation? Location,
    [property: JsonPropertyName("containerName")] string? ContainerName);

/// <summary>textDocument/documentSymbol result item (hierarchical form).</summary>
public sealed record LspDocumentSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("range")] LspRange Range,
    [property: JsonPropertyName("selectionRange")] LspRange SelectionRange,
    [property: JsonPropertyName("children")] IReadOnlyList<LspDocumentSymbol>? Children);

public static class Lsp
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Normalizes Location | Location[] | LocationLink[] | null into locations (LocationLink uses its target selection).</summary>
    public static IReadOnlyList<LspLocation> ParseLocations(JsonElement result)
    {
        var list = new List<LspLocation>();
        void Add(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return;
            if (item.TryGetProperty("targetUri", out var targetUri))
            {
                var range = item.TryGetProperty("targetSelectionRange", out var sel) ? sel : item.GetProperty("targetRange");
                list.Add(new LspLocation(targetUri.GetString()!, range.Deserialize<LspRange>(JsonOptions)!));
            }
            else if (item.TryGetProperty("uri", out _) && item.TryGetProperty("range", out _))
            {
                list.Add(item.Deserialize<LspLocation>(JsonOptions)!);
            }
        }

        if (result.ValueKind == JsonValueKind.Array)
            foreach (var item in result.EnumerateArray())
                Add(item);
        else
            Add(result);
        return list;
    }

    /// <summary>Plain text of a Hover's contents (MarkupContent, MarkedString or MarkedString[]); code fences removed.</summary>
    public static string? HoverText(JsonElement hover)
    {
        if (hover.ValueKind != JsonValueKind.Object || !hover.TryGetProperty("contents", out var contents))
            return null;

        var parts = new List<string>();
        void Add(JsonElement c)
        {
            switch (c.ValueKind)
            {
                case JsonValueKind.String:
                    parts.Add(c.GetString()!);
                    break;
                case JsonValueKind.Object when c.TryGetProperty("value", out var value):
                    parts.Add(value.GetString() ?? "");
                    break;
                case JsonValueKind.Array:
                    foreach (var item in c.EnumerateArray())
                        Add(item);
                    break;
            }
        }
        Add(contents);

        var lines = string.Join("\n", parts)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .Select(l => l.TrimEnd());
        var text = string.Join("\n", lines).Trim();
        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");
        return text.Length == 0 ? null : text;
    }

    public static string PathToUri(string path) => new Uri(path).AbsoluteUri;

    public static string UriToPath(string uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;

    public static string KindName(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property",
        8 => "field", 9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable",
        14 => "constant", 15 => "string", 16 => "number", 17 => "boolean", 18 => "array", 19 => "object", 20 => "key",
        21 => "null", 22 => "enum member", 23 => "struct", 24 => "event", 25 => "operator", 26 => "type parameter",
        _ => $"kind {kind}",
    };

    /// <summary>LSP kinds for a user-facing kind name (class, method, ...), or null if the name is unknown.</summary>
    public static int[]? KindsFor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "class" => [5],
        "interface" => [11],
        "struct" => [23],
        "enum" => [10],
        "type" => [5, 10, 11, 23],
        "method" => [6, 12],
        "function" => [6, 12],
        "property" => [7],
        "field" => [8, 14],
        "constant" => [14],
        "event" => [24],
        "enummember" or "enum member" => [22],
        "operator" => [25],
        "namespace" => [3],
        _ => null,
    };

    public static readonly int[] TypeKinds = [5, 10, 11, 23];
}

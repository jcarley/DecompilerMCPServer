using System.Text.Json;
using DecompilerMCPServer.LanguageServer;

namespace DecompilerMCPServer.Tests;

public class LspTypesTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Range = """{"start":{"line":8,"character":20},"end":{"line":8,"character":36}}""";

    [Fact]
    public void ParseLocations_array_of_locations()
    {
        var locations = Lsp.ParseLocations(Json($$$"""[{"uri":"file:///C:/out/A.cs","range":{{{Range}}}},{"uri":"file:///C:/out/B.cs","range":{{{Range}}}}]"""));

        Assert.Equal(2, locations.Count);
        Assert.Equal(new LspPosition(8, 20), locations[0].Range.Start);
    }

    [Fact]
    public void ParseLocations_single_location_and_location_links()
    {
        Assert.Single(Lsp.ParseLocations(Json($$$"""{"uri":"file:///C:/out/A.cs","range":{{{Range}}}}""")));

        var link = Assert.Single(Lsp.ParseLocations(Json($$$"""
            [{"targetUri":"file:///C:/out/A.cs","targetRange":{"start":{"line":1,"character":0},"end":{"line":9,"character":1}},"targetSelectionRange":{{{Range}}}}]
            """)));
        Assert.Equal("file:///C:/out/A.cs", link.Uri);
        Assert.Equal(8, link.Range.Start.Line);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void ParseLocations_empty(string json) => Assert.Empty(Lsp.ParseLocations(Json(json)));

    [Fact]
    public void HoverText_strips_code_fences()
    {
        var text = Lsp.HoverText(Json("""{"contents":{"kind":"markdown","value":"```csharp\r\nclass Ns.Widget\r\n```\r\n  \r\nA widget.\r\n\r\n\r\n\r\nMore."}}"""));

        Assert.Equal("class Ns.Widget\n\nA widget.\n\nMore.", text);
    }

    [Theory]
    [InlineData("""{"contents":"plain"}""", "plain")]
    [InlineData("""{"contents":[{"language":"csharp","value":"int X"},"doc"]}""", "int X\ndoc")]
    [InlineData("""{"contents":{"kind":"markdown","value":"```csharp\n```"}}""", null)]
    [InlineData("null", null)]
    public void HoverText_variants(string json, string? expected) => Assert.Equal(expected, Lsp.HoverText(Json(json)));

    [Fact]
    public void DocumentSymbol_deserializes_roslyn_output()
    {
        // Captured from Roslyn LS 5.12 (glyph is a Roslyn extension and is ignored)
        const string json = """
            [{"glyph":48,"children":[{"glyph":7,"children":[{"glyph":49,"children":[],"name":"Configure(McpServerOptions) : void","detail":"Configure(McpServerOptions) : void","kind":6,"range":{"start":{"line":8,"character":1},"end":{"line":38,"character":2}},"selectionRange":{"start":{"line":8,"character":13},"end":{"line":8,"character":22}}}],"name":"McpServerOptionsSetup","detail":"McpServerOptionsSetup","kind":5,"range":{"start":{"line":6,"character":0},"end":{"line":39,"character":1}},"selectionRange":{"start":{"line":6,"character":22},"end":{"line":6,"character":43}}}],"name":"ModelContextProtocol","detail":"ModelContextProtocol","kind":3,"range":{"start":{"line":4,"character":0},"end":{"line":39,"character":1}},"selectionRange":{"start":{"line":4,"character":10},"end":{"line":4,"character":30}}}]
            """;

        var symbols = JsonSerializer.Deserialize<List<LspDocumentSymbol>>(json, Lsp.JsonOptions)!;

        var method = symbols[0].Children![0].Children![0];
        Assert.Equal("Configure(McpServerOptions) : void", method.Name);
        Assert.Equal(6, method.Kind);
        Assert.Equal(new LspPosition(8, 13), method.SelectionRange.Start);
    }

    [Fact]
    public void SymbolInformation_deserializes_roslyn_output()
    {
        const string json = """
            {"name":"McpServerOptions","kind":5,"location":{"uri":"file:///C:/out/Core.dll/Ns/McpServerOptions.cs","range":{"start":{"line":8,"character":20},"end":{"line":8,"character":36}}},"containerName":"project ModelContextProtocol.Core (net10.0)"}
            """;

        var symbol = JsonSerializer.Deserialize<LspSymbolInformation>(json, Lsp.JsonOptions)!;

        Assert.Equal("McpServerOptions", symbol.Name);
        Assert.Equal(5, symbol.Kind);
        Assert.Equal(8, symbol.Location!.Range.Start.Line);
    }

    [Fact]
    public void Uri_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "dir with space", "Ns#1", "File.cs");

        var uri = Lsp.PathToUri(path);

        Assert.StartsWith("file:///", uri);
        Assert.Equal(path, Lsp.UriToPath(uri));
    }

    [Theory]
    [InlineData("class", new[] { 5 })]
    [InlineData("Type", new[] { 5, 10, 11, 23 })]
    [InlineData(" method ", new[] { 6, 12 })]
    [InlineData("constructor", null)]
    [InlineData("widget", null)]
    public void KindsFor(string name, int[]? expected) => Assert.Equal(expected, Lsp.KindsFor(name));
}

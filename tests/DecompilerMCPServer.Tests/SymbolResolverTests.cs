using DecompilerMCPServer.LanguageServer;

namespace DecompilerMCPServer.Tests;

public class SymbolResolverTests
{
    [Theory]
    [InlineData("Widget", new[] { "Widget" }, null, null, false)]
    [InlineData("My.Ns.Widget", new[] { "My", "Ns", "Widget" }, null, null, false)]
    [InlineData("Outer+Inner", new[] { "Outer", "Inner" }, null, null, false)]
    [InlineData("Ns.Widget`2", new[] { "Ns", "Widget" }, 2, null, false)]
    [InlineData("Ns.Widget<TKey, TValue>", new[] { "Ns", "Widget" }, 2, null, false)]
    [InlineData("Dictionary<string, List<int>>", new[] { "Dictionary" }, 2, null, false)]
    [InlineData("Type.Add(int, string)", new[] { "Type", "Add" }, null, "int, string", false)]
    [InlineData("Type.Run()", new[] { "Type", "Run" }, null, "", false)]
    [InlineData("Type.Map(Dictionary<string, int>)", new[] { "Type", "Map" }, null, "Dictionary<string, int>", false)]
    [InlineData("Ns.Type..ctor", new[] { "Ns", "Type", "Type" }, null, null, true)]
    [InlineData("Ns.Type.#ctor(int)", new[] { "Ns", "Type", "Type" }, null, "int", true)]
    [InlineData("  Widget  ", new[] { "Widget" }, null, null, false)]
    public void ParseQuery_valid(string text, string[] segments, int? arity, string? parameters, bool isConstructor)
    {
        var query = SymbolResolver.ParseQuery(text);

        Assert.NotNull(query);
        Assert.Equal(segments, query.Segments);
        Assert.Equal(arity, query.Arity);
        Assert.Equal(parameters, query.Parameters);
        Assert.Equal(isConstructor, query.IsConstructor);
    }

    [Fact]
    public void ParseQuery_keeps_container_arity()
    {
        var query = SymbolResolver.ParseQuery("Ns.ConfigureNamedOptions`2.Configure(TOptions)")!;

        Assert.Equal(["Ns", "ConfigureNamedOptions", "Configure"], query.Segments);
        Assert.Equal([null, 2, null], query.Arities);
    }

    [Fact]
    public void ParseQuery_constructor_of_generic_type_moves_arity_to_the_type_segment()
    {
        var query = SymbolResolver.ParseQuery("Widget`1..ctor")!;

        Assert.Equal(["Widget", "Widget"], query.Segments);
        Assert.Equal([1, null], query.Arities);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Widget<")]
    [InlineData("Widget>")]
    [InlineData("Ns..Widget")]
    [InlineData(".Widget")]
    [InlineData("Widget`x")]
    [InlineData("Run)(")]
    public void ParseQuery_invalid(string text) => Assert.Null(SymbolResolver.ParseQuery(text));

    [Theory]
    [InlineData("SimpleMethod", "SimpleMethod", 0, null, "")]
    [InlineData("Name : string", "Name", 0, null, ": string")]
    [InlineData("Add(int, int) : int", "Add", 0, "int, int", "(int, int) : int")]
    [InlineData("ToString()", "ToString", 0, "", "()")]
    [InlineData("Position : (int X, string Y)", "Position", 0, null, ": (int X, string Y)")]
    [InlineData("Widget<T>", "Widget", 1, null, "")]
    [InlineData("ConfigureNamedOptions<TOptions, TDep1, TDep2>", "ConfigureNamedOptions", 3, null, "")]
    [InlineData("Configure<TDep>(Action<TOptions, TDep>) : OptionsBuilder<TOptions>", "Configure", 1, "Action<TOptions, TDep>", "(Action<TOptions, TDep>) : OptionsBuilder<TOptions>")]
    [InlineData("OptionsBuilder(IServiceCollection, string?)", "OptionsBuilder", 0, "IServiceCollection, string?", "(IServiceCollection, string?)")]
    [InlineData("Map(Dictionary<string, Func<int, int>>) : void", "Map", 0, "Dictionary<string, Func<int, int>>", "(Dictionary<string, Func<int, int>>) : void")]
    public void ParseRoslynName(string roslynName, string name, int arity, string? parameters, string detail)
    {
        var parsed = SymbolResolver.ParseRoslynName(roslynName);

        Assert.Equal(new RoslynName(name, arity, parameters, detail), parsed);
    }

    [Theory]
    [InlineData("int, string", "int,string")]
    [InlineData("System.Collections.Generic.List<System.String> items", "List<String>items")]
    [InlineData("  Action<TOptions,  TDep> ", "Action<TOptions,TDep>")]
    public void NormalizeParameters(string input, string expected) =>
        Assert.Equal(expected, SymbolResolver.NormalizeParameters(input));

    private static LspDocumentSymbol Sym(string name, int kind, int line, params LspDocumentSymbol[] children) =>
        new(name, null, kind, Range(line), Range(line), children);

    private static LspRange Range(int line) => new(new LspPosition(line, 0), new LspPosition(line, 1));

    private static readonly IReadOnlyList<LspDocumentSymbol> Tree =
    [
        Sym("Microsoft.Extensions.Options", 3, 3,
            Sym("OptionsBuilder<TOptions>", 5, 5,
                Sym("OptionsBuilder(IServiceCollection, string?)", 6, 13),
                Sym("Configure(Action<TOptions>) : OptionsBuilder<TOptions>", 6, 20),
                Sym("Configure<TDep>(Action<TOptions, TDep>) : OptionsBuilder<TOptions>", 6, 27),
                Sym("Name : string", 7, 9),
                Sym("Inner", 5, 40,
                    Sym("Run() : void", 6, 41)))),
    ];

    [Fact]
    public void Flatten_builds_qualified_containers_and_splits_namespaces()
    {
        var entries = SymbolResolver.Flatten(Tree).ToList();

        var run = entries.Single(e => e.Parsed.Name == "Run");
        Assert.Equal(["Microsoft", "Extensions", "Options", "OptionsBuilder", "Inner"], run.Container);
        Assert.Equal([0, 0, 0, 1, 0], run.ContainerArities);
        Assert.Equal("Microsoft.Extensions.Options.OptionsBuilder<TOptions>.Inner.Run() : void", run.QualifiedDisplay);

        var ns = entries.Single(e => e.Kind == 3);
        Assert.Equal(["Microsoft", "Extensions"], ns.Container);
        Assert.Equal("Options", ns.Parsed.Name);
    }

    private static List<SymbolEntry> Resolve(string text) =>
        SymbolResolver.Flatten(Tree).Where(e => SymbolResolver.Matches(e, SymbolResolver.ParseQuery(text)!, StringComparison.Ordinal)).ToList();

    [Theory]
    [InlineData("OptionsBuilder", 2)]                                // the type and its constructor
    [InlineData("Microsoft.Extensions.Options.OptionsBuilder", 1)]   // qualified: only the type
    [InlineData("OptionsBuilder`1", 1)]                              // constructor has arity 0
    [InlineData("OptionsBuilder.Configure", 2)]
    [InlineData("OptionsBuilder.Configure(Action<TOptions>)", 1)]
    [InlineData("OptionsBuilder.Configure<TDep>", 1)]
    [InlineData("OptionsBuilder`1.Configure", 2)]
    [InlineData("OptionsBuilder`2.Configure", 0)]
    [InlineData("Options.OptionsBuilder.Name", 1)]
    [InlineData("Other.OptionsBuilder.Name", 0)]
    [InlineData("OptionsBuilder+Inner.Run", 1)]
    [InlineData("Inner.Run()", 1)]
    [InlineData("Inner.Run(int)", 0)]
    [InlineData("optionsbuilder", 0)]                                // ordinal comparison here
    public void Matches(string query, int expected) => Assert.Equal(expected, Resolve(query).Count);

    [Fact]
    public void Matches_constructor_query_finds_member_named_after_type()
    {
        var query = SymbolResolver.ParseQuery("OptionsBuilder..ctor")!;
        var hits = SymbolResolver.Flatten(Tree).Where(e => SymbolResolver.Matches(e, query, StringComparison.Ordinal)).ToList();

        var ctor = Assert.Single(hits);
        Assert.Equal(6, ctor.Kind);
        Assert.Equal(13, ctor.SelectionRange.Start.Line);
    }
}

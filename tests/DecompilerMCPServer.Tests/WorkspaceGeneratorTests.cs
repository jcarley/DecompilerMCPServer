using System.Xml.Linq;
using DecompilerMCPServer.LanguageServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace DecompilerMCPServer.Tests;

public sealed class WorkspaceGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmcp-tests-" + Guid.NewGuid().ToString("N"));
    private string Scan => Path.Combine(_root, "scan");
    private string Output => Path.Combine(_root, "out");

    public WorkspaceGeneratorTests()
    {
        Directory.CreateDirectory(Scan);
        Directory.CreateDirectory(Output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string SdkProject(string assemblyName, string tfm, params (string Include, string? Hint)[] references) =>
        new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("AssemblyName", assemblyName),
                new XElement("TargetFramework", tfm)),
            new XElement("ItemGroup", references.Select(r =>
                new XElement("Reference", new XAttribute("Include", r.Include),
                    r.Hint is null ? null : new XElement("HintPath", r.Hint))))).ToString();

    /// <summary>Creates &lt;out&gt;/&lt;relative&gt;/ with a manifest and a csproj whose hint paths point into the scan dir.</summary>
    private void Export(string relative, string tfm, params string[] referencedRelatives)
    {
        var projectDir = Path.Combine(Output, relative);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, ".decompiled.json"), "{}");
        var name = Path.GetFileNameWithoutExtension(relative);
        var refs = referencedRelatives
            .Select(r => (Path.GetFileNameWithoutExtension(r), (string?)Path.GetRelativePath(projectDir, Path.Combine(Scan, r))))
            .Append(("System.Runtime", (string?)null))
            .ToArray();
        File.WriteAllText(Path.Combine(projectDir, name + ".csproj"), SdkProject(name, tfm, refs));
    }

    private WorkspaceGenerator CreateGenerator() =>
        new(new ScanOptions(Scan),
            new ProjectExportCache(new ExportOptions(Output), new DecompilerService(), NullLogger<ProjectExportCache>.Instance),
            NullLogger<WorkspaceGenerator>.Instance);

    [Fact]
    public void ParseProject_reads_sdk_style_tfm_and_resolves_hint_paths()
    {
        var projectFile = Path.Combine(Output, "A.dll", "A.csproj");
        var xml = SdkProject("A", "net10.0", ("B", @"..\..\scan\B.dll"), ("System.Runtime", null));

        var project = WorkspaceGenerator.ParseProject("A.dll", projectFile, Path.Combine(Scan, "A.dll"), xml);

        Assert.Equal("net10.0", project.TargetFramework);
        Assert.Equal(2, project.References.Count);
        Assert.Equal(Path.Combine(Scan, "B.dll"), project.References[0].HintPath);
        Assert.Null(project.References[1].HintPath);
    }

    [Theory]
    [InlineData("<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>", "net472")]
    [InlineData("<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>", "netstandard2.0")]
    [InlineData("<TargetFrameworkIdentifier>Silverlight</TargetFrameworkIdentifier><TargetFrameworkVersion>v5.0</TargetFrameworkVersion>", null)]
    [InlineData("", null)]
    public void ParseProject_target_framework_variants(string properties, string? expected)
    {
        var xml = $"<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup>{properties}</PropertyGroup></Project>";

        var project = WorkspaceGenerator.ParseProject("A.dll", Path.Combine(Output, "A.dll", "A.csproj"), "A.dll", xml);

        Assert.Equal(expected, project.TargetFramework);
    }

    private static WorkspaceProject Project(string name, string? tfm, params string[] references) =>
        new($"{name}.dll", $"/out/{name}.dll/{name}.csproj", $"/scan/{name}.dll", tfm,
            references.Select(r => new WorkspaceReference(r, $"/scan/{r}.dll")).ToList());

    [Fact]
    public void ComputeEdges_links_exported_references_only()
    {
        var a = Project("A", "net10.0", "B", "NotExported");
        var b = Project("B", "net10.0");

        var edges = WorkspaceGenerator.ComputeEdges([a, b], StringComparison.Ordinal);

        var edge = Assert.Single(edges);
        Assert.Same(a, edge.From);
        Assert.Same(b, edge.To);
        Assert.Equal("B", edge.ReferenceInclude);
    }

    [Theory]
    [InlineData("net10.0", "netstandard2.0", true)]
    [InlineData("net472", "netstandard2.0", true)]
    [InlineData("netstandard2.0", "net10.0", false)]
    [InlineData("net8.0", "net10.0", false)]
    [InlineData("net472", "net10.0", false)]
    [InlineData("net10.0", null, false)]
    [InlineData(null, "net10.0", false)]
    public void IsCompatible(string? consumer, string? dependency, bool expected) =>
        Assert.Equal(expected, WorkspaceGenerator.IsCompatible(consumer, dependency));

    [Fact]
    public void ComputeEdges_skips_incompatible_frameworks()
    {
        var edges = WorkspaceGenerator.ComputeEdges([Project("A", "netstandard2.0", "B"), Project("B", "net10.0")], StringComparison.Ordinal);

        Assert.Empty(edges);
    }

    [Fact]
    public void ComputeEdges_breaks_cycles_deterministically()
    {
        var a = Project("A", "net10.0", "B");
        var b = Project("B", "net10.0", "C");
        var c = Project("C", "net10.0", "A");
        var cycles = new List<(string, string)>();

        var edges = WorkspaceGenerator.ComputeEdges([c, b, a], StringComparison.Ordinal, (f, t) => cycles.Add((f.RelativePath, t.RelativePath)));

        Assert.Equal(["A.dll->B.dll", "B.dll->C.dll"], edges.Select(e => $"{e.From.RelativePath}->{e.To.RelativePath}"));
        Assert.Equal([("C.dll", "A.dll")], cycles);
    }

    [Fact]
    public void ComputeEdges_ignores_self_references_and_respects_path_case()
    {
        var a = new WorkspaceProject("A.dll", "/out/A.dll/A.csproj", "/scan/A.dll", "net10.0",
            [new WorkspaceReference("A", "/scan/A.dll"), new WorkspaceReference("B", "/SCAN/B.DLL")]);
        var b = Project("B", "net10.0");

        Assert.Empty(WorkspaceGenerator.ComputeEdges([a, b], StringComparison.Ordinal));
        Assert.Single(WorkspaceGenerator.ComputeEdges([a, b], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Escape_msbuild_special_characters() =>
        Assert.Equal("C:\\it%27s %2410%25 %40home%3Bx%3F%2A", WorkspaceGenerator.Escape("C:\\it's $10% @home;x?*"));

    [Fact]
    public void Generate_writes_solution_props_and_targets_then_is_stable()
    {
        Export("A.dll", "net10.0", "lib/B.dll");
        Export(Path.Combine("lib", "B.dll"), "netstandard2.0");
        Directory.CreateDirectory(Path.Combine(Output, ".lsp", "build", "Fake.dll")); // never treated as an export
        var generator = CreateGenerator();

        var first = generator.Generate();

        Assert.True(first.Changed);
        Assert.Equal(2, first.ProjectCount);
        Assert.Equal(1, first.ProjectReferenceCount);

        var solution = XDocument.Load(first.SolutionPath);
        Assert.Equal(["A.dll/A.csproj", "lib/B.dll/B.csproj"],
            solution.Root!.Elements("Project").Select(p => p.Attribute("Path")!.Value));

        var targets = XDocument.Load(Path.Combine(Output, WorkspaceGenerator.TargetsFileName));
        var group = Assert.Single(targets.Root!.Elements("ItemGroup"));
        Assert.Contains(Path.Combine(Output, "A.dll", "A.csproj"), group.Attribute("Condition")!.Value);
        Assert.Equal("B", group.Element("Reference")!.Attribute("Remove")!.Value);
        Assert.Equal(Path.Combine(Output, "lib", "B.dll", "B.csproj"), group.Element("ProjectReference")!.Attribute("Include")!.Value);

        var props = File.ReadAllText(Path.Combine(Output, WorkspaceGenerator.PropsFileName));
        Assert.Contains("<BaseIntermediateOutputPath>$(MSBuildThisFileDirectory).lsp\\build\\", props);

        var second = generator.Generate();
        Assert.False(second.Changed);
    }

    [Fact]
    public void Generate_with_no_exports_writes_an_empty_solution()
    {
        var result = CreateGenerator().Generate();

        Assert.Equal(0, result.ProjectCount);
        Assert.Empty(XDocument.Load(result.SolutionPath).Root!.Elements());
    }
}

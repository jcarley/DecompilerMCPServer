using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NuGet.Frameworks;

namespace DecompilerMCPServer.LanguageServer;

/// <summary>An exported project as the workspace generator sees it.</summary>
/// <param name="RelativePath">Output-relative (= scan-relative) path of the export, e.g. "sub/Foo.dll".</param>
/// <param name="AssemblyPath">Full path of the DLL in the scan directory the export came from.</param>
/// <param name="TargetFramework">Short TFM (net10.0, netstandard2.0, net472), or null when unknown.</param>
public sealed record WorkspaceProject(
    string RelativePath,
    string ProjectFile,
    string AssemblyPath,
    string? TargetFramework,
    IReadOnlyList<WorkspaceReference> References);

/// <summary>A &lt;Reference&gt; item; <paramref name="HintPath"/> is resolved to a full path when present.</summary>
public sealed record WorkspaceReference(string Include, string? HintPath);

/// <summary>A reference to another export, to be swapped for a ProjectReference.</summary>
public sealed record WorkspaceEdge(WorkspaceProject From, WorkspaceProject To, string ReferenceInclude);

public sealed record WorkspaceResult(string SolutionPath, bool Changed, int ProjectCount, int ProjectReferenceCount);

/// <summary>
/// Writes the files that turn the output directory into one solution for the Roslyn language server:
/// <list type="bullet">
/// <item><c>DecompiledWorkspace.slnx</c> listing every exported project;</item>
/// <item><c>Directory.Build.props</c> redirecting bin/obj to <c>.lsp/build/&lt;relative path&gt;/</c> so restores don't
/// litter the exports (and <c>search_code</c>);</item>
/// <item><c>Directory.Build.targets</c> swapping each &lt;Reference&gt; to another exported DLL for a ProjectReference,
/// so navigation crosses assemblies. ILSpy's .csproj files are left untouched.</item>
/// </list>
/// </summary>
public sealed class WorkspaceGenerator(ScanOptions scan, ProjectExportCache cache, ILogger<WorkspaceGenerator> logger)
{
    public const string SolutionFileName = "DecompiledWorkspace.slnx";
    public const string PropsFileName = "Directory.Build.props";
    public const string TargetsFileName = "Directory.Build.targets";

    internal PlatformInfo Platform { get; init; } = PlatformInfo.Current;

    public string SolutionPath => Path.Combine(cache.OutputDirectory, SolutionFileName);

    /// <summary>Regenerates the workspace files from the exports on disk; files whose content is unchanged aren't rewritten.</summary>
    public WorkspaceResult Generate()
    {
        var projects = new List<WorkspaceProject>();
        foreach (var relative in cache.FindExportedAssemblies().Order(StringComparer.Ordinal))
        {
            var projectDir = cache.ProjectDirectoryFor(relative);
            var projectFile = Directory.EnumerateFiles(projectDir, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault();
            if (projectFile is null)
                continue;
            try
            {
                projects.Add(ParseProject(relative, projectFile, Path.GetFullPath(Path.Combine(scan.ScanDirectory, relative)),
                    File.ReadAllText(projectFile)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                logger.LogWarning(ex, "Skipping {Project} in the language server workspace: could not read it", projectFile);
            }
        }

        var edges = ComputeEdges(projects, Platform.PathComparison, (from, to) =>
            logger.LogDebug("Not linking {From} -> {To}: it would create a reference cycle", from.RelativePath, to.RelativePath));

        bool changed = false;
        changed |= WriteIfChanged(SolutionPath, RenderSolution(projects, cache.OutputDirectory));
        changed |= WriteIfChanged(Path.Combine(cache.OutputDirectory, PropsFileName), RenderProps());
        changed |= WriteIfChanged(Path.Combine(cache.OutputDirectory, TargetsFileName), RenderTargets(edges));

        if (changed)
            logger.LogInformation("Language server workspace: {Projects} projects, {References} cross-assembly project references",
                projects.Count, edges.Count);
        return new WorkspaceResult(SolutionPath, changed, projects.Count, edges.Count);
    }

    /// <summary>Reads the target framework and references from an ILSpy-generated (SDK-style or legacy) project file.</summary>
    public static WorkspaceProject ParseProject(string relativePath, string projectFile, string assemblyPath, string xml)
    {
        var doc = XDocument.Parse(xml);
        var elements = doc.Descendants().ToList();
        string? Value(string name) => elements.FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var tfm = Value("TargetFramework") ?? Value("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
        if (tfm is null && Value("TargetFrameworkVersion") is { } legacy)
        {
            // Legacy projects: <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion> -> net472
            tfm = (Value("TargetFrameworkIdentifier") ?? ".NETFramework") == ".NETFramework"
                ? "net" + legacy.TrimStart('v', 'V').Replace(".", "")
                : null;
        }

        var projectDir = Path.GetDirectoryName(projectFile)!;
        var references = elements
            .Where(e => e.Name.LocalName == "Reference" && e.Attribute("Include") is not null)
            .Select(e =>
            {
                var hint = e.Elements().FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value.Trim();
                string? full = null;
                if (!string.IsNullOrEmpty(hint))
                {
                    try
                    {
                        full = Path.GetFullPath(Path.Combine(projectDir, hint.Replace('\\', Path.DirectorySeparatorChar)));
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        // unusable hint path: keep the reference, just don't link it
                    }
                }
                return new WorkspaceReference(e.Attribute("Include")!.Value, full);
            })
            .ToList();

        return new WorkspaceProject(relativePath, projectFile, assemblyPath, tfm, references);
    }

    /// <summary>
    /// References whose hint path is another export's DLL and whose target framework the referencing project can
    /// consume. Edges are added in a stable order and any edge that would close a cycle is dropped.
    /// </summary>
    public static IReadOnlyList<WorkspaceEdge> ComputeEdges(
        IReadOnlyList<WorkspaceProject> projects,
        StringComparison pathComparison,
        Action<WorkspaceProject, WorkspaceProject>? onCycle = null)
    {
        var comparer = pathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var byAssembly = new Dictionary<string, WorkspaceProject>(comparer);
        foreach (var project in projects)
            byAssembly.TryAdd(project.AssemblyPath, project);

        var adjacency = projects.ToDictionary(p => p, _ => new List<WorkspaceProject>());
        var edges = new List<WorkspaceEdge>();

        foreach (var from in projects.OrderBy(p => p.RelativePath, StringComparer.Ordinal))
        {
            foreach (var reference in from.References.OrderBy(r => r.Include, StringComparer.Ordinal))
            {
                if (reference.HintPath is null || !byAssembly.TryGetValue(reference.HintPath, out var to) || ReferenceEquals(to, from))
                    continue;
                if (!IsCompatible(from.TargetFramework, to.TargetFramework))
                    continue;
                if (Reaches(adjacency, to, from))
                {
                    onCycle?.Invoke(from, to);
                    continue;
                }
                adjacency[from].Add(to);
                edges.Add(new WorkspaceEdge(from, to, reference.Include));
            }
        }
        return edges;
    }

    /// <summary>Whether a project targeting <paramref name="consumer"/> can reference one targeting <paramref name="dependency"/>.</summary>
    public static bool IsCompatible(string? consumer, string? dependency)
    {
        if (consumer is null || dependency is null)
            return false;
        var from = NuGetFramework.Parse(consumer);
        var to = NuGetFramework.Parse(dependency);
        if (from.IsUnsupported || to.IsUnsupported)
            return false;
        return DefaultCompatibilityProvider.Instance.IsCompatible(from, to);
    }

    private static bool Reaches(Dictionary<WorkspaceProject, List<WorkspaceProject>> adjacency, WorkspaceProject start, WorkspaceProject target)
    {
        var stack = new Stack<WorkspaceProject>([start]);
        var seen = new HashSet<WorkspaceProject>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (ReferenceEquals(node, target))
                return true;
            if (!seen.Add(node))
                continue;
            foreach (var next in adjacency[node])
                stack.Push(next);
        }
        return false;
    }

    public static string RenderSolution(IReadOnlyList<WorkspaceProject> projects, string outputDirectory)
    {
        var solution = new XElement("Solution",
            projects
                .Select(p => Path.GetRelativePath(outputDirectory, p.ProjectFile).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .Select(path => new XElement("Project", new XAttribute("Path", path))));
        return Serialize(new XDocument(solution));
    }

    public static string RenderProps()
    {
        const string build = $"$(MSBuildThisFileDirectory){ProjectExportCache.LanguageServerFolderName}\\build\\$(_DecompiledProjectRelativeDir)\\";
        var project = new XElement("Project",
            new XComment(" Generated by DecompilerMCPServer for the Roslyn language server. Do not edit; it is rewritten. "),
            new XElement("PropertyGroup",
                new XElement("_DecompiledProjectRelativeDir",
                    "$([MSBuild]::MakeRelative('$(MSBuildThisFileDirectory)', '$(MSBuildProjectDirectory)'))"),
                new XElement("BaseIntermediateOutputPath", build + "obj\\"),
                new XElement("BaseOutputPath", build + "bin\\"),
                new XElement("GenerateDocumentationFile", "false"),
                new XElement("TreatWarningsAsErrors", "false")));
        return Serialize(new XDocument(project));
    }

    public static string RenderTargets(IReadOnlyList<WorkspaceEdge> edges)
    {
        var project = new XElement("Project",
            new XComment(" Generated by DecompilerMCPServer for the Roslyn language server. Do not edit; it is rewritten. "));
        foreach (var group in edges.GroupBy(e => e.From).OrderBy(g => g.Key.RelativePath, StringComparer.Ordinal))
        {
            var items = new XElement("ItemGroup",
                new XAttribute("Condition", $"'$(MSBuildProjectFullPath)' == '{Escape(group.Key.ProjectFile)}'"));
            foreach (var edge in group)
            {
                items.Add(new XElement("Reference", new XAttribute("Remove", Escape(edge.ReferenceInclude))));
                items.Add(new XElement("ProjectReference", new XAttribute("Include", Escape(edge.To.ProjectFile))));
            }
            project.Add(items);
        }
        return Serialize(new XDocument(project));
    }

    /// <summary>MSBuild-escapes characters with special meaning in item specs and conditions.</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '%' => "%25",
                '$' => "%24",
                '@' => "%40",
                '\'' => "%27",
                ';' => "%3B",
                '?' => "%3F",
                '*' => "%2A",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private static string Serialize(XDocument doc)
    {
        var sb = new StringBuilder();
        using (var writer = System.Xml.XmlWriter.Create(sb, new System.Xml.XmlWriterSettings
               {
                   Indent = true,
                   OmitXmlDeclaration = true,
                   NewLineChars = "\n",
               }))
            doc.Save(writer);
        return sb.Append('\n').ToString();
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        var temp = $"{path}.tmp-{Environment.ProcessId}";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
        return true;
    }
}

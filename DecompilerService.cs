using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

namespace DecompilerMCPServer;

/// <summary>Outcome of one whole-project export. Errors are non-fatal: ILSpy writes them into the files as comments.</summary>
public sealed record ProjectExportResult(int FileCount, IReadOnlyList<string> Errors);

public class DecompilerService
{
    /// <summary>ILSpy engine version. Recorded in export manifests so an engine upgrade re-exports everything.</summary>
    public static readonly string EngineVersion =
        typeof(WholeProjectDecompiler).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static DecompilerSettings CreateSettings() => new()
    {
        // Missing references shouldn't abort decompilation of an otherwise valid assembly
        ThrowOnAssemblyResolveErrors = false,
    };

    /// <summary>
    /// Exports the assembly as an ILSpy project into <paramref name="targetDirectory"/> (which must exist):
    /// one .cs file per top-level type under a folder named after its namespace, Properties/AssemblyInfo.cs,
    /// embedded resources, and a .csproj.
    /// </summary>
    public ProjectExportResult ExportProject(string assemblyPath, string targetDirectory, CancellationToken cancellationToken = default)
    {
        var settings = CreateSettings();
        using var module = new PEFile(assemblyPath);
        // Same resolver CSharpDecompiler(string, DecompilerSettings) builds for itself
        var resolver = new UniversalAssemblyResolver(assemblyPath, settings.ThrowOnAssemblyResolveErrors,
            module.DetectTargetFrameworkId(), module.DetectRuntimePack());

        // WholeProjectDecompiler carries per-export state (target directory, error list): one instance per export
        var decompiler = new WholeProjectDecompiler(settings, resolver, null, null, null);
        decompiler.DecompileProject(module, targetDirectory, cancellationToken);

        int fileCount = Directory.EnumerateFiles(targetDirectory, "*.cs", SearchOption.AllDirectories).Count();
        return new ProjectExportResult(fileCount, decompiler.Errors.Select(e => e.Message).ToList());
    }

    /// <summary>
    /// Path, relative to a project folder, of the file ILSpy writes a type to. Uses the same name-cleaning
    /// helpers as the exporter, so it matches exactly: generic arity is dropped (Widget`1 -> Widget.cs), and
    /// nested types (Outer+Inner) map to the outer type's file.
    /// </summary>
    public static string GetTypeFileRelativePath(string reflectionTypeName)
    {
        var (ns, name) = SplitTopLevelName(reflectionTypeName);
        var file = WholeProjectDecompiler.CleanUpFileName(name, ".cs");
        return ns.Length == 0 ? file : Path.Combine(WholeProjectDecompiler.CleanUpDirectoryName(ns), file);
    }

    /// <summary>Module version id: a fresh GUID on every compile, so it detects rebuilds that keep size and timestamp.</summary>
    public Guid ReadMvid(string assemblyPath)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("Not a .NET assembly (no CLI metadata).", assemblyPath);

        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }

    /// <summary>
    /// Metadata-only probe (no decompilation) for whether the assembly defines the given type.
    /// Matches the top-level part of the name; nested resolution is left to the exported file.
    /// </summary>
    public bool ContainsType(string assemblyPath, string reflectionTypeName)
    {
        var (ns, name) = SplitTopLevelName(reflectionTypeName);

        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata)
            return false;

        var md = pe.GetMetadataReader();
        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            if (td.IsNested)
                continue;
            if (md.StringComparer.Equals(td.Name, name) && md.StringComparer.Equals(td.Namespace, ns))
                return true;
        }
        return false;
    }

    /// <summary>"Ns.Outer+Inner" -> ("Ns", "Outer"); "Global" -> ("", "Global").</summary>
    private static (string Namespace, string Name) SplitTopLevelName(string reflectionTypeName)
    {
        var topLevel = reflectionTypeName.Split('+')[0];
        int lastDot = topLevel.LastIndexOf('.');
        return lastDot < 0 ? ("", topLevel) : (topLevel[..lastDot], topLevel[(lastDot + 1)..]);
    }
}

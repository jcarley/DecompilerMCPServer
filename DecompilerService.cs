using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace DecompilerMCPServer;

public class DecompilerService
{
    private static DecompilerSettings CreateSettings() => new()
    {
        // Missing references shouldn't abort decompilation of an otherwise valid assembly
        ThrowOnAssemblyResolveErrors = false,
    };

    public string DecompileAssembly(string assemblyPath, CancellationToken cancellationToken = default)
    {
        var decompiler = new CSharpDecompiler(assemblyPath, CreateSettings())
        {
            CancellationToken = cancellationToken,
        };
        return decompiler.DecompileWholeModuleAsString();
    }

    /// <summary>
    /// Decompiles a single type by reflection name, e.g. "Ns.Outer+Nested" or "Ns.List`1".
    /// </summary>
    public string DecompileType(string assemblyPath, string reflectionTypeName, CancellationToken cancellationToken = default)
    {
        var decompiler = new CSharpDecompiler(assemblyPath, CreateSettings())
        {
            CancellationToken = cancellationToken,
        };
        return decompiler.DecompileTypeAsString(new FullTypeName(reflectionTypeName));
    }

    /// <summary>
    /// Metadata-only probe (no decompilation) for whether the assembly defines the given type.
    /// Matches the top-level part of the name; nested resolution is left to DecompileType.
    /// </summary>
    public bool ContainsType(string assemblyPath, string reflectionTypeName)
    {
        var topLevel = reflectionTypeName.Split('+')[0];
        int lastDot = topLevel.LastIndexOf('.');
        var (ns, name) = lastDot < 0 ? ("", topLevel) : (topLevel[..lastDot], topLevel[(lastDot + 1)..]);

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
}

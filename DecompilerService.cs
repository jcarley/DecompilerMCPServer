using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace DecompilerMCPServer;

public class DecompilerService
{
    public string DecompileAssembly(string assemblyPath)
    {
        var settings = new DecompilerSettings
        {
            // Missing references shouldn't abort decompilation of an otherwise valid assembly
            ThrowOnAssemblyResolveErrors = false,
        };
        var decompiler = new CSharpDecompiler(assemblyPath, settings);
        return decompiler.DecompileWholeModuleAsString();
    }
}

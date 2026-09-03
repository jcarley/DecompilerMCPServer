using System.CommandLine;
using DecompilerMCPServer;

Option<FileInfo> dllOption = new("--dll", "-d")
{
    Description = "Path to the .NET DLL to decompile",
    Required = true,
};
dllOption.AcceptExistingOnly();

RootCommand rootCommand = new("Decompiles a .NET assembly to C# source code");
rootCommand.Options.Add(dllOption);

rootCommand.SetAction(parseResult =>
{
    FileInfo dll = parseResult.GetValue(dllOption)!;
    try
    {
        var service = new DecompilerService();
        Console.WriteLine(service.DecompileAssembly(dll.FullName));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: could not decompile '{dll.FullName}': {ex.Message}");
        return 1;
    }
});

return rootCommand.Parse(args).Invoke();

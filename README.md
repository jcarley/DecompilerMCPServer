# DecompilerMCPServer

An MCP (Model Context Protocol) server that decompiles .NET assemblies to C# source code. Point it at a folder, and it recursively finds every DLL in it, letting an MCP client (such as Claude Code or Claude Desktop) list assemblies, decompile whole assemblies or individual types, and regex-search across all of the decompiled code.

Built on [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) (the ILSpy engine) and the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), communicating over stdio.

## Install

Prebuilt self-contained binaries (no .NET runtime required) are published to [GitHub Releases](https://github.com/jcarley/DecompilerMCPServer/releases) for macOS (Apple Silicon and Intel) and Windows (x64 and arm64).

> The one-liners below require the repository to be public. While it is private, use `gh release download` and follow the manual steps instead.

**macOS** — installs to `~/.local/bin/DecompilerMCPServer`:

```bash
curl -fsSL https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.sh | bash
# or pin a version:
curl -fsSL https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.sh | bash -s -- v0.1.0
```

**Windows** — installs to `%LOCALAPPDATA%\Programs\DecompilerMCPServer` and adds it to your user PATH:

```powershell
irm https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.ps1 | iex
# or pin a version:
$env:DECOMPILER_MCP_VERSION = 'v0.1.0'; irm https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.ps1 | iex
```

Both scripts download the release archive, verify its SHA256 checksum against the release's `SHA256SUMS` file, install the binary, run a quick launch check, and print the exact registration command for your machine.

### Manual download

Grab the archive for your platform from the [releases page](https://github.com/jcarley/DecompilerMCPServer/releases) — artifacts are named `DecompilerMCPServer-v<version>-<platform>` with platforms `osx-arm64`, `osx-x64`, `win-x64`, `win-arm64`. Verify against `SHA256SUMS` (`shasum -a 256 -c` on macOS, `Get-FileHash` on Windows), then extract the single binary wherever you like.

The binaries are not code-signed. Browser downloads on macOS get quarantined — clear it with `xattr -d com.apple.quarantine <binary>`. On Windows, right-click → Properties → Unblock (or `Unblock-File`) if the file is flagged. The install scripts handle both automatically.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

The server binary lands at `bin/Release/net10.0/DecompilerMCPServer`. To produce the same self-contained single-file binary as the releases, publish with a runtime identifier:

```bash
dotnet publish -c Release -r osx-arm64   # or osx-x64, win-x64, win-arm64
# -> bin/Release/net10.0/osx-arm64/publish/DecompilerMCPServer
```

## Configuration

One setting is required: **`ScanDirectory`** — the folder scanned recursively for `*.dll` files. It binds through the standard .NET configuration stack, so either form works:

```bash
# Command-line argument
DecompilerMCPServer --ScanDirectory /path/to/dlls

# Environment variable
ScanDirectory=/path/to/dlls DecompilerMCPServer
```

If the setting is missing or the directory doesn't exist, the server prints an error to stderr and exits with code 1 instead of starting.

## Registering with an MCP client

The examples below use the macOS install-script path, `/Users/you/.local/bin/DecompilerMCPServer` — substitute your own username, or your build-from-source path. JSON config files do **not** expand `~`, so always spell out the home directory. Windows users: see the [Windows notes](#windows-notes) below.

### Claude Code

```bash
claude mcp add decompiler -- \
  /Users/you/.local/bin/DecompilerMCPServer \
  --ScanDirectory /path/to/dlls
```

Run `/mcp` in a session to confirm it's connected.

### Claude Desktop (or any JSON-configured client)

```json
{
  "mcpServers": {
    "decompiler": {
      "command": "/Users/you/.local/bin/DecompilerMCPServer",
      "args": ["--ScanDirectory", "/path/to/dlls"]
    }
  }
}
```

### GitHub Copilot in VS Code

Add a `.vscode/mcp.json` file to the workspace you want to use it from (or run **MCP: Add Server** from the command palette and choose *stdio*):

```json
{
  "servers": {
    "decompiler": {
      "type": "stdio",
      "command": "/Users/you/.local/bin/DecompilerMCPServer",
      "args": ["--ScanDirectory", "/path/to/dlls"]
    }
  }
}
```

Open Copilot Chat in **Agent** mode, click the tools icon, and enable the `decompiler` tools. To make the server available in every workspace, add the same entry to your user-level `mcp.json` instead (**MCP: Open User Configuration**).

### GitHub Copilot in Visual Studio

Create a `.mcp.json` file next to your solution file with the same `servers` shape as the VS Code config above. Visual Studio 2022 17.14+ picks it up; enable the tools from the Copilot Chat tool picker in Agent mode.

### GitHub Copilot in JetBrains Rider

The Copilot plugin reads MCP servers from `~/.config/github-copilot/intellij/mcp.json` (same `servers` shape as the VS Code config above). The easy way to get there: open Copilot Chat, switch to **Agent** mode, click the tools icon, and choose **Add MCP Tools… / Edit config** — Rider opens the file for you. Restart the chat after saving and enable the `decompiler` tools from the same tools picker.

```json
{
  "servers": {
    "decompiler": {
      "command": "/Users/you/.local/bin/DecompilerMCPServer",
      "args": ["--ScanDirectory", "/path/to/dlls"]
    }
  }
}
```

### Claude Code in JetBrains Rider

Install the **Claude Code** plugin from the JetBrains marketplace (Settings → Plugins). The plugin runs the same Claude Code CLI, so any server registered with `claude mcp add` (see above) is available in Rider automatically.

To share the server with everyone who opens this repo, register it at project scope instead — this writes a `.mcp.json` file at the project root that Claude Code picks up in any IDE or terminal:

```bash
claude mcp add decompiler --scope project -- \
  /Users/you/.local/bin/DecompilerMCPServer \
  --ScanDirectory /path/to/dlls
```

> Note: Visual Studio Copilot and project-scoped Claude Code both use a `.mcp.json` at the repo root, but with different top-level keys (`servers` vs `mcpServers`). Each client ignores the other's key, so one file can carry both blocks side by side.

### Windows notes

The same registrations work on Windows with three adjustments: the binary is `DecompilerMCPServer.exe`, Claude Desktop's config lives at `%APPDATA%\Claude\claude_desktop_config.json`, and backslashes must be doubled inside JSON strings:

```json
{
  "mcpServers": {
    "decompiler": {
      "command": "C:\\Users\\you\\AppData\\Local\\Programs\\DecompilerMCPServer\\DecompilerMCPServer.exe",
      "args": ["--ScanDirectory", "C:\\path\\to\\dlls"]
    }
  }
}
```

`claude mcp add` takes the path unescaped:

```powershell
claude mcp add decompiler -- "C:\Users\you\AppData\Local\Programs\DecompilerMCPServer\DecompilerMCPServer.exe" --ScanDirectory C:\path\to\dlls
```

---

Whatever the client: use absolute paths — MCP clients launch the server from an unpredictable working directory. And don't register it as `dotnet run ...`: MSBuild writes build output to stdout, which corrupts the JSON-RPC stream.

## Tools

### `list_assemblies`

Lists every DLL found recursively under the scan directory. The returned relative paths are the identifiers the other tools accept.

### `decompile_assembly`

Decompiles an entire assembly to C#.

| Parameter | Description |
|---|---|
| `assembly` | Relative path from `list_assemblies`, or a simple name like `MyLib` / `MyLib.dll` |

Output is capped at 200,000 characters; for large assemblies prefer `decompile_type` or `search_code`. If a simple name matches more than one DLL (common in build trees), the tool returns the candidate paths and asks for the relative path.

### `decompile_type`

Decompiles a single type — the context-friendly way to read code.

| Parameter | Description |
|---|---|
| `typeName` | Full type name, e.g. `MyNamespace.MyClass`. Nested types use `+` (`Outer+Inner`); generic types include arity (``Widget`1``) |
| `assembly` | Optional. If omitted, all scanned assemblies are probed (fast metadata check) and the owning assembly is reported in a `// From:` header |

### `search_code`

Regex search across the decompiled source of all scanned assemblies. Results are grouped by assembly with 1-based line numbers that align with `decompile_assembly` output for the same DLL.

| Parameter | Default | Description |
|---|---|---|
| `pattern` | — | .NET regular expression |
| `ignoreCase` | `true` | Case-insensitive matching |
| `contextLines` | `2` | Lines of context around each match (max 10) |
| `maxResults` | `100` | Total matching lines to return (max 500) |
| `assembly` | all | Restrict the search to one assembly |

## Behavior notes

- **Caching**: each DLL is decompiled at most once per server session and cached in memory, keyed by file path and last-write time — rebuilding a DLL refreshes it automatically. The first `search_code` across a large folder pays the full decompilation cost (a ~3 MB assembly takes tens of seconds); subsequent calls are instant.
- **Resilience**: a corrupt or unreadable DLL is skipped with a note in search results and reported as an error text elsewhere; bad input (invalid regex, unknown type) returns a descriptive `Error: ...` message. The server itself keeps running.
- **Regex safety**: patterns run with a 2-second match timeout; a timeout returns partial results with a warning.
- **Logging**: all logs go to stderr; stdout carries only JSON-RPC.

## Project layout

| File | Responsibility |
|---|---|
| `Program.cs` | Host bootstrap, config validation, DI, MCP registration |
| `DecompilerTools.cs` | The four MCP tools and their error handling |
| `DecompilerService.cs` | Pure decompilation (whole module, single type, type probe) |
| `AssemblyDiscoveryService.cs` | Recursive scan and identifier resolution |
| `DecompilationCache.cs` | Path + timestamp keyed cache of decompiled source |

## Releasing (maintainers)

Tag a version on `main` and push it — the [release workflow](.github/workflows/release.yml) publishes self-contained single-file binaries for all four platforms, generates `SHA256SUMS`, and creates the GitHub Release:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

Tags containing a `-` (e.g. `v0.1.0-rc.1`) are marked as prereleases — useful for dry-running the pipeline before a real release.

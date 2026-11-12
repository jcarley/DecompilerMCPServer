# DecompilerMCPServer

An MCP (Model Context Protocol) server that decompiles .NET assemblies to C# source code on disk. Point it at a folder, and it recursively finds every DLL in it, exports each one as an ILSpy project (one `.cs` file per type, grouped by namespace) into an output tree that mirrors the DLL folder layout, and lets an MCP client (such as Claude Code or Claude Desktop) list assemblies, locate the file for an assembly or a type, and regex-search across all of the decompiled code. Tool responses carry file paths and summaries (plus short line snippets for searches and navigation), not whole files, so the client reads only the files it needs.

Optionally (`--EnableLanguageServer true`), it also runs a [Roslyn language server](#language-server-optional) over the decompiled sources and adds semantic navigation: find symbols, go to definition across assemblies, find references and implementations, outline a file, and show signatures.

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

One setting is optional: **`OutputDirectory`** — where decompiled projects are written. Same two forms (`--OutputDirectory /path` or the `OutputDirectory` environment variable). When omitted it defaults to a per-user folder derived from the scan directory:

| Platform | Default |
|---|---|
| macOS | `~/Library/Application Support/DecompilerMCPServer/sources/<folder>-<hash>/` |
| Windows | `%LOCALAPPDATA%\DecompilerMCPServer\sources\<folder>-<hash>\` |

`<folder>` is the scan directory's last path segment and `<hash>` the first 8 hex digits of a SHA-256 of its full path, so two scan folders with the same name don't collide. `list_assemblies` prints the resolved location.

Another optional setting: **`WatchScanDirectory`** (`true` by default; `--WatchScanDirectory false` or the `WatchScanDirectory` environment variable). When on, the server exports every DLL in the background as soon as it starts, then watches the scan directory and its subfolders: new or changed DLLs are re-exported a couple of seconds after they settle, and the export of a deleted or renamed DLL is removed. Set it to `false` to export only on demand, when a tool first needs a DLL.

Inside the output directory, each DLL gets a project folder at its scan-relative path, **keeping the `.dll` suffix** so project folders never collide with namespace folders:

```
<OutputDirectory>/
  lib/Foo/Bar.dll/            <- from <ScanDirectory>/lib/Foo/Bar.dll
    Bar.csproj
    .decompiled.json          <- manifest used to detect a changed DLL
    Properties/AssemblyInfo.cs
    My.Namespace/Widget.cs    <- one file per top-level type; nested types live in their outer type's file
    My.Namespace.Sub/Other.cs
```

Rules:

- The server owns the output directory: it deletes and rewrites project folders whenever a DLL changes, and (with `WatchScanDirectory` on) deletes project folders whose DLL no longer exists in the scan directory. Don't keep anything else there, and don't share one custom `--OutputDirectory` between servers with different scan directories. To reset everything, delete the directory.
- The scan and output directories must not be the same or nested inside each other (the export writes embedded resources out as files, which can include DLLs). The server refuses to start otherwise.
- On Windows, deep namespace paths under the default location can approach the 260-character path limit; pass a short `--OutputDirectory` (or enable long paths) if exports fail with path errors.

### Language server (optional)

**`EnableLanguageServer`** (`false` by default; `--EnableLanguageServer true`) adds six tools backed by the official Roslyn language server (`Microsoft.CodeAnalysis.LanguageServer`, the engine behind the VS Code C# extension). Nothing starts until the first of those tools is called; that call exports every DLL in the scan directory, builds a solution over the exports, and waits for the language server to load it, so expect it to take from a few seconds to a few minutes depending on the folder. Later calls are fast, and the workspace reloads automatically when exports change.

Prerequisites: a **.NET 10 SDK** on the machine (the release binaries are self-contained, but the language server is a .NET 10 app that loads projects through MSBuild). Get it from [dot.net/download](https://dot.net/download).

The server reuses what is already installed before downloading anything:

1. **dotnet**: `--DotnetPath`, then `DOTNET_ROOT`, then `PATH`, then the default install locations. It needs a .NET 10+ runtime and SDK.
2. **Roslyn language server**, first compatible match:
   1. `--LanguageServerPath`
   2. a copy this server downloaded earlier (`%LOCALAPPDATA%\DecompilerMCPServer\roslyn-ls\` on Windows, `~/Library/Application Support/DecompilerMCPServer/roslyn-ls/` on macOS)
   3. the copy bundled with the C# extension of VS Code, VS Code Insiders, VS Code Server, Cursor or Windsurf
   4. a `roslyn-language-server` dotnet tool

   A candidate counts as compatible when its runtime requirements are met by the dotnet found in step 1, and it really starts. If a candidate fails to start, the next one is tried.
3. **Download**: if nothing usable is found, the pinned `roslyn-language-server.<platform>` package (version 5.11.0, about 70 MB) is downloaded once from nuget.org. It is checked against a SHA256 built into the server and cached in the folder above. The language server is MIT-licensed and isn't redistributed with this server.

stderr logs which server was chosen (`Using Roslyn language server ... from ...`).

| Setting | Default | Description |
|---|---|---|
| `EnableLanguageServer` | `false` | Register the language-server tools |
| `LanguageServerPath` | discovered | Path to `Microsoft.CodeAnalysis.LanguageServer.dll`; skips discovery and download |
| `LanguageServerVersion` | `5.11.0` | Use exactly this `roslyn-language-server` version (from the cache, downloading it if needed; versions other than the pinned one are not checksum-verified) and skip editor installs |
| `DotnetPath` | discovered | The `dotnet` executable to run the language server with |
| `LanguageServerLoadTimeoutSeconds` | `300` | How long a call waits for the workspace to load before answering with what is loaded |

All of them accept the same `--Name value` / environment-variable forms as the other settings.

Inside the output directory, the language server adds:
- `DecompiledWorkspace.slnx`
- `Directory.Build.props` and `Directory.Build.targets`. These redirect build output to `.lsp/`, and turn references between exported assemblies into project references, so "go to definition" crosses assemblies.
- the `.lsp/` folder itself.

ILSpy's project files are not modified. Types from assemblies outside the scan directory (the framework, missing dependencies) resolve to Roslyn's own metadata view and are reported as external. Expect diagnostics noise in decompiled code that references assemblies the folder doesn't contain; the tools don't surface diagnostics.

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

Exports an entire assembly to disk as an ILSpy project and returns the project folder path, the `.csproj` path, file and error counts, the namespace folders (at most 50 listed, then `+N more`), and whether an existing export was reused.

| Parameter | Description |
|---|---|
| `assembly` | Relative path from `list_assemblies`, or a simple name like `MyLib` / `MyLib.dll` |

If a simple name matches more than one DLL (common in build trees), the tool returns the candidate paths and asks for the relative path.

### `decompile_type`

Returns the path of the file holding a single type, exporting its assembly first if needed — the context-friendly way to read code.

| Parameter | Description |
|---|---|
| `typeName` | Full type name, e.g. `MyNamespace.MyClass`. Nested types use `+` (`Outer+Inner`) and resolve to the outer type's file; generic types include arity (``Widget`1``) and share a file with same-named non-generic types |
| `assembly` | Optional. If omitted, all scanned assemblies are probed (fast metadata check) and the owning assembly is reported |

Compiler-generated and hidden types (`<Module>`, `<PrivateImplementationDetails>`, embedded attribute shims) are not exported and return an error pointing at the project folder.

### `decompile_all`

Exports every scanned assembly (skipping ones already exported and unchanged) and returns totals plus a list of any DLLs that failed. Takes no parameters. Sends MCP progress notifications per assembly for clients that supply a progress token; on a large folder this can run for minutes.

### `search_code`

Regex search across the decompiled source of all scanned assemblies, exporting any assembly not yet on disk first. Results are grouped by source file (absolute path) with 1-based line numbers into that file.

| Parameter | Default | Description |
|---|---|---|
| `pattern` | — | .NET regular expression |
| `ignoreCase` | `true` | Case-insensitive matching |
| `contextLines` | `2` | Lines of context around each match (max 10) |
| `maxResults` | `100` | Total matching lines to return (max 500) |
| `assembly` | all | Restrict the search to one assembly |

### Language server tools

Registered only with `--EnableLanguageServer true` (see [Language server](#language-server-optional)). Line and column numbers are 1-based. Results list absolute file paths with the matching line, like `search_code`.

The navigation tools (`go_to_definition`, `find_references`, `find_implementations`, `hover`) identify the symbol in one of two ways.

The first is **by name**, in `symbol`:
- `Ns.Type`
- `Type.Member`
- ``Ns.Type`2.Method(int, string)``, which uses generic arity and a parameter list to pick an overload. `Widget<T>` works as well as ``Widget`1``.
- `Ns.Type..ctor` for constructors.

A partial name matches any declaration whose qualified name ends with it. If several match, the tool lists them and asks for a more specific name.

The second is **by position**: `file` + `line` + `column`, or `file` + `line` + `symbol` to locate the name on that line. `file` is a decompiled `.cs` path under the output directory, absolute or relative to it.

#### `find_symbol`

Fuzzy search for declarations by name across all assemblies.

| Parameter | Default | Description |
|---|---|---|
| `query` | — | Name or partial name |
| `kind` | any | `class`, `interface`, `struct`, `enum`, `type`, `method`, `property`, `field`, `constant`, `event`, `enummember`, `operator` or `namespace`. Constructors are reported as methods |
| `assembly` | all | Restrict to one assembly |
| `maxResults` | `50` | Max 200 |

#### `get_document_symbols`

Outline of one file: namespaces, types and members with signatures and line numbers (at most 2000 entries). Pass `file`, or `type` to outline the file declaring it.

#### `go_to_definition`

Where the symbol is declared, with `contextLines` (default 2, max 10) of surrounding source. Works across assemblies in the scan directory. Anything else is reported as `(external) [Assembly] <declaration>`.

#### `find_references`

All references, grouped by file.

| Parameter | Default | Description |
|---|---|---|
| `includeDeclaration` | `false` | Include the declaration itself |
| `contextLines` | `0` | Max 10 |
| `maxResults` | `100` | Max 500 |

#### `find_implementations`

Implementations of an interface or interface member, and overrides of abstract or virtual members. Takes `contextLines` (default 0) and `maxResults` (default 100, max 500).

#### `hover`

The symbol's full signature, including constraints, and its XML documentation when the assembly shipped any.

## Behavior notes

- **Exports persist**: each project folder carries a `.decompiled.json` manifest recording the DLL's size, timestamp, module version id (MVID) and the ILSpy engine version. While all four match, every tool reuses the export — across server restarts too. Rebuilding a DLL (or upgrading the server's decompiler) triggers a fresh export into a temp folder that is swapped into place, so a crash never leaves a half-written project. A large folder takes a while to export the first time (a ~3 MB assembly takes tens of seconds); with `WatchScanDirectory` on this happens in the background at startup, and a tool that needs a DLL still being exported waits for that export to finish. Subsequent calls are instant.
- **Background sync** (`WatchScanDirectory`, on by default): exports run one at a time on a background thread so the server stays responsive. A DLL that is still locked or being written is retried with backoff; a missed or overflowing file-watcher event triggers a full rescan. Progress and failures are logged to stderr.
- **Shared output**: several server processes (say, Claude Code and Claude Desktop registered on the same folder) can share an output directory; a lock file beside each project folder makes them take turns, so a DLL is exported once.
- **Resilience**: a corrupt or unreadable DLL is skipped with a note in search results, listed under "Failed assemblies" by `decompile_all`, and reported as an error text elsewhere; bad input (invalid regex, unknown type) returns a descriptive `Error: ...` message. The server itself keeps running.
- **Regex safety**: patterns run with a 2-second match timeout; a timeout returns partial results with a warning.
- **Logging**: all logs go to stderr; stdout carries only JSON-RPC.

## Project layout

| File | Responsibility |
|---|---|
| `Program.cs` | Host bootstrap, config validation, DI, MCP registration |
| `DecompilerTools.cs` | The five core MCP tools and their error handling |
| `DecompilerService.cs` | ILSpy wrapper: whole-project export, type-to-file mapping, metadata probes |
| `AssemblyDiscoveryService.cs` | Recursive scan and identifier resolution |
| `ProjectExportCache.cs` | Manifest-backed export cache: freshness check, cross-process lock, temp-and-swap writes |
| `ScanDirectoryMonitor.cs` | Background startup export and file watching that keeps exports in sync with the scan directory |
| `LanguageServerTools.cs` | The six optional language-server tools |
| `LanguageServer/` | Roslyn language server hosting: dotnet/server discovery, download fallback, workspace generation, LSP client, symbol resolution |
| `tests/DecompilerMCPServer.Tests/` | xUnit tests (`dotnet test tests/DecompilerMCPServer.Tests`) |

## Releasing (maintainers)

Tag a version on `main` and push it — the [release workflow](.github/workflows/release.yml) publishes self-contained single-file binaries for all four platforms, generates `SHA256SUMS`, and creates the GitHub Release:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

Tags containing a `-` (e.g. `v0.1.0-rc.1`) are marked as prereleases — useful for dry-running the pipeline before a real release.

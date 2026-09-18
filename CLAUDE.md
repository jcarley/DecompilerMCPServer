# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A stdio MCP server (ModelContextProtocol 2.2.0, .NET 10) that decompiles .NET assemblies to C# with the ILSpy engine (`ICSharpCode.Decompiler`). Each DLL is exported **to disk** as an ILSpy project (one `.cs` per top-level type under a namespace folder, plus a `.csproj`) into an output tree mirroring the scan directory; tools return paths and summaries, never source text. It exposes five tools over a recursively scanned DLL directory: `list_assemblies`, `decompile_assembly`, `decompile_type`, `decompile_all`, `search_code`. Single project, no `src/` folder, solution file is `DecompilerMCPServer.slnx`.

## Commands

```bash
dotnet build -c Release                 # -> bin/Release/net10.0/DecompilerMCPServer
dotnet publish -c Release -r osx-arm64  # self-contained single file (also osx-x64, win-x64, win-arm64)
```

Run the built binary directly, never via `dotnet run` (MSBuild output on stdout corrupts the JSON-RPC stream):

```bash
./bin/Debug/net10.0/DecompilerMCPServer --ScanDirectory /path/to/dlls [--OutputDirectory /path/to/sources]
```

There is no test project and no CI on push/PR. Smoke testing:

- Bare run with no args must exit 1 and print a message containing `ScanDirectory` to stderr. Both install scripts (`scripts/install.sh`, `scripts/install.ps1`) rely on this as their post-install check. Do not change that exit code or message substring.
- End-to-end: pipe JSON-RPC lines (`initialize` → `notifications/initialized` → `tools/call`) into the binary. Keep stdin open until the response is read (e.g. `( printf '...'; sleep 1 ) | ./bin/...`), otherwise the server shuts down before flushing. The project's own `bin/Debug/net10.0` is a convenient corpus (contains a duplicated simple name for ambiguity tests). Always pass `--OutputDirectory` pointing at a scratch folder when testing, or the default under `~/Library/Application Support/DecompilerMCPServer/sources/` fills up. A large DLL like `ICSharpCode.Decompiler.dll` takes ~40s to export the first time (874 files); the on-disk manifest makes repeats instant, even across restarts. Staleness can be forced with `touch` on the DLL or by editing the `Mvid` in the project folder's `.decompiled.json`.
- The scan and output directories must not be the same or nested; the server exits 1 for that too, but only **after** the `ScanDirectory` check, so the bare-run contract above is unaffected.
- Register with a real client: `claude mcp add decompiler -- <abs path>/DecompilerMCPServer --ScanDirectory <dir>`, then `/mcp`.

Release: `git tag v0.1.0 && git push origin v0.1.0`. Tags containing `-` are published as prereleases. The workflow cross-compiles all four RIDs on Ubuntu and passes the tag as `-p:Version`, which overrides `<Version>` in the csproj; keep the two in sync by hand.

## Architecture

`Program.cs` validates `ScanDirectory` (from `--ScanDirectory` arg or the bare `ScanDirectory` env var, via the standard config stack) **before** starting the transport, then resolves `OutputDirectory` (same stack; default `<LocalApplicationData>/DecompilerMCPServer/sources/<leaf>-<8 hex of sha256(lowercased scan path)>`), rejects same/nested directories, creates it, registers the singletons and `WithTools<DecompilerTools>()`. Console logging is routed entirely to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`).

- `DecompilerTools.cs` — the tool surface. `[McpServerToolType]` class with a primary constructor injecting the three services below. `decompile_all` takes an `IProgress<ProgressNotificationValue>` parameter that the SDK binds automatically (no-op when the client sent no progress token); it is not part of the tool schema.
- `AssemblyDiscoveryService.cs` — recursive `*.dll` scan (fresh walk every call, nothing cached) and identifier resolution: exact relative path first, then simple name (`.dll` appended if missing). Multiple matches return an "Ambiguous" error rather than picking one. Also defines the `ScanOptions` and `ExportOptions` records.
- `ProjectExportCache.cs` — ensures a DLL's project exists at `<OutputDirectory>/<scan-relative path incl. .dll>/` and returns a `ProjectExport` record. In memory: `ConcurrentDictionary<string, Entry>` keyed by full path (`StringComparer.Ordinal` on purpose) with a `Lazy<ProjectExport>` (`ExecutionAndPublication`, exception cached until the DLL's size/timestamp stamp changes, only cancellation evicts). On disk: a `.decompiled.json` manifest in the project folder (assembly path, mtime ticks, length, MVID, ILSpy version, format version, counts); when all match, the export is reused without touching ILSpy. Otherwise it takes a cross-process lock (`<projectDir>.lock`, `FileShare.None`, 250 ms retry), deletes stale `.tmp-*` siblings, exports into `<projectDir>.tmp-<pid>`, writes the manifest last, deletes the old folder and `Directory.Move`s the temp into place. `DeleteOwnedDirectory` refuses to delete anything not under `OutputDirectory` and not named `*.dll` / `*.dll.tmp-*`; keep that guard.
- `DecompilerService.cs` — ILSpy wrapper. `ExportProject` builds a `PEFile` + `UniversalAssemblyResolver` (same construction `CSharpDecompiler(string, settings)` uses) and a **fresh** `WholeProjectDecompiler` per call (it holds per-export state). `ThrowOnAssemblyResolveErrors = false` is deliberate: user folders have no guaranteed reference closure, so unresolved types in output are expected. Settings are otherwise ILSpy defaults, so `UseNestedDirectoriesForNamespaces` is off and a namespace is one folder. `GetTypeFileRelativePath` reproduces ILSpy's file naming with its own public `CleanUpDirectoryName`/`CleanUpFileName` helpers (generic arity dropped, nested types -> outer file); change the settings and this mapping must follow. `ContainsType` is a metadata-only probe (`System.Reflection.Metadata`) that matches top-level type names only. `ReadMvid` is the metadata-only module version id used by the manifest.

Which tools export: `decompile_assembly`, `decompile_type` (once the owning assembly is known), `search_code` (every target, so the first unfiltered search pays a full export of every DLL) and `decompile_all`. `decompile_type` without `assembly` first re-reads metadata of every DLL on each call to find the owner. ILSpy skips compiler-generated/hidden types (`<Module>` etc.), so `decompile_type` checks `File.Exists`, then tries a `<name>.*.cs` companion (XAML/resx-backed types), then returns an error.

## Conventions

- **stdout is sacred.** Never `Console.WriteLine` to stdout; logs go to stderr, startup errors use `Console.Error.WriteLine`.
- Tools return `string`. User-facing failures are returned as text starting with `"Error: "` plus a remediation hint, not thrown. `OperationCanceledException` is always re-caught and rethrown before the generic `catch (Exception)`.
- Per-item resilience: in multi-assembly operations a bad DLL is skipped (`(skipped: ...)` in `search_code`, silently in the `decompile_type` probe); the server never dies from bad input.
- Adding a tool: add a method to `DecompilerTools` with `[McpServerTool(Name = "snake_case")]`, a `[Description]` on the method and on every parameter, and `CancellationToken cancellationToken = default` last. No registration change needed. A new tool *class* needs its own `.WithTools<T>()` in `Program.cs`.
- `search_code` clamps `contextLines` to 0–10 and `maxResults` to 1–500, and uses a 2-second regex match timeout; it reads the exported `*.cs` files (sorted ordinal) and labels each group with the absolute file path. `decompile_assembly` lists at most 50 namespace folders. The README tool tables mirror these numbers; update both together.
- Tool annotations: `list_assemblies` is `ReadOnly`; the exporting tools are `Idempotent = true, Destructive = false` (the attribute defaults `Destructive` to true).
- The manifest `FormatVersion` constant in `ProjectExportCache` must be bumped whenever the on-disk layout or naming changes, so old exports are regenerated.
- Nullable and implicit usings are enabled.

## Release artifacts and installers

- Artifact name `DecompilerMCPServer-v{version}-{rid}.{tar.gz|zip}` is hardcoded in three places that must move together: `.github/workflows/release.yml`, `scripts/install.sh`, `scripts/install.ps1`. `SHA256SUMS` holds bare filenames; `install.sh` greps one line and pipes it to `shasum -a 256 -c -` (macOS has no `sha256sum`).
- `install.sh` is macOS only and refuses to run elsewhere; there is no Linux artifact. Adding one means touching the RID loop in the workflow.
- Binaries are unsigned. `install.sh` strips quarantine and applies an ad-hoc `codesign`; `install.ps1` runs `Unblock-File`. `install.ps1` is written for Windows PowerShell 5.1 compatibility.
- Windows is cross-compiled from Ubuntu and has no CI; treat it as untested end-to-end. The install one-liners require the repo to be public.
- Both installers accept a version via positional arg / `-Version`, then `DECOMPILER_MCP_VERSION`, else latest. `DECOMPILER_MCP_BASE_URL` points them at a local server (a `file://` or `python -m http.server` base) for testing.

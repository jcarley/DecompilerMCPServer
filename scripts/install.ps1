<#
.SYNOPSIS
DecompilerMCPServer installer for Windows.

.DESCRIPTION
Downloads the self-contained binary from GitHub Releases, verifies its SHA256
checksum, installs it to a per-user location, and adds it to the user PATH.
Compatible with Windows PowerShell 5.1 and later.

Usage:
  irm https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.ps1 | iex

Environment overrides (useful with irm | iex, which cannot pass parameters):
  DECOMPILER_MCP_VERSION      release tag to install, e.g. v0.1.0 (default: latest)
  DECOMPILER_MCP_INSTALL_DIR  install directory (default: %LOCALAPPDATA%\Programs\DecompilerMCPServer)
  DECOMPILER_MCP_BASE_URL     alternate artifact base URL (for testing)

.PARAMETER Version
Release tag to install (e.g. v0.1.0). Defaults to the latest release.
#>
param([string]$Version)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is very slow with the progress bar in 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072  # TLS 1.2

$Repo = 'jcarley/DecompilerMCPServer'
$Binary = 'DecompilerMCPServer.exe'

function Get-ReleaseAsset([string]$Url, [string]$OutFile) {
    try {
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    } catch {
        throw "Download failed: $Url"
    }
}

# Arch: OSArchitecture is reliable even under x64 emulation on arm64
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
switch ($arch) {
    'Arm64' { $rid = 'win-arm64' }
    'X64'   { $rid = 'win-x64' }
    default { throw "Unsupported architecture: $arch" }
}

# Version: parameter > env var > latest release
$tag = $Version
if (-not $tag) { $tag = $env:DECOMPILER_MCP_VERSION }
if (-not $tag) {
    try {
        $tag = (Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest").tag_name
    } catch {
        throw "Could not determine the latest release (GitHub API unreachable or rate-limited). Set `$env:DECOMPILER_MCP_VERSION = 'v0.1.0' and retry."
    }
}
$plainVersion = $tag -replace '^v', ''

$name = "DecompilerMCPServer-v$plainVersion-$rid"
$baseUrl = $env:DECOMPILER_MCP_BASE_URL
if (-not $baseUrl) { $baseUrl = "https://github.com/$Repo/releases/download/$tag" }

$installDir = $env:DECOMPILER_MCP_INSTALL_DIR
if (-not $installDir) { $installDir = Join-Path $env:LOCALAPPDATA 'Programs\DecompilerMCPServer' }

$tmp = Join-Path $env:TEMP "decompiler-mcp-install-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    Write-Host "Downloading $name.zip ($tag, $rid)..."
    $zipPath = Join-Path $tmp "$name.zip"
    try {
        Get-ReleaseAsset "$baseUrl/$name.zip" $zipPath
    } catch {
        if ($rid -eq 'win-arm64') {
            Write-Warning 'Download failed for win-arm64. The win-x64 build runs under emulation as a fallback — download it from the releases page and follow the manual install steps in the README.'
        }
        throw
    }
    Get-ReleaseAsset "$baseUrl/SHA256SUMS" (Join-Path $tmp 'SHA256SUMS')

    Write-Host 'Verifying checksum...'
    $sumsLine = Get-Content (Join-Path $tmp 'SHA256SUMS') | Where-Object { $_ -match [regex]::Escape("$name.zip") }
    if (-not $sumsLine) { throw "SHA256SUMS has no entry for $name.zip" }
    $expected = ($sumsLine -split '\s+')[0]
    $actual = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash
    if ($actual -ne $expected) { throw "Checksum verification failed for $name.zip (expected $expected, got $actual)" }

    # Strip Mark-of-the-Web before extraction so the exe isn't flagged
    Unblock-File $zipPath -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Expand-Archive -Path $zipPath -DestinationPath $installDir -Force
    $exePath = Join-Path $installDir $Binary
    if (-not (Test-Path $exePath)) { throw "Archive did not contain the expected $Binary" }
    Unblock-File $exePath -ErrorAction SilentlyContinue

    # The server exits 1 with a ScanDirectory message on stderr when run bare — proves the binary runs.
    # 2>&1 on a native command throws under ErrorActionPreference=Stop in 5.1, so relax it briefly.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $smoke = (& $exePath 2>&1) -join "`n"
    $smokeExit = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    if ($smokeExit -ne 1 -or $smoke -notmatch 'ScanDirectory') {
        throw "Installed binary failed its smoke check (exit $smokeExit): $smoke"
    }

    # Add to the user PATH if not already present
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $userPath) { $userPath = '' }
    if (($userPath -split ';') -notcontains $installDir) {
        $newPath = if ($userPath) { $userPath.TrimEnd(';') + ';' + $installDir } else { $installDir }
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        $env:Path += ";$installDir"
        Write-Host "Added $installDir to your user PATH (open a new terminal to pick it up)."
    }

    $jsonPath = $exePath -replace '\\', '\\'
    Write-Host ''
    Write-Host "Installed DecompilerMCPServer $tag to $exePath"
    Write-Host ''
    Write-Host 'Next, register it with your MCP client (pick a folder of DLLs to scan):'
    Write-Host ''
    Write-Host '  Claude Code:'
    Write-Host "    claude mcp add decompiler -- `"$exePath`" --ScanDirectory C:\path\to\dlls"
    Write-Host ''
    Write-Host '  Claude Desktop (%APPDATA%\Claude\claude_desktop_config.json — note the doubled backslashes):'
    Write-Host '    {'
    Write-Host '      "mcpServers": {'
    Write-Host '        "decompiler": {'
    Write-Host "          `"command`": `"$jsonPath`","
    Write-Host '          "args": ["--ScanDirectory", "C:\\path\\to\\dlls"]'
    Write-Host '        }'
    Write-Host '      }'
    Write-Host '    }'
    Write-Host ''
    Write-Host 'See the README for other clients (VS Code, Visual Studio, Rider).'
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

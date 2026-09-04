#!/usr/bin/env bash
#
# DecompilerMCPServer installer for macOS.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/jcarley/DecompilerMCPServer/main/scripts/install.sh | bash -s -- v0.1.0
#
# Environment overrides:
#   DECOMPILER_MCP_VERSION      release tag to install (same as the positional arg)
#   DECOMPILER_MCP_INSTALL_DIR  install directory (default: ~/.local/bin)
#   DECOMPILER_MCP_BASE_URL     alternate artifact base URL (for testing against a local server)

set -euo pipefail

REPO="jcarley/DecompilerMCPServer"
BINARY="DecompilerMCPServer"

err() { printf 'Error: %s\n' "$1" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || err "this script is for macOS. On Windows use install.ps1; on other platforms build from source (see README)."

# Arch detection; a Rosetta shell reports x86_64, so check proc_translated too
arch="$(uname -m)"
if [ "$arch" = "arm64" ] || [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
  rid="osx-arm64"
elif [ "$arch" = "x86_64" ]; then
  rid="osx-x64"
else
  err "unsupported architecture: $arch"
fi

# Version: positional arg > env var > latest release
tag="${1:-${DECOMPILER_MCP_VERSION:-}}"
if [ -z "$tag" ]; then
  tag="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null \
    | grep '"tag_name"' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')" || true
  [ -n "$tag" ] || err "could not determine the latest release (GitHub API unreachable or rate-limited). Pass a version explicitly, e.g.: ... | bash -s -- v0.1.0"
fi
version="${tag#v}"

name="${BINARY}-v${version}-${rid}"
base_url="${DECOMPILER_MCP_BASE_URL:-https://github.com/$REPO/releases/download/$tag}"
install_dir="${DECOMPILER_MCP_INSTALL_DIR:-$HOME/.local/bin}"
dest="$install_dir/$BINARY"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $name.tar.gz ($tag, $rid)..."
curl -fsSL -o "$tmp/$name.tar.gz" "$base_url/$name.tar.gz" \
  || err "download failed: $base_url/$name.tar.gz"
curl -fsSL -o "$tmp/SHA256SUMS" "$base_url/SHA256SUMS" \
  || err "download failed: $base_url/SHA256SUMS"

echo "Verifying checksum..."
(cd "$tmp" && grep " ${name}.tar.gz\$" SHA256SUMS | shasum -a 256 -c - >/dev/null) \
  || err "checksum verification failed for $name.tar.gz"

tar -C "$tmp" -xzf "$tmp/$name.tar.gz"
[ -f "$tmp/$BINARY" ] || err "archive did not contain the expected $BINARY binary"

mkdir -p "$install_dir"
mv -f "$tmp/$BINARY" "$dest"
chmod +x "$dest"

# Unsigned distribution: clear any quarantine xattr and ensure an ad-hoc signature
xattr -d com.apple.quarantine "$dest" 2>/dev/null || true
codesign --verify "$dest" 2>/dev/null || codesign --force --sign - "$dest" 2>/dev/null || true

# The server exits 1 with a ScanDirectory message when run bare — proves the binary runs
set +e
smoke="$("$dest" 2>&1)"
status=$?
set -e
if [ "$status" -ne 1 ] || ! printf '%s' "$smoke" | grep -q "ScanDirectory"; then
  err "installed binary failed its smoke check (exit $status): $smoke"
fi

echo
echo "Installed $BINARY $tag to $dest"
case ":$PATH:" in
  *":$install_dir:"*) ;;
  *) echo "Note: $install_dir is not on your PATH. MCP clients use the absolute path below, so this only matters for running it by hand." ;;
esac
echo
echo "Next, register it with your MCP client (pick a folder of DLLs to scan):"
echo
echo "  Claude Code:"
echo "    claude mcp add decompiler -- $dest --ScanDirectory /path/to/dlls"
echo
echo "  Claude Desktop (claude_desktop_config.json):"
echo "    {"
echo "      \"mcpServers\": {"
echo "        \"decompiler\": {"
echo "          \"command\": \"$dest\","
echo "          \"args\": [\"--ScanDirectory\", \"/path/to/dlls\"]"
echo "        }"
echo "      }"
echo "    }"
echo
echo "See the README for other clients (VS Code, Visual Studio, Rider)."

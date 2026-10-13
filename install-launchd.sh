#!/usr/bin/env bash
#
# install-launchd.sh — macOS equivalent of install-service.cmd
#
# Builds/publishes ShaderToE131 and installs it as a per-user launchd
# LaunchAgent (auto-start at login, restart on crash).
#
# A LaunchAgent (not a LaunchDaemon) is used deliberately: the app creates an
# OpenGL context via Silk.NET/GLFW, which requires the logged-in user's
# WindowServer session.
#
# Usage:
#   ./install-launchd.sh            install (or update) and start the service
#   launchctl bootout gui/$(id -u)/com.phobicadz.shadertoe131   stop & remove
#   Logs: logs/shadertoe131.out.log, logs/shadertoe131.err.log

set -euo pipefail

LABEL="com.phobicadz.shadertoe131"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUBLISH_DIR="$REPO_DIR/publish"
LOG_DIR="$REPO_DIR/logs"
PLIST_DIR="$HOME/Library/LaunchAgents"
PLIST="$PLIST_DIR/$LABEL.plist"

# Same runtime parameters as install-service.cmd on Windows.
# Note: audio capture is Windows-only in this codebase and gracefully
# self-disables on macOS, so --audio is harmless but has no effect there.
PARAMS=(
  --no-preview
  --string
  --string-ip ledstring.local
  --string-universe 1
  --audio
  --loopback 0
  --fps 30
  --mqtt 192.168.2.11
)

# --- Locate the dotnet host -------------------------------------------------
DOTNET="$(command -v dotnet || true)"
if [ -z "$DOTNET" ]; then
  for candidate in /usr/local/share/dotnet/dotnet /opt/homebrew/bin/dotnet; do
    if [ -x "$candidate" ]; then DOTNET="$candidate"; break; fi
  done
fi
if [ -z "$DOTNET" ]; then
  echo "ERROR: .NET SDK not found on PATH. Install from https://dotnet.microsoft.com/download" >&2
  exit 1
fi
echo "Using dotnet: $DOTNET"

# --- Publish ----------------------------------------------------------------
echo "Publishing ShaderToE131 to $PUBLISH_DIR ..."
# Use the resolved host, not a bare `dotnet`: this script is often run from a
# non-interactive shell whose PATH does not include the SDK directory.
"$DOTNET" publish "$REPO_DIR/ShaderToE131/ShaderToE131.csproj" -c Release -o "$PUBLISH_DIR" --nologo

mkdir -p "$LOG_DIR" "$PLIST_DIR"

# --- Write the LaunchAgent plist ----------------------------------------------
{
  cat <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$DOTNET</string>
        <string>$PUBLISH_DIR/ShaderToE131.dll</string>
EOF
  for p in "${PARAMS[@]}"; do
    echo "        <string>$p</string>"
  done
  cat <<EOF
    </array>
    <key>WorkingDirectory</key>
    <string>$REPO_DIR</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$LOG_DIR/shadertoe131.out.log</string>
    <key>StandardErrorPath</key>
    <string>$LOG_DIR/shadertoe131.err.log</string>
    <key>ThrottleInterval</key>
    <integer>10</integer>
</dict>
</plist>
EOF
} > "$PLIST"
echo "Wrote $PLIST"

# --- (Re)load the agent -------------------------------------------------------
GUI_DOMAIN="gui/$(id -u)"
if launchctl bootout "$GUI_DOMAIN/$LABEL" 2>/dev/null; then
  echo "Stopped previous instance."
fi
launchctl bootstrap "$GUI_DOMAIN" "$PLIST"
echo "Installed and started $LABEL."

# --- Quick verification -------------------------------------------------------
sleep 3
if launchctl print "$GUI_DOMAIN/$LABEL" >/dev/null 2>&1; then
  echo "Service is registered with launchd."
  tail -n 5 "$LOG_DIR/shadertoe131.out.log" 2>/dev/null || true
fi
echo
echo "Web control panel: http://localhost:8080/"
echo "Uninstall with:    launchctl bootout $GUI_DOMAIN/\$LABEL && rm '$PLIST'"

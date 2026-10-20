#!/usr/bin/env bash
# Installs the .NET SDK the repo's global.json pins into $HOME/.dotnet — the one install path for every AWS box
# (SkyPilot setup blocks, the persistent runner's provision.sh, and run-gate.sh before each gate run).
#
# global.json pins a feature band (sdk.version 10.0.400 + rollForward latestPatch = latest 10.0.4xx). dotnet-install
# ignores rollForward and its --jsonfile installs the exact sdk.version, i.e. the band's OLDEST patch, so the band
# channel (10.0.4xx) is derived here instead. Idempotent: dotnet-install exits early when that version is present.
#
# Usage:  install-dotnet.sh [CHANNEL]   CHANNEL overrides the band (e.g. 11.0.1xx while trying a retarget)
set -euo pipefail

GLOBAL_JSON="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/global.json"
BAND="$(sed -nE 's/.*"version"[[:space:]]*:[[:space:]]*"([0-9]+\.[0-9]+\.[0-9])[0-9]{2}".*/\1xx/p' "$GLOBAL_JSON" | head -1)"
CHANNEL="${1:-$BAND}"
if [ -z "$CHANNEL" ]; then
  echo "install-dotnet.sh: no sdk.version in $GLOBAL_JSON" >&2
  exit 1
fi

curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel "$CHANNEL" --install-dir "$HOME/.dotnet"

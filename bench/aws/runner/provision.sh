#!/usr/bin/env bash
# Typhon CI persistent self-hosted runner — one-time toolchain provisioner (#466).
#
# Installs the pinned toolchain onto the box's EBS root (survives stop/start). Version-parameterized so a
# minor bump is `provision.sh 10.0.3xx 22` in place; a MAJOR bump (.NET 10 -> 11) should instead rebuild the
# box from a fresh image running this script (Q1: the major retarget invalidates bin/obj anyway, so a clean
# wipe is free and keeps the box drift-free). Idempotent — safe to re-run.
#
# Usage:  ./provision.sh [DOTNET_CHANNEL] [NODE_MAJOR]
#   DOTNET_CHANNEL  default 10.0   (e.g. 11.0 for the .NET 11 move; keep in sync with the repo's global.json)
#   NODE_MAJOR      default 22     (only the Workbench client needs Node)
set -euo pipefail

DOTNET_CHANNEL="${1:-10.0}"
NODE_MAJOR="${2:-22}"

echo "== apt prereqs =="
sudo apt-get update -qq
# git: checkout; fio: P0c/ongoing disk checks; jq/unzip/curl: tooling; build-essential: native deps some tests pull.
sudo apt-get install -y -qq git fio jq unzip curl ca-certificates build-essential

echo "== AWS CLI v2 (idle-stop.sh calls it to self-stop via the instance IAM role) =="
if ! command -v aws >/dev/null 2>&1; then
  curl -sSL "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip" -o /tmp/awscliv2.zip
  ( cd /tmp && unzip -q -o awscliv2.zip && sudo ./aws/install )
fi

echo "== .NET SDK (channel $DOTNET_CHANNEL) =="
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel "$DOTNET_CHANNEL" --install-dir "$HOME/.dotnet"
# Make the SDK available to every login shell + the runner's job environment.
if ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
  cat >> "$HOME/.bashrc" <<'EOF'
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
EOF
fi

echo "== Node (major $NODE_MAJOR — Workbench client) =="
curl -fsSL "https://deb.nodesource.com/setup_${NODE_MAJOR}.x" | sudo -E bash -
sudo apt-get install -y -qq nodejs

echo "== installed versions =="
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
echo "dotnet $(dotnet --version) | node $(node --version) | $(git --version)"
echo "provision.sh: toolchain installed on the EBS root. Next: register the runner + enable typhon-nvme.service (see README)."

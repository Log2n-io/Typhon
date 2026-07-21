#!/usr/bin/env bash
# Runs on every boot (systemd oneshot, ordered before the runner service). The c6id instance-store NVMe is
# EPHEMERAL — wiped on stop — so re-create + mount it each start. The gate points test DBs at /nvme via
# TMPDIR=/nvme; measured ~10-40x faster than the gp3 root on the fsync-bound WAL/durability fixtures (#466).
# Runs as root under systemd (no sudo). No-op on a c6i (no instance store) so the same image works on both.
set -euo pipefail

RUNNER_USER="${RUNNER_USER:-ubuntu}"
DEV=$(lsblk -dno NAME,MODEL | grep -i 'Instance Storage' | head -1 | awk '{print $1}')
if [ -z "$DEV" ]; then echo "no instance-store NVMe (c6i?) — test DBs will fall back to the EBS root"; exit 0; fi
if mountpoint -q /nvme; then echo "/nvme already mounted"; exit 0; fi

mkfs.ext4 -F -q "/dev/$DEV"           # lazy-init: seconds, not a full format
mkdir -p /nvme && mount "/dev/$DEV" /nvme && chown "$RUNNER_USER" /nvme
echo "/nvme ready (/dev/$DEV)"

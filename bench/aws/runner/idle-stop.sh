#!/usr/bin/env bash
# Level-triggered idle self-stop — the PRIMARY stopper (#466). Run every minute by typhon-idle-stop.timer.
# Stops THIS instance once the GitHub runner has had no active job for IDLE_MINUTES consecutive checks.
#
# Why this is queue-aware (vs a Lambda edge on workflow_job:completed): the runner is ONLINE the whole time
# the box is up, so any queued `typhon-c6id` job is picked up immediately as a new Runner.Worker, resetting
# the counter. "no worker for N min" therefore means the queue is DRAINED, not merely that one job finished —
# it cannot misfire between two back-to-back jobs. (Concurrency section of the design.)
set -euo pipefail

IDLE_MINUTES="${IDLE_MINUTES:-10}"
REGION="${AWS_REGION:-eu-west-1}"
STATE=/run/typhon-idle-count      # tmpfs — resets to absent on boot, so a fresh box starts its idle count at 0

# A running job means a Runner.Worker process exists (the runner forks one per job). The [R] bracket keeps
# pgrep's own cmdline ("pgrep -f [R]unner.Worker") from matching the pattern — belt-and-braces vs a self-match.
if pgrep -f '[R]unner\.Worker' >/dev/null 2>&1; then
  echo 0 > "$STATE"
  echo "runner busy — idle counter reset"
  exit 0
fi

n=$(( $(cat "$STATE" 2>/dev/null || echo 0) + 1 ))
echo "$n" > "$STATE"
echo "idle tick ${n}/${IDLE_MINUTES}"
[ "$n" -lt "$IDLE_MINUTES" ] && exit 0

# IMDSv2 (token-required) — works whether or not the box enforces it.
TOKEN=$(curl -sS -X PUT "http://169.254.169.254/latest/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 60")
IID=$(curl -sS -H "X-aws-ec2-metadata-token: $TOKEN" http://169.254.169.254/latest/meta-data/instance-id)
echo "idle ${IDLE_MINUTES} min — stopping ${IID}"
aws ec2 stop-instances --instance-ids "$IID" --region "$REGION"

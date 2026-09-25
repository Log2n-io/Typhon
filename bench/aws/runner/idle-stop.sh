#!/usr/bin/env bash
# Level-triggered idle self-stop — the PRIMARY stopper (#466). Run every minute by typhon-idle-stop.timer.
# Stops THIS instance once the GitHub runner has had no active job for IDLE_MINUTES consecutive checks.
#
# Why this is queue-aware (vs a Lambda edge on workflow_job:completed): the runner is ONLINE the whole time
# the box is up, so any queued `typhon-c6id` job is picked up immediately as a new Runner.Worker, resetting
# the counter. "no worker for N min" therefore means the queue is DRAINED, not merely that one job finished —
# it cannot misfire between two back-to-back jobs. (Concurrency section of the design.)
set -euo pipefail

IDLE_MINUTES="${IDLE_MINUTES:-2}"
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

# Take the runner OFFLINE before stopping the box (#1048). Stopping the instance with the runner online left a window of
# ~15-20 s — the stop call, then the OS shutdown — in which GitHub could hand a new job to a box that was going down: it
# was killed mid-checkout. With the runner offline, a job queued from here on stays `queued` on GitHub, and the
# power-toggle Lambda waits out the stop and starts the box again. The only window left is between the check below and
# the service stop, a few milliseconds.
RUNNER_UNIT=$(systemctl list-units --type=service --all --plain --no-legend 'actions.runner.*' | awk '{print $1}' | head -n1)
if pgrep -f '[R]unner\.Worker' >/dev/null 2>&1; then
  echo 0 > "$STATE"
  echo "a job started while deciding — idle counter reset"
  exit 0
fi
if [ -n "$RUNNER_UNIT" ]; then
  systemctl stop "$RUNNER_UNIT"
  echo "runner ${RUNNER_UNIT} stopped: new jobs stay queued"
fi

# IMDSv2 (token-required) — works whether or not the box enforces it.
TOKEN=$(curl -sS -X PUT "http://169.254.169.254/latest/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 60")
IID=$(curl -sS -H "X-aws-ec2-metadata-token: $TOKEN" http://169.254.169.254/latest/meta-data/instance-id)
echo "idle ${IDLE_MINUTES} min — stopping ${IID}"
if ! aws ec2 stop-instances --instance-ids "$IID" --region "$REGION"; then
  # The box stays up: bring the runner back so queued jobs are served, and count idle from zero again.
  [ -n "$RUNNER_UNIT" ] && systemctl start "$RUNNER_UNIT"
  echo 0 > "$STATE"
  echo "stop-instances failed — runner restarted" >&2
  exit 1
fi

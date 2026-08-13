#!/usr/bin/env bash
# Run what the merge gate runs, before pushing.
#
# WHY THIS EXISTS
# ---------------
# Of the 17 merge-gate failures in the week to 2026-08-12, NINE were policy jobs on free GitHub runners — rule
# coverage, invariants, closing-keywords — every one of which is a Python script already sitting in this repo. They
# cost nothing to run and were never run. Of the rest, five were one known bug reaching the gate through
# `Typhon.Workbench.Tests`, a suite CLAUDE.md's local workflow never mentions, so nobody had run it locally either.
#
# The gate is not doing anything exotic. It runs the same suites this machine can run; the only reasons a failure gets
# discovered on a billed c6id instead of here are that the local workflow stops at the engine project, and that it
# stops at Debug.
#
# WHAT PARITY MEANS HERE
# ----------------------
#   * BOTH test projects, not just the engine one.
#   * Release, not Debug — the gate builds Release, and this repo has behaviour that differs between them.
#   * The gate's own category exclusion, read from shard.py so there is one definition of "excluded" (#774).
#
# NOT reproduced: the 8-way sharding and the serial Sensitive pass. Those change CONTENTION, which is a real source of
# gate-only failures, and reproducing them faithfully means reproducing the box. This script is the cheap 90%; when a
# test fails only on the gate and passes here, contention is the first suspect and `bench/aws/shard.py run` is the
# tool for it.
#
# USAGE
#   scripts/pre-push.sh              # policy checks + both suites (Release)
#   scripts/pre-push.sh --policy     # policy checks only (seconds — the nine free failures)
#   scripts/pre-push.sh --no-build   # skip the Release builds (suites must already be built)
#
# Install as a real hook if you want it automatic (opt-in — it is not wired up by default):
#   ln -s ../../scripts/pre-push.sh .git/hooks/pre-push
set -uo pipefail

cd "$(git rev-parse --show-toplevel)" || exit 1

POLICY_ONLY=0
BUILD=1
for arg in "$@"; do
  case "$arg" in
    --policy) POLICY_ONLY=1 ;;
    --no-build) BUILD=0 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

RC=0
FAILED=()

step() {
  local name="$1"; shift
  printf '\n\033[1m── %s\033[0m\n' "$name"
  if "$@"; then
    printf '\033[32m   PASS\033[0m  %s\n' "$name"
  else
    printf '\033[31m   FAIL\033[0m  %s\n' "$name"
    RC=1
    FAILED+=("$name")
  fi
}

# ── policy checks — these are the gate's free jobs, and they are seconds each ────────────────────────────────────────
step "rule scopes (gate: invariants)"          python3 scripts/check-rule-scopes.py --quiet
step "rule coverage (gate: rule-coverage)"     python3 scripts/audit-rule-coverage.py
step "test suppressions (gate: invariants)"    python3 scripts/lint-test-suppressions.py
step "runsettings (gate: invariants)"          python3 scripts/check-runsettings.py
step "doc links (gate: doc-accuracy)"          python3 scripts/check-doc-links.py
step "script unit tests (gate: invariants)"    python3 -m unittest discover -s scripts/tests

if [ "$POLICY_ONLY" -eq 1 ]; then
  if [ "$RC" -eq 0 ]; then
    printf '\n\033[32mpolicy checks passed\033[0m — run without --policy for the test suites.\n'
  else
    printf '\n\033[31mFAILED:\033[0m %s\n' "${FAILED[*]}"
  fi
  exit "$RC"
fi

# ── test suites — Release, both projects, gate's exclusion filter ────────────────────────────────────────────────────
FILTER="$(python3 bench/aws/shard.py filter)" || { echo "could not read the gate filter from shard.py" >&2; exit 1; }
echo ""
echo "gate category filter: ${FILTER}"

ENGINE=test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj
WORKBENCH=test/Typhon.Workbench.Tests/Typhon.Workbench.Tests.csproj

if [ "$BUILD" -eq 1 ]; then
  step "build engine tests (Release)"    dotnet build "$ENGINE" -c Release
  # SkipClientBuild mirrors the gate: the SPA toolchain is a separate job there and takes minutes here.
  step "build workbench tests (Release)" dotnet build "$WORKBENCH" -c Release -p:SkipClientBuild=true
fi

step "engine suite (Release)"    dotnet test "$ENGINE"    -c Release --no-build --filter "$FILTER"
step "workbench suite (Release)" dotnet test "$WORKBENCH" -c Release --no-build --filter "$FILTER"

echo ""
if [ "$RC" -eq 0 ]; then
  printf '\033[32mAll gate-equivalent checks passed.\033[0m\n'
  echo "Not covered locally: shard contention + the serial Sensitive pass. If the gate still reddens on a test that"
  echo "passes here, that difference is the first thing to suspect — see bench/aws/shard.py run."
else
  printf '\033[31mFAILED:\033[0m %s\n' "${FAILED[*]}"
  echo "Each line names the gate job it corresponds to, so a failure here is the same failure CI would have reported."
fi
exit "$RC"

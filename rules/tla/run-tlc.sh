#!/usr/bin/env bash
# run-tlc.sh — run the TLA+ model checker (TLC) on a spec in this directory.
#
# Usage:
#   ./run-tlc.sh <SpecName>            # model-check with <SpecName>.cfg (expects GREEN)
#   ./run-tlc.sh <SpecName> <suffix>   # model-check with <SpecName>-<suffix>.cfg
#   ./run-tlc.sh <SpecName> mutant --expect-violation
#                                      # genuineness: expects TLC to REPORT a violation (inverts exit code)
#
# Downloads tla2tools v1.7.4 into ./.tools/ on first run. v1.7.4 is the last TLC release that runs on Java 8
# (v1.8+ requires Java 11); it is pinned so local (Java 8) and CI behave identically.
#
# Exit code: 0 iff TLC printed "No error has been found" (or, with --expect-violation, iff it printed a violation).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS="$HERE/.tools"
JAR="$TOOLS/tla2tools.jar"
JAR_URL="https://github.com/tlaplus/tlaplus/releases/download/v1.7.4/tla2tools.jar"

spec="${1:?usage: run-tlc.sh <SpecName> [cfgSuffix] [--expect-violation]}"
suffix=""
expect_violation=0
shift
for arg in "$@"; do
  case "$arg" in
    --expect-violation) expect_violation=1 ;;
    *) suffix="$arg" ;;
  esac
done

if [[ -n "$suffix" ]]; then
  cfg="$HERE/${spec}-${suffix}.cfg"
else
  cfg="$HERE/${spec}.cfg"
fi

[[ -f "$HERE/${spec}.tla" ]] || { echo "ERROR: $HERE/${spec}.tla not found" >&2; exit 2; }
[[ -f "$cfg" ]] || { echo "ERROR: $cfg not found" >&2; exit 2; }

if [[ ! -f "$JAR" ]]; then
  echo "tla2tools.jar absent → downloading v1.7.4 ..."
  mkdir -p "$TOOLS"
  curl -fL --retry 3 -o "$JAR" "$JAR_URL"
fi

out="$TOOLS/${spec}${suffix:+-$suffix}.out"
cd "$HERE"
# tlc2.TLC = the model checker. -workers auto uses all cores; -config selects the model bounds + invariants.
set +e
java -XX:+UseParallelGC -cp "$JAR" tlc2.TLC -workers auto -config "$cfg" "${spec}.tla" 2>&1 | tee "$out"
set -e

tlc_rc="${PIPESTATUS[0]}"

# Three-state classification. The distinction matters: a mutant run is asserted to FAIL, so anything that merely
# "is not green" -- a parse error, an undeclared constant, an OOM, a failed jar download -- used to satisfy
# --expect-violation and exit 0. That let a genuineness check pass without the invariant ever being evaluated.
# Require positive evidence that TLC actually evaluated an invariant and found it violated.
if grep -q "No error has been found" "$out"; then
  result="GREEN"
elif grep -qE "(Invariant .* is violated|Temporal property .* is violated|Error: Invariant)" "$out"; then
  result="VIOLATION"
else
  result="ERROR"
fi

echo "----"
if [[ "$expect_violation" -eq 1 ]]; then
  if [[ "$result" == "VIOLATION" ]]; then
    echo "TLC: $spec ($suffix) — VIOLATION as expected (genuineness check passed: the invariant bites)."
    exit 0
  fi
  if [[ "$result" == "GREEN" ]]; then
    echo "TLC: $spec ($suffix) — expected a violation but got GREEN (genuineness check FAILED: the mutant does not break the invariant)." >&2
    exit 1
  fi
  echo "TLC: $spec ($suffix) — expected a violation but TLC never evaluated the invariant (rc=$tlc_rc): parse error, bad config, or OOM. TOOLING failure, not a proof. See $out." >&2
  exit 1
fi

if [[ "$result" == "GREEN" ]]; then
  echo "TLC: $spec — GREEN (no error)."
  exit 0
fi
if [[ "$result" == "VIOLATION" ]]; then
  echo "TLC: $spec — INVARIANT VIOLATED. See $out." >&2
  exit 1
fi
echo "TLC: $spec — TLC did not complete (rc=$tlc_rc): parse error, bad config, or OOM. See $out." >&2
exit 1

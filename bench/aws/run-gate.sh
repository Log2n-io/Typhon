#!/usr/bin/env bash
# Correctness-gate execution for the persistent self-hosted runner (#466). Mirrors the test-running core of
# bench/aws/ci.sky.yaml's `run:` block, minus the SkyPilot/S3 specifics — the *caller* (merge-gate.yml's
# self-hosted path) owns checkout + artifact upload. Writes summary.md + *.trx + logs to $OUT; exits non-zero
# if any suite failed.
#
# Assumes: toolchain already installed (provision.sh), a warm bin/obj in the worktree (incremental build),
# and TMPDIR=/nvme exported by the caller (test DBs + WAL on the local NVMe — ~10-40x faster, measured 2026-07-21).
#
# Env: RUN_ENGINE, RUN_WORKBENCH ("true"/"false"), RUN_ID, OUT (results dir), REPO (repo root).
#
# NOTE: kept as a separate copy from ci.sky.yaml for now so the *working* SkyPilot gate stays untouched during
# cutover. Once the self-hosted path is proven, dedupe by pointing ci.sky.yaml's run: block at this script too.
set -uo pipefail
: "${RUN_ENGINE:=false}" "${RUN_WORKBENCH:=false}" "${RUN_ID:=local}"
: "${OUT:?OUT (results dir) required}" "${REPO:?REPO (repo root) required}"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
mkdir -p "$OUT"; cd "$REPO"
RC=0; S="$OUT/summary.md"
{ echo "# Typhon CI Gate — run ${RUN_ID}"; echo ""; } > "$S"

# Build only the test projects the gate runs (warm bin/obj → incremental). shard.py + the workbench run --no-build.
if [ "$RUN_ENGINE" = "true" ]; then
  dotnet build test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Release
fi
if [ "$RUN_WORKBENCH" = "true" ]; then
  dotnet build test/Typhon.Workbench.Tests/Typhon.Workbench.Tests.csproj -c Release -p:SkipClientBuild=true
fi

# --- Engine: sharded suite (K serial worker=1 processes; shard 0 is a catch-all) ---
if [ "$RUN_ENGINE" = "true" ]; then
  echo "## Engine tests" >> "$S"
  if SHARD_REPO="$REPO" SHARD_CONFIG=Release python3 bench/aws/shard.py run --results-dir "$OUT" 2>&1 | tee "$OUT/engine.log"
  then echo "- ✅ engine suite passed (sharded: K×workers=1 + serial Sensitive pass)" >> "$S"
  else RC=1; echo "- ❌ engine suite FAILED (sharded)" >> "$S"
  fi
  echo "" >> "$S"
fi

# --- Workbench: TS toolchain (single orval + vite build, #466) + .NET tests ---
if [ "$RUN_WORKBENCH" = "true" ]; then
  echo "## Workbench" >> "$S"
  (
    cd "$REPO/tools/Typhon.Workbench/ClientApp"
    npm ci --no-audit --no-fund && \
    npm run generate-types && \
    npx tsc --noEmit && \
    npm run lint && \
    npx vitest run && \
    npx vite build
  ) 2>&1 | tee "$OUT/workbench-client.log"
  if [ "${PIPESTATUS[0]}" -eq 0 ]; then echo "- ✅ client toolchain (typecheck/lint/vitest/build) passed" >> "$S"
  else RC=1; echo "- ❌ client toolchain FAILED" >> "$S"
  fi
  # The retry net the engine suite has had since #405, applied here too (#774). It was engine-only because that
  # suite is sharded through shard.py and this one is a bare `dotnet test` — so an identical timing flake was
  # absorbed there and hard-red here, decided by which directory the test file sits in rather than by the test.
  # `retry` re-runs only the recorded failures, alone, and still fails the gate when they fail every attempt.
  # Honour the SAME category exclusion the sharded engine suite applies. This ran unfiltered until #774, so a
  # [Category("Quarantine")] test in this project would have executed and reddened the gate anyway — quarantine was a
  # property of which script launched the suite rather than of the repository. The string comes from shard.py so there
  # is one definition of "excluded from the gate" and not two that drift apart.
  WB_FILTER="$(python3 bench/aws/shard.py filter)"
  echo "[workbench] filter: ${WB_FILTER}"
  if dotnet test test/Typhon.Workbench.Tests/Typhon.Workbench.Tests.csproj -c Release --no-build \
       --filter "$WB_FILTER" \
       --logger "trx;LogFileName=workbench.trx" --results-directory "$OUT" 2>&1 | tee "$OUT/workbench-dotnet.log"
  then echo "- ✅ workbench .NET tests passed" >> "$S"
  elif SHARD_REPO="$REPO" SHARD_CONFIG=Release python3 bench/aws/shard.py retry \
         --trx "$OUT/workbench.trx" \
         --project test/Typhon.Workbench.Tests/Typhon.Workbench.Tests.csproj \
         --results-dir "$OUT" --label W 2>&1 | tee -a "$OUT/workbench-dotnet.log"
  then echo "- ✅ workbench .NET tests passed (after retry — see the FLAKED list)" >> "$S"
  else RC=1; echo "- ❌ workbench .NET tests FAILED" >> "$S"
  fi
  echo "" >> "$S"
fi

echo "run-gate.sh finished with RC=$RC"
exit $RC

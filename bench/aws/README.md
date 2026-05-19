# AntHill AWS Benchmark — Operator Runbook

SkyPilot automation that runs `AntHill.Harness` on ephemeral AWS bare-metal
instances and retrieves the `.typhon-trace` output via S3.

Full design and one-time operator setup: see
`design/Infrastructure/skypilot-aws-automation.md` in the `typhon-claude` docs repo.

## Prerequisites (one-time)

WSL + AWS CLI + SkyPilot installed, a dedicated `skypilot-bot` IAM user, the
`typhon-traces` S3 bucket in `eu-west-1`, and a 128-vCPU service-quota increase.
Work through the design doc's **Operator Setup Guide**, then confirm:

```bash
sky check aws        # must report: AWS: enabled
```

## Files

| File | Purpose |
|------|---------|
| `anthill-bench.sky.yaml`  | Production task — `m6idn.metal`, the 500K-ant scenario |
| `anthill-dryrun.sky.yaml` | Dry-run task — `c5d.metal`, a small trace-on scenario |
| `run.sh`                  | Thin wrapper — launches the production task with `--down` |

## 1. Dry run — do this first

Validates provisioning, NVMe mount, build, harness run, and trace→S3 retrieval
cheaply on `c5d.metal` before spending on a production run:

```bash
sky launch bench/aws/anthill-dryrun.sky.yaml --down -y
```

Then verify:

```bash
aws s3 ls s3://typhon-traces/   # expect the dry-run .typhon-trace + JSON report
sky status                      # expect no active cluster
```

A green dry run is the gate to the production run.

## 2. Production run

```bash
./bench/aws/run.sh
# equivalently: sky launch bench/aws/anthill-bench.sky.yaml --down -y
```

Runs the 500K-ant scenario across the 16/32/64/128 worker sweep. Each run emits
a `.typhon-trace`; all are copied to `s3://typhon-traces/` with the JSON report.
`--down` terminates the instance when finished.

## 3. Retrieve traces

```bash
aws s3 ls s3://typhon-traces/
aws s3 cp s3://typhon-traces/<file>.typhon-trace .
```

Open the trace in the Typhon Workbench profiler.

## If an instance gets stuck

`--down` should always terminate the instance. If `sky status` still shows a
cluster, force it down:

```bash
sky down <cluster-name>
```

Cross-check the AWS EC2 console (`eu-west-1`) — a leaked `m6idn.metal` bills
~$10/hr. The account budget alarm is the backstop.

## Notes

- SkyPilot `workdir: .` syncs the local repo — the benchmark runs whatever is
  checked out. Launch from the Typhon repo root.
- Bare-metal boot takes 5–7 min, teardown 10–20 min — all billed per-second.
- The harness exits non-zero if any run fails; the task propagates that, so a
  failed benchmark surfaces as a failed SkyPilot task.

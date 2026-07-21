# Typhon CI — persistent self-hosted runner (#466)

Stands up the **persistent, stop-when-idle `c6id.8xlarge`** that replaces the ephemeral SkyPilot provision
for the correctness gate. Design + rationale + P0 measurements:
[`ci-gate-persistent-runner.md`](https://github.com/log2n-io/typhon-claude/blob/main/design/Infrastructure/ci-gate-persistent-runner.md).

**P0 (measured 2026-07-21)** froze the shape: **`c6id` + local NVMe** (WAL fixtures 3 s on NVMe vs 32 s on gp3),
**~19 s resume**, warm `bin/obj` survives stop/start (incremental build 3.6 s), **30 GB gp3 root**.

## Files

| File | Role |
|---|---|
| `provision.sh` | one-time toolchain install on the EBS root (.NET SDK + Node + apt prereqs); version-parameterized |
| `on-start.sh` | per-boot: format + mount the ephemeral instance-store NVMe at `/nvme` (wiped on stop) |
| `typhon-nvme.service` | systemd oneshot that runs `on-start.sh` before the runner service |

## One-time manual setup (P1 — before Terraform)

Per Q1, do this by hand first to validate, **then** codify in Terraform.

1. **Launch the box** — see the P0 runbook in the design doc (30 GB gp3 root, `eu-west-1`, key + SG). Do **not**
   pin an EIP (idle cost); auto-assign a public IP or reach it via SSM.
2. **Copy these files** to `~/typhon-runner/` on the box and `chmod +x *.sh`.
3. **Toolchain:** `./provision.sh 10.0 22` (keep the SDK channel in sync with the repo `global.json`).
4. **NVMe on boot:** `sudo cp typhon-nvme.service /etc/systemd/system/ && sudo systemctl enable --now typhon-nvme.service`.
5. **Register the runner (long-lived, Q2):**
   ```bash
   mkdir ~/actions-runner && cd ~/actions-runner
   curl -o r.tar.gz -L https://github.com/actions/runner/releases/latest/download/actions-runner-linux-x64.tar.gz && tar xzf r.tar.gz
   ./config.sh --url https://github.com/Log2n-io/Typhon \
     --token <REGISTRATION_TOKEN> --labels self-hosted,linux,typhon-c6id --unattended
   sudo ./svc.sh install && sudo ./svc.sh start     # auto-reconnects across stop/start; no per-job token
   ```
   Mint `<REGISTRATION_TOKEN>` (1 h TTL) with: `gh api --method POST repos/Log2n-io/Typhon/actions/runners/registration-token --jq .token`.
6. **Warm the build once** so the first real run is incremental: clone the repo to a stable path and
   `dotnet build test/Typhon.Engine.Tests -c Release` (+ the Workbench test project).

## The `merge-gate.yml` change (cutover)

Point the `aws-gate` job at the box instead of SkyPilot:
- `runs-on: [self-hosted, linux, typhon-c6id]` (was `ubuntu-latest` + `sky launch`).
- **First step per job:** `git clean -xdf && git reset --hard <sha>` — the per-job isolation + disk-bound guard (Q2/Security).
- Export **`TMPDIR=/nvme`** for the test steps (the ~10–40× WAL win; already in `ci.sky.yaml`).
- Keep the SkyPilot path behind a flag/branch condition for rollback until P1/P2 are green across several PRs.

## Power-toggle (P2 — not in this dir yet)

Webhook `workflow_job:queued` → API Gateway → Lambda `start-instances` (label-filtered, HMAC-verified);
**level-triggered local idle self-stop** (idle ≡ queue-empty) as the primary stopper + EventBridge max-uptime
kill + billing alarm. See the design doc §Power-toggle / §Concurrency. Until P2 exists, start the box manually
(`aws ec2 start-instances`) to validate P1 runs.

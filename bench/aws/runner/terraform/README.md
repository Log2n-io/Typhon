# Typhon CI persistent runner — Terraform (P1 box + P2 power-toggle, #466)

Provisions the persistent `c6id.8xlarge` + the serverless power-toggle (webhook→Lambda start, EventBridge
max-uptime backstop, optional billing alarm). The **normal stop** is the box's own idle-self-stop
(`../idle-stop.sh`), not the Lambda — so a webhook miss can never strand a running box.

> ⚠️ **DRAFT** — this module was written without a live `terraform plan`. **Review the plan and expect small
> fixes** (attribute names, IAM JSON) before `apply`. The P0 spike validated the *box* (resume ~19 s, NVMe 10×,
> 30 GB sizing); this codifies that shape.

## What it creates
`main.tf` — default-VPC SG (SSH admin in), IAM instance profile (box can self-stop/tag), the EC2 box (30 GB gp3,
no EIP). `toggle.tf` — the toggle Lambda + IAM, HTTP API webhook endpoint, EventBridge 15-min backstop rule,
and (if `alert_email` set) a us-east-1 billing alarm. `lambda/lambda_function.py` — start-on-webhook + max-uptime stop.

## Apply

```bash
cd bench/aws/runner/terraform
cat > terraform.tfvars <<EOF
ssh_cidr       = "<YOUR_IP>/32"
webhook_secret = "<a long random string — also set it on the GitHub webhook>"
# alert_email  = "you@example.com"   # optional billing alarm
EOF
terraform init
terraform plan      # <-- review carefully; fix anything the draft got wrong
terraform apply
```

## Then, one-time

1. **Provision the box** (SSH in): run `../provision.sh`, register the long-lived runner, and enable the systemd
   units — see [`../README.md`](../README.md). The units: `typhon-nvme.service` (mount `/nvme` on boot),
   `typhon-idle-stop.timer` (the primary stopper). Warm the build once.
2. **Wire the webhook:** GitHub → org/repo Settings → Webhooks → add `terraform output webhook_url`, content-type
   `application/json`, secret = `webhook_secret`, events = **Workflow jobs** only.
3. **Flip the gate:** set the repo variable **`GATE_RUNNER=selfhosted`**. Now `merge-gate.yml`'s
   `aws-gate-selfhosted` job runs on the box; the SkyPilot `aws-gate` job goes dormant. Open a PR to validate green.

## Rollback
Unset `GATE_RUNNER` → the gate reverts to SkyPilot instantly. `terraform destroy` removes the box + toggle
(the SkyPilot path and its `ci.sky.yaml` are untouched by this module).

## Cost
Idle ≈ **~$3/mo** (30 GB gp3, box stopped, Lambda/API-GW/EventBridge free-tier). Compute only during runs
(~$0.31/run at $1.8312/hr × ~10 min). See the design doc's cost model.

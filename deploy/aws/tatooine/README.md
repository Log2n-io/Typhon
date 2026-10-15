# `deploy/aws/tatooine` — the two-instance deployment

**Status:** authored and `terraform validate`-clean. **Never applied.** No instance, bucket, distribution or budget
exists yet; nothing here has been run against an AWS account.

This is phase **I3–I7, I9, I10** of [`14-game-programme.md`](../../../claude/design/SwgTatooine/14-game-programme.md) § I,
built to the topology in [`08-hosting.md`](../../../claude/design/SwgTatooine/08-hosting.md) § 11.

Two front doors, mutually exclusive. **The default is the cheap one** — the instance serves TLS itself, with no CDN, no
load balancer and no Elastic IP, so nothing bills while the stack is stopped:

```
 anyone with the URL ──https──► demo.typhondb.io ─► EC2 game server :443  (public_tls = true)
                                 A record, TTL 60, published by the box at boot
                                 Let's Encrypt cert, DNS-01, renewed by certbot.timer
                                        │
                                        ├── EBS world volume (prod only, prevent_destroy)
                                        │
 EC2 bot box ──ws──► server PRIVATE ip ─┘   /auth answered only inside the VPC
   SwgTatooine.Bots + Blazor console on loopback, reached by `aws ssm start-session`
```

The other is the CDN, for when egress or abuse justifies $15/month (`enable_front_door = true`, `public_tls = false`):

```
 anyone with the URL ──https──► CloudFront + WAF ──X-Tatooine-Origin──► EC2 game server
```

**Standing cost of the default path: the $0.50/month hosted zone.** Everything else is instance hours.

## One module, two variable sets

08-hosting § 11.2's distinction is the **data**, not the instance:

| | `ephemeral.tfvars.example` | `prod.tfvars.example` |
|---|---|---|
| World | fresh from the seed every start | on an EBS volume, `prevent_destroy`, never wiped |
| Lifetime | hours, then the TTL backstop stops it | long-lived, **stopped by default** |
| Count | many, in parallel | exactly one |
| Created by | `tatooine-ephemeral.yml` (a button, a branch, a mandatory TTL) | once, by hand, updated in place |
| Schema gate | skipped — no previous world to open | **required** |

## Two stacks, and why

| | |
|---|---|
| `deploy/aws/tatooine-shared/` | **Created once, never destroyed.** The DNS zone, which carries `prevent_destroy` — its four nameservers are written by hand at the registrar, so recreating it would hand out four new ones and silently break the delegation. Also the certificate archive, which is worthless unless it outlives the stack, and the GitHub OIDC deploy role, which every workflow needs before it can reach the account at all |
| `deploy/aws/tatooine/` | The game. Created, tested and **destroyed** as often as you like. It writes into the shared zone and creates nothing DNS-shaped |

That split exists because of the cost shape. Measured in eu-west-1 on 2026-10-15 — gp3 from the pricing API, spot from
`describe-spot-price-history`:

| | |
|---|---:|
| Running, both instances on demand | **$1.21/hour** |
| Running, both spot | **$0.54/hour** |
| **Stopped** (two root volumes, 60 GB of gp3 at $0.088/GB-month) | **~$5.28/month** |
| **Destroyed** (the shared zone, plus pennies of state) | **~$0.51/month** |

**Destroying is about ten times cheaper than stopping.** Instance hours go to zero when a box is stopped and its public IP
is released — but EBS is billed in full, whether the volume is attached to a running instance or not. So the cycle this is
built for is apply, test, **destroy** — and that only works without a registrar edit each time because the zone is in the
other stack.

Note the corollary: **`persistent_data = true` makes a stack undestroyable on purpose.** Prod's world volume carries
`prevent_destroy`, because prod exists to accumulate a world with history for schema-evolution testing. Use
`ephemeral.tfvars.example` for anything you intend to throw away.

## Applying it

```bash
terraform init \
  -backend-config="bucket=<state-bucket>" \
  -backend-config="key=stacks/<name>.tfstate" \
  -backend-config="region=eu-west-1"

terraform apply -var-file=ephemeral.tfvars
```

The backend is a **partial configuration** — the key is one per stack, so it cannot be hard-coded. `terraform init
-backend=false` is enough to `validate` or `fmt` without touching S3, which is all that has been done so far.

**Set `cert_bucket` whenever `public_tls` is on.** It keeps `/etc/letsencrypt` in a bucket that outlives the stack, so a
re-created stack restores the certificate it already had rather than asking for a new one. Let's Encrypt allows five
certificates per week per exact set of names — no override, one slot back every 34 hours — and reusing a stack name is
exactly what spends them, so without this the sixth create of one hostname in a week serves plain HTTP. The restore is
skipped when the instance already has a certificate on disk, and the archive is refreshed from certbot's `--deploy-hook`,
so a boot that changed nothing uploads nothing.

**A stack gets its public name from `name` + `base_domain`.** `name = "temp01"` with `base_domain = "demo.typhondb.io"`
serves `https://temp01.demo.typhondb.io`; `terraform output url` prints it. Set `hostname` only for a name that does not
follow that pattern. One input names the resources, the state key, the SSM path and the URL, so they cannot drift apart.

**The delegation is a one-time step, and it belongs to `deploy/aws/tatooine-shared`, not here.** That stack owns the zone
and carries `prevent_destroy` precisely so this one can be created and destroyed without ever costing another registrar
edit. After its first apply:

```bash
cd ../tatooine-shared && terraform output delegation    # four nameservers, and the DS record NOT to add
```

Add those as `NS` records on the subzone's label in the parent zone at the registrar — for `demo.typhondb.io`, that is the
`demo` label in `typhondb.io`. **Add no DS record.** `typhondb.io` is DNSSEC-signed, and an unsigned child under a signed
parent is a valid *insecure delegation*; a DS record for an unsigned child instead breaks every name beneath it with
SERVFAIL, and only for validating resolvers — so it presents as "DNS is intermittently broken" rather than as a DNSSEC
mistake. Verify with `dig NS demo.typhondb.io @8.8.8.8` and `dig DS demo.typhondb.io @8.8.8.8` before going further.

Until that delegation exists, certbot cannot issue and the box serves plain HTTP on the game port with a loud line. That
is deliberate: a half-configured stack stays diagnosable instead of crash-looping.

## Driving it from GitHub: the secrets, in the order they can exist

`tatooine-ephemeral.yml` and `tatooine-deploy.yml` both open with `configure-aws-credentials` and
`role-to-assume: ${{ secrets.AWS_DEPLOY_ROLE_ARN }}`, so **that secret is the gate on everything else** — without it both
workflows fail on their first AWS step, whatever the other secrets say. The role lives in `tatooine-shared`
(`github-oidc.tf`), because it must outlive every stack.

| Secret | Where the value comes from | Missing |
|---|---|---|
| `AWS_DEPLOY_ROLE_ARN` | `tatooine-shared` → `terraform output deploy_role_arn` | **Both workflows fail immediately** |
| `TATOOINE_STATE_BUCKET` | the backend bucket you `init` against | **`terraform init` has no backend** |
| `TATOOINE_BASE_DOMAIN` | `tatooine-shared` → `zone_name` | warns; stack is IP-only |
| `TATOOINE_DNS_ZONE_ID` | `tatooine-shared` → `zone_id` | warns; stack is IP-only |
| `TATOOINE_ACME_EMAIL` | yours | warns; stack is IP-only |
| `TATOOINE_CERT_BUCKET` | `tatooine-shared` → `cert_bucket` | warns; **each create spends one of five weekly certificates** |
| `TATOOINE_ALERT_EMAIL` | yours; becomes an SNS subscription you confirm by mail | budget alerts go nowhere |

The three DNS secrets are **all-or-none** in the workflow, because a half-set is the configuration most likely to exist
and `public_tls` with no name fails a precondition at plan time rather than on the box.

**Do not set `TATOOINE_CERT_BUCKET` before `tatooine-shared` has been applied.** Unset is handled — both halves of
`tatooine-certificate` test it and skip cleanly. A name whose bucket does not exist is *not*: the `--deploy-hook` archive
script runs under `set -eu`, so its `aws s3 cp` fails, `certbot` exits non-zero, and `tatooine-certificate.service` goes
red on a stack that is in fact serving a valid certificate. The unit is `Wants=`, not `Requires=`, so the game still
comes up — which makes it the most misleading of the available failures.

**The deploy role's trust policy names refs explicitly, and that refuses the first run.** A `workflow_dispatch` token's
`sub` claim names the branch the *workflow* ran from, not the branch it deploys — `tatooine-ephemeral.yml` checks out
`inputs.ref` separately — so `github_refs` defaults to `["refs/heads/main"]`. Before these workflows are on main, add the
branch for bring-up and remove it afterwards:

```bash
cd ../tatooine-shared
terraform apply -var 'zone_name=demo.typhondb.io' -var 'github_refs=["refs/heads/main","refs/heads/my-branch"]'
terraform output github_secrets      # the gh commands for the two secrets only this stack can produce
```

Widening it to a wildcard instead would also match `pull_request` and `environment` subjects, which is how a fork's pull
request ends up holding a deploy role.

**The payload bucket is not a secret.** It is per stack (`tatooine-payload-<account>-<name>`, see `iam.tf`), so
`tatooine-deploy.yml` derives it from the account and the stack name. A single secret cannot name a per-stack bucket, and
when it tried, the upload went to one bucket while the instance's IAM policy granted read on another.

Then, from the outputs:

```bash
# deploy a payload (I6): build, gate, swap the symlink, restart — all over SSM, no SSH
gh workflow run tatooine-deploy.yml -f stack=prod \
  -f server_instance=<server_instance_id> -f bot_instances=<bot_instance_ids> -f schema_gate=true

# reach the bot console (it binds loopback; this is the only way in)
terraform output -raw console_tunnel | bash
```

## What each file is

| File | |
|---|---|
| `versions.tf` | Provider pinning, the per-stack S3 backend, and the `project`/`tier`/`stack` tags every cost control keys on |
| `variables.tf` | The inputs, with the reasoning for each default beside it |
| `network.tf` | Default VPC, **one subnet** so the bots share the server's AZ, and the two security groups |
| `iam.tf` | The payload bucket (private, versioned, encrypted) and the instance role: SSM core, CloudWatch agent, payload read |
| `instances.tf` | The two boxes, IMDSv2 required, plus prod's world volume |
| `cost.tf` | **I5.** The TTL backstop, the budget at 50/80/100 % and forecast, and cost anomaly detection |
| `frontdoor.tf` | CloudFront + WAF rate rules on `/ws` and `/auth`, and the EIP that gives the origin a stable name. **Off by default** |
| `outputs.tf` | What the deploy workflow and the operator need, including the ready-made SSM tunnel command |
| `templates/` | cloud-init for both boxes, which installs the systemd units (**I4**) and nothing else |

## Decisions worth knowing before you read the code

**No SSH anywhere.** No key pair, no port 22, no bastion. Administration is `aws ssm start-session`, the deploy is
`aws ssm send-command`, and the bot console is an SSM port forward. That is 08-hosting § 6's posture and it is also why
the bot security group has **no inbound rules at all**.

**The payload arrives by deploy, not at boot.** cloud-init installs the unit files and the directory layout; the binary
comes from `tatooine-deploy.yml`. A box that fetched the latest payload on boot could not be rolled back by restarting
it, and "which build is running" would become a property of the last reboot rather than of the deploy.

**The prefix list is necessary and not sufficient.** CloudFront Pro cannot use a private VPC origin (that needs
Business), so the origin is public and the security group can only narrow it to the CDN's origin-facing ranges — which
include a distribution an attacker creates in their own account. `X-Tatooine-Origin` is what closes that, and
`TatooineHost` now checks it: requests from outside the VPC without it get a bare 403. Callers on private addresses are
exempt, because the bot fleet reaches the private IP by design and has no secret to present.

**`ignore_changes = [ami, user_data]`** on both instances. Canonical publishes a new AMI continually, and a plan that
silently replaced the server — taking prod's attached volume through a detach — is not something to discover during a
deploy. The cost is that **rotating `origin_header_secret` does not propagate by `apply`**: update
`/etc/tatooine/server.env` over SSM and restart, or taint the instance deliberately.

**Prod's volume carries `prevent_destroy`.** It is the only irreplaceable thing in this module. The ephemeral workflow
also passes `persistent_data = false` on every destroy so the volume is never in that plan at all, and refuses the stack
name `prod` outright. The sweep additionally excludes `prod` when it enumerates the state bucket. Note what that is NOT:
the `sweep` job has no `needs: guard`, because `guard` is dispatch-only and the sweep also runs on a schedule — so on the
sweep path the protection is the name exclusion and `prevent_destroy`, not the guard job.

## Known gaps, named rather than assumed closed

- **Nothing has been applied.** `terraform validate` passes; a `plan` against a real account will find attribute
  mistakes that validation cannot, exactly as `bench/aws/runner/terraform`'s header warns about itself. A code review on
  2026-10-15 found nine defects in this module and its workflows that validation could not see — among them a
  `start_stopped` variable that nothing read (so prod came up running, ~$800/month against a $450 budget), one payload
  bucket name shared by every stack (so the second stack's apply failed and an ephemeral destroy targeted prod's
  bucket), a state backend with no lock, and cloud-init chowning files to a user that `runcmd` had not created yet.
  All are fixed; the list is here because it is the measure of what a first real `plan` and `apply` should still be
  expected to turn up.
- **`aws_ec2_instance_state` is how `start_stopped` is honoured, and it `ignore_changes = [state]`.** That is deliberate
  — "started on demand" has to survive the next `apply` — but it means Terraform will not stop a box you started by
  hand. Stopping it is also a manual act.
- **Only if you turn the CDN on: CloudFront would speak plaintext to the origin** (`origin_protocol_policy =
  "http-only"`). That was the module's most serious gap and **the default path no longer has it**: with `public_tls` the
  instance holds a publicly-trusted Let's Encrypt certificate, so there is no CDN hop and nothing crosses the internet in
  clear. When the CDN does go in front, that same certificate is what makes `origin_protocol_policy = "https-only"`
  possible — so set it then, and do not run a distribution against an `http-only` origin carrying real credentials.
- **The exemption trusts the default VPC, which the CI runner also lives in.** `bench/aws/runner/terraform` uses the same
  default VPC, so "inside the VPC" includes the merge-gate box. Keying the exemption on the bot security group's CIDR, or
  giving the game its own VPC, is the real answer.
- **No CAPTCHA on token issuance**, which 08-hosting § 8 wants. The WAF rate rule on `/auth` is the whole protection
  today, and `/auth` is a 600 000-iteration PBKDF2 behind a `ProcessorCount/4` semaphore — so the rate limit is doing
  real work and is not belt-and-braces.
- **The flat-rate CloudFront plan is an account-level subscription** and is not managed here. Without it the same
  distribution bills per-GB, which is 08-hosting § 4's $2 275/month rather than its $15.
- **The profiler exporter still binds all interfaces with no authentication** (`TcpExporter.cs:108`). This module does
  not open its port, which is the half we control from here; the engine change is still owed.
- **The account's cost guard is one AWS Budget, `skypilot-monthly-cap`, at $100 — and it alerts without acting.**
  Checked on 2026-10-15: there is **no CloudWatch billing alarm at all**, the budget has `filters: null` so it covers
  the whole account, it has four notification thresholds (actual at 85 % and 100 %, forecast at 80 % and 100 %) and
  **no budget actions** — so nothing is stopped by it. With $20.93 spent and $32.61 forecast, a test session costs
  $1.62–3.60 and will not approach it. A **forgotten** instance will: a c6id.4xlarge on demand is ~$22/day, so the
  remaining headroom is about 3.6 days. The TTL backstop in `cost.tf` is therefore the actual brake; the budget is a
  notification. Raise or scope the $100 once the game runs regularly, so the game and SkyPilot stop sharing one ceiling.
  (The notification thresholds report no subscriber addresses through the API, so whether the alerts reach anyone is
  worth confirming in the console.)
- **The budget and the anomaly monitor are account-wide, so only the `prod` stack creates them** (`persistent_data`
  gates it). An account with no prod stack therefore has **no budget at all** — create prod, or add them by hand, before
  anything runs overnight.
- **A bot fleet needs the server started with `--auth-burst` / `--auth-refill-ms`.** The `/auth` limiter keys on the
  caller's /24, so an entire bot box is one key: on the defaults a thousand signups take about ninety-nine minutes.
  `run-fleet.sh` says so in its header; nothing in the deployment sets it, and raising it on a server that is *also*
  public raises it for the internet in front of a 600 000-iteration PBKDF2. Sign the fleet up **before** going public,
  or give the bot path its own listener.
- **No backup/restore implementation exists** in the engine, so prod's persistence is the pre-deploy EBS snapshot the
  deploy takes. That snapshot is crash-consistent, which makes every restore a real power-loss recovery test — a
  feature, but one to exercise deliberately rather than discover.
- **The schema gate costs a short outage, by design.** It stops the service, copies the quiescent world, opens the copy
  with the new build, and only then swaps the symlink. Copying a world the server is still writing to gives files
  captured at different instants — not even crash-consistent — so the gate could fail, or pass, for reasons that had
  nothing to do with the schema. The stop costs nothing extra because the deploy restarts the service anyway, and a
  failed gate restarts the **old** release and leaves the live world untouched.
- **The gate needs free space for a second copy of the world** on the filesystem it writes to (`/tmp`, i.e. the root
  volume). It measures that before copying and refuses rather than filling the disk — but on a stack whose world
  outgrows its root volume the gate will refuse every deploy until `server_root_gb` is raised.

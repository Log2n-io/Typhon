# IAM for the AWS gate (#971)

The two policy documents the CI gate's AWS access is built from, kept in the repo so a change to them is
reviewable rather than a click in a console nobody can diff.

| File | Applies to | Why it exists |
|------|-----------|---------------|
| `skypilot-min.json` | IAM **user** `skypilot-bot` — the static keys in `secrets.AWS_ACCESS_KEY_ID` | What the GitHub runner may do: launch and tear down gate VMs, read and write the trace bucket. |
| `skypilot-v1-instance-role.json` | IAM **role** `skypilot-v1` — the instance profile every SkyPilot VM carries | What code *on the VM* may do. This is the one that matters for a fork PR: the tests run there. |

Account `940864285707`, region `eu-west-1`.

## Why these are scoped the way they are

The fork gate (`merge-gate-fork.yml`) runs an outside contributor's code on a SkyPilot VM after a maintainer
approves the deployment. The approval bounds *who triggers a run*, not *what the run can reach* — the payload does
not have to appear in the diff being approved — so what the VM holds is the whole question.

**The instance role was `AmazonEC2FullAccess` + `AmazonS3FullAccess`.** Any process on the VM reads those from
IMDS at `169.254.169.254`: a `[Test]` method, an MSBuild target, an npm `postinstall`. `ec2:*` account-wide meant
terminating the persistent gate runner, snapshotting its EBS volume and sharing the snapshot to an outside
account, and launching any instance type in any region. SkyPilot's documentation says the role's EC2 access is
for instances that "create other EC2 nodes" when launching **nested clusters**; the gate never does that. The VM
needs S3 for the `/outputs` mount, and `ec2:Describe*` for the provisioner. Nothing else.

**`skypilot-min` granted `iam:AttachRolePolicy` on `role/skypilot-v1` with no condition on which policy**, next to
`iam:PassRole` on the same role. Attach `AdministratorAccess` to `skypilot-v1`, launch an instance with that
profile, and the instance is account admin. Two API calls. `CreateRole`, `CreateInstanceProfile` and
`AddRoleToInstanceProfile` were only ever needed to bootstrap `skypilot-v1` on the first launch, which happened
long ago, so they are gone too — a bootstrap permission that outlives the bootstrap is just a standing grant.

That one was **latent rather than open**: it needs the static keys, and those never reach the VM. SkyPilot uploads
credentials to a cluster only when the identity comes from a shared credentials file
(`AWSIdentityType.SHARED_CREDENTIALS_FILE`); the gate authenticates from environment variables and never runs
`aws configure`, so `get_credential_file_mounts()` returns `{}`. The keys stay on the GitHub runner, where no fork
code executes — `checkout` fetches the tree, nothing builds it, `sky launch` ships it to the VM. Worth deleting
calmly; not worth pretending it was an open door.

**The `NeverTouchThePersistentRunner` Deny** is why `TerminateInstances`/`StopInstances` can stay broad without
being dangerous where it counts. `wake-gate-runner` needs `StartInstances` on `i-0b7b66d9dd6f0c6b8`, so that stays
allowed; nothing in CI needs to stop or destroy that box (it stops itself — `runner/idle-stop.sh`, using its own
`typhon-ci-runner` profile, not these keys).

S3 is scoped to `typhon-traces` in both documents. It is the only bucket in the account today, which is exactly
why `*/*` was easy to leave in place and exactly why it should not be.

## The instance-type allowlist, and why the `RunInstances` statement is split in two

`skypilot-min` allowed `ec2:RunInstances` on any instance type in any region. The gate only ever launches four:

| Task | Type |
|------|------|
| `ci.sky.yaml`, `coverage.sky.yaml` | `c6id.8xlarge` |
| `benchmark.sky.yaml` | `z1d.metal` |
| `anthill-bench.sky.yaml` | `m6idn.metal` |
| `anthill-dryrun.sky.yaml` | `c5d.metal` |

`LaunchOnlyGateInstanceTypes` pins it to those. The point is what it does to the failure mode: the account's
budget alarm notifies, it does not cap, and AWS Budgets refreshes every 8-12 hours. A `p5.48xlarge` fleet burns
roughly $100 an hour, so the forecast alarm reaches a human a day and several thousand dollars later. A condition
on the launch denies it at the API instead, which is the difference between noticing and preventing.

**The statement is split because `ec2:InstanceType` only exists in the request context for the `instance`
resource.** A single statement carrying both the condition and the subnet / volume / network-interface /
security-group ARNs evaluates the condition as false for those four, and the entire launch is denied — with an
error naming `RunInstances`, which reads like the allowlist is wrong when the shape of the statement is. Keep the
supporting resources in their own unconditioned statement.

Adding a task with a different instance type means adding that type here. A gate that fails because a new
`.sky.yaml` asked for something unlisted is the system working; widening this to `*` to make the failure go away
is not.

## The budget

`skypilot-monthly-cap` is a COST budget notifying `ACTUAL > 85%`, `FORECASTED > 80%` and `FORECASTED > 100%`. It
is a backstop against a slow leak, not a control on abuse — see above for why. Measured spend for calibration:
$4.73 (2026-06), $27.59 (2026-07), $23.36 (2026-08).

## Applying

```bash
# Instance role — add the scoped policy BEFORE detaching the managed ones, so there is no window without S3.
aws iam put-role-policy --role-name skypilot-v1 \
  --policy-name typhon-gate-instance \
  --policy-document file://bench/aws/iam/skypilot-v1-instance-role.json
aws iam detach-role-policy --role-name skypilot-v1 --policy-arn arn:aws:iam::aws:policy/AmazonEC2FullAccess
aws iam detach-role-policy --role-name skypilot-v1 --policy-arn arn:aws:iam::aws:policy/AmazonS3FullAccess

# User policy — a new version, which is also the rollback handle.
aws iam create-policy-version --policy-arn arn:aws:iam::940864285707:policy/skypilot-min \
  --policy-document file://bench/aws/iam/skypilot-min.json --set-as-default
```

A policy holds at most five versions; delete the oldest with `aws iam delete-policy-version` when
`create-policy-version` starts refusing.

## Rolling back

```bash
aws iam attach-role-policy --role-name skypilot-v1 --policy-arn arn:aws:iam::aws:policy/AmazonEC2FullAccess
aws iam attach-role-policy --role-name skypilot-v1 --policy-arn arn:aws:iam::aws:policy/AmazonS3FullAccess
aws iam delete-role-policy --role-name skypilot-v1 --policy-name typhon-gate-instance
aws iam set-default-policy-version --policy-arn arn:aws:iam::940864285707:policy/skypilot-min --version-id v3
```

## Verifying

`GATE_RUNNER=selfhosted`, so an ordinary PR never touches SkyPilot — the only paths that do are a fork PR and a
`workflow_dispatch` of Merge Gate carrying a `pr` input. So verification means dispatching the gate against a PR
and watching `aws-gate` succeed.

A denial surfaces as an `AccessDenied` naming the action it wanted, either from `sky launch` on the runner (the
user policy is short something) or from the VM's S3 mount (the instance role is). Read the action out of the
error and add exactly that, rather than widening a resource back to `*`.

## Considered and not done

- **Rotating the `skypilot-bot` key.** Recommended, then withdrawn once the credential-forwarding question above
  was actually checked instead of assumed: the reason to rotate a key is that it was exposed, and this one was
  not. What fork code *did* hold were the instance role's temporary IMDS credentials, which expire on their own.
- **A new budget alarm.** One already exists — see "The budget". Its thresholds were tightened; nothing else about
  it would have helped, for the latency reason given above.

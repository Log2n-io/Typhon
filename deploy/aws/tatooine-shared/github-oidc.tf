# How GitHub Actions gets into this account. Nothing else in these two stacks is about the game.
#
# <b>Every tatooine workflow's first AWS step is `configure-aws-credentials` with `role-to-assume:
# secrets.AWS_DEPLOY_ROLE_ARN`, and nothing in this repository ever created that role.</b> iam.tf in the game stack says
# the deploy path "uses GitHub OIDC (I9) and is in the workflow, not here" -- which was true of the workflow and false of
# the account. Both tatooine workflows therefore fail on their first step, before any terraform runs, and having the DNS
# secrets set makes no difference to that.
#
# OIDC rather than an access key, which is the half of I9 this finishes: GitHub mints a short-lived token describing the
# repository, the ref and the event, AWS exchanges it for credentials that expire with the job, and there is no
# long-lived secret in the repository to leak or rotate. The AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY pair that
# benchmark.yml still uses is what this replaces for everything tatooine.

# --- The identity provider ---------------------------------------------------------------------------------------------
#
# <b>There can be exactly one provider per URL per account</b>, so creating a second one fails with EntityAlreadyExists --
# and an account that already has GitHub OIDC configured for something else is a normal case, not an edge case. Hence the
# switch: set create_oidc_provider = false and this stack uses the existing one instead of fighting it. The ARN is
# deterministic, so finding it needs no data source.
variable "create_oidc_provider" {
  type        = bool
  default     = true
  description = "False when this account already has a token.actions.githubusercontent.com provider. There can only be one."
}

variable "github_repository" {
  type        = string
  default     = "Log2n-io/Typhon"
  description = "owner/repo allowed to assume the deploy role. Case-sensitive: it is matched against the token's sub claim."
}

# <b>The refs, and the one input to think about before applying.</b> A workflow_dispatch token's `sub` names the branch the
# WORKFLOW ran from, not the branch it deploys -- tatooine-ephemeral.yml checks out `inputs.ref` separately -- so
# main-only is the right default and costs nothing in day-to-day use.
#
# It does refuse the very first run, because the workflow that needs this role is not on main yet. Add the branch here for
# bring-up and take it out afterwards, rather than widening the pattern to a wildcard and leaving it wide.
variable "github_refs" {
  type        = list(string)
  default     = ["refs/heads/main"]
  description = "Git refs whose workflow runs may assume the deploy role, e.g. refs/heads/main."
}

locals {
  oidc_url = "token.actions.githubusercontent.com"

  # Deterministic, which is why create_oidc_provider needs no matching data source.
  oidc_provider_arn = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:oidc-provider/${local.oidc_url}"

  github_subs = [for ref in var.github_refs : "repo:${var.github_repository}:ref:${ref}"]
}

# No thumbprint. The argument is optional, and for GitHub -- as for Auth0, GitLab and Google -- IAM validates against its
# own library of trusted root CAs and ignores whatever thumbprint is configured. Pinning one would buy nothing and add a
# way for every deploy to break on a certificate rotation nobody was watching.
resource "aws_iam_openid_connect_provider" "github" {
  count = var.create_oidc_provider ? 1 : 0

  url            = "https://${local.oidc_url}"
  client_id_list = ["sts.amazonaws.com"]

  tags = {
    Name = "github-actions"
  }
}

# --- The deploy role ---------------------------------------------------------------------------------------------------
data "aws_iam_policy_document" "assume_github" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [local.oidc_provider_arn]
    }

    # <b>Both conditions are load-bearing, and the audience one is the subtle half.</b> Without the `aud` check the role
    # accepts a GitHub token minted for any audience, including one some third party asked a workflow in this repository
    # to mint for it. Without the `sub` check it accepts a token from ANY repository on GitHub, which is the canonical
    # OIDC misconfiguration and the reason this file does not use a wildcard anywhere.
    condition {
      test     = "StringEquals"
      variable = "${local.oidc_url}:aud"
      values   = ["sts.amazonaws.com"]
    }

    # StringEquals, not StringLike: an exact list of refs, so there is no wildcard to get wrong. A sub pattern of
    # "repo:owner/repo:*" would also match pull_request and environment subjects, which is how a fork's pull request ends
    # up holding a deploy role.
    condition {
      test     = "StringEquals"
      variable = "${local.oidc_url}:sub"
      values   = local.github_subs
    }
  }
}

resource "aws_iam_role" "deploy" {
  name               = "tatooine-github-deploy"
  description        = "Assumed by GitHub Actions over OIDC to apply, deploy to and destroy tatooine stacks."
  assume_role_policy = data.aws_iam_policy_document.assume_github.json

  # The default, stated because it is a real constraint on the jobs: configure-aws-credentials cannot request a session
  # longer than this, and a terraform apply that outlives its credentials fails midway with a half-created stack. An hour
  # is comfortable for the stacks this module builds; the CloudFront distribution is the only resource that approaches
  # it, and ephemeral stacks never create one.
  max_session_duration = 3600

  # <b>Explicit, because the trust policy names the provider by a STRING.</b> local.oidc_provider_arn is computed from the
  # account id rather than read off the resource, so Terraform sees no edge between them and is free to create the role
  # first -- and IAM rejects a trust policy whose federated principal does not exist yet with MalformedPolicyDocument.
  # The whole stack then fails on its first apply and succeeds on the second, which is the kind of flake that gets
  # explained as "AWS being eventually consistent" and never fixed.
  depends_on = [aws_iam_openid_connect_provider.github]

  tags = {
    Name = "tatooine-github-deploy"
  }
}

# --- What it may do ----------------------------------------------------------------------------------------------------
#
# <b>PowerUserAccess plus a narrow IAM grant, rather than a hand-enumerated policy.</b> That is a deliberate trade and
# worth being explicit about, because least privilege would normally argue the other way.
#
# The game stack creates resources in fourteen services -- EC2, EBS, Elastic IP, security groups, S3, Route 53, SSM
# Parameter Store, SNS, Budgets, Cost Explorer anomaly detection, EventBridge Scheduler, WAFv2, CloudFront and IAM -- and
# a destroy touches all of them again. An enumerated policy for that is some two hundred actions, and the way it fails is
# the worst available: one missing action aborts an apply halfway, leaving a stack that is half-created, still billing,
# and whose state file disagrees with reality. Then the same thing happens to the destroy.
#
# What actually matters is that this role cannot escalate, and IAM is the only service where it could. PowerUserAccess
# excludes IAM entirely, so the grant below adds back exactly what terraform needs, scoped to names beginning
# `tatooine-`, with the two escalation paths closed:
#
#   * AttachRolePolicy is conditioned on the two managed policies the instances actually use. Without that condition this
#     role could attach AdministratorAccess to a `tatooine-*` role and pass it to an instance it also controls, which is
#     a complete account takeover through a policy that reads as scoped.
#   * PassRole is conditioned on the service it is passed to, so a role can be handed to EC2 or to EventBridge Scheduler
#     and to nothing else.
#
# Widening this is a one-line change; discovering six months from now that the deploy role could mint an admin is not.
resource "aws_iam_role_policy_attachment" "deploy_poweruser" {
  role       = aws_iam_role.deploy.name
  policy_arn = "arn:aws:iam::aws:policy/PowerUserAccess"
}

data "aws_iam_policy_document" "deploy_iam" {
  statement {
    sid = "ManageTatooineRolesAndProfiles"

    actions = [
      "iam:CreateRole",
      "iam:DeleteRole",
      "iam:GetRole",
      "iam:UpdateRole",
      "iam:UpdateAssumeRolePolicy",
      "iam:TagRole",
      "iam:UntagRole",
      "iam:ListRoleTags",
      "iam:PutRolePolicy",
      "iam:DeleteRolePolicy",
      "iam:GetRolePolicy",
      "iam:ListRolePolicies",
      "iam:ListAttachedRolePolicies",
      "iam:ListInstanceProfilesForRole",
      "iam:CreateInstanceProfile",
      "iam:DeleteInstanceProfile",
      "iam:GetInstanceProfile",
      "iam:AddRoleToInstanceProfile",
      "iam:RemoveRoleFromInstanceProfile",
      "iam:TagInstanceProfile",
      "iam:UntagInstanceProfile",
      "iam:ListInstanceProfileTags",
    ]

    resources = [
      "arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/tatooine-*",
      "arn:aws:iam::${data.aws_caller_identity.current.account_id}:instance-profile/tatooine-*",
    ]
  }

  statement {
    sid       = "AttachOnlyTheTwoPoliciesTheInstancesUse"
    actions   = ["iam:AttachRolePolicy", "iam:DetachRolePolicy"]
    resources = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/tatooine-*"]

    condition {
      test     = "ArnEquals"
      variable = "iam:PolicyARN"

      values = [
        "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore",
        "arn:aws:iam::aws:policy/CloudWatchAgentServerPolicy",
      ]
    }
  }

  statement {
    sid       = "PassTatooineRolesToEc2AndTheScheduler"
    actions   = ["iam:PassRole"]
    resources = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/tatooine-*"]

    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ec2.amazonaws.com", "scheduler.amazonaws.com"]
    }
  }

  statement {
    # Read-only, and needed because every refresh reads back the policies this role manages. PowerUserAccess grants
    # iam:ListRoles and nothing else under iam:, so without this a plan against an existing stack fails on a Get rather
    # than on anything it is trying to change.
    sid       = "ReadWhatItManages"
    actions   = ["iam:GetPolicy", "iam:GetPolicyVersion", "iam:ListPolicyVersions", "iam:ListEntitiesForPolicy"]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "deploy_iam" {
  name   = "tatooine-iam"
  role   = aws_iam_role.deploy.id
  policy = data.aws_iam_policy_document.deploy_iam.json
}

output "deploy_role_arn" {
  value       = aws_iam_role.deploy.arn
  description = "Set this as the AWS_DEPLOY_ROLE_ARN repository secret. Both tatooine workflows' first AWS step needs it."
}

output "github_secrets" {
  value = join("\n", [
    "Set these as repository secrets on ${var.github_repository}:",
    "",
    "  gh secret set AWS_DEPLOY_ROLE_ARN  --body '${aws_iam_role.deploy.arn}'",
    "  gh secret set TATOOINE_CERT_BUCKET --body '${aws_s3_bucket.certs.id}'",
    "",
    "TATOOINE_STATE_BUCKET is the bucket this stack's own state lives in -- a -backend-config argument rather than",
    "anything this stack creates -- so it is not printed here.",
  ])
  description = "The repository secrets that cannot exist until this stack has been applied."
}

# The server's runtime configuration, in SSM Parameter Store rather than in user_data.
#
# This one file closes three separate review findings, and they share a cause: the secret and the front door's hostname were
# baked into `user_data` at instance-creation time.
#
#   1. **user_data is readable by anything on the instance**, through the instance metadata service, with no credentials. A
#      compromise of the game process could read the front door's shared secret out of it. A SecureString parameter needs the
#      instance role AND a KMS decrypt, and is auditable in CloudTrail.
#   2. **Rotating the secret did not work.** `instances.tf` carries `ignore_changes = [user_data]` -- which it needs, or a new
#      Canonical AMI would replace the server and take prod's volume through a detach -- so a new secret reached CloudFront and
#      never reached Kestrel. Every viewer got a 403 and the fix was undocumented.
#   3. **The first deploy to any stack could not start.** `ORIGIN` was written empty, `run.sh` therefore omitted `--origin`, and
#      the server REFUSES a non-loopback bind with no origin (correctly -- I1). Nothing anywhere set it, so every stack needed a
#      hand edit on the box before it would serve.
#
# And it breaks the dependency cycle that made (3) awkward: the distribution needs the instance, so the instance cannot be told
# the distribution's hostname at create time. A parameter the instance reads AT BOOT can depend on the distribution freely,
# because nothing in the instance's own graph points at it.

# 32 bytes of signing key. `random_password` rather than `random_string` so the value is marked sensitive and never shows
# up in a plan or an output.
resource "random_password" "auth_key" {
  length  = 44
  special = false
}

resource "aws_ssm_parameter" "server_env" {
  name = "/tatooine/${var.name}/server-env"
  type = "SecureString"

  # Shell-sourceable lines, because that is what the unit's ExecStartPre writes to /run and systemd then reads. Keeping the
  # format trivial keeps the fetch script trivial, and the script runs as root on every start.
  value = join("\n", compact([
    "ORIGIN=${local.client_origin}",
    var.origin_header_secret == "" ? null : "TATOOINE_ORIGIN_SECRET=${var.origin_header_secret}",

    # The token signing key. Without it the server generates one per process, so EVERY token a browser holds stops working
    # the moment the service restarts -- which a deploy does. The accounts survive; the sessions do not, and a user
    # experiences it as being silently logged out. Generated once and kept in state, which is part of why the state bucket
    # is private and encrypted.
    "SWG_AUTH_KEY=${random_password.auth_key.result}",

    # The session table (`--max-sessions`). Here rather than in user_data for the same reason as ORIGIN: the table is sized
    # once at start-up, so raising it is "update the parameter, restart the unit" and not "replace the instance". Empty
    # leaves the demo's default of 8 192.
    var.max_sessions == 0 ? null : "MAX_SESSIONS=${var.max_sessions}",

    # The /auth limiter, raised for a stack that a bot fleet signs up against. It keys on the caller's /24 and a bot box is
    # ONE key, so the defaults (burst 10, one attempt back every 6 s) make a player fleet of any size a matter of hours.
    # Empty leaves the server's defaults, which is what a stack serving only humans wants.
    var.auth_burst == 0 ? null : "AUTH_BURST=${var.auth_burst}",
    var.auth_refill_ms == 0 ? null : "AUTH_REFILL_MS=${var.auth_refill_ms}",
  ]))

  description = "ORIGIN, the signing key and the front-door secret for the ${var.name} game server. Read at service start; update and restart to rotate."

  tags = {
    Name = "tatooine-${var.name}-server-env"
  }
}

locals {
  # <b>Derived, in three cases, so nobody has to set it.</b> The server REFUSES a reachable bind with no `--origin` (I1), and
  # an operator who had to supply this by hand would discover that on the first deploy -- which is what happened before it was
  # derived at all.
  #
  #   1. an explicit override, for a front door this module did not create;
  #   2. the CDN's own hostname (or its first alias) when there is a distribution;
  #   3. the stack's own derived hostname when the instance serves TLS itself -- the cheap path.
  #
  # Empty otherwise, which is right for a private soak: loopback, or callers only from inside the VPC.
  client_origin = (
    var.client_origin != "" ? var.client_origin :
    var.enable_front_door ? (
      length(var.front_door_aliases) > 0 ? "https://${var.front_door_aliases[0]}" : "https://${aws_cloudfront_distribution.front_door[0].domain_name}"
    ) :
    var.public_tls && local.hostname != "" ? "https://${local.hostname}" : ""
  )
}

variable "client_origin" {
  type        = string
  default     = ""
  description = <<-EOT
    What the server accepts as `--origin` — the URL the browser client is served from. Normally left empty and derived: the
    distribution's hostname when there is a front door, `https://<name>.<base_domain>` when the instance serves TLS itself.

    Set it only for a front door this module did not create. It reaches the instance through SSM Parameter Store, so it can be
    corrected without replacing the box.
  EOT
}

# What lets the instance read it. Scoped to this stack's parameter, so a compromise of one stack's role cannot read another's
# secret -- which the account-wide alternative would have allowed.
data "aws_iam_policy_document" "server_env_read" {
  statement {
    sid       = "ReadTheServerEnvironment"
    actions   = ["ssm:GetParameter"]
    resources = [aws_ssm_parameter.server_env.arn]
  }

  statement {
    # SecureString with no key id uses the account's default SSM key, whose alias cannot be named in a resource ARN here. The
    # condition is what keeps this from being a general decrypt grant: it only works for SSM, for this account's parameters.
    sid       = "DecryptItWithTheDefaultSsmKey"
    actions   = ["kms:Decrypt"]
    resources = ["*"]

    condition {
      test     = "StringEquals"
      variable = "kms:ViaService"
      values   = ["ssm.${var.region}.amazonaws.com"]
    }
  }
}

resource "aws_iam_role_policy" "server_env_read" {
  name   = "server-env-read"
  role   = aws_iam_role.instance.id
  policy = data.aws_iam_policy_document.server_env_read.json
}

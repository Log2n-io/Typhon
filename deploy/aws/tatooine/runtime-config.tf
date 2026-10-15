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

resource "aws_ssm_parameter" "server_env" {
  name = "/tatooine/${var.name}/server-env"
  type = "SecureString"

  # Shell-sourceable lines, because that is what the unit's ExecStartPre writes to /run and systemd then reads. Keeping the
  # format trivial keeps the fetch script trivial, and the script runs as root on every start.
  value = join("\n", compact([
    "ORIGIN=${local.client_origin}",
    var.origin_header_secret == "" ? null : "TATOOINE_ORIGIN_SECRET=${var.origin_header_secret}",
  ]))

  description = "ORIGIN and the front-door secret for the ${var.name} game server. Read at service start; update and restart to rotate."

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
  #   3. the subzone's name when the instance serves TLS itself -- the cheap path.
  #
  # Empty otherwise, which is right for a private soak: loopback, or callers only from inside the VPC.
  client_origin = (
    var.client_origin != "" ? var.client_origin :
    var.enable_front_door ? (
      length(var.front_door_aliases) > 0 ? "https://${var.front_door_aliases[0]}" : "https://${aws_cloudfront_distribution.front_door[0].domain_name}"
    ) :
    var.public_tls && var.dns_zone_name != "" ? "https://${var.dns_zone_name}" : ""
  )
}

variable "client_origin" {
  type        = string
  default     = ""
  description = <<-EOT
    What the server accepts as `--origin` — the URL the browser client is served from. Normally left empty and derived: the
    distribution's hostname when there is a front door, `https://<dns_zone_name>` when the instance serves TLS itself.

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

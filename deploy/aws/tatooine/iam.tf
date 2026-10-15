# What the boxes are allowed to do, and the bucket the payload lands in.
#
# 08-hosting § 8: IMDSv2 required, instance role = SSM core + read of the artifact prefix + CloudWatch agent, no keys on
# disk, admin only over SSM. The deploy path uses GitHub OIDC (I9) and is in the workflow, not here -- this file is only
# what the INSTANCES may do.

data "aws_caller_identity" "current" {}

# --- The payload bucket ----------------------------------------------------------------------------------------------
#
# Private, versioned, encrypted. Versioned because rollback is "the previous payload", and a bucket that overwrites in
# place cannot answer what the previous payload was.
resource "aws_s3_bucket" "payload" {
  # PER STACK, not per account. Named only for the account, every stack declared the SAME bucket: the second stack's
  # apply failed with BucketAlreadyOwnedByYou, and worse, an ephemeral stack's `terraform destroy` -- or the hourly
  # sweep -- targeted PROD's payload bucket. That destroy then fails on a non-empty versioned bucket, the sweep
  # swallows the failure as a warning, and the stack is left half-destroyed and still billing.
  bucket = "tatooine-payload-${data.aws_caller_identity.current.account_id}-${var.name}"

  # So an ephemeral stack can actually be destroyed. The bucket holds payloads that are reproducible from a commit, and
  # a destroy that fails on leftover object versions is how an ephemeral stack becomes permanent. PROD keeps the default
  # refusal, because there a destroy that fails is the correct outcome: its payload history is what a rollback uses.
  force_destroy = !var.persistent_data
}

resource "aws_s3_bucket_public_access_block" "payload" {
  bucket                  = aws_s3_bucket.payload.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "payload" {
  bucket = aws_s3_bucket.payload.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "payload" {
  bucket = aws_s3_bucket.payload.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "payload" {
  bucket = aws_s3_bucket.payload.id

  rule {
    id     = "expire-old-payloads"
    status = "Enabled"

    filter {}

    # Keep enough history to roll back a few deploys, not enough to pay for a year of them. A payload is ~250 MB, so
    # this is the difference between cents and dollars a month -- small, but the kind of small that accumulates
    # unnoticed, which is the whole subject of cost.tf.
    noncurrent_version_expiration {
      noncurrent_days = 30
    }
  }
}

# --- The instance role -----------------------------------------------------------------------------------------------
data "aws_iam_policy_document" "assume_ec2" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ec2.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "instance" {
  name               = "tatooine-${var.name}-instance"
  assume_role_policy = data.aws_iam_policy_document.assume_ec2.json
}

# SSM core: this is what replaces SSH. The deploy sends commands through it and the operator port-forwards through it,
# so there is no key pair and no port 22 anywhere in this module.
resource "aws_iam_role_policy_attachment" "ssm" {
  role       = aws_iam_role.instance.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

resource "aws_iam_role_policy_attachment" "cloudwatch" {
  role       = aws_iam_role.instance.name
  policy_arn = "arn:aws:iam::aws:policy/CloudWatchAgentServerPolicy"
}

data "aws_iam_policy_document" "payload_read" {
  statement {
    sid       = "ReadThePayload"
    actions   = ["s3:GetObject", "s3:GetObjectVersion"]
    resources = ["${aws_s3_bucket.payload.arn}/*"]
  }

  statement {
    sid       = "FindThePayload"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.payload.arn]
  }
}

resource "aws_iam_role_policy" "payload_read" {
  name   = "payload-read"
  role   = aws_iam_role.instance.id
  policy = data.aws_iam_policy_document.payload_read.json
}

resource "aws_iam_instance_profile" "instance" {
  name = "tatooine-${var.name}-instance"
  role = aws_iam_role.instance.name
}

# --- Route 53, for the boot-time A record and for the certificate ------------------------------------------------------
#
# Two jobs, one policy, and NEITHER needs a stored credential — which is the whole reason the subzone is delegated to Route 53
# rather than driven through the registrar's own API:
#
#   * `tatooine-dns` upserts this stack's A records at every boot, so the name follows the public IP with no Elastic IP
#     allocated and nothing billing while the instance is stopped;
#   * `certbot --dns-route53` answers the ACME DNS-01 challenge by writing a TXT record. DNS-01 rather than HTTP-01 because
#     HTTP-01 would need port 80 open to arbitrary validation addresses, which is a hole this deployment does not want.
#
# Had the records stayed at the registrar, both jobs would need a long-lived API key on a public-facing game server. The
# instance role replaces it.
data "aws_iam_policy_document" "route53_write" {
  count = var.dns_zone_name == "" ? 0 : 1

  statement {
    sid     = "WriteThisStacksRecords"
    actions = ["route53:ChangeResourceRecordSets", "route53:ListResourceRecordSets"]

    # Scoped to this stack's zone. A compromise of the game process can rewrite demo.typhondb.io and nothing else -- not the
    # parent domain, which is not in this account at all, and not another stack's zone.
    resources = ["arn:aws:route53:::hostedzone/${aws_route53_zone.stack[0].zone_id}"]
  }

  statement {
    # Both of these refuse a resource scope: GetChange names a change id that does not exist until the call is made, and
    # ListHostedZones is how certbot's plugin finds which zone to write the challenge into.
    sid       = "PollChangesAndFindTheZone"
    actions   = ["route53:GetChange", "route53:ListHostedZones", "route53:ListHostedZonesByName"]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "route53_write" {
  count = var.dns_zone_name == "" ? 0 : 1

  name   = "route53-write"
  role   = aws_iam_role.instance.id
  policy = data.aws_iam_policy_document.route53_write[0].json
}

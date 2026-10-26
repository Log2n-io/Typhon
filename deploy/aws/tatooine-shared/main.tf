# The things that must outlive a `terraform destroy` of the game stack.
#
# <b>Right now that is one resource, and it earns its own state file for one specific reason.</b> The DNS zone's four
# nameservers are written by hand into the parent domain at the registrar (`typhondb.io` at OVH). Destroying the zone and
# creating it again hands out FOUR DIFFERENT nameservers — so the records at OVH would be stale, every name under
# `demo.typhondb.io` would stop resolving, and the manual delegation step would have to be repeated. Every time.
#
# In the game stack's own state, an ordinary `terraform destroy` took the zone with it. That makes the intended workflow —
# turn a stack on, test, destroy it, repeat — cost a registrar edit per cycle, which is exactly the kind of friction that
# ends with somebody leaving an instance running instead.
#
# So: this stack is created once and never destroyed. The game stack takes `dns_zone_id` as an input and creates nothing.
#
#   terraform init -backend-config="bucket=<state>" -backend-config="key=shared/dns.tfstate" -backend-config="region=eu-west-1"
#   terraform apply -var 'zone_name=demo.typhondb.io'
#
# <b>The certificate archive is here for the same reason, arrived at from the other direction.</b> Let's Encrypt allows five
# certificates per week per exact set of names, with no override and a refill of one every 34 hours. A stack that reissues on
# every create spends a slot per create -- so the sixth `temp01` of a week boots with no certificate, and the fix is to wait a
# day and a half. Keeping /etc/letsencrypt in a bucket that outlives the stack means a re-created stack RESTORES its
# certificate instead of asking for a new one, and reusing a name costs nothing. The bucket has to outlive the stack to do
# that, so it cannot live in the stack.
#
# The same argument applies to the payload bucket and the account-wide budget, which a review flagged as created per stack.
# They are not moved here yet; this file is the place for them when they are.

terraform {
  required_version = ">= 1.5"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 5.0" }
  }

  backend "s3" {
    use_lockfile = true
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      project = "tatooine"
      tier    = "shared"
    }
  }
}

variable "region" {
  type    = string
  default = "eu-west-1"
}

variable "zone_name" {
  type        = string
  description = "The subzone delegated from the parent domain, e.g. \"demo.typhondb.io\"."

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9.-]+\\.[a-z]{2,}$", var.zone_name))
    error_message = "zone_name must be a domain name, e.g. demo.typhondb.io."
  }
}

resource "aws_route53_zone" "stack" {
  name    = var.zone_name
  comment = "SWG Tatooine. Delegated from the parent domain by hand; records are written by the game instance at boot."

  # <b>Not force_destroy, unlike the copy this replaces.</b> There it existed so an ephemeral stack could be torn down with
  # records in it. Here the whole point is that the zone is NOT torn down, and refusing to delete a zone that still holds
  # records is a useful last line of defence against destroying the delegation by accident.

  lifecycle {
    # The one thing in this repository that costs a manual step at the registrar to recreate.
    prevent_destroy = true
  }

  tags = {
    Name = "tatooine-dns"
  }
}

output "zone_id" {
  value       = aws_route53_zone.stack.zone_id
  description = "Pass this to the game stack as dns_zone_id."
}

output "zone_name" {
  value       = aws_route53_zone.stack.name
  description = "Pass this to the game stack as base_domain. Every stack name becomes a label under it."
}

output "delegation" {
  value = join("\n", concat([
    "Add these to the PARENT zone at the registrar, as NS records on the '${split(".", var.zone_name)[0]}' label:",
    "",
    ], aws_route53_zone.stack.name_servers, [
    "",
    "Add NO DS record. An unsigned child under a signed parent is a valid insecure delegation;",
    "a DS record for an unsigned child breaks every name under ${var.zone_name} for validating resolvers.",
    "",
    "These change only if this zone is destroyed and recreated, which prevent_destroy exists to stop.",
  ]))
  description = "The one manual step, and the reason this stack is separate."
}

# --- The certificate archive ------------------------------------------------------------------------------------------
#
# <b>This holds private keys.</b> Private, encrypted, every form of public access blocked, and the game stack's instance role
# is granted Get and Put under one prefix -- its own hostname -- and nothing else. The alternative is not "no private key in
# S3": it is a private key issued afresh on every create, which spends a rate-limit slot that cannot be bought back and ends
# with a public demo serving plain HTTP. One object per hostname, a few kilobytes each.
#
# No DeleteObject in the grant either, so a compromise of a game process can overwrite its own stack's backup but cannot
# erase the archive.
data "aws_caller_identity" "current" {}

resource "aws_s3_bucket" "certs" {
  bucket = "tatooine-certs-${data.aws_caller_identity.current.account_id}"

  # Never force_destroy, and never destroyed at all in the normal course: this bucket exists precisely because the thing it
  # holds is expensive to replace.
  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_public_access_block" "certs" {
  bucket                  = aws_s3_bucket.certs.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "certs" {
  bucket = aws_s3_bucket.certs.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

# Versioned, because the failure this guards against is a truncated or empty upload overwriting a good archive -- which would
# be discovered at the next create, by which time the only copy is the bad one. Noncurrent versions cost nothing at this size
# and are expired after 90 days so the bucket does not accumulate forever.
resource "aws_s3_bucket_versioning" "certs" {
  bucket = aws_s3_bucket.certs.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "certs" {
  bucket = aws_s3_bucket.certs.id

  rule {
    id     = "expire-superseded-archives"
    status = "Enabled"

    filter {}

    noncurrent_version_expiration {
      noncurrent_days = 90
    }
  }
}

output "cert_bucket" {
  value       = aws_s3_bucket.certs.id
  description = "Pass this to the game stack as cert_bucket. Without it every stack create issues a new certificate."
}

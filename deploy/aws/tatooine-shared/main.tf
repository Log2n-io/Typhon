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
  description = "Pass this to the game stack as dns_zone_name."
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

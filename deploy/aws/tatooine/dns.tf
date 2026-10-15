# The stack's own DNS zone, delegated from a domain this module does not own.
#
# <b>A SUBZONE, so the parent domain is untouched.</b> `typhondb.io` lives at OVH and is DNSSEC-signed; delegating
# `demo.typhondb.io` to Route 53 means adding four NS records in the OVH zone and nothing else — the apex, the MX records and
# the company site stay exactly where they are. That delegation is the one manual step in this module (see the `delegation`
# output), because only the holder of the parent zone can create it.
#
# <b>And it must be an INSECURE delegation: NS records, and NO DS record.</b> With no DS, the signed parent proves the absence
# of one, a validating resolver concludes the child is unsigned, and stops validating below that point. That is correct and
# standard. A DS record for an unsigned child instead breaks every name under the subzone with SERVFAIL — and only for
# validating resolvers, so it presents as "DNS is intermittently broken" rather than as a DNSSEC mistake. Verified on
# 2026-10-15: `typhondb.io` has a DS at the `io` registry (key tag 23311), and `demo.typhondb.io` has none.
#
# Signing this zone later is additive — enable signing, THEN add the DS — and needs a KMS key. Not done: the parent's chain of
# trust is what matters for the company domain, and an unsigned child does not weaken it.

variable "dns_zone_name" {
  type        = string
  default     = ""
  description = <<-EOT
    The subzone this stack owns, e.g. "demo.typhondb.io". Empty creates no zone at all, which is right for an ephemeral stack
    reached by IP or only from inside the VPC.

    It must be a subdomain of a domain you hold, and you must add its NS records at the registrar — `terraform output
    delegation` prints them.
  EOT
}

variable "acme_email" {
  type        = string
  default     = ""
  description = <<-EOT
    Where Let's Encrypt sends expiry warnings. Required when dns_zone_name is set and public_tls is true.

    A role address rather than a personal one: the warning that matters arrives in two years, to whoever is there then. It does
    not have to be on the same domain as the certificate — Let's Encrypt never checks that.
  EOT
}

resource "aws_route53_zone" "stack" {
  count = var.dns_zone_name == "" ? 0 : 1

  name    = var.dns_zone_name
  comment = "SWG Tatooine ${var.name}. Delegated from the parent domain by hand; records are written by the instance."

  # <b>Because the instance writes the records, not Terraform.</b> The A records follow the public IP across every stop and
  # start, so Terraform cannot own them without fighting the boot-time update on each apply. It owns the ZONE; the instance
  # owns what is in it. force_destroy is what then lets an ephemeral stack actually be destroyed, since a zone holding records
  # Terraform never created would otherwise refuse to go.
  force_destroy = true

  tags = {
    Name = "tatooine-${var.name}"
  }
}

output "delegation" {
  value = var.dns_zone_name == "" ? "no DNS zone in this stack (dns_zone_name is empty)" : join("\n", concat([
    "Add these to the PARENT zone at the registrar, as NS records on the '${split(".", var.dns_zone_name)[0]}' label:",
    "",
    ], aws_route53_zone.stack[0].name_servers, [
    "",
    "Add NO DS record. An unsigned child under a signed parent is a valid insecure delegation;",
    "a DS record for an unsigned child breaks every name under ${var.dns_zone_name} for validating resolvers.",
    "",
    "Verify before going further:",
    "  dig NS ${var.dns_zone_name} @8.8.8.8      # should list the four names above",
    "  dig DS ${var.dns_zone_name} @8.8.8.8      # should return nothing",
  ]))
  description = "The one manual step: the NS records to add at the registrar, and the DS record not to."
}

output "dns_zone_id" {
  value       = var.dns_zone_name == "" ? "" : aws_route53_zone.stack[0].zone_id
  description = "The hosted zone the instance writes its own A records into."
}

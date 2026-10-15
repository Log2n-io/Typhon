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

variable "base_domain" {
  type        = string
  default     = ""
  description = <<-EOT
    The delegated subzone every stack's name sits under, e.g. "demo.typhondb.io" — `deploy/aws/tatooine-shared`'s
    `zone_name` output. With it set, a stack needs nothing but its `name`: `name = "temp01"` serves
    "temp01.demo.typhondb.io".

    Empty publishes no records and serves no certificate, which is right for a stack reached by IP or only from inside the
    VPC. Set it together with dns_zone_id.
  EOT
}

variable "hostname" {
  type        = string
  default     = ""
  description = <<-EOT
    Overrides the derived "<name>.<base_domain>" when a stack must answer on a name that does not follow it.

    Normally left empty. Deriving it is the point: the stack name is already constrained to a legal DNS label, so one input
    names the resources, the state key, the SSM path AND the URL, and they cannot drift apart. Whatever this resolves to must
    still live in dns_zone_id's zone — that zone holds every name ending in its own, with no further delegation.
  EOT
}

locals {
  # <b>One name, derived, for the reason in `hostname`'s description.</b> `var.name` is validated as a DNS label
  # (variables.tf), so the concatenation cannot produce an illegal name.
  #
  # Mirrors the var/local pair in runtime-config.tf: the variable is the override, the local is what the module uses.
  # `trimsuffix` because the obvious way to fill base_domain is from tatooine-shared's `zone_name`, and a Route 53 zone name
  # may carry the trailing dot. "temp01.demo.typhondb.io." is a legal FQDN to Route 53 and an INVALID identifier to Let's
  # Encrypt, so the stack would publish its records and then fail to certify them.
  hostname = (
    var.hostname != "" ? trimsuffix(var.hostname, ".") :
    var.base_domain != "" ? "${var.name}.${trimsuffix(var.base_domain, ".")}" : ""
  )
}

output "hostname" {
  value       = local.hostname
  description = "The name this stack answers on, derived from name + base_domain unless overridden. Empty when it publishes nothing."
}

output "url" {
  value       = local.hostname == "" ? "" : (var.public_tls ? "https://${local.hostname}" : "http://${local.hostname}")
  description = "Where to point a browser once the stack has booted and certified itself."
}

variable "acme_email" {
  type        = string
  default     = ""
  description = <<-EOT
    Where Let's Encrypt sends expiry warnings. Required when the stack has a hostname (base_domain is set) and public_tls is true.

    A role address rather than a personal one: the warning that matters arrives in two years, to whoever is there then. It does
    not have to be on the same domain as the certificate — Let's Encrypt never checks that.
  EOT
}

variable "dns_zone_id" {
  type        = string
  default     = ""
  description = <<-EOT
    The hosted zone this stack writes its records into — `deploy/aws/tatooine-shared`'s `zone_id` output. Required with
    base_domain.

    This stack does NOT create the zone, deliberately. The zone's four nameservers are written by hand at the registrar, and
    a `terraform destroy` that took the zone would hand out four new ones and silently invalidate that delegation — so the
    workflow this module exists for, turning a stack on and destroying it afterwards, would cost a registrar edit per cycle.
    The zone lives in a stack that is never destroyed; this one only writes into it.
  EOT
}

# <b>Nothing DNS-shaped is created here.</b> The records are written by the INSTANCE at boot, because they follow the public
# IP across every stop and start and Terraform cannot own them without fighting that update on each apply. The zone itself
# belongs to the shared stack, for the reason above.
#
# The id is needed for exactly two things: the IAM policy that scopes the instance's write permission to that one zone, and
# the cloud-init script that performs the write.

output "dns_names" {
  value       = local.dns_names
  description = "The names the instance publishes at boot and that the certificate covers."
}

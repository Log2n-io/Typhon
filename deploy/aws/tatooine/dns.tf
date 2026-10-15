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

variable "cert_bucket" {
  type        = string
  default     = ""
  description = <<-EOT
    The bucket that keeps /etc/letsencrypt across the life of a stack — `deploy/aws/tatooine-shared`'s `cert_bucket` output.

    Without it, a re-created stack asks Let's Encrypt for a new certificate, and Let's Encrypt allows five per week per exact
    set of names with no override: the sixth create of one hostname in a week boots with no certificate. With it, the stack
    restores the certificate it already had and reuses a name for free.

    Empty disables the restore and the backup, which is correct for a stack with no certificate at all.
  EOT
}

# <b>The records are written TWICE, by two owners, and that is deliberate.</b>
#
# The instance publishes them at boot from IMDS, because the public address changes on every stop and start and there is no
# Elastic IP to pin it -- an allocated EIP bills while the instance is stopped, which is the cost this deployment is built to
# avoid. Nothing but the box itself knows the new address, and no apply is running when it changes.
#
# Terraform owns the RESOURCE anyway, with `ignore_changes = [records]` so it never fights that update. The reason is
# `destroy`: when the instance owned the records outright, every destroyed stack left an A record behind pointing at a public
# address AWS had already released and would hand to somebody else. That was cleaned up by hand twice on 2026-10-15, which
# does not scale to a zone full of ephemeral stacks -- and a stale record is worse than a missing one, because it resolves.
#
# `destroy` deletes a Route 53 record by VALUE, so the delete only succeeds if state agrees with the live record. It does:
# destroy refreshes first, which reads back whatever the instance last published. A `destroy -refresh=false` would fail
# here, loudly, which is the right way for that mistake to present.
resource "aws_route53_record" "stack" {
  for_each = toset(local.dns_names)

  zone_id = var.dns_zone_id
  name    = each.value
  type    = "A"
  ttl     = 60

  # The address at create time. The instance overwrites this with the same value at its first boot, and with a different one
  # after any stop and start -- hence ignore_changes below.
  records = [aws_instance.server.public_ip]

  # The instance's own UPSERT may well have landed first: after a stop and start, or on an apply that lost its state. Without
  # this, Terraform's create fails with "already exists" on a record it is about to set to the right value anyway.
  allow_overwrite = true

  lifecycle {
    ignore_changes = [records]
  }
}

output "dns_names" {
  value       = local.dns_names
  description = "The names the instance publishes at boot and that the certificate covers."
}

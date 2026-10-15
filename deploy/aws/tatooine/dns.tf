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

variable "dns_zone_id" {
  type        = string
  default     = ""
  description = <<-EOT
    The hosted zone this stack writes its records into — `deploy/aws/tatooine-shared`'s `zone_id` output. Required with
    dns_zone_name.

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

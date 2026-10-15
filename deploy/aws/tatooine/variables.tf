# SWG Tatooine deployment — inputs (I3).
#
# ONE module, two variable sets. 08-hosting § 11.2's distinction is the DATA, not the instance: an ephemeral stack's
# database dies with it, prod's lives on an EBS volume that survives instance stop/start. Everything else about the two
# is the same, so making them two modules would mean two things to keep in step and one of them under-exercised.
#
#   ephemeral:  name = "pr-1372", persistent_data = false, ttl_hours = 4
#   prod:       name = "prod",    persistent_data = true,  ttl_hours = 0, start_stopped = true

variable "name" {
  type        = string
  description = "What this stack is called. Goes in every resource name, so it must be unique per stack in the account."
  default     = "demo"

  validation {
    # Resource names, DNS labels and the payload prefix all come from this, so it has to be safe in all three.
    condition     = can(regex("^[a-z][a-z0-9-]{1,30}$", var.name))
    error_message = "name must be 2-31 characters, start with a letter, and use only lowercase letters, digits and hyphens."
  }
}

variable "region" {
  type        = string
  default     = "eu-west-1"
  description = "08-hosting § 3: the account's quota, trace bucket, CI box and billing alarm are already in Ireland."
}

variable "persistent_data" {
  type        = bool
  default     = false
  description = <<-EOT
    True makes this the prod stack: a separate EBS volume holds the world, it is NOT destroyed with the instance, and the
    deploy pipeline's schema-evolution gate (I10) applies. False is an ephemeral stack whose database dies with it.

    Read 14-game-programme I8 before setting this true: prod's world must not be created until I10 exists, or the first
    schema change destroys the only thing prod is for (EN-8, adding a component to an existing archetype on reopen).
  EOT
}

variable "server_instance_type" {
  type        = string
  default     = "c6id.4xlarge"
  description = <<-EOT
    08-hosting § 5. c6id for the local NVMe: fsync is ~68 us against ~2.77 ms on gp3, measured on this account, and
    player mode writes Inventory on every loot. c8a is the alternative and is a wash on price; Graviton is held back for
    the SERVER by § 1 (the arm64 failure class is a torn page with a valid CRC, rule SL-07) and NOT for the bots.
  EOT
}

variable "bot_instance_type" {
  type        = string
  default     = "c7g.2xlarge"
  description = <<-EOT
    Graviton, because bots are client code and the arm64 risk that holds the server back does not apply to them
    (07-bots § 5). ~5-10k bots per host at this size. Pair with bot_spot = true day to day.
  EOT
}

variable "bot_count" {
  type        = number
  default     = 1
  description = "How many bot boxes. 08-hosting § 9's demo-day row is 4; one is enough for a thousand bots."

  validation {
    condition     = var.bot_count >= 0 && var.bot_count <= 8
    error_message = "bot_count must be between 0 and 8; more than that needs a cost conversation, not a terraform apply."
  }
}

variable "bot_spot" {
  type        = bool
  default     = true
  description = <<-EOT
    Spot for the bot fleet day to day. 07-bots § 5: on demo day set this false — a spot reclaim mid-demo costs more
    credibility than the discount saves.
  EOT
}

variable "server_root_gb" {
  type        = number
  default     = 40
  description = "Root volume. The payload is ~250 MB self-contained; the rest is the OS, logs and headroom."
}

variable "data_gb" {
  type        = number
  default     = 50
  description = <<-EOT
    Prod's world volume, in GB. Only created when persistent_data is true.

    Fifty rather than a round hundred: the demo rebuilds its world from a seed and writes little until player mode, and gp3
    bills per PROVISIONED gigabyte ($0.088/GB-month in eu-west-1, so each 50 GB is ~$4.40/month whether or not the instance
    is running). Growing a gp3 volume later is an online operation; shrinking one is not, so the cheap direction is up.
  EOT
}

variable "ttl_hours" {
  type        = number
  default     = 4
  description = <<-EOT
    An ephemeral stack is stopped this many hours after it is created, by the backstop in cost.tf. 0 disables it, which
    is only correct for prod (which is stopped by default instead).

    08-hosting § 11.6, stated plainly because it is the thing that goes wrong quietly: ephemeral instances are destroyed
    by a timer and not by discipline.
  EOT

  validation {
    condition     = var.ttl_hours >= 0 && var.ttl_hours <= 72
    error_message = "ttl_hours must be between 0 and 72."
  }
}

variable "start_stopped" {
  type        = bool
  default     = false
  description = <<-EOT
    Create the instances and leave them stopped. This is prod's posture — 08-hosting § 11.2: "off by default, started on
    demand" — because an occasional demo server does not need to run at all, and stopping it is the cost control.
  EOT
}

variable "game_port" {
  type        = number
  default     = 8080
  description = "The port Kestrel serves the client and /ws on. Reached through the front door, never directly."
}

variable "public_tls" {
  type        = bool
  default     = false
  description = <<-EOT
    Open 443 and 80 to the internet so the instance serves the game directly, with a Let's Encrypt certificate and no CDN in
    front of it.

    This is the cheap path: no CloudFront subscription, no load balancer, no Elastic IP. What it gives up is the WAF that was
    going to rate-limit `/auth` at the edge — which is why the deployment also sets `--auth-local-only`, answering `/auth`
    only for callers inside the VPC. Do NOT turn that off while this is true: a signup is a 600 000-iteration PBKDF2 behind a
    semaphore of ProcessorCount/4, so a few hundred junk attempts a second starve the tick loop.

    Requires dns_zone_name and acme_email.
  EOT
}

variable "console_port" {
  type        = number
  default     = 8090
  description = "The bot console's port. It binds LOOPBACK and is reached over an SSM port forward, so nothing opens this."
}

variable "front_door_prefix_list" {
  type        = string
  default     = "com.amazonaws.global.cloudfront.origin-facing"
  description = <<-EOT
    The only source allowed to reach the game port. 08-hosting § 4: CloudFront Pro cannot use a private VPC origin (that
    needs Business), so the origin is public and locked by this managed prefix list PLUS a secret header checked in
    Kestrel. The prefix list alone is necessary and not sufficient — anyone's CloudFront distribution is in it.
  EOT
}

variable "origin_header_secret" {
  type        = string
  sensitive   = true
  default     = ""
  description = <<-EOT
    The secret CloudFront adds and Kestrel requires, which is what makes the prefix list sufficient. Empty means the
    header is not enforced, which is only acceptable while nothing is public.

    Checked by the server since 2026-10-15: TatooineHost.UseOriginSecret requires it on every request from outside the VPC and
    answers a bare 403 without it. It reaches the instance through SSM Parameter Store (runtime-config.tf), NOT through
    user_data, so it can be rotated without replacing the instance and is not readable through the metadata service.

    Note what it does NOT protect against while origin_protocol_policy is "http-only": CloudFront speaks plaintext to the
    origin, so the secret itself crosses the public internet in clear and can be lifted off the path.
  EOT
}

variable "alert_email" {
  type        = string
  default     = ""
  description = <<-EOT
    Where the budget alerts go. Leave empty to skip them, which I5 says not to do before anything runs overnight: a
    forgotten c8a.8xlarge is about $1350/month.

    Note the existing CI billing alarm is ACCOUNT-WIDE at $50 and will fire on the first game instance. Raise or scope it.
  EOT
}

variable "monthly_budget_usd" {
  type        = number
  default     = 450
  description = "08-hosting § 9's always-on minimal row is ~420/month. Alerts at 50/80/100 % and on the forecast."
}

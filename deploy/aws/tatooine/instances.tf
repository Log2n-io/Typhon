# The two boxes.
#
# Both are plain EC2 instances with cloud-init that installs the unit files and nothing else: the PAYLOAD arrives by
# `aws ssm send-command` from the deploy workflow (I6), not at boot. That split is deliberate -- a box that fetches the
# latest payload on boot cannot be rolled back by stopping and starting it, and "which build is running" stops being a
# property of the deploy and becomes a property of the last reboot.

locals {
  # The world lives on the data volume when there is one, and under /var/lib when there is not. Prod's is never on a disk
  # that dies with the instance; an ephemeral stack's dies with the instance, which is the whole distinction.
  #
  # <b>NOT inside the release directory, and that was a real failure.</b> It was /opt/tatooine/current/data, which moves
  # with every deploy and does not exist until one has happened -- and the unit's `ReadWritePaths` needs its target to
  # exist BEFORE the unit starts. systemd sets the mount namespace up per command and fails it with 226/NAMESPACE when a
  # path is missing, before any ExecStartPre runs, so nothing inside the unit can create it. A `+`-prefixed ExecStartPre
  # does not help either: the prefix drops the privilege restrictions, not the namespace. A fixed path that cloud-init
  # creates once is the only shape that works.
  #
  # Consequence worth stating: an ephemeral world now survives a REDEPLOY within one instance's life, where it used to be
  # discarded with the release directory. It still dies with the instance, which is what "ephemeral" means here.

  #
  # <b>Both paths are MOUNT POINTS now, and the ephemeral one is the INSTANCE STORE.</b> It used to be a plain directory on
  # the root gp3 -- which quietly threw away the reason the server runs on a c6id, whose local NVMe fsyncs in 68 us against
  # gp3's 2.77 ms, so every durability number ever measured on an ephemeral stack was a network-disk number.
  # `tatooine-data.service` formats and mounts it on every boot (cloud-init-server.yaml). The world still dies with the
  # instance, which is both what "ephemeral" means here and exactly the instance store's own contract -- the two agree, which
  # is why this is the right disk for it and not a trade.
  data_dir = var.persistent_data ? "/mnt/tatooine" : "/var/lib/tatooine/world"

  # BOTH names on one certificate, from the first issue. The apex is what a viewer types; `origin.` exists so that putting a
  # CDN in front later changes only the apex to an ALIAS -- no re-issue, no rename, no second manual step. Let's Encrypt
  # charges nothing for the extra SAN.
  dns_names = local.hostname == "" ? [] : [local.hostname, "origin.${local.hostname}"]

  # certbot's layout under the --cert-name we pass. Empty when there is no certificate to ask for, which is what makes the
  # server fall back to plain HTTP instead of binding a port it cannot complete a handshake on.
  tls_chain = local.hostname == "" || var.acme_email == "" ? "" : "/etc/letsencrypt/live/tatooine/fullchain.pem"
  tls_key   = local.hostname == "" || var.acme_email == "" ? "" : "/etc/letsencrypt/live/tatooine/privkey.pem"
}

# --- The game server -------------------------------------------------------------------------------------------------
resource "aws_instance" "server" {
  ami                     = data.aws_ssm_parameter.ubuntu_x86.value
  instance_type           = var.server_instance_type
  subnet_id               = local.subnet_id
  vpc_security_group_ids  = [aws_security_group.server.id]
  iam_instance_profile    = aws_iam_instance_profile.instance.name
  disable_api_termination = var.persistent_data

  # IMDSv2 required (08-hosting § 8). http_tokens = "required" is what stops a server-side request forgery in the
  # application from reading the instance role's credentials out of the metadata service.
  metadata_options {
    http_endpoint               = "enabled"
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
  }

  root_block_device {
    volume_size = var.server_root_gb
    volume_type = "gp3"
    encrypted   = true
  }

  # <b>GZIPPED, because user_data is capped at 16 KB and this template is past it.</b> EC2 enforces that limit on the raw
  # value and cloud-init decompresses a gzipped one transparently, so base64gzip is the standard way out rather than a
  # trick -- 20 KB of template becomes about 7.7 KB. The alternative was deleting the comments that explain why each of
  # these units is shaped the way it is, which is the wrong thing to trade for headroom.
  user_data_base64 = base64gzip(templatefile("${path.module}/templates/cloud-init-server.yaml", {
    payload_bucket = aws_s3_bucket.payload.id
    stack          = var.name
    game_port      = var.game_port
    data_dir       = local.data_dir
    persistent     = var.persistent_data
    region         = var.region

    # The parameter's NAME, not its value. Deriving it rather than referencing aws_ssm_parameter.server_env.name keeps the
    # instance out of that resource's dependency graph, which is what lets the parameter depend on the distribution -- and the
    # distribution depends on the instance.
    env_parameter = "/tatooine/${var.name}/server-env"

    # DNS and TLS. Empty strings when this stack has no zone, which the scripts check for: the box then serves plain HTTP on
    # the game port and says so, rather than failing to boot.
    dns_zone_id = var.dns_zone_id
    dns_names   = join(" ", local.dns_names)
    acme_email  = var.acme_email
    tls_chain   = local.tls_chain
    tls_key     = local.tls_key

    # Where /etc/letsencrypt is kept between stacks, and under what prefix. Empty bucket disables both halves; the prefix is
    # the hostname rather than the stack name, because the rate limit Let's Encrypt applies is per set of NAMES.
    cert_bucket = var.cert_bucket
    cert_prefix = local.hostname
  }))

  tags = {
    Name = "tatooine-${var.name}-server"
    role = "server"
  }

  lifecycle {
    # The AMI moves whenever Canonical publishes, and a plan that silently replaces the server -- taking prod's attached
    # volume through a detach -- is not something to discover during a deploy. Upgrading the AMI is a deliberate act:
    # taint the instance, or remove this line for a stack you intend to recreate.
    ignore_changes = [ami, user_data_base64]

    # Checked at PLAN time, because every one of these fails on the box instead: a missing zone means no name to certify, a
    # missing email means certbot declines to register, and either leaves a stack that boots, serves plain HTTP, and looks
    # like a TLS bug. A plan that refuses costs seconds; the alternative costs a debugging session on a live instance.
    precondition {
      condition     = !var.public_tls || local.hostname != ""
      error_message = "public_tls serves a certificate for a name, so it needs base_domain (e.g. \"demo.typhondb.io\") or an explicit hostname."
    }

    precondition {
      # The two go together: the name is what the certificate covers, the id is where the records are written. One without
      # the other produces a box that either certifies a name it cannot publish, or publishes into a zone it cannot name.
      condition     = (local.hostname == "") == (var.dns_zone_id == "")
      error_message = "base_domain and dns_zone_id go together. Get both from deploy/aws/tatooine-shared's outputs."
    }

    precondition {
      condition     = !var.public_tls || var.acme_email != ""
      error_message = "public_tls needs acme_email: Let's Encrypt will not register an account without one, so no certificate is ever issued."
    }

    precondition {
      # Both at once would put the game behind a CDN AND on the open internet, with two certificates and two postures. Pick
      # one: the CDN for flat-rate egress and a WAF, or direct TLS for no standing cost.
      condition     = !(var.public_tls && var.enable_front_door)
      error_message = "public_tls and enable_front_door are two different front doors. Use one: the CDN, or the instance serving TLS itself."
    }

    precondition {
      condition     = var.acme_email == "" || local.hostname != ""
      error_message = "acme_email is only used to obtain a certificate for this stack's hostname, and it has none (base_domain is empty)."
    }
  }
}

# --- Off by default -------------------------------------------------------------------------------------------------
#
# <b>This resource is what makes `start_stopped` mean anything.</b> The variable, prod.tfvars and the README all promised
# "stopped by default" -- 08-hosting § 11.2's cost control, where stopping the instance IS the control -- and nothing
# read it, so a prod apply left a c6id.4xlarge and a c7g.2xlarge running continuously: roughly $800 a month against a
# $450 budget, for a server that is supposed to be started on demand.
#
# `aws_ec2_instance_state` is also how the stack is started later without Terraform owning the decision: set it, apply,
# and the box comes up; or start it from the console and `ignore_changes` keeps the next apply from stopping it again.
resource "aws_ec2_instance_state" "server" {
  count = var.start_stopped ? 1 : 0

  instance_id = aws_instance.server.id
  state       = "stopped"

  lifecycle {
    # So that starting the box by hand, which is what "started on demand" means, is not undone by the next apply.
    ignore_changes = [state]
  }
}

resource "aws_ec2_instance_state" "bots" {
  count = var.start_stopped ? var.bot_count : 0

  instance_id = aws_instance.bots[count.index].id
  state       = "stopped"

  lifecycle {
    ignore_changes = [state]
  }
}

# --- Prod's world ----------------------------------------------------------------------------------------------------
#
# 08-hosting § 11.2: the distinction between prod and ephemeral is the DATA. This volume is the whole distinction.
resource "aws_ebs_volume" "data" {
  count = var.persistent_data ? 1 : 0

  availability_zone = aws_instance.server.availability_zone
  size              = var.data_gb
  type              = "gp3"
  encrypted         = true

  tags = {
    Name = "tatooine-${var.name}-world"
  }

  lifecycle {
    # The one irreplaceable thing in this module. Prod exists to be a world with history -- schema evolution against a
    # database written by the previous build, WAL growth over days, data that accumulated -- and all of that is on this
    # volume. A `terraform destroy` that took it would delete the only thing prod is for.
    prevent_destroy = true
  }
}

resource "aws_volume_attachment" "data" {
  count = var.persistent_data ? 1 : 0

  device_name = "/dev/sdf"
  volume_id   = aws_ebs_volume.data[0].id
  instance_id = aws_instance.server.id

  # Terraform's default is a plain DetachVolume, which simply HANGS on a mounted filesystem -- and the force-detach it offers
  # instead would be a power cut. Stopping the instance first is the only option that is neither. The engine recovers
  # from one -- that is what the WAL is for, and 08-hosting § 11 points out that a crash-consistent restore is a real
  # recovery test -- but it should be a test we choose to run, not one a destroy runs for us.
  stop_instance_before_detaching = true
}

# --- The bot boxes ---------------------------------------------------------------------------------------------------
resource "aws_instance" "bots" {
  count = var.bot_count

  ami                    = data.aws_ssm_parameter.ubuntu_arm.value
  instance_type          = var.bot_instance_type
  subnet_id              = local.subnet_id
  vpc_security_group_ids = [aws_security_group.bots[0].id]
  iam_instance_profile   = aws_iam_instance_profile.instance.name

  metadata_options {
    http_endpoint               = "enabled"
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
  }

  root_block_device {
    volume_size = 20
    volume_type = "gp3"
    encrypted   = true
  }

  # Spot day to day, on-demand on demo day (07-bots § 5). A reclaim costs a run; mid-demo it costs credibility, which is
  # why this is a variable and not a default anybody has to remember.
  dynamic "instance_market_options" {
    for_each = var.bot_spot ? [1] : []
    content {
      market_type = "spot"
      spot_options {
        # "stop" rather than "terminate" would need a persistent request; a bot box holds nothing worth keeping except
        # its accounts file, which is reproducible from SWG_BOT_KEY, so losing it costs one round of logins.
        spot_instance_type = "one-time"
      }
    }
  }

  # <b>GZIPPED, because user_data is capped at 16 KB and this template is past it.</b> EC2 enforces that limit on the raw
  # value and cloud-init decompresses a gzipped one transparently, so base64gzip is the standard way out rather than a
  # trick -- 20 KB of template becomes about 7.7 KB. The alternative was deleting the comments that explain why each of
  # these units is shaped the way it is, which is the wrong thing to trade for headroom.
  user_data_base64 = base64gzip(templatefile("${path.module}/templates/cloud-init-bots.yaml", {
    payload_bucket  = aws_s3_bucket.payload.id
    stack           = var.name
    server_endpoint = "ws://${aws_instance.server.private_ip}:${var.game_port}/ws"
    console_port    = var.console_port
    bot_sessions    = var.bot_sessions
    bot_kind        = var.bot_kind
  }))

  tags = {
    Name = "tatooine-${var.name}-bots-${count.index}"
    role = "bots"
  }

  lifecycle {
    ignore_changes = [ami, user_data_base64]
  }
}

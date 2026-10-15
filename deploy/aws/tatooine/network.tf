# Where the two boxes sit, and what can reach them.
#
# The default VPC and one subnet, as bench/aws/runner/terraform does. A dedicated VPC would be the textbook answer and
# would buy nothing here: there is no private-subnet-plus-NAT posture to protect, because CloudFront Pro cannot use a
# private origin at all (08-hosting § 4) and the instances need outbound to reach S3 and SSM.
#
# ONE subnet, and both boxes in it. That is not laziness: 07-bots § 5 wants the bots in the SAME AZ as the server, both
# so transfer is $0 and so the measured path is a real network without a cross-AZ bill. Spreading them would cost about
# $1050/month at a thousand bots and 20 KB/s, and measure nothing extra.

data "aws_vpc" "default" {
  default = true
}

data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }
}

# The first subnet, deterministically. `sort` matters: without it the list order can change between plans and move both
# instances to another AZ, which is a replace rather than an update.
locals {
  subnet_id = sort(data.aws_subnets.default.ids)[0]
}

data "aws_ssm_parameter" "ubuntu_x86" {
  name = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"
}

data "aws_ssm_parameter" "ubuntu_arm" {
  name = "/aws/service/canonical/ubuntu/server/24.04/stable/current/arm64/hvm/ebs-gp3/ami-id"
}

data "aws_ec2_managed_prefix_list" "front_door" {
  name = var.front_door_prefix_list
}

# --- The game server -------------------------------------------------------------------------------------------------
#
# Inbound: the game port from the front door's prefix list, and the same port from the bot boxes. Nothing else, and in
# particular NOT 22 (SSM only), NOT the subscriptions port and NOT 9100 — 08-hosting § 8 names the profiler exporter as
# an engine change still owed, because it binds all interfaces with no authentication, so one security-group mistake
# publishes it. Not opening it is the half of that we control from here.
resource "aws_security_group" "server" {
  name        = "tatooine-${var.name}-server"
  description = "Tatooine game server: front door and bots in, all out"
  vpc_id      = data.aws_vpc.default.id

  # NO inline rules. Mixing an inline `egress` block with standalone rule resources on one security group is unsupported: the
  # inline block considers itself authoritative and the provider will fight the standalone rules, removing and re-adding them on
  # alternating applies. All rules for this group are therefore standalone resources, below.

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_vpc_security_group_egress_rule" "server_out" {
  security_group_id = aws_security_group.server.id
  description       = "Outbound for S3, SSM and package updates"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "server_from_front_door" {
  # ONLY when there is a front door. Opening the game port to the CDN's prefix list unconditionally made a stack with
  # enable_front_door = false reachable by ANY CloudFront distribution in the world -- including one an attacker creates in
  # their own account -- while, having no origin_header_secret, the server installed no header check at all. The two halves of
  # the posture are now created together or not at all.
  count = var.enable_front_door ? 1 : 0

  security_group_id = aws_security_group.server.id
  description       = "The CDN's origin-facing ranges. Necessary, not sufficient: Kestrel must also check the origin header."
  prefix_list_id    = data.aws_ec2_managed_prefix_list.front_door.id
  from_port         = var.game_port
  to_port           = var.game_port
  ip_protocol       = "tcp"
}

# --- Serving the public directly -------------------------------------------------------------------------------------
#
# Only with public_tls. The game port itself is NEVER opened to the internet: viewers arrive on 443, the bot fleet arrives on
# the private address, and the plain port stays inside the VPC. That separation is what lets `--auth-local-only` work -- the
# bots can still reach /auth over the private address while the internet cannot reach it at all.
resource "aws_vpc_security_group_ingress_rule" "server_https" {
  count = var.public_tls ? 1 : 0

  security_group_id = aws_security_group.server.id
  description       = "HTTPS from anywhere: this is the public game"
  cidr_ipv4         = "0.0.0.0/0"
  from_port         = 443
  to_port           = 443
  ip_protocol       = "tcp"
}

resource "aws_vpc_security_group_ingress_rule" "server_http_redirect" {
  count = var.public_tls ? 1 : 0

  security_group_id = aws_security_group.server.id
  description       = "HTTP from anywhere, which serves only a 308 to HTTPS"
  cidr_ipv4         = "0.0.0.0/0"
  from_port         = 80
  to_port           = 80
  ip_protocol       = "tcp"
}

resource "aws_vpc_security_group_ingress_rule" "server_from_bots" {
  # The bots speak plain WebSocket to the private IP (07-bots § 5), so they do not go through the front door at all --
  # which is also why a bot run costs nothing in transfer and still exercises the real TCP and WS path.
  count = var.bot_count > 0 ? 1 : 0

  security_group_id            = aws_security_group.server.id
  description                  = "The bot boxes, by security group rather than by CIDR"
  referenced_security_group_id = aws_security_group.bots[0].id
  from_port                    = var.game_port
  to_port                      = var.game_port
  ip_protocol                  = "tcp"
}

# --- The bot boxes ---------------------------------------------------------------------------------------------------
#
# NO inbound rules at all, deliberately. The control panel binds loopback and is reached over an SSM port forward
# (08-hosting § 8, roadmap Q6: tunnel-only, loopback bind, no operator password), so there is nothing to open. A panel
# with a button that opens a thousand sessions is exactly the thing not to put on an interface.
resource "aws_security_group" "bots" {
  count = var.bot_count > 0 ? 1 : 0

  name        = "tatooine-${var.name}-bots"
  description = "Tatooine bot box: nothing in, all out. The console is reached over SSM."
  vpc_id      = data.aws_vpc.default.id

  # Standalone rules only, for the reason the server's group gives.

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_vpc_security_group_egress_rule" "bots_out" {
  count = var.bot_count > 0 ? 1 : 0

  security_group_id = aws_security_group.bots[0].id
  description       = "Outbound to the server, S3 and SSM"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

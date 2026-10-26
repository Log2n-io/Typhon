# The public front door: CloudFront in front of the instance, with WAF rate rules on the paths that cost something.
#
# OFF by default (enable_front_door = false). Nothing about a private soak or an ephemeral stack needs it, and a
# distribution takes ~15 minutes to deploy and another ~15 to tear down -- which is long enough that having it on by
# default would make every ephemeral stack slow for no reason.
#
# 08-hosting § 4, which is why this shape and not an ALB:
#   * egress is the only cost that grows with success. 100 god-mode viewers at 100 KB/s around the clock is ~26 TB a
#     month: about $2275 served straight from EC2, $15 through the flat-rate Pro plan. An ALB adds standing cost and
#     does nothing for egress.
#   * WebSockets work on every tier, over HTTP/1.1 only -- so WebTransport cannot pass through this and would need a
#     second, unadvertised hostname terminated on the instance.
#   * Pro cannot use a private VPC origin (that needs Business at $200), so the origin is PUBLIC and locked by the
#     origin-facing prefix list in network.tf PLUS the secret header this file sets and TatooineHost now checks.
#
# THE FLAT-RATE PLAN IS AN ACCOUNT-LEVEL SUBSCRIPTION AND IS NOT MANAGED HERE. Subscribe once in the console; this
# module only creates the distribution. Without the subscription the same distribution bills per-request and per-GB,
# which is the $2275 number above rather than the $15 one.

variable "origin_protocol_policy" {
  type        = string
  default     = "http-only"
  description = <<-EOT
    How CloudFront speaks to the instance: "http-only", "https-only" or "match-viewer".

    The default is plaintext and that is a REAL EXPOSURE, not a formality — see the comment on the origin block below. It is the
    default only because `https-only` needs a publicly-trusted certificate on the instance itself (CloudFront rejects a
    self-signed origin certificate), which needs a domain, which this module does not yet create.

    Until this is "https-only", treat the deployment as unable to carry real credentials: H3's player mode in 08-hosting § 10
    depends on fixing it.
  EOT

  validation {
    condition     = contains(["http-only", "https-only", "match-viewer"], var.origin_protocol_policy)
    error_message = "origin_protocol_policy must be http-only, https-only or match-viewer."
  }
}

variable "origin_https_port" {
  type        = number
  default     = 443
  description = "The port CloudFront uses when origin_protocol_policy is not http-only."
}

variable "enable_front_door" {
  type        = bool
  default     = false
  description = "Create the CloudFront distribution and its WAF. Requires origin_header_secret to be set."
}

variable "front_door_aliases" {
  type        = list(string)
  default     = []
  description = <<-EOT
    Custom hostnames for the distribution. Empty uses the generated *.cloudfront.net name, which is correct until there
    is a domain. Setting this needs acm_certificate_arn, and the certificate must be in us-east-1.
  EOT
}

variable "acm_certificate_arn" {
  type        = string
  default     = ""
  description = "A us-east-1 certificate covering front_door_aliases. CloudFront accepts certificates from no other region."
}

variable "waf_rate_limit" {
  type        = number
  default     = 500
  description = <<-EOT
    Requests per five minutes from one IP before WAF blocks it, on /ws and /auth/*. 08-hosting § 8 wants a rate rule on
    both. 500 is generous for a browser and still stops a scripted upgrade flood; a real player opens one WebSocket and
    posts to /auth twice.

    The BOTS are unaffected: they reach the private IP and never pass through the front door at all.
  EOT
}

# CloudFront's WAF must live in us-east-1 whatever region the rest of the stack is in. A provider alias rather than a
# second root module, so one `terraform apply` still builds the whole thing.
provider "aws" {
  alias  = "us_east_1"
  region = "us-east-1"

  default_tags {
    tags = {
      project = "tatooine"
      tier    = var.persistent_data ? "prod" : "demo"
      stack   = var.name
    }
  }
}

resource "aws_wafv2_web_acl" "front_door" {
  count    = var.enable_front_door ? 1 : 0
  provider = aws.us_east_1

  name        = "tatooine-${var.name}"
  description = "Rate rules for the Tatooine front door"
  scope       = "CLOUDFRONT"

  default_action {
    allow {}
  }

  # The WebSocket upgrade. Rate-based and scoped to the path, because a blanket rule would count the static client's
  # hashed assets -- a single page load is dozens of requests, so a global limit either lets a flood through or blocks
  # the second visitor.
  rule {
    name     = "ws-rate"
    priority = 1

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit              = var.waf_rate_limit
        aggregate_key_type = "IP"

        scope_down_statement {
          byte_match_statement {
            positional_constraint = "STARTS_WITH"
            search_string         = "/ws"
            field_to_match {
              uri_path {}
            }
            text_transformation {
              priority = 0
              type     = "LOWERCASE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "ws-rate"
      sampled_requests_enabled   = true
    }
  }

  # /auth, which is the expensive one: a signup is PBKDF2 at 600 000 iterations behind a semaphore of ProcessorCount/4,
  # so a few hundred concurrent posts is a CPU denial of service on a path that is public by necessity. A tenth of the
  # WebSocket allowance, because a human posts here twice in a session and a fleet does not come this way at all.
  #
  # 08-hosting § 8 also wants CAPTCHA on token issuance. That is not here: it needs a client-side integration the
  # browser client does not have, so the rate rule is the whole protection today and the gap is named rather than assumed.
  rule {
    name     = "auth-rate"
    priority = 2

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit              = max(100, floor(var.waf_rate_limit / 10))
        aggregate_key_type = "IP"

        scope_down_statement {
          byte_match_statement {
            positional_constraint = "STARTS_WITH"
            search_string         = "/auth"
            field_to_match {
              uri_path {}
            }
            text_transformation {
              priority = 0
              type     = "LOWERCASE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "auth-rate"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "tatooine-${var.name}"
    sampled_requests_enabled   = true
  }
}

# A stable public name for the origin, and it is REQUIRED rather than a nicety for two separate reasons.
#
#   1. CloudFront custom origins take a DOMAIN NAME and reject an IP address, so something has to provide a name.
#   2. The instance's own public name encodes its public IP, which changes on every stop and start -- and prod is
#      stopped by default (08-hosting § 11.2) while an ephemeral stack is stopped by the TTL backstop. Pointing the
#      distribution at that name would mean the front door broke the first time the box was started again, with a 502
#      that looks like the server failing rather than the DNS having moved.
#
# 08-hosting § 9's cost model already budgets two IPv4 addresses, which is this and the bot box's egress.
resource "aws_eip" "server" {
  count    = var.enable_front_door ? 1 : 0
  instance = aws_instance.server.id
  domain   = "vpc"

  tags = {
    Name = "tatooine-${var.name}-server"
  }
}

# The secret the origin requires. An origin request policy cannot carry a custom VALUE, so it goes on the origin itself
# as a custom header -- which is why it is on aws_cloudfront_distribution below and not in a policy resource.
resource "aws_cloudfront_distribution" "front_door" {
  count = var.enable_front_door ? 1 : 0

  enabled         = true
  is_ipv6_enabled = true
  comment         = "SWG Tatooine (${var.name})"
  web_acl_id      = aws_wafv2_web_acl.front_door[0].arn
  aliases         = var.front_door_aliases

  # HTTP/1.1 to the origin, because that is all a WebSocket upgrade can use.
  #
  # <b>AND, by default, PLAINTEXT to the origin — which is the most serious accepted risk in this module.</b> CloudFront
  # terminates TLS at the edge and then speaks `var.origin_protocol_policy` to the instance; on `http-only` the shared secret,
  # every `/auth` password POST and every bearer token cross the public internet in clear. Anyone on that path can lift the
  # secret and then satisfy the origin's header check from any address inside the CDN's prefix list, which is the entire
  # posture. 08-hosting § 4 does not mention origin TLS at all; this is the gap, named.
  #
  # It is a variable rather than a hard-coded value so that the day there is a domain and an ACM certificate ON the instance,
  # `https-only` is a one-line change. Until then: this deployment must not carry real player credentials.
  origin {
    # The EIP's name, not the instance's: see aws_eip.server above.
    domain_name = aws_eip.server[0].public_dns
    origin_id   = "instance"

    custom_origin_config {
      http_port              = var.game_port
      https_port             = var.origin_https_port
      origin_protocol_policy = var.origin_protocol_policy
      origin_ssl_protocols   = ["TLSv1.2"]
    }

    # What makes the prefix list sufficient. TatooineHost checks this and answers 403 without it, so an attacker's own
    # distribution pointed at this address reaches nothing.
    custom_header {
      name  = "X-Tatooine-Origin"
      value = var.origin_header_secret
    }
  }

  # Everything, uncached, with the WebSocket headers forwarded. A game served from the origin is not a CDN workload in
  # the usual sense: the value here is TLS, the flat-rate egress and the WAF, not the cache.
  default_cache_behavior {
    target_origin_id       = "instance"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    # CachingDisabled and AllViewerExceptHostHeader, by their AWS-managed ids. Named by id because the data sources for
    # managed policies add a round trip per plan for values that are fixed and documented.
    cache_policy_id          = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"
    origin_request_policy_id = "b689b0a8-53d0-40ab-baf2-68738e2966ac"
  }

  # The client's hashed assets, which ARE a cache workload: Vite fingerprints them, so they are immutable and the edge
  # should serve them without touching the instance. This is where the egress saving actually comes from.
  ordered_cache_behavior {
    path_pattern           = "/assets/*"
    target_origin_id       = "instance"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    # CachingOptimized.
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = length(var.front_door_aliases) == 0
    acm_certificate_arn            = length(var.front_door_aliases) == 0 ? null : var.acm_certificate_arn
    ssl_support_method             = length(var.front_door_aliases) == 0 ? null : "sni-only"
    minimum_protocol_version       = length(var.front_door_aliases) == 0 ? "TLSv1" : "TLSv1.2_2021"
  }

  lifecycle {
    precondition {
      # A distribution with no secret is a public origin with a CDN in front of it and nothing else: the prefix list
      # lets any CloudFront distribution through, so the posture would be "unprotected" while looking deployed.
      condition     = var.origin_header_secret != ""
      error_message = "enable_front_door requires origin_header_secret: without it the public origin is reachable through anyone's CloudFront distribution."
    }

    precondition {
      condition     = length(var.front_door_aliases) == 0 || var.acm_certificate_arn != ""
      error_message = "front_door_aliases needs acm_certificate_arn, and the certificate must be in us-east-1."
    }
  }
}

output "front_door_url" {
  value = var.enable_front_door ? (
    length(var.front_door_aliases) > 0 ? "https://${var.front_door_aliases[0]}" : "https://${aws_cloudfront_distribution.front_door[0].domain_name}"
  ) : "no front door in this stack (enable_front_door = false)"
  description = "The URL anyone with it can open. The browser client derives wss://<this host>/ws by itself, so nothing else is configured."
}

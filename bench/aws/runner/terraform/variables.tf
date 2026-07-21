# Typhon CI persistent self-hosted runner — inputs (#466). Values are the P0-decided defaults; override in a
# terraform.tfvars. NOTE: this whole module is a DRAFT — it was authored without a live `terraform plan`, so
# review the plan and expect minor fixes (attribute names, IAM JSON) before `apply`.

variable "region" {
  type    = string
  default = "eu-west-1" # MUST match the typhon-traces S3 bucket region
}

variable "instance_type" {
  type    = string
  default = "c6id.8xlarge" # P0c: NVMe fsync latency 68us vs gp3 2.77ms -> WAL fixtures 10x faster. Do NOT drop to c6i.
}

variable "root_gb" {
  type    = number
  default = 30 # P0: ~12 GB used with SDK+repo+build; ~2.5x headroom
}

variable "key_name" {
  type        = string
  default     = "typhon-spike" # reuse the P0 key pair, or a dedicated one
  description = "Existing EC2 key pair name for SSH admin access."
}

variable "ssh_cidr" {
  type        = string
  description = "CIDR allowed to SSH (your IP/32). The runner needs only OUTBOUND to reach GitHub; SSH is admin-only."
}

variable "runner_label" {
  type    = string
  default = "typhon-c6id"
}

variable "webhook_secret" {
  type        = string
  sensitive   = true
  description = "Shared secret configured on the GitHub org/repo webhook; the Lambda HMAC-verifies X-Hub-Signature-256 with it."
}

variable "max_uptime_min" {
  type    = number
  default = 120 # backstop: stop a box running longer than this (a normal gate run is ~10 min; idle-stop fires at 10 min idle)
}

variable "alert_email" {
  type        = string
  default     = ""
  description = "If set, an SNS-subscribed email for the CloudWatch billing alarm. Leave empty to skip the alarm."
}

terraform {
  required_version = ">= 1.5"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 5.0" }
  }
}

provider "aws" {
  region = var.region
}

# --- Where to place the box: default VPC + its first subnet, latest Ubuntu 24.04 AMI ---
data "aws_vpc" "default" { default = true }

data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }
}

data "aws_ssm_parameter" "ubuntu" {
  name = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"
}

# --- Security group: SSH admin only (inbound); the runner reaches GitHub via outbound. No inbound webhook here
#     (the webhook hits API Gateway, not the box). ---
resource "aws_security_group" "runner" {
  name        = "typhon-ci-runner"
  description = "Typhon CI persistent runner: SSH admin in, all out"
  vpc_id      = data.aws_vpc.default.id

  ingress {
    description = "SSH admin"
    from_port   = 22
    to_port     = 22
    protocol    = "tcp"
    cidr_blocks = [var.ssh_cidr]
  }
  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

# --- Instance profile: lets the box stop ITSELF (idle-stop.sh) and tag/describe itself. ec2:StopInstances is
#     scoped to this account's instances; tighten to the instance ARN post-apply if desired. ---
data "aws_iam_policy_document" "assume_ec2" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ec2.amazonaws.com"]
    }
  }
}

data "aws_iam_policy_document" "runner_self_stop" {
  statement {
    actions   = ["ec2:StopInstances", "ec2:DescribeInstances", "ec2:CreateTags"]
    resources = ["*"]
  }
}

resource "aws_iam_role" "runner" {
  name               = "typhon-ci-runner"
  assume_role_policy = data.aws_iam_policy_document.assume_ec2.json
}

resource "aws_iam_role_policy" "runner_self_stop" {
  role   = aws_iam_role.runner.id
  policy = data.aws_iam_policy_document.runner_self_stop.json
}

resource "aws_iam_instance_profile" "runner" {
  name = "typhon-ci-runner"
  role = aws_iam_role.runner.name
}

# --- The persistent box. Stopped when idle; NO EIP (public IP auto-assigned on start, released on stop) to
#     avoid the standing IPv4 charge. Toolchain + runner + systemd units are installed per runner/README.md
#     (kept out of user_data so the box can be rebuilt from a golden image without re-bootstrapping). ---
resource "aws_instance" "runner" {
  ami                         = data.aws_ssm_parameter.ubuntu.value
  instance_type               = var.instance_type
  key_name                    = var.key_name
  subnet_id                   = data.aws_subnets.default.ids[0]
  vpc_security_group_ids      = [aws_security_group.runner.id]
  iam_instance_profile        = aws_iam_instance_profile.runner.name
  associate_public_ip_address = true

  root_block_device {
    volume_size           = var.root_gb
    volume_type           = "gp3"
    delete_on_termination = true
  }

  tags = { Name = "typhon-ci-runner" }

  # The AMI updates over time; don't let a newer default-AMI silently trigger a destroy/recreate of the box
  # that holds the registered runner + warm bin/obj. Rebuild deliberately (Q1 major-bump path) instead.
  lifecycle {
    ignore_changes = [ami]
  }
}

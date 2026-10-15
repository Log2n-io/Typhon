# What the deploy workflow and the operator need to know after an apply.

output "server_instance_id" {
  value       = aws_instance.server.id
  description = "The target for `aws ssm send-command` and `aws ssm start-session`."
}

output "server_private_ip" {
  value       = aws_instance.server.private_ip
  description = "What the bots connect to: same AZ, $0 transfer, real TCP and WebSocket path."
}

output "server_public_ip" {
  # The EIP when there is a front door, because that is the address the distribution resolves to and the one the
  # security group's prefix-list rule is about. Without a front door the instance's own, which moves on every restart.
  value       = var.enable_front_door ? aws_eip.server[0].public_ip : aws_instance.server.public_ip
  description = "The front door's origin. Reachable only from the CDN's prefix list, which is not the same as being private."
}

output "bot_instance_ids" {
  value       = [for instance in aws_instance.bots : instance.id]
  description = "The bot boxes, for the SSM command that starts a fleet and the port forward to each console."
}

output "payload_bucket" {
  value       = aws_s3_bucket.payload.id
  description = "Where the deploy puts the tarball the instances download."
}

output "console_tunnel" {
  value = length(aws_instance.bots) == 0 ? "no bot box in this stack" : join(" ", [
    "aws ssm start-session --target ${aws_instance.bots[0].id}",
    "--document-name AWS-StartPortForwardingSession",
    "--parameters 'portNumber=${var.console_port},localPortNumber=${var.console_port}'",
  ])
  description = "The one command that reaches the bot console. It binds loopback, so this is the only way in."
}

output "world_volume" {
  value       = var.persistent_data ? aws_ebs_volume.data[0].id : "ephemeral: the world dies with the instance"
  description = "Prod's world. 08-hosting § 11.2: this volume IS the distinction between prod and an ephemeral stack."
}

output "ttl_deadline" {
  value       = var.ttl_hours > 0 ? aws_scheduler_schedule.backstop[0].schedule_expression : "no TTL (prod is stopped by default instead)"
  description = "When the backstop stops this stack. It does not slide on a later apply, by design."
}

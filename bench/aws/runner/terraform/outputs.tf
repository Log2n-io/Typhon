output "instance_id" {
  value       = aws_instance.runner.id
  description = "The persistent runner box. Set this + labels on the GitHub self-hosted runner."
}

output "webhook_url" {
  value       = "${aws_apigatewayv2_api.webhook.api_endpoint}/webhook"
  description = "Point the GitHub org/repo webhook (workflow_job events, content-type application/json, the shared secret) here."
}

output "public_ip_lookup" {
  value       = "aws ec2 describe-instances --instance-ids ${aws_instance.runner.id} --query 'Reservations[0].Instances[0].PublicIpAddress' --output text"
  description = "The public IP is re-assigned on each start (no EIP) — fetch it with this."
}

# Serverless power-toggle (P2, #466): webhook -> Lambda START, + EventBridge max-uptime backstop.
# The normal STOP is the box's own idle-self-stop (runner/idle-stop.sh); nothing here stops on the hot path.

# --- Lambda package (from ./lambda/lambda_function.py) ---
data "archive_file" "toggle" {
  type        = "zip"
  source_dir  = "${path.module}/lambda"
  output_path = "${path.module}/lambda.zip"
}

data "aws_iam_policy_document" "assume_lambda" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

data "aws_iam_policy_document" "toggle" {
  statement {
    actions   = ["ec2:StartInstances", "ec2:StopInstances", "ec2:DescribeInstances", "ec2:CreateTags"]
    resources = ["*"] # tighten to aws_instance.runner.arn post-apply if desired
  }
  statement {
    actions   = ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["arn:aws:logs:*:*:*"]
  }
}

resource "aws_iam_role" "toggle" {
  name               = "typhon-ci-toggle"
  assume_role_policy = data.aws_iam_policy_document.assume_lambda.json
}

resource "aws_iam_role_policy" "toggle" {
  role   = aws_iam_role.toggle.id
  policy = data.aws_iam_policy_document.toggle.json
}

resource "aws_lambda_function" "toggle" {
  function_name    = "typhon-ci-toggle"
  role             = aws_iam_role.toggle.arn
  runtime          = "python3.12"
  handler          = "lambda_function.handler"
  filename         = data.archive_file.toggle.output_path
  source_code_hash = data.archive_file.toggle.output_base64sha256
  timeout          = 15

  environment {
    variables = {
      INSTANCE_ID    = aws_instance.runner.id
      RUNNER_LABEL   = var.runner_label
      WEBHOOK_SECRET = var.webhook_secret
      MAX_UPTIME_MIN = tostring(var.max_uptime_min)
    }
  }
}

# --- HTTP API: the GitHub webhook target. Point the org/repo webhook (workflow_job events) at the output URL
#     + /webhook, with the same secret as var.webhook_secret. ---
resource "aws_apigatewayv2_api" "webhook" {
  name          = "typhon-ci-webhook"
  protocol_type = "HTTP"
}

resource "aws_apigatewayv2_integration" "webhook" {
  api_id                 = aws_apigatewayv2_api.webhook.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.toggle.invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_route" "webhook" {
  api_id    = aws_apigatewayv2_api.webhook.id
  route_key = "POST /webhook"
  target    = "integrations/${aws_apigatewayv2_integration.webhook.id}"
}

resource "aws_apigatewayv2_stage" "webhook" {
  api_id      = aws_apigatewayv2_api.webhook.id
  name        = "$default"
  auto_deploy = true
}

resource "aws_lambda_permission" "apigw" {
  statement_id  = "AllowAPIGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.toggle.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.webhook.execution_arn}/*/*"
}

# --- Backstop: every 15 min, the same Lambda (no body -> max-uptime path) stops a box stuck running > MAX_UPTIME_MIN. ---
resource "aws_cloudwatch_event_rule" "max_uptime" {
  name                = "typhon-ci-max-uptime"
  schedule_expression = "rate(15 minutes)"
}

resource "aws_cloudwatch_event_target" "max_uptime" {
  rule = aws_cloudwatch_event_rule.max_uptime.name
  arn  = aws_lambda_function.toggle.arn
}

resource "aws_lambda_permission" "events" {
  statement_id  = "AllowEventBridgeInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.toggle.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.max_uptime.arn
}

# --- Optional billing alarm. AWS/Billing EstimatedCharges is published ONLY in us-east-1, so these use an
#     aliased provider. Skipped entirely when alert_email is empty. ---
provider "aws" {
  alias  = "us_east_1"
  region = "us-east-1"
}

resource "aws_sns_topic" "billing" {
  count    = var.alert_email == "" ? 0 : 1
  provider = aws.us_east_1
  name     = "typhon-ci-billing"
}

resource "aws_sns_topic_subscription" "billing" {
  count     = var.alert_email == "" ? 0 : 1
  provider  = aws.us_east_1
  topic_arn = aws_sns_topic.billing[0].arn
  protocol  = "email"
  endpoint  = var.alert_email
}

resource "aws_cloudwatch_metric_alarm" "billing" {
  count               = var.alert_email == "" ? 0 : 1
  provider            = aws.us_east_1
  alarm_name          = "typhon-ci-estimated-charges"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "EstimatedCharges"
  namespace           = "AWS/Billing"
  period              = 21600 # 6h
  statistic           = "Maximum"
  threshold           = 50 # USD — tune to taste
  dimensions          = { Currency = "USD" }
  alarm_actions       = [aws_sns_topic.billing[0].arn]
}

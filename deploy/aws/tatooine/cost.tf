# I5 — the money. This file exists before anything is long-lived, and 08-hosting § 11.6 says why plainly: the thing that
# goes wrong quietly is cost, and a forgotten c8a.8xlarge is about $1350/month.
#
# Three layers, because each catches what the others miss:
#
#   1. the TTL backstop     stops an ephemeral stack whose workflow never got round to destroying it. Catches the case
#                           where a CI job was cancelled, which is the common one.
#   2. the budget           tells a human at 50/80/100 % and on the forecast. Catches a slow leak that no single stack
#                           is responsible for.
#   3. anomaly detection    catches the shape nobody predicted, which is what it is for.
#
# Note the existing CI billing alarm is ACCOUNT-WIDE at $50 (I5's own warning). It will fire on the first game instance.
# Raise or scope it, or it becomes noise and gets muted, and then it protects nothing.

# --- 1. The TTL backstop ---------------------------------------------------------------------------------------------
#
# A scheduled stop rather than a terminate: stopping is reversible, costs only the EBS, and leaves the box to look at
# when the question is why the run went long. Destroying the stack is the workflow's job (I7) and it has the TTL too --
# this is the layer that works when the workflow does not.
resource "aws_scheduler_schedule_group" "backstop" {
  count = var.ttl_hours > 0 ? 1 : 0
  name  = "tatooine-${var.name}-backstop"
}

data "aws_iam_policy_document" "assume_scheduler" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["scheduler.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "backstop" {
  count              = var.ttl_hours > 0 ? 1 : 0
  name               = "tatooine-${var.name}-backstop"
  assume_role_policy = data.aws_iam_policy_document.assume_scheduler.json
}

data "aws_iam_policy_document" "backstop_stop" {
  statement {
    sid     = "StopThisStacksInstances"
    actions = ["ec2:StopInstances"]

    # Scoped to this stack's instances by name, so the backstop cannot stop prod or the CI runner. A policy that could
    # stop anything in the account would be a worse bug than the one it is here to prevent.
    resources = concat(
      ["arn:aws:ec2:${var.region}:${data.aws_caller_identity.current.account_id}:instance/${aws_instance.server.id}"],
      [for instance in aws_instance.bots : "arn:aws:ec2:${var.region}:${data.aws_caller_identity.current.account_id}:instance/${instance.id}"]
    )
  }
}

resource "aws_iam_role_policy" "backstop" {
  count  = var.ttl_hours > 0 ? 1 : 0
  name   = "stop-instances"
  role   = aws_iam_role.backstop[0].id
  policy = data.aws_iam_policy_document.backstop_stop.json
}

resource "aws_scheduler_schedule" "backstop" {
  count = var.ttl_hours > 0 ? 1 : 0

  name       = "tatooine-${var.name}-ttl"
  group_name = aws_scheduler_schedule_group.backstop[0].name

  # A one-time schedule computed from apply time. It does NOT slide: a later apply that changes nothing leaves the
  # original deadline in place, because `timestamp()` would re-plan the schedule on every run and a TTL that resets
  # whenever somebody touches the stack is not a TTL. `ignore_changes` below is what makes that true.
  schedule_expression          = "at(${formatdate("YYYY-MM-DD'T'hh:mm:ss", timeadd(timestamp(), "${var.ttl_hours}h"))})"
  schedule_expression_timezone = "UTC"

  flexible_time_window {
    mode = "OFF"
  }

  target {
    arn      = "arn:aws:scheduler:::aws-sdk:ec2:stopInstances"
    role_arn = aws_iam_role.backstop[0].arn

    input = jsonencode({
      InstanceIds = concat([aws_instance.server.id], [for instance in aws_instance.bots : instance.id])
    })
  }

  lifecycle {
    ignore_changes = [schedule_expression]
  }
}

# --- 2. The budget ---------------------------------------------------------------------------------------------------
# <b>The budget and the anomaly monitor measure the WHOLE ACCOUNT, so exactly one stack may own them.</b> Created per stack,
# five ephemeral stacks each alarmed on the shared project total -- five copies of the same alert, none of them about the stack
# that sent it -- and each added a duplicate account-wide SERVICE anomaly monitor. `persistent_data` is the discriminator
# because prod is the one long-lived stack; an account with no prod stack gets no budget, which the README names as a gap.
locals {
  owns_cost_controls = var.persistent_data && var.alert_email != ""
}

resource "aws_sns_topic" "cost" {
  count = local.owns_cost_controls ? 1 : 0
  name  = "tatooine-${var.name}-cost"
}

resource "aws_sns_topic_subscription" "cost" {
  count     = local.owns_cost_controls ? 1 : 0
  topic_arn = aws_sns_topic.cost[0].arn
  protocol  = "email"
  endpoint  = var.alert_email
}

resource "aws_budgets_budget" "tatooine" {
  count = local.owns_cost_controls ? 1 : 0

  name         = "tatooine-${var.name}"
  budget_type  = "COST"
  limit_amount = tostring(var.monthly_budget_usd)
  limit_unit   = "USD"
  time_unit    = "MONTHLY"

  # On the project tag, so this measures the game and not the whole account -- which is also why versions.tf puts the
  # tag on everything through default_tags. An untagged resource is outside this budget.
  cost_filter {
    name   = "TagKeyValue"
    values = ["user:project$tatooine"]
  }

  # Actual at 50 and 80 so there is time to react, actual at 100 because that is the number that was agreed, and
  # FORECASTED at 100 because by the time actual reaches it the month is already spent.
  dynamic "notification" {
    for_each = [50, 80, 100]
    content {
      comparison_operator       = "GREATER_THAN"
      threshold                 = notification.value
      threshold_type            = "PERCENTAGE"
      notification_type         = "ACTUAL"
      subscriber_sns_topic_arns = [aws_sns_topic.cost[0].arn]
    }
  }

  notification {
    comparison_operator       = "GREATER_THAN"
    threshold                 = 100
    threshold_type            = "PERCENTAGE"
    notification_type         = "FORECASTED"
    subscriber_sns_topic_arns = [aws_sns_topic.cost[0].arn]
  }
}

# --- 3. Anomaly detection --------------------------------------------------------------------------------------------
#
# One monitor for the account's services rather than one per stack: an anomaly is by definition the thing we did not
# think to watch, so scoping it to what we expected would defeat it.
resource "aws_ce_anomaly_monitor" "services" {
  count             = local.owns_cost_controls ? 1 : 0
  name              = "tatooine-${var.name}-services"
  monitor_type      = "DIMENSIONAL"
  monitor_dimension = "SERVICE"
}

resource "aws_ce_anomaly_subscription" "services" {
  count     = local.owns_cost_controls ? 1 : 0
  name      = "tatooine-${var.name}-anomalies"
  frequency = "DAILY"

  monitor_arn_list = [aws_ce_anomaly_monitor.services[0].arn]

  subscriber {
    type    = "EMAIL"
    address = var.alert_email
  }

  # Absolute dollars, not a percentage: a 300 % rise on a $2 day is noise, and $40 of unexpected spend is not.
  threshold_expression {
    dimension {
      key           = "ANOMALY_TOTAL_IMPACT_ABSOLUTE"
      match_options = ["GREATER_THAN_OR_EQUAL"]
      values        = ["40"]
    }
  }
}

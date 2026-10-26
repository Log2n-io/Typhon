#!/usr/bin/env bash
# Run a shell script on an instance over SSM, wait for it, and fail with its output.
#
# This is the whole "no SSH" posture in one file (08-hosting § 6). `aws ssm send-command` is asynchronous and returns a
# command id, so without the wait-and-report below a deploy step reports success for a command that has not run yet --
# which is the failure mode that makes people give up on SSM and open port 22.
#
#   ssm-run.sh <instance-id> <script> [timeout-seconds]
#
# The script is passed as ONE argument and sent as a single command, so it may contain newlines. It runs as root through
# AWS-RunShellScript, which is why nothing here needs sudo.
set -euo pipefail

instance="${1:?usage: ssm-run.sh <instance-id> <script> [timeout-seconds]}"
script="${2:?usage: ssm-run.sh <instance-id> <script> [timeout-seconds]}"
timeout_seconds="${3:-900}"

echo "::group::ssm $instance"

# --cli-input-json over --parameters, because a script with quotes, newlines and $ in it cannot survive being spliced
# into the shorthand `commands=[...]` syntax. jq builds the document, so the shell never has to quote the script twice.
document=$(jq -n --arg instance "$instance" --arg script "$script" --argjson timeout "$timeout_seconds" '{
    InstanceIds: [$instance],
    DocumentName: "AWS-RunShellScript",
    Comment: "tatooine deploy",
    TimeoutSeconds: $timeout,
    Parameters: { commands: [$script] }
}')

command_id=$(aws ssm send-command --cli-input-json "$document" --query 'Command.CommandId' --output text)
echo "command $command_id (timeout ${timeout_seconds}s)"

# POLLED HERE RATHER THAN WITH `aws ssm wait command-executed`, and the difference is not cosmetic: that waiter is a
# fixed 5 s x 20 attempts, so it gives up after about a hundred seconds and reports failure for a command that is still
# running perfectly well. The schema gate -- copy a world, open it, run two hundred ticks -- goes well past that, so
# every gated deploy would have failed on a command that then succeeded invisibly on the box. Polling to the command's
# OWN timeout is the only thing consistent with the TimeoutSeconds we asked for.
deadline=$(( $(date +%s) + timeout_seconds + 60 ))
status=Pending
while :; do
    status=$(aws ssm get-command-invocation --command-id "$command_id" --instance-id "$instance" --query 'Status' --output text 2>/dev/null || echo Pending)

    # The terminal set, from the SSM documentation. Anything else -- Pending, InProgress, Delayed, Cancelling -- means
    # keep waiting.
    case "$status" in
        Success|Failed|Cancelled|TimedOut|Undeliverable|Terminated) break ;;
    esac

    if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "::error::SSM command $command_id on $instance still $status after ${timeout_seconds}s; giving up watching it"
        echo "::endgroup::"
        exit 1
    fi

    sleep 5
done

stdout=$(aws ssm get-command-invocation --command-id "$command_id" --instance-id "$instance" --query 'StandardOutputContent' --output text)
stderr=$(aws ssm get-command-invocation --command-id "$command_id" --instance-id "$instance" --query 'StandardErrorContent' --output text)

# `|| true` on each, because a non-final command in an && list is exempt from `set -e` but the LAST one is not, and
# these are the last statements before the status check.
[ -n "$stdout" ] && echo "$stdout" || true
if [ -n "$stderr" ]; then
    echo "--- stderr ---"
    echo "$stderr" >&2
fi

echo "::endgroup::"

if [ "$status" != "Success" ]; then
    echo "::error::SSM command on $instance ended $status"
    exit 1
fi

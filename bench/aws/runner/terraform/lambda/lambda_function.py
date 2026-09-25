"""Typhon CI power-toggle Lambda (#466).

Two entry paths, routed by event shape:

  * API Gateway (GitHub `workflow_job` webhook)  -> START the persistent runner box.
        Verify the HMAC (X-Hub-Signature-256), require action=="queued" AND our runner label in
        workflow_job.labels, then ec2:StartInstances (no-op if already running) and stamp a
        `TyphonStartedAt` tag so the max-uptime backstop can bound the run.

  * Self-invocation {"wake": ...} (asynchronous, from the webhook path) -> WAKE A BOX THAT IS ON ITS WAY DOWN.
        A job queued while the box is `running` but about to idle-stop, or already `stopping`, cannot be started by
        one start-instances call: it is a no-op, or refused. The waker watches the box, waits out the stop, then starts
        it (#1048). The webhook itself answers at once — GitHub gives a delivery 10 s.
  * EventBridge scheduled rule (no body/headers) -> MAX-UPTIME BACKSTOP.
        Stop the box if it has been running longer than MAX_UPTIME_MIN (catches a stuck box whose
        local idle-self-stop — the PRIMARY stopper, on the box — failed).

The normal stop is the box's own level-triggered idle-self-stop (see runner/idle-stop.sh); this Lambda
only STARTS on demand and provides the backstop, so a webhook miss/leak can never strand a $1.83/hr box.
"""
import base64
import hashlib
import hmac
import json
import os
import time

import boto3

ec2 = boto3.client("ec2")

INSTANCE_ID = os.environ["INSTANCE_ID"]
RUNNER_LABEL = os.environ.get("RUNNER_LABEL", "typhon-c6id")
WEBHOOK_SECRET = os.environ["WEBHOOK_SECRET"].encode()
MAX_UPTIME_MIN = int(os.environ.get("MAX_UPTIME_MIN", "120"))
# How long the waker watches a `running` box before concluding it is healthy (the idle-stop stops the runner service,
# then the instance, within seconds, so a box that stays `running` this long is serving the job), and how long it waits
# for a `stopping` box to reach `stopped`. Both stay under the function's timeout.
WAKE_OBSERVE_S = int(os.environ.get("WAKE_OBSERVE_S", "90"))
WAKE_STOP_WAIT_S = int(os.environ.get("WAKE_STOP_WAIT_S", "180"))
WAKE_POLL_S = 5
lam = boto3.client("lambda")


def _verify(sig_header: str, body: bytes) -> bool:
    if not sig_header or not sig_header.startswith("sha256="):
        return False
    expected = "sha256=" + hmac.new(WEBHOOK_SECRET, body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, sig_header)


def _webhook(event: dict, function_name: str) -> dict:
    raw = event.get("body") or ""
    body = base64.b64decode(raw) if event.get("isBase64Encoded") else raw.encode()
    headers = {k.lower(): v for k, v in (event.get("headers") or {}).items()}

    if not _verify(headers.get("x-hub-signature-256"), body):
        return {"statusCode": 401, "body": "bad signature"}
    if headers.get("x-github-event") != "workflow_job":
        return {"statusCode": 204, "body": "ignored event"}

    payload = json.loads(body or b"{}")
    if payload.get("action") != "queued":
        return {"statusCode": 204, "body": "not a queued job"}
    labels = (payload.get("workflow_job") or {}).get("labels") or []
    if RUNNER_LABEL not in labels:
        return {"statusCode": 204, "body": "not our runner label"}

    state = _state()
    if state == "stopped":
        _start()
        return {"statusCode": 202, "body": f"starting {INSTANCE_ID}"}
    if state == "pending":
        return {"statusCode": 202, "body": f"{INSTANCE_ID} already starting"}
    # `running` (possibly about to idle-stop, with its runner already offline) or `stopping`: one call cannot settle it,
    # and waiting here would outlive GitHub's 10 s delivery timeout. Hand the wait to an asynchronous copy of ourselves.
    lam.invoke(FunctionName=function_name, InvocationType="Event", Payload=json.dumps({"wake": state}).encode())
    return {"statusCode": 202, "body": f"{INSTANCE_ID} is {state}: waker started"}


def _state() -> str:
    return ec2.describe_instances(InstanceIds=[INSTANCE_ID])["Reservations"][0]["Instances"][0]["State"]["Name"]


def _start() -> None:
    ec2.start_instances(InstanceIds=[INSTANCE_ID])
    ec2.create_tags(
        Resources=[INSTANCE_ID],
        Tags=[{"Key": "TyphonStartedAt", "Value": str(int(time.time()))}],
    )


def _wake() -> dict:
    """Waits out a box on its way down, then starts it; returns as soon as the box is known to serve the queue."""
    t0 = time.monotonic()
    while True:
        state = _state()
        elapsed = time.monotonic() - t0
        if state == "stopped":
            _start()
            return {"woke": True, "after_s": round(elapsed)}
        if state == "pending":
            return {"woke": False, "reason": "already starting"}
        if state == "running" and elapsed >= WAKE_OBSERVE_S:
            return {"woke": False, "reason": f"still running after {WAKE_OBSERVE_S} s: serving the queue"}
        if elapsed >= WAKE_OBSERVE_S + WAKE_STOP_WAIT_S:
            return {"woke": False, "reason": f"gave up in state {state}"}
        time.sleep(WAKE_POLL_S)


def _max_uptime_backstop() -> dict:
    inst = ec2.describe_instances(InstanceIds=[INSTANCE_ID])["Reservations"][0]["Instances"][0]
    if inst["State"]["Name"] != "running":
        return {"stopped": False, "reason": "not running"}
    started = next((int(t["Value"]) for t in inst.get("Tags", []) if t["Key"] == "TyphonStartedAt"), None)
    if started is None:
        # Started outside the webhook, tag removed: measure from the launch, never skip — a box the backstop ignores can
        # run until the bill says so (#1048).
        started = int(inst["LaunchTime"].timestamp())
    if time.time() - started > MAX_UPTIME_MIN * 60:
        ec2.stop_instances(InstanceIds=[INSTANCE_ID])
        return {"stopped": True, "reason": f"exceeded {MAX_UPTIME_MIN} min uptime"}
    return {"stopped": False, "reason": "within max uptime"}


def handler(event, context):
    if "wake" in event:  # asynchronous self-invocation from the webhook path
        return _wake()
    if "body" in event or "headers" in event:  # API Gateway (webhook)
        return _webhook(event, context.function_name)
    return _max_uptime_backstop()  # EventBridge scheduled tick

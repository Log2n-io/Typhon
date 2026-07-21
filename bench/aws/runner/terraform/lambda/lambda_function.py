"""Typhon CI power-toggle Lambda (#466).

Two entry paths, routed by event shape:

  * API Gateway (GitHub `workflow_job` webhook)  -> START the persistent runner box.
        Verify the HMAC (X-Hub-Signature-256), require action=="queued" AND our runner label in
        workflow_job.labels, then ec2:StartInstances (no-op if already running) and stamp a
        `TyphonStartedAt` tag so the max-uptime backstop can bound the run.

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


def _verify(sig_header: str, body: bytes) -> bool:
    if not sig_header or not sig_header.startswith("sha256="):
        return False
    expected = "sha256=" + hmac.new(WEBHOOK_SECRET, body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, sig_header)


def _webhook(event: dict) -> dict:
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

    ec2.start_instances(InstanceIds=[INSTANCE_ID])  # idempotent — no-op if already running
    ec2.create_tags(
        Resources=[INSTANCE_ID],
        Tags=[{"Key": "TyphonStartedAt", "Value": str(int(time.time()))}],
    )
    return {"statusCode": 202, "body": f"starting {INSTANCE_ID}"}


def _max_uptime_backstop() -> dict:
    inst = ec2.describe_instances(InstanceIds=[INSTANCE_ID])["Reservations"][0]["Instances"][0]
    if inst["State"]["Name"] != "running":
        return {"stopped": False, "reason": "not running"}
    started = next((int(t["Value"]) for t in inst.get("Tags", []) if t["Key"] == "TyphonStartedAt"), None)
    if started is None:
        return {"stopped": False, "reason": "no TyphonStartedAt tag"}
    if time.time() - started > MAX_UPTIME_MIN * 60:
        ec2.stop_instances(InstanceIds=[INSTANCE_ID])
        return {"stopped": True, "reason": f"exceeded {MAX_UPTIME_MIN} min uptime"}
    return {"stopped": False, "reason": "within max uptime"}


def handler(event, _context):
    if "body" in event or "headers" in event:  # API Gateway (webhook)
        return _webhook(event)
    return _max_uptime_backstop()  # EventBridge scheduled tick

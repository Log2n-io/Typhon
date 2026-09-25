#!/usr/bin/env python3
"""
Self-tests for the CI runner's power-toggle Lambda (`bench/aws/runner/terraform/lambda/lambda_function.py`, #1048).

WHY: a job queued while the box was on its way down (idle-stop deciding, or the instance `stopping`) was answered with one
start-instances call — a no-op or a refusal — and then waited until GitHub dropped it. The webhook now hands such a box to
an asynchronous waker. These tests drive both paths and the max-uptime backstop against a fake EC2 whose state moves as the
waker polls; boto3 is replaced before import, so nothing reaches AWS.

Run:  python3 -m unittest discover -s scripts/tests -v
"""
import datetime
import hashlib
import hmac
import importlib.util
import json
import os
import sys
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
LAMBDA_PY = os.path.join(REPO, "bench", "aws", "runner", "terraform", "lambda", "lambda_function.py")
SECRET = b"s3cret"


class FakeEc2:
    """A box whose state follows a script: each describe consumes the next state, the last one repeats."""

    def __init__(self, states, tags=None, launch=None):
        self.states = list(states)
        self.tags = tags if tags is not None else [{"Key": "TyphonStartedAt", "Value": "0"}]
        self.launch = launch or datetime.datetime(2026, 1, 1, tzinfo=datetime.timezone.utc)
        self.started = self.stopped = 0

    def describe_instances(self, InstanceIds):
        state = self.states.pop(0) if len(self.states) > 1 else self.states[0]
        return {"Reservations": [{"Instances": [{"State": {"Name": state}, "Tags": self.tags, "LaunchTime": self.launch}]}]}

    def start_instances(self, InstanceIds):
        self.started += 1

    def stop_instances(self, InstanceIds):
        self.stopped += 1

    def create_tags(self, Resources, Tags):
        self.tags = Tags


class FakeLambda:
    def __init__(self):
        self.invocations = []

    def invoke(self, FunctionName, InvocationType, Payload):
        self.invocations.append((FunctionName, InvocationType, json.loads(Payload)))


def load(ec2):
    boto3 = types.ModuleType("boto3")
    lam = FakeLambda()
    boto3.client = lambda name: ec2 if name == "ec2" else lam
    sys.modules["boto3"] = boto3
    os.environ.update(INSTANCE_ID="i-test", WEBHOOK_SECRET=SECRET.decode(), MAX_UPTIME_MIN="120",
                      WAKE_OBSERVE_S="10", WAKE_STOP_WAIT_S="20")
    spec = importlib.util.spec_from_file_location("toggle", LAMBDA_PY)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    clock = [0.0]
    mod.time = types.SimpleNamespace(monotonic=lambda: clock[0], time=lambda: 1_000_000.0,
                                     sleep=lambda s: clock.__setitem__(0, clock[0] + s))
    return mod, lam


def webhook_event(labels=("self-hosted", "linux", "typhon-c6id"), action="queued"):
    body = json.dumps({"action": action, "workflow_job": {"labels": list(labels)}}).encode()
    sig = "sha256=" + hmac.new(SECRET, body, hashlib.sha256).hexdigest()
    return {"body": body.decode(), "headers": {"X-Hub-Signature-256": sig, "X-GitHub-Event": "workflow_job"}}


CONTEXT = types.SimpleNamespace(function_name="typhon-ci-toggle")


class WebhookTests(unittest.TestCase):
    def test_a_stopped_box_is_started_at_once(self):
        ec2 = FakeEc2(["stopped"])
        mod, lam = load(ec2)
        self.assertEqual(mod.handler(webhook_event(), CONTEXT)["statusCode"], 202)
        self.assertEqual(ec2.started, 1)
        self.assertEqual(lam.invocations, [])

    def test_a_box_on_its_way_down_is_handed_to_the_waker(self):
        for state in ("running", "stopping"):
            ec2 = FakeEc2([state])
            mod, lam = load(ec2)
            mod.handler(webhook_event(), CONTEXT)
            self.assertEqual(ec2.started, 0, state)
            self.assertEqual(lam.invocations, [("typhon-ci-toggle", "Event", {"wake": state})], state)

    def test_a_starting_box_needs_nothing(self):
        ec2 = FakeEc2(["pending"])
        mod, lam = load(ec2)
        mod.handler(webhook_event(), CONTEXT)
        self.assertEqual((ec2.started, lam.invocations), (0, []))

    def test_other_labels_and_bad_signatures_do_nothing(self):
        ec2 = FakeEc2(["stopped"])
        mod, _ = load(ec2)
        self.assertEqual(mod.handler(webhook_event(labels=("ubuntu-latest",)), CONTEXT)["statusCode"], 204)
        bad = webhook_event()
        bad["headers"]["X-Hub-Signature-256"] = "sha256=00"
        self.assertEqual(mod.handler(bad, CONTEXT)["statusCode"], 401)
        self.assertEqual(ec2.started, 0)


class WakerTests(unittest.TestCase):
    def test_the_race_the_job_queued_while_the_box_stops_is_started_once_it_has_stopped(self):
        # 2026-09-25: idle-stop stopping the box as a job was queued. running -> stopping ... -> stopped.
        ec2 = FakeEc2(["running", "stopping", "stopping", "stopping", "stopped"])
        mod, _ = load(ec2)
        result = mod.handler({"wake": "running"}, CONTEXT)
        self.assertTrue(result["woke"], result)
        self.assertEqual(ec2.started, 1)

    def test_a_box_that_stays_running_is_left_alone(self):
        ec2 = FakeEc2(["running"])
        mod, _ = load(ec2)
        result = mod.handler({"wake": "running"}, CONTEXT)
        self.assertFalse(result["woke"])
        self.assertIn("serving the queue", result["reason"])
        self.assertEqual(ec2.started, 0)

    def test_a_stop_that_never_ends_is_given_up_within_the_budget(self):
        ec2 = FakeEc2(["stopping"])
        mod, _ = load(ec2)
        result = mod.handler({"wake": "stopping"}, CONTEXT)
        self.assertFalse(result["woke"])
        self.assertIn("gave up", result["reason"])


class BackstopTests(unittest.TestCase):
    def test_a_box_without_the_tag_is_measured_from_its_launch(self):
        launched = datetime.datetime.fromtimestamp(1_000_000.0 - 121 * 60, datetime.timezone.utc)
        ec2 = FakeEc2(["running"], tags=[], launch=launched)
        mod, _ = load(ec2)
        self.assertTrue(mod.handler({}, CONTEXT)["stopped"])
        self.assertEqual(ec2.stopped, 1)

    def test_a_recent_box_is_kept(self):
        ec2 = FakeEc2(["running"], tags=[{"Key": "TyphonStartedAt", "Value": str(1_000_000 - 60)}])
        mod, _ = load(ec2)
        self.assertFalse(mod.handler({}, CONTEXT)["stopped"])


if __name__ == "__main__":
    unittest.main()

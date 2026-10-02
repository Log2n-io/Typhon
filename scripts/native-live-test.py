#!/usr/bin/env python3
"""The native SDK's live differential test (design/Subscriptions/13 § 7).

1. Starts test/Typhon.Subscriptions.E2EHost, which serves a small deterministic world over TCP and prints `PORT <n>`.
2. Runs the native `typhon_client_live` against it: it applies what it receives, sends a command and waits for its echo, and writes
   every message it received (record.bin) and its replica after every frame (snapshots.json).
3. Runs Typhon.Client.Tests' NativeLiveDifferentialTests on that output: the .NET client replays the same bytes and must hold the same
   replica after every frame.

A run in which the .NET fixture did not execute — filtered out, ignored, missing — fails: a differential check that compared nothing is the
false green this script exists to prevent.

Usage:
    python3 scripts/native-live-test.py --live <path to typhon_client_live[.exe]> [--config Release] [--no-build] [--frames 60]
"""

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOST = os.path.join(REPO, "test", "Typhon.Subscriptions.E2EHost", "Typhon.Subscriptions.E2EHost.csproj")
CLIENT_TESTS = os.path.join(REPO, "test", "Typhon.Client.Tests", "Typhon.Client.Tests.csproj")
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def fail(message):
    print(f"native-live-test: {message}", file=sys.stderr)
    return 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", required=True, help="the native typhon_client_live executable")
    parser.add_argument("--config", default="Release")
    parser.add_argument("--no-build", action="store_true", help="use the host and client tests as last built")
    parser.add_argument("--frames", type=int, default=60)
    args = parser.parse_args()

    args.live = os.path.abspath(args.live)
    if not os.path.isfile(args.live):
        return fail(f"{args.live} does not exist: build the native SDK first (cmake --build)")

    if not args.no_build:
        for project in (HOST, CLIENT_TESTS):
            if subprocess.run(["dotnet", "build", project, "-c", args.config, "-v", "q", "-nologo"]).returncode != 0:
                return fail(f"cannot build {project}")

    work = tempfile.mkdtemp(prefix="typhon-native-live-")
    # The built assembly, not `dotnet run`: a wrapper process would be the one a timeout kills, leaving the host serving.
    dll = os.path.join(os.path.dirname(HOST), "bin", args.config, "net10.0", "Typhon.Subscriptions.E2EHost.dll")
    if not os.path.isfile(dll):
        return fail(f"{dll} does not exist: build the E2E host first")

    host = subprocess.Popen(["dotnet", dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    try:
        port = None
        # The host prints PORT once it accepts connections; a host that never does is a failure, not a hang.
        reader = {}

        def read_port():
            for line in host.stdout:
                if line.startswith("PORT "):
                    reader["port"] = int(line.split()[1])
                    return

        thread = threading.Thread(target=read_port, daemon=True)
        thread.start()
        thread.join(timeout=120)
        port = reader.get("port")
        if port is None:
            return fail("the E2E host never printed its port")

        print(f"native-live-test: host on port {port}", flush=True)
        live = subprocess.run([args.live, "--port", str(port), "--frames", str(args.frames), "--out", work], timeout=90)
        if live.returncode != 0:
            return fail(f"the native client failed its live run (exit {live.returncode})")

        trx_dir = os.path.join(work, "trx")
        env = dict(os.environ, TYPHON_NATIVE_LIVE_DIR=work)
        test = subprocess.run(["dotnet", "test", CLIENT_TESTS, "-c", args.config, "--no-build",
                               "--filter", "TestCategory=NativeSdk",
                               "--logger", "trx;LogFileName=native-live.trx", "--results-directory", trx_dir], env=env)
        trx = os.path.join(trx_dir, "native-live.trx")
        if not os.path.isfile(trx):
            return fail("the .NET differential fixture produced no results")

        counters = ET.parse(trx).getroot().find(f"{NS}ResultSummary/{NS}Counters")
        executed = int(counters.get("executed", "0"))
        passed = int(counters.get("passed", "0"))
        if test.returncode != 0 or executed == 0 or passed != executed:
            return fail(f"the .NET differential fixture: executed {executed}, passed {passed} (exit {test.returncode})")

        print(f"native-live-test: ok — {passed} differential test(s) passed", flush=True)
        return 0
    finally:
        # Closing stdin is the host's stop signal; it deletes its own database on the way out.
        try:
            host.stdin.close()
            host.wait(timeout=30)
        except subprocess.TimeoutExpired:
            host.kill()
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

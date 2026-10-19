#!/usr/bin/env python3
"""Smoke test of the native SDK's installed package (design/Subscriptions/07-delivery.md § 5).

From an install prefix (`cmake --install … --prefix <dir>`, or an unpacked release archive) it configures a fresh CMake project that finds the
SDK with `find_package(TyphonClient)` alone, links a C program against `Typhon::typhon_client` (static) and — when the prefix has it —
`Typhon::typhon_client_shared`, and runs both. A missing header, an unexported symbol, a broken package config or a library that does not load
fails here, before a release ships it.

Usage: python3 scripts/native-package-smoke-test.py <install-prefix>
Exit 0 = PASS.
"""

import os
import shutil
import subprocess
import sys
import tempfile

CMAKE = r"""cmake_minimum_required(VERSION 3.25)
project(typhon_native_smoke LANGUAGES C CXX)
find_package(TyphonClient REQUIRED CONFIG)
add_executable(smoke_static smoke.c)
target_link_libraries(smoke_static PRIVATE Typhon::typhon_client)
set_target_properties(smoke_static PROPERTIES LINKER_LANGUAGE CXX)
if(TARGET Typhon::typhon_client_shared)
  add_executable(smoke_shared smoke.c)
  target_link_libraries(smoke_shared PRIVATE Typhon::typhon_client_shared)
endif()
"""

SMOKE_C = r"""#include <stdio.h>
#include <typhon/client.h>

int main(void)
{
    /* Restoring the default allocator hooks with no client alive succeeds: the call crosses the C ABI and runs the library's code. */
    typhon_status status = typhon_set_allocator(NULL);
    if (status != TYPHON_OK)
    {
        fprintf(stderr, "typhon_set_allocator returned %d\n", (int)status);
        return 1;
    }

    printf("NATIVE OK\n");
    return 0;
}
"""


def main():
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2

    prefix = os.path.abspath(sys.argv[1])
    work = tempfile.mkdtemp(prefix="typhon-native-smoke-")
    try:
        with open(os.path.join(work, "CMakeLists.txt"), "w", encoding="utf-8") as f:
            f.write(CMAKE)
        with open(os.path.join(work, "smoke.c"), "w", encoding="utf-8") as f:
            f.write(SMOKE_C)
        build = os.path.join(work, "build")
        subprocess.run(["cmake", "-S", work, "-B", build, f"-DCMAKE_PREFIX_PATH={prefix}", "-DCMAKE_BUILD_TYPE=Release"], check=True)
        subprocess.run(["cmake", "--build", build, "--config", "Release"], check=True)

        ran = 0
        for name in ("smoke_static", "smoke_shared"):
            exe = next((os.path.join(d, f) for d, _, files in os.walk(build) for f in files if f in (name, name + ".exe")), None)
            if exe is None:
                if name == "smoke_static":
                    print("native-smoke: FAIL — smoke_static was not built", file=sys.stderr)
                    return 1
                continue
            env = dict(os.environ)
            # The shared library is found where the package installed it: bin/ on Windows, lib/ elsewhere.
            if os.name == "nt":
                env["PATH"] = os.path.join(prefix, "bin") + os.pathsep + env.get("PATH", "")
            else:
                env["LD_LIBRARY_PATH"] = os.path.join(prefix, "lib") + os.pathsep + env.get("LD_LIBRARY_PATH", "")
                env["DYLD_LIBRARY_PATH"] = os.path.join(prefix, "lib") + os.pathsep + env.get("DYLD_LIBRARY_PATH", "")
            out = subprocess.run([exe], env=env, capture_output=True, text=True, timeout=60)
            print(f"  {name}> {out.stdout.strip()} {out.stderr.strip()}")
            if out.returncode != 0 or "NATIVE OK" not in out.stdout:
                print(f"native-smoke: FAIL — {name}", file=sys.stderr)
                return 1
            ran += 1

        print(f"native-smoke: PASS ({ran} linkage{'s' if ran != 1 else ''})")
        return 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

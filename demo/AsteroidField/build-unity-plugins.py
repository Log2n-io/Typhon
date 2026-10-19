#!/usr/bin/env python3
"""Builds Typhon.Protocol and Typhon.Client for netstandard2.1 (the target Unity loads) and copies them into the Unity project's Assets/Plugins/Typhon.

Run it once before opening the project, and again after changing either library. Nothing else is needed: the two libraries depend only on
System.Text.Json 8.0 and System.Runtime.CompilerServices.Unsafe 6.0, which Unity 6.6 ships itself (Editor/Data/BCLExtensions), so no other DLL goes
beside them — a second copy of an assembly Unity already loads would be refused.

Usage: python3 demo/AsteroidField/build-unity-plugins.py [--debug]
"""

import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
PLUGINS = os.path.join(HERE, "Unity", "Assets", "Plugins", "Typhon")


def main():
    config = "Debug" if "--debug" in sys.argv else "Release"
    os.makedirs(PLUGINS, exist_ok=True)
    for name in ("Typhon.Protocol", "Typhon.Client"):
        project = os.path.join(REPO, "src", name, f"{name}.csproj")
        subprocess.run(["dotnet", "build", project, "-c", config, "-f", "netstandard2.1", "-nologo", "-v", "q"], check=True)
        out = os.path.join(REPO, "src", name, "bin", config, "netstandard2.1")
        for ext in (".dll", ".pdb", ".xml"):
            source = os.path.join(out, name + ext)
            if os.path.isfile(source):
                shutil.copy2(source, PLUGINS)
    print(f"build-unity-plugins: Typhon.Protocol and Typhon.Client ({config}, netstandard2.1) are in {PLUGINS}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

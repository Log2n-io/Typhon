#!/usr/bin/env python3
"""Compiles the Unity project's scripts outside Unity, the way Unity compiles them: C# 9, netstandard2.1, against an installed editor's UnityEngine
assemblies and the netstandard2.1 builds of Typhon.Protocol and Typhon.Client. A quick check that needs no Unity licence and no editor running — but an
installed editor, so it is a local check, not a CI one. It does not replace opening the project: Unity's own compiler and its player builds are the truth.

Usage: python3 demo/AsteroidField/check-unity-scripts.py [--unity <Editor/Data dir>]
       (default: the newest editor under C:/Program Files/Unity/Hub/Editor, or $UNITY_EDITOR_DATA)
Exit 0 = the scripts compile.
"""

import argparse
import glob
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
ASSETS = os.path.join(HERE, "Unity", "Assets", "AsteroidField")


def editor_data(explicit):
    if explicit:
        return explicit
    if os.environ.get("UNITY_EDITOR_DATA"):
        return os.environ["UNITY_EDITOR_DATA"]
    editors = sorted(glob.glob("C:/Program Files/Unity/Hub/Editor/*/Editor/Data"))
    return editors[-1] if editors else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--unity")
    args = parser.parse_args()
    data = editor_data(args.unity)
    if data is None or not os.path.isdir(os.path.join(data, "Managed", "UnityEngine")):
        print("check-unity-scripts: no Unity editor found (pass --unity <Editor/Data>)", file=sys.stderr)
        return 2

    modules = sorted(glob.glob(os.path.join(data, "Managed", "UnityEngine", "*.dll")))
    refs = "\n".join(f'    <Reference Include="{os.path.splitext(os.path.basename(m))[0]}"><HintPath>{m}</HintPath><Private>false</Private></Reference>'
                     for m in modules)
    work = tempfile.mkdtemp(prefix="unity-scripts-check-")
    try:
        with open(os.path.join(work, "UnityScripts.csproj"), "w", encoding="utf-8") as f:
            f.write(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{ASSETS}/**/*.cs" />
    <ProjectReference Include="{REPO}/src/Typhon.Client/Typhon.Client.csproj" SetTargetFramework="TargetFramework=netstandard2.1" />
    <ProjectReference Include="{REPO}/src/Typhon.Protocol/Typhon.Protocol.csproj" SetTargetFramework="TargetFramework=netstandard2.1" />
{refs}
  </ItemGroup>
</Project>
""")
        r = subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"], cwd=work, capture_output=True, text=True)
        errors = sorted({line.strip() for line in r.stdout.splitlines() if ": error " in line or ": warning " in line})
        for e in errors:
            print(e)
        print("check-unity-scripts: OK" if r.returncode == 0 else "check-unity-scripts: FAIL")
        return r.returncode
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

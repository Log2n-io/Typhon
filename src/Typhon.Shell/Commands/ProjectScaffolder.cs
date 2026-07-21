using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Typhon.Shell.Commands;

/// <summary>
/// Materialises the <c>typhon new</c> starter project. The schema (<c>Harvester.cs</c>) and the app template
/// (<c>Program.cs</c>/<c>Systems.cs</c>/<c>typhon.telemetry.json</c>/<c>.gitignore</c>) are emitted <b>verbatim</b> from
/// resources embedded in this assembly — single-sourced from the in-repo SWG Light sample and the guide example, so the
/// scaffold can never drift from what the guide teaches (#532/F2). The <c>.csproj</c> and <c>README.md</c> are generated
/// per-project: the csproj carries a single pinned <c>Typhon</c> package reference (the published engine + bundled
/// consumer generator), so the emitted project builds and profiles with no manual edits.
/// </summary>
internal static class ProjectScaffolder
{
    /// <summary>The published <c>Typhon</c> package version the scaffold pins. Bump on each engine release.</summary>
    internal const string TyphonPackageVersion = "0.0.1-alpha.3";

    /// <summary>Embedded template resources (assembly-manifest logical name → emitted file name), copied byte-for-byte.</summary>
    internal static readonly IReadOnlyList<(string ResourceName, string OutputFile)> EmbeddedTemplates = new[]
    {
        ("Typhon.Shell.Templates.Harvester.cs", "Harvester.cs"),
        ("Typhon.Shell.Templates.Program.cs", "Program.cs"),
        ("Typhon.Shell.Templates.Systems.cs", "Systems.cs"),
        ("Typhon.Shell.Templates.typhon.telemetry.json", "typhon.telemetry.json"),
        ("Typhon.Shell.Templates.gitignore", ".gitignore"),
    };

    private static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    /// <summary>Validate a project name — a safe directory name and a plausible C# root namespace.</summary>
    internal static bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name) && NamePattern.IsMatch(name);

    /// <summary>
    /// Emit the starter project into <paramref name="targetDir"/> (created if absent). Writes the embedded templates
    /// verbatim, then the generated <c>{projectName}.csproj</c> + <c>README.md</c>.
    /// </summary>
    internal static void Emit(string targetDir, string projectName)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);
        ArgumentException.ThrowIfNullOrEmpty(projectName);
        Directory.CreateDirectory(targetDir);

        var asm = typeof(ProjectScaffolder).Assembly;
        foreach (var (resource, outputFile) in EmbeddedTemplates)
        {
            using var stream = asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Scaffold template resource '{resource}' is missing from the assembly — check the <EmbeddedResource> includes in Typhon.Shell.csproj.");
            using var output = File.Create(Path.Combine(targetDir, outputFile));
            stream.CopyTo(output);
        }

        File.WriteAllText(Path.Combine(targetDir, projectName + ".csproj"), CsprojContent());
        File.WriteAllText(Path.Combine(targetDir, "README.md"), ReadmeContent(projectName));
    }

    private static string CsprojContent() =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            <Nullable>disable</Nullable>
          </PropertyGroup>

          <ItemGroup>
            <!-- The one dependency: the Typhon engine + bundled Schema.Definition + the source generator that emits
                 the archetype accessors. Nothing else to add. -->
            <PackageReference Include="Typhon" Version="{TyphonPackageVersion}" />
          </ItemGroup>

          <ItemGroup>
            <!-- Config-driven profiling: copied next to the exe so the engine self-wires and writes ./captures/*.typhon-trace. -->
            <None Update="typhon.telemetry.json" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>

        </Project>

        """;

    private static string ReadmeContent(string projectName) =>
        $"""
        # {projectName}

        A Typhon starter app, scaffolded by `typhon new`. It models a small world of roaming **harvester drones** (the
        SWG Light sample), runs the Typhon runtime for a few dozen ticks, and — because profiling is enabled in
        `typhon.telemetry.json` — writes a profiler trace you can open in the Workbench.

        ## Run it

        ```bash
        dotnet run
        ```

        The first run restores the `Typhon` package from NuGet, spawns drones, ticks the runtime, and writes a
        non-empty `./captures/guide.typhon-trace` — with zero edits.

        ## Explore the trace

        ```bash
        typhon ui --open-latest
        ```

        ## What's here

        | File | What it is |
        |------|------------|
        | `Harvester.cs` | The data model — the `Harvester` archetype and its components (one per storage mode + spatial + index). |
        | `Systems.cs` | The tick-loop systems: spawn drones, roam, keep the spatial index coherent, accumulate cargo. |
        | `Program.cs` | Opens the engine, walks the API (spawn / read / transact / query / view), then runs the runtime. |
        | `typhon.telemetry.json` | Turns on config-driven profiling (the engine self-wires it; no code needed). |

        Edit the components and systems to model your own world. Change what's profiled with `typhon telemetry`.

        """;
}

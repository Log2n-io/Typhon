using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Typhon.Shell.Commands;

/// <summary>
/// Materialises the <c>typhon new</c> starter project. The schema (<c>Character.cs</c>) and the app template
/// (<c>Program.cs</c>/<c>Systems.cs</c>/<c>typhon.telemetry.json</c>/<c>.gitignore</c>) are emitted <b>verbatim</b> from
/// resources embedded in this assembly — single-sourced from the in-repo SWG Light sample and the guide example, so the
/// scaffold can never drift from what the guide teaches (#532/F2). The <c>.csproj</c> and <c>README.md</c> are generated
/// per-project: the csproj carries a single pinned <c>Typhon</c> package reference (the published engine + bundled
/// consumer generator), so the emitted project builds and profiles with no manual edits.
/// </summary>
internal static class ProjectScaffolder
{
    /// <summary>
    /// The <c>Typhon</c> package version the scaffold pins. Tracks the CLI's <b>own</b> version — the tool and the
    /// engine package ship together from the same git tag (MinVer), so the CLI's version is exactly the matching
    /// engine version. This removes the manual per-release bump that previously lagged: a stale pin scaffolds a
    /// pre-#514 engine whose <c>[Archetype]</c> still requires an id, so <c>dotnet run</c> fails to compile.
    /// </summary>
    internal static readonly string TyphonPackageVersion = ResolveTyphonPackageVersion();

    /// <summary>Known-good published fallback used only when the assembly carries no MinVer-stamped prerelease version.</summary>
    private const string FallbackTyphonPackageVersion = "0.0.1-alpha.4";

    /// <summary>
    /// Reads the CLI's own package version from its MinVer-stamped <see cref="AssemblyInformationalVersionAttribute"/>
    /// (format <c>&lt;version&gt;+&lt;sha&gt;</c>), returning the NuGet-valid <c>&lt;version&gt;</c>. Falls back to
    /// <see cref="FallbackTyphonPackageVersion"/> when the attribute is absent or not a prerelease (e.g. a bare
    /// <c>dotnet build</c> with the default <c>1.0.0</c>), since the engine is published prerelease-only.
    /// </summary>
    private static string ResolveTyphonPackageVersion()
    {
        var informational = typeof(ProjectScaffolder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return FallbackTyphonPackageVersion;
        }

        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;

        return version.Contains('-') ? version : FallbackTyphonPackageVersion;
    }

    /// <summary>Embedded template resources (assembly-manifest logical name → emitted file name), copied byte-for-byte.</summary>
    internal static readonly IReadOnlyList<(string ResourceName, string OutputFile)> EmbeddedTemplates = new[]
    {
        ("Typhon.Shell.Templates.Character.cs", "Character.cs"),
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
            <!-- Config-driven profiling: copied next to the exe so the engine self-wires. It names no output path, so the
                 capture lands in the database's own world-shard.typhon/profilings/ directory. -->
            <None Update="typhon.telemetry.json" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>

        </Project>

        """;

    private static string ReadmeContent(string projectName) =>
        $"""
        # {projectName}

        A Typhon starter app, scaffolded by `typhon new`. It models a small **real-time world shard** — a planet full of
        characters that roam, regenerate their HAM pools, and trade credits — runs the Typhon runtime for a few hundred
        ticks, and — because profiling is enabled in `typhon.telemetry.json` — writes a profiler trace you can open in
        the Workbench.

        ## Run it

        ```bash
        dotnet run
        ```

        The first run restores the `Typhon` package from NuGet, deploys the shard, ticks the runtime, and records a
        non-empty capture into `world-shard.typhon/profilings/` — with zero edits. Captures live with the database
        they describe, so the Workbench can correlate a capture with the data it was recorded against.

        ## Explore the trace

        ```bash
        typhon ui --open-latest
        ```

        ## What's here

        | File | What it is |
        |------|------------|
        | `Character.cs` | The data model — the `Character` archetype and its components, each in the storage mode its access pattern needs (SingleVersion hot state + spatial + index, one Versioned wallet, Transient scratch). |
        | `Systems.cs` | The tick-loop systems: spawn characters, move + regenerate them lock-free, keep the spatial index coherent, and settle credit trades as atomic Versioned transactions. |
        | `Program.cs` | Opens the engine, walks the API (spawn / read / transact / query / view), then runs the runtime. |
        | `typhon.telemetry.json` | Turns on config-driven profiling (the engine self-wires it; no code needed). |

        Edit the components and systems to model your own world. Change what's profiled with `typhon telemetry`.

        """;
}

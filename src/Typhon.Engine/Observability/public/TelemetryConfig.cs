// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Global telemetry configuration for Typhon Engine.
///
/// <para>
/// This class provides <c>static readonly</c> fields that allow the JIT compiler to
/// eliminate disabled telemetry code paths entirely. When a readonly field is <c>false</c>,
/// the JIT can treat <c>if (TelemetryConfig.ProfilerActive)</c> as dead code and
/// remove it completely in Tier 1 compilation.
/// </para>
///
/// <para>
/// <b>Source of truth:</b> every gate flag (the <c>*Active</c> / <c>*Enabled</c> fields) and the static
/// constructor that resolves them are GENERATED from <c>Observability/telemetry-flags.jsonc</c> by
/// <c>Typhon.Generators.Telemetry.TelemetryConfigGenerator</c> (see the generated partial in
/// <c>TelemetryConfig.g.cs</c>). This file holds only the non-gate fields, the config-reading helpers,
/// and the diagnostics. Add or change a flag by editing the catalog, not this file.
/// </para>
///
/// <para>
/// <b>IMPORTANT:</b> Call <see cref="EnsureInitialized"/> once at application startup,
/// BEFORE any hot paths are JIT compiled. This ensures the static constructor runs
/// early and the JIT sees the final values when compiling performance-critical methods.
/// </para>
///
/// <para>
/// Configuration precedence (highest to lowest):
/// <list type="number">
///   <item>Environment variables (TYPHON__PROFILER__ENABLED, etc.)</item>
///   <item>typhon.telemetry.json in current directory</item>
///   <item>typhon.telemetry.json next to the assembly</item>
///   <item>Built-in defaults (all disabled)</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Environment variable naming uses double underscore (<c>__</c>) as hierarchy separator
/// for cross-platform compatibility:
/// <code>
/// TYPHON__PROFILER__ENABLED=true
/// TYPHON__PROFILER__GCTRACING__ENABLED=true
/// TYPHON__PROFILER__SCHEDULER__GAUGES__STRAGGLERGAP__ENABLED=true
/// </code>
/// </remarks>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static partial class TelemetryConfig
{
    // ═══════════════════════════════════════════════════════════════════════════
    // NON-GATE FIELDS (declared here; assigned by the generated static constructor)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Producer-side duration threshold (ms) for kinds 56/57/58 (PageCache:DiskRead/Write/Flush Completed).
    /// When &gt; 0 the emit path skips records whose duration is shorter than the threshold; when 0 it
    /// matches today's behaviour (always emit when un-suppressed). Default: 1 ms.
    /// </summary>
    public static readonly int StoragePageCacheCompletionThresholdMs;

    /// <summary>
    /// The configuration file path that was loaded, or null if using defaults/env vars only.
    /// </summary>
    public static readonly string LoadedConfigurationFile;

    /// <summary>
    /// The merged configuration built by <see cref="BuildConfiguration"/> — <c>typhon.telemetry.json</c> (current dir then assembly dir) overlaid with
    /// environment variables. Exposed so the profiler bootstrap can resolve <see cref="ProfilerLaunchConfig.FromConfiguration"/> from the same source without
    /// re-running the multi-location probe.
    /// </summary>
    internal static readonly IConfiguration Configuration;

    /// <summary>
    /// The profiler launch config resolved from the file/environment layer only — <c>typhon.telemetry.json</c> plus <c>TYPHON__PROFILER__*</c> variables. The
    /// process command line is deliberately NOT read here: a host parses its own arguments and injects the launch config through DI (<c>AddTyphonProfiler</c>),
    /// which <see cref="Typhon.Engine.internals.ProfilerBootstrap"/> merges on top of this. An active value here also turns <see cref="ProfilerActive"/> on — declaring an output
    /// channel in config enables the profiler.
    /// </summary>
    internal static readonly ProfilerLaunchConfig ProfilerLaunch;

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIG READING HELPERS (used by the generated static constructor)
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool ReadBool(IConfiguration config, string key, bool defaultValue)
    {
        var v = config[key];
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        return bool.TryParse(v, out var b) ? b : defaultValue;
    }

    private static int ReadInt(IConfiguration config, string key, int defaultValue)
    {
        var v = config[key];
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        return int.TryParse(v, out var i) ? i : defaultValue;
    }

    private static (IConfiguration config, string loadedPath) BuildConfiguration()
    {
        var builder = new ConfigurationBuilder();
        string loadedPath = null;

        // 1. Look for config file in current directory
        var currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), "typhon.telemetry.json");
        if (File.Exists(currentDirPath))
        {
            builder.AddJsonFile(currentDirPath, true, false);
            loadedPath = currentDirPath;
        }

        // 2. Look for config file next to the assembly (fallback)
        var assemblyLocation = typeof(TelemetryConfig).Assembly.Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var assemblyConfigPath = Path.Combine(assemblyDir, "typhon.telemetry.json");
                if (File.Exists(assemblyConfigPath) && assemblyConfigPath != currentDirPath)
                {
                    builder.AddJsonFile(assemblyConfigPath, true, false);
                    loadedPath ??= assemblyConfigPath;
                }
            }
        }

        // 3. Environment variables override everything
        // Uses __ as hierarchy separator: TYPHON__PROFILER__ENABLED -> Typhon:Profiler:Enabled
        builder.AddEnvironmentVariables();

        return (builder.Build(), loadedPath);
    }

    /// <summary>
    /// Forces early initialization of telemetry configuration.
    /// Call this at application startup to ensure the JIT compiler sees the
    /// readonly field values before compiling hot paths.
    /// </summary>
    /// <remarks>
    /// The <see cref="MethodImplOptions.NoInlining"/> attribute ensures this method
    /// is actually called and not optimized away, guaranteeing the static constructor runs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureInitialized() =>
        // Accessing any static field triggers the static constructor.
        // The NoInlining attribute ensures this method call isn't optimized away.
        _ = ProfilerActive;

    /// <summary>
    /// Returns a human-readable summary of the current telemetry configuration.
    /// Useful for logging at application startup.
    /// </summary>
    /// <returns>A multi-line string describing all telemetry settings.</returns>
    public static string GetConfigurationSummary() =>
        $"""
         Typhon Profiler Configuration:
           Config File: {LoadedConfigurationFile ?? "(none - using defaults/env vars)"}
           Master Active: {ProfilerActive}

           Profiler: Active={ProfilerActive}
             GcTracing={ProfilerGcTracingEnabled} (Active={ProfilerGcTracingActive}),
             MemoryAllocations={ProfilerMemoryAllocationsEnabled} (Active={ProfilerMemoryAllocationsActive}),
             Gauges={ProfilerGaugesEnabled} (Active={ProfilerGaugesActive}),
             CpuSampling={ProfilerCpuSamplingEnabled} (Active={ProfilerCpuSamplingActive})

           Scheduler: Active={SchedulerActive}
             TransitionLatency={SchedulerTrackTransitionLatency}, WorkerUtilization={SchedulerTrackWorkerUtilization},
             StragglerGap={SchedulerTrackStragglerGap}, ArchetypeTouches={SchedulerArchetypeTouchesActive}
         """;

    /// <summary>
    /// Returns a concise one-line summary of active telemetry components.
    /// </summary>
    public static string GetActiveComponentsSummary()
    {
        if (!ProfilerActive)
        {
            return "Profiler: Disabled";
        }

        var active = new System.Collections.Generic.List<string>();

        if (SchedulerActive)
        {
            active.Add("Scheduler");
        }

        if (ProfilerActive)
        {
            var suffix = new System.Collections.Generic.List<string>();
            if (ProfilerGcTracingActive)
            {
                suffix.Add("GcTracing");
            }
            if (ProfilerMemoryAllocationsActive)
            {
                suffix.Add("MemoryAllocations");
            }
            if (ProfilerGaugesActive)
            {
                suffix.Add("Gauges");
            }
            if (ProfilerCpuSamplingActive)
            {
                suffix.Add("CpuSampling");
            }
            active.Add(suffix.Count > 0 ? $"Profiler+{string.Join("+", suffix)}" : "Profiler");
        }

        return active.Count > 0 ? $"Profiler: Enabled [{string.Join(", ", active)}]" : "Profiler: Enabled (no components active)";
    }
}

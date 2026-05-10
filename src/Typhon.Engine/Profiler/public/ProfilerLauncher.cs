using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Host-side helpers that turn a parsed <see cref="ProfilerLaunchConfig"/> into the side effects the host needs:
/// flipping the telemetry gate before <see cref="TelemetryConfig"/> is first read, building the exporter list, and
/// printing a diagnostic banner.
/// </summary>
/// <remarks>
/// Designed so that any Typhon host (AntHill, IOProfileRunner, MonitoringDemo, …) can use the same conventions and
/// the same code paths — no copy-pasted parsing or env-var setup logic across host repos.
/// </remarks>
public static class ProfilerLauncher
{
    /// <summary>
    /// If <paramref name="config"/> requests any profiler output: set <c>TYPHON__PROFILER__ENABLED</c>, call
    /// <see cref="TelemetryConfig.EnsureInitialized"/>, AND eagerly allocate the spillover ring pool. <b>Must run
    /// before any engine type JITs methods that read <see cref="TelemetryConfig.ProfilerActive"/></b> — i.e., before
    /// constructing the bridge / runtime — otherwise the JIT bakes the gate as <c>false</c> and no events are
    /// emitted.
    /// </summary>
    /// <param name="config">Parsed profiler launch options.</param>
    /// <param name="options">Tunables for the spillover pool (and any other engine-side options consumed at this
    /// stage). Defaults to <see cref="ProfilerOptions"/> if omitted, which currently gives an 8 × 16 MiB spillover
    /// pool. The pool is allocated HERE so that events emitted between gate-open and <see cref="TyphonProfiler.Start"/>
    /// — typically during host bridge construction's spawn burst — can extend the chain instead of dropping.</param>
    /// <returns><c>true</c> if the gate was opened, <c>false</c> if the config wasn't active.</returns>
    public static bool EnableTelemetryGateIfActive(ProfilerLaunchConfig config, ProfilerOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.IsActive)
        {
            return false;
        }
        // The TYPHON__PROFILER__ENABLED env var (double-underscore: .NET configuration section separator) is the
        // master gate. Setting it BEFORE EnsureInitialized ensures the static-readonly TelemetryConfig fields read
        // it in their first-load. After this point, all JIT'd code sees ProfilerActive == true.
        Environment.SetEnvironmentVariable("TYPHON__PROFILER__ENABLED", "true");
        TelemetryConfig.EnsureInitialized();

        // Allocate the spillover pool eagerly — events emitted before TyphonProfiler.Start (e.g. AntHill's bulk
        // spawn during bridge initialization) need a place to chain to. Without this, ~11 MiB of EcsSpawn records
        // would silently drop on primary overflow because the pool wasn't allocated yet.
        options ??= new ProfilerOptions();
        options.Validate();
        if (!SpilloverRingPool.IsInitialized)
        {
            SpilloverRingPool.Initialize(options.SpilloverBufferCount, options.SpilloverBufferSizeBytes);
        }
        return true;
    }

    /// <summary>
    /// Construct the exporter list per <paramref name="config"/>. Returns an empty list when the config isn't active
    /// (no trace file, no live port). The caller attaches each via <see cref="TyphonProfiler.AttachExporter"/> and
    /// then calls <see cref="TyphonProfiler.Start"/> — at that point each exporter's <c>Initialize</c> runs, which
    /// for a <see cref="TcpExporter"/> with <see cref="ProfilerLaunchConfig.LiveWaitMs"/> &gt; 0 blocks until the
    /// first viewer connects.
    /// </summary>
    public static List<IProfilerExporter> CreateExporters(ProfilerLaunchConfig config, IResource profilerParent)
    {
        ArgumentNullException.ThrowIfNull(config);
        var exporters = new List<IProfilerExporter>(2);
        if (!config.IsActive)
        {
            return exporters;
        }
        ArgumentNullException.ThrowIfNull(profilerParent);

        if (config.TraceFilePath != null)
        {
            exporters.Add(new FileExporter(config.TraceFilePath, profilerParent));
        }
        if (config.LivePort >= 0)
        {
            exporters.Add(new TcpExporter(config.LivePort, profilerParent, config.LiveWaitMs));
        }
        return exporters;
    }

    /// <summary>
    /// Print a multi-line diagnostic banner showing the active telemetry config + attached exporters. Useful at host
    /// startup when the operator wants visual confirmation that profiling is wired up. Logger delegate lets the host
    /// route output to its own log sink (Godot's <c>GD.Print</c>, <c>Console.WriteLine</c>, etc.).
    /// </summary>
    public static void PrintDiagnostics(Action<string> log, IList<IProfilerExporter> exporters)
    {
        if (log == null)
        {
            return;
        }

        log("───────────────────────────────────────────────────────────");
        log(" Typhon Telemetry Diagnostics");
        log("───────────────────────────────────────────────────────────");
        log(TelemetryConfig.GetActiveComponentsSummary());
        log("");
        log(TelemetryConfig.GetConfigurationSummary());
        log("");
        string exporterSummary;
        if (exporters == null || exporters.Count == 0)
        {
            exporterSummary = "(none — profiling not requested)";
        }
        else
        {
            var names = new string[exporters.Count];
            for (int i = 0; i < exporters.Count; i++)
            {
                names[i] = exporters[i].GetType().Name;
            }
            exporterSummary = string.Join(", ", names);
        }
        log($" Exporters:                 {exporterSummary}");
        log($" ProfilerActive (JIT gate): {TelemetryConfig.ProfilerActive}");
        log($" TyphonProfiler.IsRunning:  {TyphonProfiler.IsRunning}");
        log("───────────────────────────────────────────────────────────");
    }
}

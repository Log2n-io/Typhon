using System;

namespace Typhon.Engine;

/// <summary>
/// Parsed profiler launch options for a host application (AntHill, IOProfileRunner, MonitoringDemo, …).
/// Resolved from CLI args and/or environment variables, then handed to <see cref="ProfilerLauncher"/> to
/// produce the exporter list and (optionally) flip the telemetry gate before <see cref="TyphonProfiler.Start"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Convention.</b> Two output channels are recognized: a sidecar trace file (post-mortem analysis in the
/// workbench's Trace mode) and a TCP listener for live attach (workbench's Attach mode). Either, both, or neither
/// can be configured. <see cref="LiveWaitMs"/> turns the live exporter's <see cref="TcpExporter.Initialize"/> into
/// a synchronous "wait for first client" gate so the host can pause at startup until a viewer attaches.
/// </para>
/// <para>
/// <b>Sources of truth.</b> CLI args take precedence over environment variables (the standard tooling convention).
/// Use <see cref="FromEnvironment"/> + <see cref="FromArgs"/> + <see cref="MergedWith"/> to layer them.
/// </para>
/// <para>
/// <b>Sentinels.</b> Unset state is encoded in-band: <see cref="TraceFilePath"/> is <c>null</c>, <see cref="LivePort"/>
/// is <c>-1</c>, <see cref="LiveWaitMs"/> is <c>0</c>. <see cref="MergedWith"/> uses these sentinels to decide which
/// config "wins" per field, so an unset field in the override doesn't clobber the base.
/// </para>
/// </remarks>
public sealed record ProfilerLaunchConfig
{
    /// <summary>The default port used when <c>--live</c> is given without an explicit number.</summary>
    public const int DefaultLivePort = 9100;

    /// <summary>Path to the .typhon-trace file the <see cref="Exporters.FileExporter"/> writes to, or <c>null</c> for no file output.</summary>
    public string TraceFilePath { get; init; }

    /// <summary>TCP port the <see cref="Exporters.TcpExporter"/> listens on, or <c>-1</c> for no live exporter.</summary>
    public int LivePort { get; init; } = -1;

    /// <summary>
    /// If &gt; 0, <see cref="Exporters.TcpExporter.Initialize"/> blocks up to this many milliseconds waiting for the
    /// first live client to connect. Lets the host pause at startup until the workbench is attached. <c>0</c> disables
    /// the wait — Initialize returns immediately and clients connect when they connect.
    /// </summary>
    public int LiveWaitMs { get; init; }

    /// <summary>True if any output channel is requested (trace file OR live port).</summary>
    public bool IsActive => TraceFilePath != null || LivePort >= 0;

    /// <summary>
    /// Parse from a process-style argv array. Recognized flags:
    /// <list type="bullet">
    ///   <item><c>--trace &lt;path&gt;</c> — sidecar file path</item>
    ///   <item><c>--live [port]</c> — TCP port (default <see cref="DefaultLivePort"/> if omitted or non-numeric)</item>
    ///   <item><c>--live-wait &lt;ms&gt;</c> — synchronous wait timeout in milliseconds</item>
    /// </list>
    /// Unknown flags are ignored — the host is responsible for its own argument parsing pass; this method only picks
    /// up the profiler-specific subset and never throws on bad input (degenerate inputs leave fields at sentinel).
    /// </summary>
    public static ProfilerLaunchConfig FromArgs(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            return new ProfilerLaunchConfig();
        }

        string traceFile = null;
        int livePort = -1;
        int liveWaitMs = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trace" when i + 1 < args.Length:
                    traceFile = args[++i];
                    break;
                case "--live":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                    {
                        livePort = p;
                        i++;
                    }
                    else
                    {
                        livePort = DefaultLivePort;
                    }
                    break;
                case "--live-wait" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var ms) && ms >= 0)
                    {
                        liveWaitMs = ms;
                    }
                    break;
            }
        }

        return new ProfilerLaunchConfig
        {
            TraceFilePath = traceFile,
            LivePort = livePort,
            LiveWaitMs = liveWaitMs,
        };
    }

    /// <summary>
    /// Parse from environment variables:
    /// <list type="bullet">
    ///   <item><c>TYPHON_PROFILER_TRACE</c> — sidecar file path</item>
    ///   <item><c>TYPHON_PROFILER_LIVE</c> — TCP port (or any non-numeric value to use <see cref="DefaultLivePort"/>)</item>
    ///   <item><c>TYPHON_PROFILER_LIVE_WAIT_MS</c> — wait timeout in milliseconds</item>
    /// </list>
    /// Unset variables leave the field at its sentinel.
    /// </summary>
    public static ProfilerLaunchConfig FromEnvironment()
    {
        string traceFile = Environment.GetEnvironmentVariable("TYPHON_PROFILER_TRACE");
        if (string.IsNullOrWhiteSpace(traceFile))
        {
            traceFile = null;
        }

        int livePort = -1;
        string liveEnv = Environment.GetEnvironmentVariable("TYPHON_PROFILER_LIVE");
        if (!string.IsNullOrWhiteSpace(liveEnv))
        {
            livePort = int.TryParse(liveEnv, out var p) ? p : DefaultLivePort;
        }

        int liveWaitMs = 0;
        string waitEnv = Environment.GetEnvironmentVariable("TYPHON_PROFILER_LIVE_WAIT_MS");
        if (!string.IsNullOrWhiteSpace(waitEnv) && int.TryParse(waitEnv, out var ms) && ms >= 0)
        {
            liveWaitMs = ms;
        }

        return new ProfilerLaunchConfig
        {
            TraceFilePath = traceFile,
            LivePort = livePort,
            LiveWaitMs = liveWaitMs,
        };
    }

    /// <summary>
    /// Combine two configs — fields explicitly set in <paramref name="overrideWith"/> win over <c>this</c>.
    /// "Set" means "different from the field's sentinel": <see cref="TraceFilePath"/> non-null, <see cref="LivePort"/>
    /// &ge; 0, <see cref="LiveWaitMs"/> &gt; 0. Use as <c>env.MergedWith(args)</c> for the standard "CLI overrides env"
    /// precedence.
    /// </summary>
    public ProfilerLaunchConfig MergedWith(ProfilerLaunchConfig overrideWith)
    {
        if (overrideWith == null)
        {
            return this;
        }
        return new ProfilerLaunchConfig
        {
            TraceFilePath = overrideWith.TraceFilePath ?? TraceFilePath,
            LivePort = overrideWith.LivePort >= 0 ? overrideWith.LivePort : LivePort,
            LiveWaitMs = overrideWith.LiveWaitMs > 0 ? overrideWith.LiveWaitMs : LiveWaitMs,
        };
    }
}

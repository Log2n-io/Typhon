using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.internals;

/// <summary>
/// Carries a host-supplied profiler-launch override registered through <c>AddTyphonProfiler</c>. Resolved from the
/// service provider by <see cref="ProfilerBootstrap"/> and applied on top of the file/environment configuration.
/// </summary>
internal sealed class ProfilerLaunchOverride
{
    public ProfilerLaunchOverride(Func<ProfilerLaunchConfig, ProfilerLaunchConfig> configure) => Configure = configure;

    /// <summary>Maps the config resolved from file+env to the effective config. May be <c>null</c> (registered with no delegate).</summary>
    public Func<ProfilerLaunchConfig, ProfilerLaunchConfig> Configure { get; }
}

/// <summary>
/// Owns the profiler's entire startup and teardown sequence so hosts need zero profiler code (issue #332).
/// </summary>
/// <remarks>
/// <para>
/// The producer gate (<see cref="TelemetryConfig.ProfilerActive"/>) is driven by <c>typhon.telemetry.json</c>; this type forces that config to load at assembly
/// load (<see cref="Initialize"/>) and, when profiling is active, self-wires the exporters + CPU sampler + session metadata at runtime creation
/// (<see cref="TryStart"/>). Because the whole sequence lives here, the ordering constraint "start the CPU sampler before building metadata" is enforced in one
/// place — a host can no longer get it wrong. Teardown (<see cref="FinishStop"/>) is driven by the engine storage's
/// <c>DisposingEvent</c> (the <c>ManagedPagedMMF</c>, disposed after the <see cref="DatabaseEngine"/>): that fires
/// deterministically on every host and after the engine's shutdown teardown, so the trace is always finalized and
/// captures engine-shutdown events. The process-exit hook is kept only as a backup for hosts that skip disposal.
/// </para>
/// <para>
/// A host that needs to override the file/env config in code registers a delegate via <c>AddTyphonProfiler</c>; it is applied on top of the resolved config
/// (precedence: JSON file → environment → code).
/// </para>
/// </remarks>
internal static class ProfilerBootstrap
{
    private static readonly Lock Gate = new();
    private static bool Started;
    private static List<IProfilerExporter> Exporters;

    /// <summary>One-shot latch for the inert-profiler warning (#792). Written only under <see cref="Gate"/>, so a plain field is correct.</summary>
    private static bool WarnedInert;

    /// <summary>
    /// Runs at <c>Typhon.Engine</c> assembly load. Forces the <see cref="TelemetryConfig"/> static constructor so the JIT producer-gate is baked before any
    /// hot path is compiled, and eagerly allocates the spillover ring pool when profiling is active so events emitted before <see cref="TryStart"/> (a host's
    /// bulk-spawn burst) chain instead of dropping.
    /// </summary>
    // CA2255: a module initializer in a library is intentional here — it is the only way to run engine-side early-init (JIT gate + spillover pool) with zero
    // host code, which is the whole point of issue #332. It does no I/O beyond the config probe TelemetryConfig already performs lazily, so it is safe and
    // order-independent.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        TelemetryConfig.EnsureInitialized();
        // Bake the strict-mode gate (#422) before any hot path JITs, same rationale as the telemetry gate above.
        CheckConfig.EnsureInitialized();

        if (TelemetryConfig.ProfilerActive && !SpilloverRingPool.IsInitialized)
        {
            var options = new ProfilerOptions();
            SpilloverRingPool.Initialize(options.SpilloverBufferCount, options.SpilloverBufferSizeBytes);
        }
    }
#pragma warning restore CA2255

    /// <summary>
    /// Self-wire the profiler for <paramref name="runtime"/> when configuration enables it. Called at the end of
    /// <see cref="TyphonRuntime.Create"/>. A no-op when the producer gate is closed (the common case). Best-effort — a startup failure (port busy, unwritable
    /// trace path) is logged and swallowed so the host runs without profiling.
    /// </summary>
    /// <param name="runtime">The runtime to attach the profiler to.</param>
    /// <param name="serviceProvider">Optional — when supplied, a host override registered via <c>AddTyphonProfiler</c> is resolved from it.</param>
    internal static void TryStart(TyphonRuntime runtime, IServiceProvider serviceProvider)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        lock (Gate)
        {
            if (Started)
            {
                return;
            }

            try
            {
                // File/env config overlaid with the process command line — resolved once by TelemetryConfig.
                var config = TelemetryConfig.ProfilerLaunch;

                var ovr = serviceProvider?.GetService<ProfilerLaunchOverride>();
                if (ovr?.Configure != null)
                {
                    config = ovr.Configure(config) ?? config;
                }

                // Captures live with their database (#616, design D-1). This MUST stay ahead of the inert check below: it fills an absent
                // destination, so asking "is there an output channel?" before it would warn about a profiler that is one line away from
                // being given one — which is every database-backed capture, i.e. the default case.
                config = ApplyDefaultCaptureDestination(runtime, config);

                // Master switch on but no output channel requested — nothing to export. Say so exactly once (#792): silence here is
                // indistinguishable from "profiled fine, found nothing", and the realistic cause is a late environment variable, which the
                // host cannot see because TelemetryConfig froze the merged configuration at assembly load. Console.Error rather than an
                // ILogger, matching the startup-failure path below: a host with no logging configured is precisely the host that needs this.
                var inertWarning = BuildInertProfilerWarning(config);
                if (inertWarning != null)
                {
                    if (!WarnedInert)
                    {
                        WarnedInert = true;
                        Console.Error.WriteLine(inertWarning);
                    }
                    return;
                }

                var parent = runtime.Engine.Owner.Profiler;
                Exporters = ProfilerLauncher.CreateExporters(config, parent);
                foreach (var exporter in Exporters)
                {
                    TyphonProfiler.AttachExporter(exporter);
                }

                // CPU sampler must start BEFORE metadata is built so its QPC anchor lands in the trace header.
                var samplingQpc = ProfilerLauncher.StartCpuSampler(config);
                var metadata = ProfilerSessionMetadataBuilder.Build(runtime, samplingQpc);

                // Hand FinishStop to TyphonProfiler's process-exit safety net as a BACKUP only — it does not fire
                // reliably on every host (Godot tears the .NET runtime down without a usable AppDomain.ProcessExit).
                TyphonProfiler.Start(parent, metadata, processExitTeardown: FinishStop);
                Started = true;

                // Primary teardown: finalize the trace when the engine's storage is disposed. ManagedPagedMMF is
                // disposed after DatabaseEngine (DI reverse-registration order), deterministically by the host's
                // service-provider disposal — so this runs on every host AND after the engine's shutdown teardown,
                // letting those events reach the trace. FileExporter.Dispose then patches the trace header.
                runtime.Engine.MMF.DisposingEvent += static (_, _) => FinishStop();
            }
            catch (Exception ex)
            {
                // Never crash the host over profiling — continue without it.
                Console.Error.WriteLine($"[Typhon] Profiler startup FAILED — {ex.GetType().Name}: {ex.Message}. Continuing without profiling.");
                Exporters = null;
                Started = false;
            }
        }
    }

    /// <summary>
    /// The diagnostic for a profiler that is enabled but has no output channel, or <c>null</c> when <paramref name="config"/> resolves an output channel and
    /// there is nothing to report. Pure — the caller owns emission and the one-shot latch.
    /// </summary>
    /// <remarks>
    /// The wording carries the part that is not guessable from the symptom: the configuration is read once, at <c>Typhon.Engine</c> assembly load, so an
    /// <c>Environment.SetEnvironmentVariable</c> issued from <c>Main</c> is already too late — <c>Main</c> references engine types, so the module initializer
    /// has run before its first statement. That is #792, and the failure mode it produced was a clean run with no file and no message.
    /// </remarks>
    /// <param name="config">The effective launch config, after the file/env resolution and any host override.</param>
    internal static string BuildInertProfilerWarning(ProfilerLaunchConfig config)
    {
        if (config == null || config.IsActive)
        {
            return null;
        }

        return "[Typhon] Profiler is ENABLED but no output channel is configured, so NO trace will be produced. Set 'Typhon:Profiler:Trace' (a "
            + ".typhon-trace file path) or 'Typhon:Profiler:Live' (a TCP port) in typhon.telemetry.json beside the executable, or set "
            + "TYPHON__PROFILER__TRACE / TYPHON__PROFILER__LIVE in the environment BEFORE the process starts. Setting those variables from inside Main has "
            + "no effect: the telemetry configuration is read once when Typhon.Engine loads, which happens before Main's first statement runs.";
    }

    /// <summary>
    /// Points an otherwise-destinationless capture at <c>{bundle}/profilings/</c> and enforces the database's retention policy before the new file is created
    /// (#616, D-1 + D-6). Returns <paramref name="config"/> unchanged whenever a destination was already chosen or there is no bundle to write into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately narrow.</b> An explicit <c>Typhon:Profiler:Trace</c> path still means exactly what it meant before: the default fills an absent
    /// destination, it never overrides a chosen one.
    /// </para>
    /// <para>
    /// <b>A configured live port suppresses it.</b> <c>Live</c> and <c>Trace</c> are independent output channels — that is the whole shape of
    /// <c>typhon telemetry</c> — so asking for one must never silently produce the other. A live port is a request to <i>watch</i>; turning it into a
    /// multi-gigabyte file the caller never asked for is a decision the tool does not get to make on their behalf.
    /// </para>
    /// <para>
    /// This is load-bearing for on-demand tick capture (#805), not merely tidy. That feature exists so an operator can record a chosen window and store
    /// nothing else; the engine keeps emitting every tick over the wire and the Workbench discards what was not armed. A default file destination here
    /// defeats it completely and invisibly: the operator records 100 ticks, and the engine writes a complete capture of the entire run beside the database
    /// anyway. Measured on a 2,000-entity shard: <b>25.84 MB written against a 1.04 MB captured window</b>.
    /// </para>
    /// <para>
    /// An explicit <see cref="ProfilerLaunchConfig.TraceFilePath"/> still wins, so wanting both is one setting away
    /// (<c>typhon telemetry trace &lt;path&gt;</c>). The cost of live-only is that CPU sampling does not run — it is file-mode only — which is a visible,
    /// logged consequence of a choice the user made, not a silent one made for them.
    /// </para>
    /// <para>
    /// Pruning happens <b>here</b>, in whatever process is about to write a capture, precisely because the Workbench is not always present — a game server, a
    /// CI box, a customer deployment records captures with no tooling installed. A budget enforced only by the Workbench would leave those disks filling
    /// exactly as before. Running it before the new file is created also means the capture about to be written is never a candidate for its own eviction.
    /// </para>
    /// <para>Best-effort throughout: a host must never fail to start profiling because a directory could not be pruned.</para>
    /// </remarks>
    internal static ProfilerLaunchConfig ApplyDefaultCaptureDestination(TyphonRuntime runtime, ProfilerLaunchConfig config)
    {
        // A configured live port suppresses the default file. Live and Trace are independent channels: a request to
        // WATCH must not silently produce a file the caller never asked for. This is what makes on-demand tick capture
        // (#805) mean anything — an operator who records 100 ticks must not find a complete capture of the whole run
        // written beside the database regardless. An explicit TraceFilePath still wins, so wanting both is one setting
        // away; SuppressCapture still wins over everything.
        if (config == null || config.SuppressCapture || config.TraceFilePath != null || config.LivePort >= 0)
        {
            return config;
        }

        var bundle = runtime?.Engine?.MMF?.BundleDirectory;
        if (string.IsNullOrEmpty(bundle))
        {
            // Standalone profiling with no engine attached (Typhon.IOProfileRunner, exporter tests). There is no database to co-locate with, so leave the
            // config destinationless rather than inventing a directory somewhere.
            return config;
        }

        // Same reasoning as TraceRetention.Prune: the engine's Logger is legitimately null in some hosts, and [LoggerMessage] methods do not tolerate that.
        ILogger logger = runtime.Engine.Logger ?? (ILogger)NullLogger.Instance;
        try
        {
            var profilings = TraceLocation.ProfilingsDirectoryOf(bundle);
            var policy = RetentionPolicy.Read(profilings, out var malformedReason);
            if (malformedReason != null)
            {
                RetentionLog.PolicyUnreadable(logger, profilings, malformedReason);
            }

            // Prune BEFORE creating the new file, so the capture about to be written is never a candidate for its own eviction.
            TraceRetention.Prune(profilings, policy, logger);

            return config with { TraceFilePath = TraceLocation.NewCapturePath(bundle) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable bundle (read-only mount, foreign ownership) must degrade to "no capture", not to a failed host start.
            ProfilerBootstrapLog.CaptureDirectoryUnavailable(logger, bundle, ex.GetType().Name, ex.Message);
            return config;
        }
    }

    /// <summary>
    /// Begin the asynchronous CPU-sampler stop. Called from <see cref="TyphonRuntime.Shutdown"/> purely as an optimisation: it pre-warms the (seconds-long)
    /// <c>.nettrace</c> transcode so it overlaps the rest of teardown and the <see cref="FinishStop"/> at storage disposal has little left
    /// to do. Best-effort, idempotent — safe to skip (FinishStop falls back to a synchronous stop).
    /// </summary>
    internal static void BeginStop()
    {
        // ReSharper disable once InconsistentlySynchronizedField
        if (!Started)
        {
            return;
        }
        try { ProfilerLauncher.BeginCpuSamplerStop(); } catch { /* best-effort teardown */ }
    }

    /// <summary>
    /// Finish profiler teardown: await the CPU-sampler parse and hand the samples to the file exporter, stop the profiler, then detach every exporter.
    /// This finalizes the trace file (<c>FileExporter.Dispose</c> patches the header's section-index offsets), so it MUST run: it is invoked from the engine
    /// storage's <c>DisposingEvent</c> (subscribed in <see cref="TryStart"/>), and is also wired into <see cref="TyphonProfiler"/>'s process-exit /
    /// unhandled-exception safety net (via the <c>processExitTeardown</c> argument of <c>TyphonProfiler.Start</c>) as a backup. Best-effort, idempotent.
    /// </summary>
    internal static void FinishStop()
    {
        lock (Gate)
        {
            if (!Started)
            {
                return;
            }
            Started = false;

            try { ProfilerLauncher.StopCpuSampler(); } catch { /* best-effort teardown */ }
            try { TyphonProfiler.Stop(); } catch { /* best-effort teardown */ }

            if (Exporters != null)
            {
                foreach (var exporter in Exporters)
                {
                    try { TyphonProfiler.DetachExporter(exporter); } catch { /* best-effort teardown */ }
                }
                Exporters = null;
            }
        }
    }
}

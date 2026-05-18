using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
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

                // Master switch on but no output channel requested — nothing to export. Stay quiet.
                if (!config.IsActive)
                {
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

using System;
using System.Globalization;
using System.Text;

namespace SwgTatooine;

/// <summary>
/// One line saying whether this run is being profiled, on which port, and which layer asked for it (W2).
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of a measurement that was wrong and did not look wrong.</b> The profiler is enabled from <c>typhon.telemetry.json</c> or a
/// <c>TYPHON__PROFILER__*</c> variable, and until now the server said nothing about either: a run with live profiling on and a run without it printed identical
/// output. A <c>$env:</c> assignment persists for a whole shell session, so a variable set once to attach the Workbench kept profiling — and skewing — every
/// later run in that shell, which is why <c>claude/scratch/wp2-manual-verification-2026-09-27.md</c> warns "without that <c>Remove-Item</c> this is not a
/// default run at all".
/// </para>
/// <para>
/// <b>So the line names the SOURCE, not only the state.</b> "profiling on" answers the wrong question; an operator who did not ask for profiling needs to be
/// told that something else did.
/// </para>
/// <para>
/// <b>The two halves are separate methods on purpose.</b> Whether the profiler is on is the engine's answer and can only be read from
/// <see cref="TelemetryConfig"/>, which bakes once per process — so a test cannot vary it, and one that tried would only ever exercise whichever branch its own
/// runner happened to bake. Where the request came from is a pure function of what was scanned, so <see cref="DescribeSource"/> is testable on its own and the
/// attribution this class exists for is covered rather than skipped.
/// </para>
/// </remarks>
public static class ProfilerStatus
{
    /// <summary>Spelled once, so the two messages that name it cannot drift apart.</summary>
    private const string LiveVariableName = ProfilerArgs.LiveVariable;

    /// <summary>
    /// Whether a profiler flag on this command line has to be refused.
    /// </summary>
    /// <param name="requested">What <see cref="ProfilerArgs.From"/> read.</param>
    /// <returns><see langword="true"/> when the program must print the refusal and exit.</returns>
    /// <remarks>
    /// <para>
    /// <b>A flag is refused whenever one was given, with no exception for a profiler that is already on.</b> It cannot take effect either way — see
    /// <see cref="ProfilerArgs"/> — and the "already on" case was the worse silence of the two: <c>--live 9200</c> under an inherited
    /// <c>TYPHON__PROFILER__LIVE=9100</c> listened on 9100, exited 0, and reported that the command line had asked for nothing.
    /// </para>
    /// <para>
    /// A one-line method rather than an <c>if</c> inside <c>Main</c> so that the decision is testable without launching a process; the gate's own state is not
    /// an input precisely because it is not allowed to matter.
    /// </para>
    /// </remarks>
    public static bool MustRefuse(in ProfilerArgs.Requested requested) => requested.AskedByFlag;

    /// <summary>Describes the profiler's resolved state for the startup banner, reading the engine's own gate.</summary>
    /// <param name="requested">What <see cref="ProfilerArgs.From"/> read from the command line and the environment.</param>
    /// <returns>One line, never empty.</returns>
    /// <remarks>
    /// The engine decides whether the profiler is on; this only explains why. Asking <see cref="TelemetryConfig"/> rather than re-deriving the answer from the
    /// same inputs is deliberate: two resolutions of one question drift, and the one printed would be the one nobody tested.
    /// </remarks>
    public static string Describe(in ProfilerArgs.Requested requested) => Describe(TelemetryConfig.ProfilerActive, requested);

    /// <summary>Describes the profiler's resolved state for a caller that supplies the gate.</summary>
    /// <param name="profilerActive">Whether the engine's profiler is on — <c>TelemetryConfig.ProfilerActive</c> in production.</param>
    /// <param name="requested">What <see cref="ProfilerArgs.From"/> read.</param>
    /// <returns>One line, never empty.</returns>
    /// <remarks>
    /// <b>The gate is a parameter so that neither branch of this method is dead in a test.</b> <c>TelemetryConfig.ProfilerActive</c> is baked once per process
    /// from the file and environment layer, and this suite runs unprofiled, so a test of the one-argument overload can only ever reach the "off" branch — the
    /// "ON" wording would be asserted nowhere. Taking the gate as an argument costs one parameter at the single production call site and makes both branches
    /// real assertions.
    /// </remarks>
    public static string Describe(bool profilerActive, in ProfilerArgs.Requested requested)
    {
        if (!profilerActive)
        {
            // Said even when nothing asked for profiling, because no line at all is indistinguishable from an older build that never printed one — and this
            // line's whole job is to let a reader tell a profiled run from an unprofiled one at a glance.
            return "profiling off (set $env:" + ProfilerArgs.LiveVariable
                + " before starting, or use scripts/swg-serve.ps1 -Live, to let the Workbench attach)";
        }

        return new StringBuilder("profiling ON").Append(DescribePort(requested)).Append(DescribeSource(requested)).ToString();
    }

    /// <summary>The clause naming the port the live exporter is on, as a pure function of what was read.</summary>
    /// <param name="requested">What <see cref="ProfilerArgs.From"/> read.</param>
    /// <returns>A clause ready to append, or an empty string when no live port was asked for.</returns>
    /// <remarks>
    /// <para>
    /// <b>The port the engine RESOLVED, not the text of the variable.</b> <c>ProfilerLaunchConfig.FromConfiguration</c> falls back to
    /// <see cref="ProfilerArgs.DefaultLivePort"/> for anything it cannot parse, so echoing the raw value printed "live on port yes" while the exporter sat on
    /// 9100 — wrong in the one number an operator needs in order to attach. When the two differ, the bogus value is named as well, because the operator has a
    /// variable to fix.
    /// </para>
    /// <para>
    /// Pure, and separate from <see cref="Describe"/>, for the same reason <see cref="DescribeSource"/> is: <c>Describe</c>'s branch depends on a gate
    /// that bakes
    /// once per process, so a test of it reaches only whichever branch its runner baked.
    /// </para>
    /// </remarks>
    public static string DescribePort(in ProfilerArgs.Requested requested)
    {
        var port = requested.EffectiveLivePort;
        if (port <= 0)
        {
            return string.Empty;
        }

        var raw = port.ToString(CultureInfo.InvariantCulture);
        return string.Equals(requested.InheritedLive, raw, StringComparison.Ordinal)
            ? $", live on port {raw}"
            : $", live on port {raw} (the engine's default; {LiveVariableName}='{requested.InheritedLive}' is not a port)";
    }

    /// <summary>The clause naming who asked for profiling, as a pure function of what was read.</summary>
    /// <param name="requested">What <see cref="ProfilerArgs.Scan"/> read.</param>
    /// <returns>A clause beginning with an em dash, ready to append.</returns>
    /// <remarks>
    /// <para>
    /// Only the struct is read — never the environment. Reading the environment a second time would let this clause disagree with what
    /// <see cref="ProfilerArgs.Scan"/> captured, and it is what made an earlier version untestable: a test cannot hand a method an environment without leaking
    /// one into every fixture that runs after it in the same process.
    /// </para>
    /// <para>
    /// The launcher is distinguished from a stale variable because the warning is the point of the line, and aiming it at an operator who just typed
    /// <c>-Live</c> reads as a bug in the tool.
    /// </para>
    /// </remarks>
    public static string DescribeSource(in ProfilerArgs.Requested requested)
    {
        if (requested.FromLauncher)
        {
            return $" — asked for by {requested.Launcher}";
        }

        if (requested.AskedByEnvironment)
        {
            var names = new StringBuilder();
            if (requested.InheritedLive != null)
            {
                names.Append(ProfilerArgs.LiveVariable);
            }

            if (requested.InheritedTrace != null)
            {
                names.Append(names.Length == 0 ? string.Empty : ", ").Append(ProfilerArgs.TraceVariable);
            }

            if (requested.InheritedEnabled != null)
            {
                names.Append(names.Length == 0 ? string.Empty : ", ").Append(ProfilerArgs.EnabledVariable);
            }

            return $" — asked for by the environment ({names}), NOT by this command line — clear it for an unprofiled run";
        }

        // The last resort, and it is a real case: a typhon.telemetry.json beside the assembly, which the engine names and this does not have to guess at.
        var file = TelemetryConfig.LoadedConfigurationFile;
        return file != null
            ? $" — asked for by {file}"
            : " — asked for by configuration this program cannot name";
    }
}

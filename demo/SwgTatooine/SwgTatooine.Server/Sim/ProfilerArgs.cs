using System;
using System.Globalization;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>
/// What the command line and the environment asked of the profiler, and the message for a request this process cannot honour (W1, W2).
/// </summary>
/// <remarks>
/// <para>
/// <b>A Typhon host cannot turn its own profiler on, and that is why these flags are refused rather than applied.</b> The engine's master gate is
/// <c>TelemetryConfig.ProfilerActive</c>, a <c>static readonly bool</c> resolved in a static constructor from <c>typhon.telemetry.json</c> and
/// <c>TYPHON__PROFILER__*</c> — readonly on purpose, because that is what lets the JIT delete every disabled telemetry branch in Tier 1. And it bakes before
/// <c>Main</c>'s first instruction: this assembly declares <c>[Component]</c>s and <c>[Archetype]</c>s, so <c>Typhon.Generators</c> emits it a
/// <c>[ModuleInitializer]</c> registering them through <c>DatabaseEngine.RegisterArchetype</c> (#514 phase 5), an entry assembly's module initializers run
/// before its entry point, and the engine's own module initializer calls <c>TelemetryConfig.EnsureInitialized()</c>. Verified by experiment on 2026-10-04: with
/// the variable set as the first statement of <c>Main</c>, a probe printed the variable set and the gate shut.
/// </para>
/// <para>
/// <b>Refused in every case, including when profiling happens to be on already.</b> An earlier version refused only when the gate was shut, which left the
/// worse half open: with <c>TYPHON__PROFILER__LIVE=9100</c> in the shell, <c>--live 9200</c> ran happily on 9100 and the banner said the command line had asked
/// for nothing. A flag that is sometimes honoured and sometimes silently ignored is harder to reason about than one that never is — and "sometimes honoured"
/// was really "sometimes coincided with". One rule, no silent path.
/// </para>
/// <para>
/// <b>There is deliberately no second parser.</b> This type reads the values <see cref="CommandLine"/> already parsed, strictly and once. A previous version
/// scanned <c>argv</c> itself, so it could write the variables before the engine loaded; once that turned out to be impossible, the scan was just two
/// readers of
/// one flag that could disagree about what is well-formed — it took <c>--trace --probe</c> as a path, where the strict parser refuses it — and the disagreement
/// was invisible because the strict parser happened to run second and throw first.
/// </para>
/// </remarks>
public static class ProfilerArgs
{
    /// <summary>The port the engine's live exporter falls back to when its variable holds no usable number.</summary>
    /// <remarks>
    /// Duplicated from <c>ProfilerLaunchConfig.DefaultLivePort</c> rather than cited, because this value also goes into <c>--help</c> text. The engine should
    /// expose its environment-variable names and this port as public constants, so that renaming one cannot make this demo's help and refusal lie; EN-H.
    /// </remarks>
    public const int DefaultLivePort = 9100;

    /// <summary>The environment variable that opens the gate for the live TCP exporter.</summary>
    public const string LiveVariable = "TYPHON__PROFILER__LIVE";

    /// <summary>The environment variable for the address that exporter binds. Loopback when unset.</summary>
    public const string BindVariable = "TYPHON__PROFILER__BINDADDRESS";

    /// <summary>The environment variable for the capture file.</summary>
    public const string TraceVariable = "TYPHON__PROFILER__TRACE";

    /// <summary>The environment variable that turns collection on without naming an output channel.</summary>
    public const string EnabledVariable = "TYPHON__PROFILER__ENABLED";

    /// <summary>Set by the launcher scripts to say that the profiler variables belong to this run rather than to the shell's history.</summary>
    /// <remarks>
    /// <b>Without it the startup line cannot tell the two apart, and would have to warn about both.</b> An inherited <see cref="LiveVariable"/> is the footgun
    /// this whole area exists for; one a launcher just wrote, for an operator who asked for <c>-Live</c>, is the opposite — and being told to "clear it for an
    /// unprofiled run" when you deliberately asked for a profiled one reads as a bug in the tool. It is read for the wording of one line and nothing else: the
    /// engine never sees it, and a hand-set variable without it still works exactly as before.
    /// </remarks>
    public const string LauncherVariable = "TYPHON_SWG_PROFILER_LAUNCHER";

    /// <summary>What the command line asked for, and what the environment was already carrying when it asked.</summary>
    public readonly struct Requested
    {
        /// <summary>The port <c>--live</c> named; 0 when the flag was absent or named 0, which means off.</summary>
        public int LivePort { get; init; }

        /// <summary>What <c>--profiler-bind</c> named, or <see langword="null"/>.</summary>
        public string BindAddress { get; init; }

        /// <summary>What <c>--trace</c> named, or <see langword="null"/>. An empty string is a value the operator typed, and is kept as one.</summary>
        public string TracePath { get; init; }

        /// <summary><see cref="LiveVariable"/> as this process inherited it, or <see langword="null"/>.</summary>
        public string InheritedLive { get; init; }

        /// <summary><see cref="TraceVariable"/> as this process inherited it, or <see langword="null"/>.</summary>
        public string InheritedTrace { get; init; }

        /// <summary><see cref="EnabledVariable"/> as this process inherited it, or <see langword="null"/>.</summary>
        public string InheritedEnabled { get; init; }

        /// <summary>The launcher that set the profiler variables for this run, or <see langword="null"/> when none did.</summary>
        /// <remarks>
        /// A name rather than a flag because there are two launchers — <c>swg-serve.ps1</c> and <c>swg-client.ps1</c> — and crediting the wrong one is a small
        /// lie in the one line whose job is to say where profiling came from.
        /// </remarks>
        public string Launcher { get; init; }

        /// <summary>Whether a launcher set the profiler variables for this run.</summary>
        public bool FromLauncher => Launcher != null;

        /// <summary>Whether a flag asked for something this process cannot honour.</summary>
        /// <remarks>
        /// <see cref="TracePath"/> is tested for null rather than for emptiness: <c>--trace ""</c> is a flag the operator typed, and answering it with silence
        /// was its own small no-op.
        /// </remarks>
        public bool AskedByFlag => LivePort > 0 || BindAddress != null || TracePath != null;

        /// <summary>Whether the environment had already asked for something before this process read its arguments.</summary>
        public bool AskedByEnvironment => InheritedLive != null || InheritedTrace != null || InheritedEnabled != null;

        /// <summary>The port the live exporter is actually on, resolved the way the engine resolves it; 0 when none was asked for.</summary>
        /// <remarks>
        /// <b>The engine's rule, not a reading of the raw text.</b> <c>ProfilerLaunchConfig.FromConfiguration</c> falls back to <see cref="DefaultLivePort"/>
        /// for a value it cannot parse, so <c>TYPHON__PROFILER__LIVE=yes</c> listens on 9100 — and a banner that echoed the variable printed "live on
        /// port yes",
        /// which is the one number an operator needs in order to attach.
        /// </remarks>
        public int EffectiveLivePort
        {
            get
            {
                if (InheritedLive == null)
                {
                    return 0;
                }

                return int.TryParse(InheritedLive, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : DefaultLivePort;
            }
        }
    }

    /// <summary>Reads what the parsed command line and the current environment ask of the profiler.</summary>
    /// <param name="config">The already-parsed command line; its three profiler fields are the authority on what was typed.</param>
    /// <returns>What was asked for, and by whom.</returns>
    public static Requested From(SimConfig config) => From(config, Environment.GetEnvironmentVariable);

    /// <summary>Reads what the command line and a supplied environment ask of the profiler.</summary>
    /// <param name="config">The already-parsed command line.</param>
    /// <param name="environment">Reads an environment variable by name.</param>
    /// <returns>What was asked for, and by whom.</returns>
    /// <remarks>
    /// <b>The environment is a parameter so that the four inherited fields are drivable without mutating the process.</b> A test that set a real variable would
    /// leak it into every fixture running after it in the same process, so the alternative was leaving this mapping uncovered — and the failure it would hide is
    /// a silent one: swap two of these variable names and the startup line blames the wrong setting, with nothing failing.
    /// </remarks>
    public static Requested From(SimConfig config, Func<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environment);

        return new Requested
        {
            LivePort = config.ProfilerLivePort,
            BindAddress = config.ProfilerBindAddress,
            TracePath = config.ProfilerTracePath,
            InheritedLive = environment(LiveVariable),
            InheritedTrace = environment(TraceVariable),
            InheritedEnabled = environment(EnabledVariable),
            Launcher = SanitiseLauncher(environment(LauncherVariable)),
        };
    }

    /// <summary>Reduces an environment-supplied name to something safe to print: trimmed, no control characters, and short.</summary>
    /// <param name="value">The raw variable.</param>
    /// <returns>A printable name, or <see langword="null"/> when there is nothing worth printing.</returns>
    /// <remarks>
    /// The value reaches a console line and comes from whatever started this process, so bounding it is cheap insurance: an unbounded one could scroll
    /// a startup
    /// banner away, and an escape sequence in it would reach a terminal. The cut respects UTF-16 pairs, because half a surrogate is not a character.
    /// </remarks>
    public static string SanitiseLauncher(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return "a launcher";
            }
        }

        if (trimmed.Length <= 60)
        {
            return trimmed;
        }

        // 60 exactly would split a surrogate pair when the 60th char is its high half, leaving a lone half on the console.
        return trimmed[..(char.IsHighSurrogate(trimmed[59]) ? 59 : 60)];
    }

    /// <summary>The message for a command line that asked for profiling this process cannot switch on, naming the equivalents that work.</summary>
    /// <param name="requested">What <see cref="From"/> read.</param>
    /// <returns>A multi-line explanation ending in commands the operator can paste. Every line carries the program's name.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every flag that was given gets its own variable named.</b> An earlier version picked one flag for the headline and a different one for the suggested
    /// variable, so <c>--live 9100 --trace x</c> complained about <c>--live</c> and then told you to set the trace variable; <c>--profiler-bind</c> was refused
    /// with advice that dropped the bind entirely, which is also why its variable had no reader at all.
    /// </para>
    /// <para>
    /// Both shells are shown because the script is a convenience and the variable is the contract: a server under systemd takes an <c>Environment=</c> line and
    /// a Linux shell takes a prefix, neither of which is a <c>.ps1</c>.
    /// </para>
    /// </remarks>
    public static string ExplainRefusal(in Requested requested)
    {
        var flags = new StringBuilder();
        var powershell = new StringBuilder();
        var posix = new StringBuilder();

        void Ask(string flag, string variable, string value)
        {
            flags.Append(flags.Length == 0 ? string.Empty : ", ").Append(flag);
            powershell.Append(CultureInfo.InvariantCulture, $"  $env:{variable} = '{value}'{Environment.NewLine}");
            posix.Append(CultureInfo.InvariantCulture, $"{variable}='{value}' ");
        }

        if (requested.LivePort > 0)
        {
            Ask("--live", LiveVariable, requested.LivePort.ToString(CultureInfo.InvariantCulture));
        }

        if (requested.BindAddress != null)
        {
            Ask("--profiler-bind", BindVariable, requested.BindAddress);
        }

        if (requested.TracePath != null)
        {
            Ask("--trace", TraceVariable, requested.TracePath);
        }

        var already = requested.AskedByEnvironment || requested.FromLauncher
            ? "Profiling is already on, from " + (requested.FromLauncher ? requested.Launcher : "the environment")
                + " — but a flag still cannot change it, so this is refused rather than appearing to work." + Environment.NewLine + Environment.NewLine
            : string.Empty;

        var launcher = requested.LivePort > 0
            ? $" -Live {requested.LivePort.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        var body = $"""
                   {flags} cannot switch profiling on from inside the process, so it is refused rather than ignored.

                   {already}This assembly's generated [ModuleInitializer] registers its components and archetypes through the engine, which loads
                   Typhon.Engine and bakes the profiler's master gate — a static readonly bool, so the JIT can delete disabled telemetry — before
                   Main runs. Nothing the program does afterwards can reopen it.

                   Decide it before the process starts instead. PowerShell:

                   {powershell}
                   or a POSIX shell, which is how the deployed server does it:

                     {posix}{Environment.ProcessPath ?? "SwgTatooine.Server"} <your other flags>

                   or let a launcher do both:

                     scripts/swg-serve.ps1 -Port 5080{launcher}
                   """;

        // Every line prefixed, not only the first: an eight-line message whose continuations start at column 0 does not read as one program's output.
        var prefixed = new StringBuilder();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            prefixed.Append("SwgTatooine.Server: ").Append(line).Append(Environment.NewLine);
        }

        return prefixed.ToString().TrimEnd();
    }
}

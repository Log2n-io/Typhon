using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SwgTatooine;

/// <summary>
/// What a crashed run leaves behind so that it can be diagnosed without being reproduced (P-3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this exists for is a tick that aborts on a box nobody is watching.</b> A measured run prints its report at the end; a run that dies has no
/// end, so everything it knew — which system threw, what the tick times were doing on the way down, what world it was running — goes with the process. A
/// scheduled 16-hour server run that aborts at hour three currently leaves a console nobody read.
/// </para>
/// <para>
/// <b>Beside the database rather than in a log directory</b>, because the database is the other half of the evidence: a crash artefact whose database has been
/// deleted by the next run is a stack trace with no world behind it, and with <c>--persist</c> the pair can be opened and inspected together.
/// </para>
/// <para>
/// <b>Nothing here may throw.</b> It runs from the abort handler and from the top-level catch, where an exception would replace the fault being reported with
/// one from the reporting — the worst possible trade. Every step is guarded and the failure to write is itself reported to the console, which is the one channel
/// that cannot fail.
/// </para>
/// </remarks>
public static class CrashArtefact
{
    /// <summary>How many of the most recent ticks to record. Two hundred at 10 Hz is twenty seconds of the approach to the fault.</summary>
    /// <remarks>
    /// The number is a judgement rather than a measurement: enough that a build-up (a tick time climbing for seconds) is visible, few enough that the file
    /// stays readable in a terminal. The ring may hold fewer, and whatever it holds is what gets written.
    /// </remarks>
    public const int TickHistory = 200;

    /// <summary>
    /// Writes one artefact directory. Returns its path, or <see langword="null"/> when nothing could be written.
    /// </summary>
    /// <param name="databaseDirectory">Where the database lives; the artefact goes beside it.</param>
    /// <param name="databaseName">The database's name, so the artefact is obviously paired with it.</param>
    /// <param name="reason">One line: what happened. An aborted tick names the system.</param>
    /// <param name="exception">The fault, if there is one. A tick abort may have none.</param>
    /// <param name="ticks">The most recent tick durations in milliseconds, oldest first.</param>
    /// <param name="census">The world that was running.</param>
    /// <param name="config">The configuration, so the run can be repeated.</param>
    /// <param name="tickNumber">The tick the fault happened on.</param>
    public static string Write(
        string databaseDirectory,
        string databaseName,
        string reason,
        Exception exception,
        ReadOnlySpan<float> ticks,
        WorldCensus census,
        SimConfig config,
        long tickNumber)
    {
        try
        {
            var root = databaseDirectory ?? AppContext.BaseDirectory;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.Combine(root, $"{databaseName ?? "SwgTatooine"}.crash-{stamp}");
            Directory.CreateDirectory(dir);

            // One line at the top of the directory listing, so `ls` answers the first question without opening anything.
            File.WriteAllText(Path.Combine(dir, "reason.txt"),
                $"tick {tickNumber}: {reason}{Environment.NewLine}{DateTime.UtcNow:O}{Environment.NewLine}");

            // The WHOLE exception, ToString() rather than the message: a type and a message name a symptom, the stack names the line. Inner exceptions come
            // with it, which matters because an aborted system's fault arrives wrapped.
            File.WriteAllText(Path.Combine(dir, "exception.txt"),
                exception?.ToString() ?? "(no exception — the tick was aborted without one)" + Environment.NewLine);

            WriteTicks(dir, ticks);
            File.WriteAllText(Path.Combine(dir, "census.txt"), Describe(census));
            File.WriteAllText(Path.Combine(dir, "config.txt"), Describe(config));
            Console.WriteLine($"  !! crash artefact written to {dir}");
            return dir;
        }
        catch (Exception writeFailure)
        {
            // Reported, never rethrown: the fault being diagnosed must not be replaced by a failure to diagnose it.
            Console.WriteLine($"  !! could not write the crash artefact: {writeFailure.GetType().Name}: {writeFailure.Message}");
            return null;
        }
    }

    /// <summary>The approach to the fault, one tick per line, so a build-up is visible rather than inferred.</summary>
    private static void WriteTicks(string dir, ReadOnlySpan<float> ticks)
    {
        var sb = new StringBuilder(ticks.Length * 12);
        sb.Append("index,ms").Append(Environment.NewLine);
        for (var i = 0; i < ticks.Length; i++)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(ticks[i].ToString("F4", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        }

        File.WriteAllText(Path.Combine(dir, "ticks.csv"), sb.ToString());
    }

    private static string Describe(WorldCensus census)
    {
        if (census == null)
        {
            return "(no census — the world had not finished building)" + Environment.NewLine;
        }

        var sb = new StringBuilder();
        sb.Append(census).Append(Environment.NewLine);
        var composition = census.Composition();
        if (composition.Length > 0)
        {
            sb.Append("creatures: ").Append(composition).Append(Environment.NewLine);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Enough of the configuration to run the same world again.
    /// </summary>
    /// <remarks>
    /// <see cref="SimConfig.Label"/> is the workload's identity and is what a report is keyed by, so it is the first line; the seed is next, because without it
    /// the same label is a different world.
    /// </remarks>
    private static string Describe(SimConfig config)
    {
        if (config == null)
        {
            return "(no configuration)" + Environment.NewLine;
        }

        var sb = new StringBuilder();
        sb.Append("label: ").Append(config.Label).Append(Environment.NewLine);
        sb.Append("seed: ").Append(config.Seed.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("hz: ").Append(config.TickRateHz.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("pop: ").Append(config.PopulationScale.ToString("G", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("world-m: ").Append(config.WorldEdgeM.ToString("G", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("cell-m: ").Append(config.ResolveCellSize().ToString("G", CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("workers: ").Append(config.ResolveWorkerCount().ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("warm/measured ticks: ").Append(config.WarmTicks.ToString(CultureInfo.InvariantCulture)).Append('/')
            .Append(config.MeasuredTicks.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
        sb.Append("persist: ").Append(config.Persist ? "yes" : "no").Append(Environment.NewLine);
        sb.Append("db: ").Append(config.DatabaseName).Append(Environment.NewLine);
        return sb.ToString();
    }
}

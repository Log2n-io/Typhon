using System;
using System.Collections.Generic;
using System.Text.Json;
using SwgTatooine.Bots.Brain;

namespace SwgTatooine.Bots.Telemetry;

/// <summary>
/// Writes the bot report the dashboard ingests at <c>POST /bots/report</c>: version 1 of the contract documented, field by field, in the dashboard's
/// <c>BotReport.cs</c> — the one place it is described. Nothing here references the dashboard: the JSON is the whole coupling.
/// </summary>
public static class BotReportWriter
{
    /// <summary>The contract's version; the dashboard refuses any other.</summary>
    public const int Version = 1;

    /// <summary>Writes one report.</summary>
    /// <param name="w">Where to.</param>
    /// <param name="run">This fleet process's id: the dashboard starts its traced bots' history again when it changes.</param>
    /// <param name="at">When, UTC.</param>
    /// <param name="ticksPerSecond">The driver's rate, which every <c>t</c> in the report counts in.</param>
    /// <param name="tick">The driver's tick now.</param>
    /// <param name="fleet">The fleet's statistics, sampled.</param>
    /// <param name="traced">The traced bots.</param>
    /// <param name="cursors">Filled, per traced bot in order, with the last event and trail sequence written.</param>
    public static void Write(Utf8JsonWriter w, string run, DateTime at, double ticksPerSecond, long tick, FleetStats fleet,
        IReadOnlyList<BotTracer> traced, Span<(long Events, long Trail)> cursors)
    {
        w.WriteStartObject();
        w.WriteNumber("v", Version);
        w.WriteString("run", run);
        w.WriteString("at", at);
        w.WriteNumber("hz", ticksPerSecond);
        w.WriteNumber("tick", tick);

        w.WriteStartObject("fleet");
        w.WriteNumber("bots", fleet.Bots);
        w.WriteNumber("windowS", Math.Round(fleet.WindowSeconds, 1));
        w.WriteNumber("stuckS", Math.Round(fleet.Stats[(int)FleetStat.StuckTicks] / Math.Max(1d, ticksPerSecond), 1));
        Counts(w, "goals", fleet.Goals, GoalNames);
        Counts(w, "totals", fleet.Stats, StatNames);
        Rates(w, "perMin", fleet.StatsPerMin, StatNames);
        Counts(w, "why", fleet.Why, WhyNames);
        Rates(w, "whyPerMin", fleet.WhyPerMin, WhyNames);
        Counts(w, "legs", fleet.Legs, LegNames);
        Rates(w, "legsPerMin", fleet.LegsPerMin, LegNames);
        SpreadOf(w, "goalChangesPerBotMin", fleet.GoalChangesPerBotMin);
        SpreadOf(w, "reversalsPerBotMin", fleet.ReversalsPerBotMin);
        w.WriteEndObject();

        w.WriteStartArray("traced");
        for (var i = 0; i < traced.Count; i++)
        {
            var written = traced[i].Write(w);
            if (i < cursors.Length)
            {
                cursors[i] = written;
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    // The names once, not per report.
    private static readonly string[] GoalNames = Enum.GetNames<Goal>();
    private static readonly string[] StatNames = Camel(Enum.GetNames<FleetStat>());
    private static readonly string[] WhyNames = Enum.GetNames<GoalWhy>();
    private static readonly string[] LegNames = Enum.GetNames<LegWhy>();

    private static string[] Camel(string[] names)
    {
        var camel = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            camel[i] = char.ToLowerInvariant(names[i][0]) + names[i][1..];
        }

        return camel;
    }

    private static void Counts(Utf8JsonWriter w, string name, long[] values, string[] names)
    {
        w.WriteStartObject(name);
        for (var i = 0; i < values.Length && i < names.Length; i++)
        {
            w.WriteNumber(names[i], values[i]);
        }

        w.WriteEndObject();
    }

    private static void Rates(Utf8JsonWriter w, string name, double[] values, string[] names)
    {
        w.WriteStartObject(name);
        for (var i = 0; i < values.Length && i < names.Length; i++)
        {
            w.WriteNumber(names[i], Math.Round(values[i], 2));
        }

        w.WriteEndObject();
    }

    private static void SpreadOf(Utf8JsonWriter w, string name, Spread s)
    {
        w.WriteStartObject(name);
        w.WriteNumber("mean", Math.Round(s.Mean, 2));
        w.WriteNumber("p50", Math.Round(s.P50, 2));
        w.WriteNumber("p90", Math.Round(s.P90, 2));
        w.WriteNumber("max", Math.Round(s.Max, 2));
        w.WriteEndObject();
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwgTatooine.Dashboard;

// THE BOT REPORT, VERSION 1 — the one place its contract is written down (bot-telemetry.md).
//
// The bot swarm (SwgTatooine.Bots, --dashboard <url>) POSTs one of these to /bots/report every three seconds. Neither side references the other:
// the bots write it with a Utf8JsonWriter (Telemetry/BotReportWriter.cs), this file reads it, and SwgTatooine.Tests holds the two to each other.
// The game server knows nothing of it.
//
//   {
//     "v": 1,                          the contract's version; any other is refused with 400
//     "run": "3fa9c1d2",               the fleet process; a new one clears the traced bots' history
//     "at": "2026-10-26T12:00:00Z",    when it was written, UTC
//     "hz": 4,                         the driver's tick rate: every "t" and "tick" below counts in it
//     "tick": 12345,                   the driver's tick now
//     "fleet": {                       EVERY thinking bot, from counters each keeps anyway
//       "bots": 2000, "windowS": 64.0, "stuckS": 812.5,
//       "goals":      { "Choosing": 0, "Travelling": 120, "Hunting": 40, ... },          bots per goal now
//       "totals":     { "goalChanges": 0, "stuckTicks": 0, "hunts": 0, "missionsAsked": 0, "missionsTaken": 0, "missionsCompleted": 0,
//                       "missionsCancelled": 0, "mounts": 0, "dismounts": 0, "reversals": 0,
//                       "listed": 0, "vendored": 0, "vendorCredits": 0, "bought": 0, "buyLost": 0, "collected": 0, "marketRefused": 0 },
//                                                                                                   since the run began; the market's keys (bazaar.md § 8)
//                                                                                                   are optional, as from a swarm before the market
//       "perMin":     { the same keys, fleet-wide per minute over the window },
//       "marketRefusedBy": { "sold": 3, "notEnough": 1, ... },   the market's refusals since the start, by the server's reason name
//                                                                (MarketStats.ReasonNames: sold = 153, the race loser's); optional
//       "why":        { "HuntTimeout": 12, "CreatureNoticed": 80, ... },   goal reasons since the start (BotBrain.Trace.cs GoalWhy)
//       "whyPerMin":  { the same keys },
//       "legs":       { "Planned": 0, "Waypoint": 0, "Done": 0, "Stretch": 0, "Replanned": 0, "Stuck": 0, "Abandoned": 0,
//                       "TravelStuck": 0, "Detour": 0, "CityEdge": 0 },        walk events since the start (LegWhy)
//       "legsPerMin": { the same keys },
//       "goalChangesPerBotMin": { "mean": 1.2, "p50": 1.0, "p90": 3.0, "max": 12.0 },   across the bots, over the window
//       "reversalsPerBotMin":   { "mean": 0.1, "p50": 0.0, "p90": 0.5, "max": 4.0 }     a move > 135° off the last within 5 s
//     },
//     "traced": [                      the sampled bots: 1 % of the fleet, at least 10, at most 50 (--trace-bots)
//       { "index": 0, "goal": "Hunting", "goalForS": 3.5, "tick": 12345, "cl": 3, "x": 10.5, "z": -40.0, "realm": 0, "netId": 3456,
//         "mounted": false, "home": true, "goalChanges": 12, "reversals": 2, "stuck": 1, "legs": 9,
//         "goalS":   { "Hunting": 120.5, "Idling": 40.0 },        seconds per goal since the run began
//         "intents": { "Move": 300, "MoveTo": 120, "Attack": 80 }, commands decided, by kind
//         "events": [                                            only those newer than the last report the dashboard took
//           { "s": 41, "t": 12001, "k": "goal", "from": "Hunting", "to": "Visiting", "exit": "HuntTimeout", "enter": "ErrandStarted",
//             "subject": 3, "m": 340.0 },
//           { "s": 42, "t": 12001, "k": "leg", "leg": "Planned", "n": 14, "m": 352.1 },
//           { "s": 43, "t": 12002, "k": "intent", "intent": "MoveTo", "speed": 0, "heading": 1.57, "reversal": false, "subject": 0 }
//         ],
//         "trail": [ { "s": 300, "t": 12000, "x": 10.5, "z": -40.0, "g": "Hunting", "realm": 0, "mounted": false } ]   one a second
//       }
//     ]
//   }
//
// "netId" is the body's wire netId as the bot's own session was told it (its SELF block), 0 before it has a body; netIds are global to the game's
// database, so a god camera finds the same body under it. "x", "z", "realm" and "netId" are read on every decision and so are fresh in every
// report — a body that crosses into another realm is replicated under another netId. They are what the bots page's "Watch" hands the 3D client
// (bot-telemetry.md § 7). Added after version 1 shipped, and optional: a report without it reads 0, and the page offers no Watch for that bot.
//
// Additive changes (a new key, a new reason) keep the version; a renamed or retyped key bumps it.

/// <summary>One report from the bot swarm (version 1, above).</summary>
public sealed class BotReport
{
    /// <summary>The version this dashboard reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>How a report is read: camelCase names, as written.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("v")]
    public int Version { get; set; }

    public string Run { get; set; }

    public DateTime At { get; set; }

    public double Hz { get; set; }

    public long Tick { get; set; }

    public BotFleetReport Fleet { get; set; }

    public List<TracedBotReport> Traced { get; set; } = [];
}

/// <summary>The whole fleet's statistics.</summary>
public sealed class BotFleetReport
{
    public int Bots { get; set; }

    public double WindowS { get; set; }

    public double StuckS { get; set; }

    public Dictionary<string, long> Goals { get; set; } = [];

    public Dictionary<string, long> Totals { get; set; } = [];

    public Dictionary<string, double> PerMin { get; set; } = [];

    public Dictionary<string, long> Why { get; set; } = [];

    public Dictionary<string, double> WhyPerMin { get; set; } = [];

    public Dictionary<string, long> Legs { get; set; } = [];

    public Dictionary<string, double> LegsPerMin { get; set; } = [];

    /// <summary>The market's refusals since the start, by reason name; empty from a swarm without the market.</summary>
    public Dictionary<string, long> MarketRefusedBy { get; set; } = [];

    public BotSpread GoalChangesPerBotMin { get; set; } = new();

    public BotSpread ReversalsPerBotMin { get; set; } = new();
}

/// <summary>A per-bot rate across the fleet.</summary>
public sealed class BotSpread
{
    public double Mean { get; set; }

    public double P50 { get; set; }

    public double P90 { get; set; }

    public double Max { get; set; }
}

/// <summary>One traced bot.</summary>
public sealed class TracedBotReport
{
    public int Index { get; set; }

    public string Goal { get; set; }

    public double GoalForS { get; set; }

    public long Tick { get; set; }

    public int Cl { get; set; }

    public double X { get; set; }

    public double Z { get; set; }

    public int Realm { get; set; }

    /// <summary>The body's wire netId, 0 for none (and from a swarm that predates the field).</summary>
    public uint NetId { get; set; }

    public bool Mounted { get; set; }

    public bool Home { get; set; }

    public long GoalChanges { get; set; }

    public long Reversals { get; set; }

    public long Stuck { get; set; }

    public long Legs { get; set; }

    public Dictionary<string, double> GoalS { get; set; } = [];

    public Dictionary<string, long> Intents { get; set; } = [];

    public List<BotEventReport> Events { get; set; } = [];

    public List<BotTrailPoint> Trail { get; set; } = [];
}

/// <summary>One decision: a goal transition, a walk's event, or a change of intent — <see cref="Kind"/> says which, and which fields it carries.</summary>
public sealed class BotEventReport
{
    [JsonPropertyName("s")]
    public long Seq { get; set; }

    [JsonPropertyName("t")]
    public long Tick { get; set; }

    [JsonPropertyName("k")]
    public string Kind { get; set; }

    public string From { get; set; }

    public string To { get; set; }

    public string Exit { get; set; }

    public string Enter { get; set; }

    public long Subject { get; set; }

    [JsonPropertyName("m")]
    public double Metres { get; set; }

    public string Leg { get; set; }

    [JsonPropertyName("n")]
    public int Points { get; set; }

    public string Intent { get; set; }

    public int Speed { get; set; }

    public double? Heading { get; set; }

    public bool Reversal { get; set; }
}

/// <summary>One point of a traced bot's trail.</summary>
public sealed class BotTrailPoint
{
    [JsonPropertyName("s")]
    public long Seq { get; set; }

    [JsonPropertyName("t")]
    public long Tick { get; set; }

    public double X { get; set; }

    public double Z { get; set; }

    [JsonPropertyName("g")]
    public string Goal { get; set; }

    public int Realm { get; set; }

    public bool Mounted { get; set; }
}

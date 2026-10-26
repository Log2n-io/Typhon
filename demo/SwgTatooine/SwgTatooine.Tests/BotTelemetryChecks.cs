using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Bots.Telemetry;
using SwgTatooine.Dashboard;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// The bot telemetry (bot-telemetry.md): the sample, the brain's observer and its reasons, the report the bots write against the model the dashboard
/// reads, and the dashboard's ingest.
/// </summary>
[TestFixture]
public sealed class BotTelemetryChecks
{
    /// <summary>Port Sable, where the errand cases stand.</summary>
    private static readonly City Here = BotMap.Cities[0];

    private const int TerminalAsset = 0;

    /// <summary>BT-2: one in a hundred, clamped to [10, 50], every k-th index from 0 — the same bots on every run.</summary>
    [TestCase(100, 10)]
    [TestCase(2_000, 20)]
    [TestCase(10_000, 50)]
    public void TheSampleIsOnePercent_ClampedTo10And50_AndDeterministic(int population, int expected)
    {
        var indices = BotTraceSampler.Indices(population, 0);
        Assert.Multiple(() =>
        {
            Assert.That(BotTraceSampler.Count(population, 0), Is.EqualTo(expected));
            Assert.That(indices, Has.Length.EqualTo(expected));
            Assert.That(indices, Is.Unique.And.Ordered);
            Assert.That(indices.All(i => i >= 0 && i < population), Is.True, "an index outside the fleet was traced");
            Assert.That(BotTraceSampler.Indices(population, 0), Is.EqualTo(indices), "the sample is not the same on a second run");
            Assert.That(Enumerable.Range(0, population).Where(i => BotTraceSampler.IsTraced(i, population, 0)), Is.EqualTo(indices),
                "the per-bot predicate disagrees with the sample");
            Assert.That(indices.Max() - indices.Min(), Is.GreaterThan(population / 2), "the sample is bunched at the start of the fleet");
        });

        Assert.That(BotTraceSampler.Count(population, 7), Is.EqualTo(7), "--trace-bots did not override the share");
    }

    /// <summary>BT-1: without a dashboard no bot is traced, and a run that thinks without one never asks the sampler.</summary>
    [Test]
    public void WithoutADashboard_NoBotIsTraced()
    {
        var off = new BotSwarmOptions { Endpoint = new Uri("ws://127.0.0.1:1/ws"), Count = 2_000, Brain = true };
        var on = new BotSwarmOptions { Endpoint = off.Endpoint, Count = 2_000, Brain = true, Dashboard = new Uri("http://127.0.0.1:5090/") };
        var wandering = new BotSwarmOptions { Endpoint = off.Endpoint, Count = 2_000, Dashboard = on.Dashboard };
        Assert.Multiple(() =>
        {
            Assert.That(off.Dashboard, Is.Null, "the default reports somewhere");
            Assert.That(Enumerable.Range(0, 2_000).Count(i => BotSwarm.Traces(i, off)), Is.Zero, "a bot was traced with no dashboard");
            Assert.That(Enumerable.Range(0, 2_000).Count(i => BotSwarm.Traces(i, wandering)), Is.Zero, "a wandering bot was traced");
            Assert.That(Enumerable.Range(0, 2_000).Count(i => BotSwarm.Traces(i, on)), Is.EqualTo(20));
            Assert.That(new BotBrain(BotMap.Cities, 7u).Trace, Is.Null, "a brain is built with an observer");
        });
    }

    /// <summary>BT-1: an observer only watches — a brain decides exactly the same commands with one as without.</summary>
    [Test]
    public void AnObserverChangesNoDecision()
    {
        var bare = Run(null);
        var watched = Run(new Recorder());
        Assert.That(watched.Intents, Is.EqualTo(bare.Intents), "the observed brain decided differently");
        Assert.That(watched.Goals, Is.EqualTo(bare.Goals));
        Assert.That(bare.Goals.Distinct().Count(), Is.GreaterThanOrEqualTo(3), "the premise: the run went through several goals");

        static (List<Intent> Intents, List<Goal> Goals) Run(IBrainTrace trace)
        {
            var brain = new BotBrain(BotMap.Cities, 11u, 4d) { Trace = trace };
            var (x, z) = (0f, 0f);
            var intents = new List<Intent>();
            var goals = new List<Goal>();
            for (var tick = 1L; tick < 3_000; tick++)
            {
                // A creature passes by now and then, so the run hunts, gives up and travels.
                var creature = tick % 400 < 150;
                var world = new Perceived
                {
                    HasBody = true, X = x, Z = z,
                    NearestCreature = creature ? 42u + (uint)(tick / 400) : 0u, NearestCreatureX = x + 15f, NearestCreatureZ = z, NearestCreatureM = 15f,
                };
                var intent = brain.Decide(in world, tick);
                intents.Add(intent);
                goals.Add(brain.Goal);
                if (intent.Kind is IntentKind.Move or IntentKind.Attack && intent.SpeedClass > 0)
                {
                    x += MathF.Cos(intent.Heading) * 2.5f;
                    z += MathF.Sin(intent.Heading) * 2.5f;
                }
            }

            return (intents, goals);
        }
    }

    /// <summary>
    /// BT-3: every goal transition reaches the observer with its reason — a hunt that does not end is given up (timeout), the choice after it is an
    /// errand to the terminal, the errand is walked and done, and the next choice says the visit is why.
    /// </summary>
    [Test]
    public void EveryTransitionIsObservedWithItsReason()
    {
        for (var seed = 1u; seed < 400u; seed++)
        {
            var recorder = new Recorder();
            var brain = new BotBrain(BotMap.Cities, seed, 4d, Content(), homeRegion: 1) { Trace = recorder };
            brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
            var (x, z) = (Here.X, Here.Z);
            var tick = 1L;

            // A creature at 10 m that never dies: hunted, then given up.
            var quarry = new Perceived
            {
                HasBody = true, X = x, Z = z, NearestCreature = 42u, NearestCreatureX = x, NearestCreatureZ = z + 10f, NearestCreatureM = 10f,
            };
            brain.Decide(in quarry, tick++);
            if (brain.Goal != Goal.Hunting)
            {
                continue;
            }

            while (brain.Goal == Goal.Hunting && tick < 400)
            {
                brain.Decide(in quarry, tick++);
            }

            if (brain.Goal != Goal.Visiting)
            {
                continue;   // the roll after the hunt is the brain's: try the next seed until one chooses the errand
            }

            // The errand, walked: MoveTo puts the body on its point.
            while (brain.FacilitiesUsed == 0 && tick < 800)
            {
                var intent = brain.Decide(new Perceived { HasBody = true, X = x, Z = z }, tick++);
                (x, z) = intent.Kind == IntentKind.MoveTo ? (intent.X, intent.Z) : (x, z);
            }

            while (brain.Goal == Goal.Visiting && tick < 1_000)
            {
                brain.Decide(new Perceived { HasBody = true, X = x, Z = z }, tick++);
            }

            var g = recorder.Goals;
            Assert.Multiple(() =>
            {
                Assert.That(g[0], Is.EqualTo((Goal.Choosing, Goal.Hunting, GoalWhy.Spawned, GoalWhy.CreatureNoticed)), "the hunt's start");
                Assert.That(g[1], Is.EqualTo((Goal.Hunting, Goal.Visiting, GoalWhy.HuntTimeout, GoalWhy.ErrandStarted)), "the hunt given up, the errand chosen");
                Assert.That(g.Count, Is.GreaterThanOrEqualTo(3), "the errand's end was not observed");
                Assert.That(g[2].From, Is.EqualTo(Goal.Visiting));
                Assert.That(g[2].Exit, Is.EqualTo(GoalWhy.VisitDone), "the choice after the errand does not say the visit was done");
                Assert.That(g.All(t => t.Enter != GoalWhy.None), Is.True, "a transition began with no reason");
                Assert.That(recorder.Legs, Does.Contain(LegWhy.Planned), "the errand's walk was not observed");
                Assert.That(recorder.Legs, Does.Contain(LegWhy.Done), "the errand's walk's end was not observed");
                Assert.That(brain.Counters.Why[(int)GoalWhy.HuntTimeout], Is.EqualTo(1), "the fleet counter missed the timeout");
                Assert.That(brain.Counters.GoalChanges, Is.EqualTo(g.Count(t => t.From != t.To)));
            });
            return;
        }

        Assert.Fail("no seed under 400 chose the errand after the hunt");
    }

    /// <summary>BT-4: the report the bots write is read by the dashboard's model, every field where it was written.</summary>
    [Test]
    public void TheReportTheBotsWrite_IsTheOneTheDashboardReads()
    {
        var report = JsonSerializer.Deserialize<BotReport>(WriteReport(out var tracer, out var cursors), BotReport.Json);
        Assert.That(report, Is.Not.Null);
        var bot = report.Traced.Single();
        Assert.Multiple(() =>
        {
            Assert.That(report.Version, Is.EqualTo(BotReport.CurrentVersion));
            Assert.That(report.Run, Is.EqualTo("run1"));
            Assert.That(report.Hz, Is.EqualTo(4d));
            Assert.That(report.Tick, Is.EqualTo(1_000));
            Assert.That(report.Fleet.Bots, Is.EqualTo(1));
            Assert.That(report.Fleet.Goals.Keys, Is.EquivalentTo(Enum.GetNames<Goal>()));
            Assert.That(report.Fleet.Why["CreatureNoticed"], Is.EqualTo(1));
            Assert.That(report.Fleet.Totals["hunts"], Is.EqualTo(3));
            Assert.That(report.Fleet.Totals["goalChanges"], Is.GreaterThanOrEqualTo(1));
            Assert.That(report.Fleet.Legs.Keys, Is.EquivalentTo(Enum.GetNames<LegWhy>()));
            Assert.That(report.Fleet.GoalChangesPerBotMin, Is.Not.Null);

            Assert.That(bot.Index, Is.EqualTo(40));
            Assert.That(bot.Goal, Is.EqualTo(nameof(Goal.Hunting)));
            Assert.That(bot.Cl, Is.EqualTo(3));
            Assert.That(bot.X, Is.EqualTo(12.5).Within(0.05));
            Assert.That(bot.Home, Is.True);
            Assert.That(bot.Events.First(e => e.Kind == "goal").Enter, Is.EqualTo(nameof(GoalWhy.CreatureNoticed)));
            Assert.That(bot.Events.First(e => e.Kind == "goal").Subject, Is.EqualTo(42));
            Assert.That(bot.Events.Any(e => e.Kind == "intent" && e.Intent == nameof(IntentKind.Attack)), Is.True, "the attack was not recorded");
            Assert.That(bot.Events.Select(e => e.Seq), Is.Unique.And.Ordered);
            Assert.That(bot.Trail, Is.Not.Empty);
            Assert.That(bot.Trail[0].Goal, Is.EqualTo(nameof(Goal.Hunting)));
            Assert.That(bot.Intents[nameof(IntentKind.Attack)], Is.GreaterThan(0));
        });

        // Once taken, a report's events are not sent again: the next carries only what is newer.
        (tracer.SentEvents, tracer.SentTrail) = cursors[0];
        var again = JsonSerializer.Deserialize<BotReport>(Write(tracer, out _), BotReport.Json);
        Assert.That(again.Traced.Single().Events, Is.Empty, "an accepted event was sent again");
    }

    /// <summary>BT-5: the dashboard takes a report on POST /bots/report and its board holds it; a wrong version is refused.</summary>
    [Test]
    public async Task TheDashboardIngestsAReport()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<BotBoard>();
        await using var app = builder.Build();
        BotIngest.Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var board = app.Services.GetRequiredService<BotBoard>();
        var changed = 0;
        board.Changed += () => changed++;

        var json = WriteReport(out _, out _);
        using var ok = await http.PostAsync("/bots/report", new StringContent(json, Encoding.UTF8, "application/json"));
        // Sent twice, as the swarm does when it did not see an answer: nothing shows twice.
        using var twice = await http.PostAsync("/bots/report", new StringContent(json, Encoding.UTF8, "application/json"));
        using var wrong = await http.PostAsync("/bots/report", new StringContent("{\"v\":99,\"fleet\":{}}", Encoding.UTF8, "application/json"));
        using var junk = await http.PostAsync("/bots/report", new StringContent("not json", Encoding.UTF8, "application/json"));

        var view = board.Latest;
        Assert.Multiple(() =>
        {
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "another version was taken");
            Assert.That(junk.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(view.Reports, Is.EqualTo(2));
            Assert.That(changed, Is.EqualTo(2));
            Assert.That(view.Run, Is.EqualTo("run1"));
            Assert.That(view.Fleet.Bots, Is.EqualTo(1));
            Assert.That(view.Bots, Has.Count.EqualTo(1));
            Assert.That(view.Bots[0].Latest.Index, Is.EqualTo(40));
            Assert.That(view.Bots[0].Events.Select(e => e.Seq), Is.Unique, "a re-sent event shows twice");
            Assert.That(view.Bots[0].Events, Is.Not.Empty);
            Assert.That(view.Bots[0].Trail, Is.Not.Empty);
            Assert.That(board.History("goal changes/bot-min p90"), Has.Length.EqualTo(2));
        });

        await app.StopAsync();
    }

    /// <summary>A report of one traced bot that noticed a creature, hunted it and moved, as the swarm writes it.</summary>
    private static string WriteReport(out BotTracer tracer, out (long, long)[] cursors)
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d);
        tracer = new BotTracer(40, 4d);
        brain.Trace = tracer;
        var watch = new MoveWatch();
        for (var tick = 990L; tick <= 1_000; tick++)
        {
            var world = new Perceived
            {
                HasBody = true, X = 12.5f, Z = 0f, Level = 3,
                NearestCreature = 42u, NearestCreatureX = 12.5f, NearestCreatureZ = 50f, NearestCreatureM = 50f,
            };
            var intent = brain.Decide(in world, tick);
            var reversal = watch.See(tick, in world, intent, 20, out var heading);
            tracer.Observe(tick, in world, intent, home: true, heading, reversal);
        }

        var fleet = new FleetStats(4, DateTime.UtcNow.AddSeconds(-30));
        fleet.Begin();
        var stats = new long[Enum.GetValues<FleetStat>().Length];
        stats[(int)FleetStat.Hunts] = 3;
        fleet.Add(0, brain.Goal, brain.Counters, stats);
        fleet.End(DateTime.UtcNow);

        cursors = new (long, long)[1];
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            BotReportWriter.Write(w, "run1", DateTime.UtcNow, 4d, 1_000, fleet, [tracer], cursors);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A report of one traced bot, nothing else.</summary>
    private static string Write(BotTracer tracer, out (long, long)[] cursors)
    {
        var fleet = new FleetStats(1, DateTime.UtcNow);
        fleet.Begin();
        fleet.End(DateTime.UtcNow);
        cursors = new (long, long)[1];
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            BotReportWriter.Write(w, "run1", DateTime.UtcNow, 4d, 1_001, fleet, [tracer], cursors);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A mission terminal, standing in the open, used from a point in front of it.</summary>
    private static BotAssets Content() =>
        new([new AssetPlan { Id = "mission_terminal", Walls = [new PlanBox(-0.4f, -0.3f, 0.4f, 0.3f, 0f, 1.6f, false)], UsePath = [(0f, 0.65f)] }]);

    /// <summary>An observer that keeps what it is told.</summary>
    private sealed class Recorder : IBrainTrace
    {
        public readonly List<(Goal From, Goal To, GoalWhy Exit, GoalWhy Enter)> Goals = [];
        public readonly List<LegWhy> Legs = [];

        public void OnGoal(long tick, Goal from, Goal to, GoalWhy exit, GoalWhy enter, uint subject, float metres) => Goals.Add((from, to, exit, enter));

        public void OnLeg(long tick, LegWhy what, int points, float metres) => Legs.Add(what);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The cross-language pin for <see cref="GroupSubscriptions"/> (14 § 4, P13): a script of requests, pumps at given times, frames' ACKS and SOURCES and a
/// session's end, with what each step produced — the ids, the commands sent and their sequences, the answers. The .NET core writes it; the TypeScript and
/// native cores replay it and must produce the same.
/// </summary>
/// <remarks>
/// The <c>.bin</c> is the catalog the script runs over: an explicit set <c>lists</c> and a region set <c>towns</c>, and <c>SubscribeRequest</c> at 5 a second
/// past a burst of 5. The pumps' bucket is the command's catalog rate, full at the first pump and refilled at the times the steps name.
/// </remarks>
[TestFixture]
public class SubscribeRequestsGoldenTests
{
    private const string VectorName = "subscribe-requests";

    private const string Description =
        "GroupSubscriptions (14 § 4, P13) over the catalog in the .bin: each step's inputs and what it produced. request: set index, key (a decimal "
        + "string), op (1 subscribe, 2 unsubscribe) -> the requestId, or throws for a set that is not explicit, key 0 or another op. pump: at atMs, send "
        + "the queued requests oldest first while the command's token bucket (its catalog rate, full at the first pump, refilled continuously) has one, "
        + "sequences from 1 -> sent, in order. observe: one applied frame's ACKS, then its SOURCES -> the answers, in order: an ACK RateLimited (1) puts "
        + "its request back at the head of the queue and answers nothing, any other ACK answers rejected with its reason, a SOURCE_APPLIED (0) applied, "
        + "a SOURCE_ERROR (1) refused with its code; an ACK or a SOURCE naming nothing sent is ignored. reset: the session ended -> every request still "
        + "sent (by requestId) then queued (in order) answers dropped. Request ids run from 1, never 0, never one still waiting.";

    [Test]
    public void TheScriptProducesTheCommittedVector()
    {
        var catalog = Catalog();
        var catalogBytes = CatalogSerializer.ToCanonicalUtf8(catalog);
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(catalogBytes));
        var lists = (uint)plan.GroupSetIndex("lists");
        var towns = (uint)plan.GroupSetIndex("towns");
        Assert.That(plan.GroupSets[lists].Selector, Is.EqualTo(CatalogGroupSet.ExplicitSelector));

        var runner = new Runner(plan);
        const byte sub = BuiltInCommands.SubscribeOp;
        const byte unsub = BuiltInCommands.UnsubscribeOp;

        // Six asks: the burst of five goes at once, the sixth waits for a token.
        for (var key = 7; key <= 12; key++)
        {
            runner.Request(lists, key, sub);
        }

        runner.Request(towns, 1, sub);    // a region set: refused at the call
        runner.Request(lists, 0, sub);    // key 0: refused at the call
        runner.Request(lists, 7, 3);      // no such op: refused at the call
        runner.Pump(0);

        // The server: 9 refused for the rate (goes again), 10 rejected for its role, 7 applied, 8 refused by the application; and noise.
        runner.Observe([(3, AckReasons.RateLimited), (4, AckReasons.Forbidden), (40, AckReasons.RateLimited)],
            [(1, SourceStatus.Applied, 0), (2, SourceStatus.Error, 150), (99, SourceStatus.Applied, 0)]);
        runner.Pump(100);    // half a token: nothing
        runner.Pump(400);    // two tokens: 9 again, then 12
        runner.Request(lists, 7, unsub);
        runner.Pump(400);    // none left: the unsubscribe waits
        runner.Observe([], [(3, SourceStatus.Applied, 0), (6, SourceStatus.Applied, 0)]);
        runner.Pump(1000);   // the unsubscribe goes
        runner.Request(lists, 13, sub);
        runner.Reset();      // the unsubscribe sent, the 13 queued: both dropped
        runner.Request(lists, 13, sub);
        runner.Pump(5000);
        runner.Observe([], [(9, SourceStatus.Applied, 0)]);

        GoldenFiles.Assert(VectorName, catalogBytes, new JsonObject
        {
            ["description"] = Description,
            ["steps"] = runner.Steps,
        });
    }

    /// <summary>An id is never 0 and never one still waiting: after 65 535 the ids wrap past the ones in use.</summary>
    [Test]
    public void RequestIdsWrapPastTheOnesStillWaiting()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(CatalogSerializer.ToCanonicalUtf8(Catalog())));
        var core = new GroupSubscriptions(plan);
        var lists = (uint)plan.GroupSetIndex("lists");
        var seq = 0;
        var first = core.Request(lists, 1, BuiltInCommands.SubscribeOp);
        core.Pump((_, _) => ++seq);
        for (var i = 1; i < ushort.MaxValue - 1; i++)
        {
            core.Request(lists, 1, BuiltInCommands.SubscribeOp);
            core.Pump((_, _) => ++seq);
            core.Observe([], [((ushort)(i + 1), SourceStatus.Applied, 0)]);
        }

        var last = core.Request(lists, 1, BuiltInCommands.SubscribeOp);
        var wrapped = core.Request(lists, 1, BuiltInCommands.SubscribeOp);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(1));
            Assert.That(last, Is.EqualTo(ushort.MaxValue));
            Assert.That(wrapped, Is.EqualTo(2), "1 is still waiting: skipped, and never 0");
        });
    }

    private static Catalog Catalog() => new()
    {
        Protocol = new CatalogProtocolVersion { Major = 3 },
        App = new CatalogApp { Name = "Subscribing", Revision = 1 },
        Tick = new CatalogTick { PeriodUs = 50_000, PingHz = 4 },
        Limits = new CatalogLimits { FrameBytes = 65_536, ClientMessageBytes = 1024 },
        Archetypes =
        [
            new CatalogArchetype
            {
                Name = "Shard", Groups = ["state"],
                Fields = [new CatalogField { Name = "kind", Codec = new CatalogCodec { Kind = CodecKind.U8 }, Group = "state" }],
            },
        ],
        Enums = new Dictionary<string, string[]>(),
        Events = [],
        Grids = [],
        GroupSets =
        [
            new CatalogGroupSet { Name = "lists", Selector = CatalogGroupSet.ExplicitSelector, Key = "i32", Members = [0] },
            new CatalogGroupSet { Name = "towns", Selector = CatalogGroupSet.RegionSelector, Key = "i32", Members = [0] },
        ],
        Commands = [BuiltInCommands.CreateSubscribeRequest()],
        RealmKinds = [""],
        Metrics = [],
    };

    /// <summary>Runs the script through the core and records each step's inputs and outputs.</summary>
    private sealed class Runner
    {
        private readonly CatalogPlan _plan;
        private readonly GroupSubscriptions _core;
        private readonly CommandBucket _bucket;
        private readonly List<SubscribeResult> _answers = [];
        private int _seq;

        public Runner(CatalogPlan plan)
        {
            _plan = plan;
            _core = new GroupSubscriptions(plan);
            _core.Answered += _answers.Add;
            _bucket = new CommandBucket(Array.Find(plan.Catalog.Commands, c => c.Name == BuiltInCommands.SubscribeRequest)!.Rate);
        }

        public JsonArray Steps { get; } = [];

        public void Request(uint set, long key, byte op)
        {
            var step = new JsonObject { ["do"] = "request", ["set"] = set, ["key"] = Key(key), ["op"] = op };
            try
            {
                step["requestId"] = _core.Request(set, key, op);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                step["throws"] = true;
            }

            Steps.Add(step);
        }

        public void Pump(double atMs)
        {
            var sent = new JsonArray();
            _core.Pump((command, values) =>
            {
                Assert.That(command.Name, Is.EqualTo(BuiltInCommands.SubscribeRequest));
                if (!_bucket.Take(atMs))
                {
                    return -1;
                }

                var seq = ++_seq;
                sent.Add(new JsonObject
                {
                    ["seq"] = seq,
                    ["requestId"] = (ushort)values[BuiltInCommands.SubscribeRequestIdField].Numbers[0],
                    ["set"] = (uint)values[BuiltInCommands.SubscribeSetField].Numbers[0],
                    ["key"] = Key(unchecked((long)values[BuiltInCommands.SubscribeKeyField].Integers[0])),
                    ["op"] = (byte)values[BuiltInCommands.SubscribeOpField].Numbers[0],
                });
                return seq;
            });
            Steps.Add(new JsonObject { ["do"] = "pump", ["atMs"] = atMs, ["sent"] = sent });
        }

        public void Observe((ushort Seq, byte Reason)[] acks, (ushort RequestId, byte Status, ushort Code)[] sources)
        {
            var ackArray = new JsonArray();
            foreach (var (seq, reason) in acks)
            {
                ackArray.Add(new JsonObject { ["seq"] = seq, ["reason"] = reason });
            }

            var sourceArray = new JsonArray();
            foreach (var (requestId, status, code) in sources)
            {
                sourceArray.Add(new JsonObject { ["requestId"] = requestId, ["status"] = status, ["code"] = code });
            }

            _core.Observe(acks, sources);
            Steps.Add(new JsonObject { ["do"] = "observe", ["acks"] = ackArray, ["sources"] = sourceArray, ["answers"] = TakeAnswers() });
        }

        public void Reset()
        {
            _core.Reset();
            Steps.Add(new JsonObject { ["do"] = "reset", ["answers"] = TakeAnswers() });
        }

        private JsonArray TakeAnswers()
        {
            var array = new JsonArray();
            foreach (var a in _answers)
            {
                array.Add(new JsonObject
                {
                    ["requestId"] = a.RequestId,
                    ["set"] = a.Set,
                    ["key"] = Key(a.Key),
                    ["op"] = a.Op,
                    ["outcome"] = a.Outcome.ToString().ToLowerInvariant(),
                    ["code"] = a.Code,
                    ["ackReason"] = a.AckReason,
                });
            }

            _answers.Clear();
            return array;
        }

        private static string Key(long key) => key.ToString(CultureInfo.InvariantCulture);
    }
}

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// Explicit and shared group sets (14 § 4, step 4.3): a session holds the keys its client asked for and the application allowed, and the ones the server
/// chose; a request is answered SOURCE_APPLIED once its key is complete — or released — or SOURCE_ERROR with a code; a shared set's key is every session's.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupRequestTests : TestBase<PushGroupRequestTests>
{
    private const string Lists = "lists";
    private const string Profile = "p";

    private static SubscriptionsOptions Options(int enterBudget = 500, int maxGroups = 8) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(120),
        MaxGroupsPerSession = maxGroups,
        EnterBudgetPerFrame = enterBudget,
        PushShadow = true,
    };

    /// <summary>Allows every key but 9, which it refuses with its own code, 150.</summary>
    private static GroupDecision Admit(in GroupRequest request) => request.Key == 9 ? GroupDecision.Deny(150) : GroupDecision.Allow;

    private FrameHarness Harness(DatabaseEngine dbe, string name, int enterBudget = 500, int maxGroups = 8, Action<SubscriptionsRegistry> more = null)
    {
        var harness = FrameHarness.Create(dbe, s =>
        {
            s.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
            s.Groups(Lists, g => g.Explicit(Admit).Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
            s.Profile(Profile, p => p.Sphere(20).Groups(Lists));
            more?.Invoke(s);
        }, name, Options(enterBudget, maxGroups));
        harness.RunFence = true;
        harness.RunIngress = true;
        harness.DrainNetIds = true;
        return harness;
    }

    private static void Shards(DatabaseEngine dbe, int key, int count, int template = 1)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(template + i) }),
                GroupPositionless.Key.Set(new GroupKey { Town = key }));
        }

        tx.Commit();
    }

    private ushort _seq;

    /// <summary>Sends a SubscribeRequest as a client would, through the protocol's own writer.</summary>
    private void Send(FrameHarness harness, SessionId session, ushort requestId, long key, byte op = BuiltInCommands.SubscribeOp, uint? set = null)
    {
        var values = new RecordValues
        {
            [BuiltInCommands.SubscribeRequestIdField] = FieldValue.Of(requestId),
            [BuiltInCommands.SubscribeSetField] = FieldValue.Of(set ?? (uint)harness.CatalogPlan.GroupSetIndex(Lists)),
            [BuiltInCommands.SubscribeKeyField] = new FieldValue { Integers = [unchecked((ulong)key)] },
            [BuiltInCommands.SubscribeOpField] = FieldValue.Of(op),
        };

        var buffer = new byte[1024];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, clientTick: (uint)harness.Tick, [(harness.CatalogPlan.CommandByName(BuiltInCommands.SubscribeRequest), ++_seq, values)],
            harness.Subscriptions.Realm0Frame);
        harness.Subscriptions.Ingress.OnCommands(session, writer.Written);
    }

    /// <summary>The answers and group records frames delivered, in order: (tick, kind, request id or set, code or key).</summary>
    private sealed class Seen
    {
        public readonly List<(long Tick, ushort RequestId, byte Status, ushort Code)> Answers = [];
        public readonly List<(long Tick, byte Kind, long Key)> Groups = [];
    }

    private static void Run(FrameHarness harness, SessionId session, int ticks, Seen seen)
    {
        for (var t = 0; t < ticks; t++)
        {
            harness.RunTick(harness.Tick + 1);
            if (harness.Deliver(session) == 0)
            {
                continue;
            }

            var store = harness.Replica(session).Store;
            foreach (var (requestId, status, code) in store.Sources)
            {
                seen.Answers.Add((harness.Tick, requestId, status, code));
            }

            foreach (var (kind, _, key) in store.Groups)
            {
                seen.Groups.Add((harness.Tick, kind, key));
            }
        }
    }

    private static int ShardsHeld(FrameHarness harness, SessionId session) => harness.Replica(session).NetIds(harness.PlanIndex(nameof(GroupPositionless))).Length;

    /// <summary>P13: a request allowed is filled under the budget and answered SOURCE_APPLIED in the frame that completes its key, after the completion.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AnAllowedRequestIsAnsweredAppliedInTheFrameThatCompletesIt()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Shards(dbe, 7, 6);
        using var harness = Harness(dbe, nameof(AnAllowedRequestIsAnsweredAppliedInTheFrameThatCompletesIt), enterBudget: 2);
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var seen = new Seen();
        Run(harness, session, 2, seen);
        Assert.That(ShardsHeld(harness, session), Is.Zero, "nothing asked for, nothing held");

        Send(harness, session, 11, 7);
        Run(harness, session, 8, seen);
        var applied = seen.Answers.Where(a => a.RequestId == 11).ToList();
        var completed = seen.Groups.Where(g => g.Kind == SourceStatus.GroupComplete && g.Key == 7).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(applied, Has.Count.EqualTo(1), "answered once");
            Assert.That(applied[0].Status, Is.EqualTo(SourceStatus.Applied));
            Assert.That(completed, Has.Count.EqualTo(1));
            Assert.That(applied[0].Tick, Is.EqualTo(completed[0].Tick), "in the frame that completes the key");
            Assert.That(ShardsHeld(harness, session), Is.EqualTo(6));
            Assert.That(harness.Replica(session).Store.IsGroupComplete((uint)harness.CatalogPlan.GroupSetIndex(Lists), 7), Is.True);
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>The application's refusal travels as SOURCE_ERROR with its own code; nothing is held.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ADeniedRequestIsAnsweredWithTheApplicationsCode()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Shards(dbe, 9, 3);
        using var harness = Harness(dbe, nameof(ADeniedRequestIsAnsweredWithTheApplicationsCode));
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var seen = new Seen();
        Run(harness, session, 1, seen);
        Send(harness, session, 12, 9);
        Run(harness, session, 3, seen);
        Assert.Multiple(() =>
        {
            Assert.That(seen.Answers, Is.EquivalentTo(new[] { (seen.Answers.FirstOrDefault().Tick, (ushort)12, SourceStatus.Error, (ushort)150) }));
            Assert.That(ShardsHeld(harness, session), Is.Zero);
            Assert.That(seen.Groups, Is.Empty);
        });
    }

    /// <summary>The engine's own refusals, each with its code: an unknown or non-explicit set, a set the profile does not name, key 0, an unknown op, the limit.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void TheEnginesRefusalsCarryTheirCodes()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(TheEnginesRefusalsCarryTheirCodes), maxGroups: 2, more: s =>
        {
            s.Static<GroupBuilding>(a => a.Position(GroupBuilding.Bounds));
            s.Groups("board", g => g.Shared(5).Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town)));
            s.Profile("other", p => p.Sphere(20).Of<GroupBuilding>().Groups("board"));
        });
        // Three sessions, so none sends past SubscribeRequest's burst of five in one tick: the rate is the command's, and a request over it is an ACK.
        var sessions = harness.OpenSessions(1, Profile).Concat(harness.OpenSessions(1, "other")).Concat(harness.OpenSessions(1, Profile)).ToArray();
        foreach (var session in sessions)
        {
            harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        }

        var seen = new Seen();
        var elsewhere = new Seen();
        var limited = new Seen();
        void Both(int ticks)
        {
            for (var t = 0; t < ticks; t++)
            {
                harness.RunTick(harness.Tick + 1);
                foreach (var (session, into) in new[] { (sessions[0], seen), (sessions[1], elsewhere), (sessions[2], limited) })
                {
                    if (harness.Deliver(session) > 0)
                    {
                        foreach (var (requestId, status, code) in harness.Replica(session).Store.Sources)
                        {
                            into.Answers.Add((harness.Tick, requestId, status, code));
                        }
                    }
                }
            }
        }

        Both(1);
        Send(harness, sessions[0], 1, 3, set: 99);
        Send(harness, sessions[0], 2, 3, set: (uint)harness.CatalogPlan.GroupSetIndex("board"));
        Send(harness, sessions[0], 3, 0);
        Send(harness, sessions[0], 4, 3, op: 0);
        Send(harness, sessions[2], 5, 1);
        Send(harness, sessions[2], 6, 2);
        Send(harness, sessions[2], 7, 3);
        Send(harness, sessions[1], 8, 1);
        Both(3);

        ushort CodeOf(Seen s, ushort id) => s.Answers.Single(a => a.RequestId == id).Code;
        Assert.Multiple(() =>
        {
            Assert.That(CodeOf(seen, 1), Is.EqualTo(SourceErrorCodes.UnknownSet), "no set 99");
            Assert.That(CodeOf(seen, 2), Is.EqualTo(SourceErrorCodes.UnknownSet), "a shared set takes no request");
            Assert.That(CodeOf(seen, 3), Is.EqualTo(SourceErrorCodes.InvalidKey));
            Assert.That(CodeOf(seen, 4), Is.EqualTo(SourceErrorCodes.InvalidOp));
            Assert.That(CodeOf(limited, 5), Is.Zero, "the first key");
            Assert.That(CodeOf(limited, 6), Is.Zero, "the second");
            Assert.That(CodeOf(limited, 7), Is.EqualTo(SourceErrorCodes.Limit), "MaxGroupsPerSession = 2");
            Assert.That(CodeOf(elsewhere, 8), Is.EqualTo(SourceErrorCodes.NotInProfile));
        });
    }

    /// <summary>An unsubscribe releases the key with its members' leaves, and is answered SOURCE_APPLIED in that frame.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AnUnsubscribeIsAnsweredWithTheRelease()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Shards(dbe, 7, 4);
        using var harness = Harness(dbe, nameof(AnUnsubscribeIsAnsweredWithTheRelease));
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var seen = new Seen();
        Run(harness, session, 1, seen);
        Send(harness, session, 1, 7);
        Run(harness, session, 4, seen);
        Assert.That(ShardsHeld(harness, session), Is.EqualTo(4));

        Send(harness, session, 2, 7, op: BuiltInCommands.UnsubscribeOp);
        Run(harness, session, 3, seen);
        var answer = seen.Answers.Single(a => a.RequestId == 2);
        var released = seen.Groups.Single(g => g.Kind == SourceStatus.GroupReleased);
        Assert.Multiple(() =>
        {
            Assert.That(answer.Status, Is.EqualTo(SourceStatus.Applied));
            Assert.That(answer.Tick, Is.EqualTo(released.Tick), "in the frame that releases the key");
            Assert.That(ShardsHeld(harness, session), Is.Zero, "its members left");
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// P14: the server's keys (SetSources) are held beside the client's, answered by nothing but their completion, and replaced as a whole — the client's
    /// own keys untouched.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void TheServersKeysAreHeldBesideTheClientsAndReplacedWhole()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Shards(dbe, 7, 2);
        Shards(dbe, 8, 3, template: 20);
        using var harness = Harness(dbe, nameof(TheServersKeysAreHeldBesideTheClientsAndReplacedWhole));
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var set = (uint)harness.CatalogPlan.GroupSetIndex(Lists);
        var store = harness.Replica(session).Store;
        var seen = new Seen();
        Run(harness, session, 1, seen);

        harness.Subscriptions.Commands.Session(session).SetSources(Lists, 7);
        Send(harness, session, 1, 8);
        Run(harness, session, 4, seen);
        Assert.Multiple(() =>
        {
            Assert.That(store.IsGroupComplete(set, 7) && store.IsGroupComplete(set, 8), Is.True, "the server's 7 and the client's 8");
            Assert.That(ShardsHeld(harness, session), Is.EqualTo(5));
            Assert.That(seen.Answers.Select(a => a.RequestId), Is.EqualTo(new ushort[] { 1 }), "only the client's request is answered");
        });

        harness.Subscriptions.Commands.Session(session).SetSources(Lists);
        Run(harness, session, 3, seen);
        Assert.Multiple(() =>
        {
            Assert.That(store.IsGroupComplete(set, 7), Is.False, "the server gave 7 back");
            Assert.That(store.IsGroupComplete(set, 8), Is.True, "the client's 8 stays");
            Assert.That(ShardsHeld(harness, session), Is.EqualTo(3));
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>P15: a shared set's one key is held by every session whose profile names it, from its first frame, with no request.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ASharedSetsKeyIsEverySessionsFromItsFirstFrame()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Shards(dbe, 5, 3);
        Shards(dbe, 6, 2, template: 30);
        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
            s.Groups("board", g => g.Shared(5).Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
            s.Profile(Profile, p => p.Sphere(20).Groups("board"));
        }, nameof(ASharedSetsKeyIsEverySessionsFromItsFirstFrame), Options());
        harness.RunFence = true;
        harness.DrainNetIds = true;
        var sessions = harness.OpenSessions(2, Profile);
        foreach (var session in sessions)
        {
            harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        }

        for (var t = 0; t < 4; t++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        Assert.Multiple(() =>
        {
            foreach (var session in sessions)
            {
                Assert.That(harness.Replica(session).Store.IsGroupComplete(0, 5), Is.True);
                Assert.That(ShardsHeld(harness, session), Is.EqualTo(3), "key 5's members, not key 6's");
            }

            Assert.That(harness.CatalogPlan.GroupSets[0].Selector, Is.EqualTo(CatalogGroupSet.SharedSelector));
        });
    }

    /// <summary>A session's subscriptions are its own, not its client store's: a RESET fills and completes them again, unasked.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void SubscriptionsSurviveAReset()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Shards(dbe, 7, 3);
        using var harness = Harness(dbe, nameof(SubscriptionsSurviveAReset));
        var session = harness.OpenSessions(1, Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var seen = new Seen();
        Run(harness, session, 1, seen);
        Send(harness, session, 1, 7);
        Run(harness, session, 3, seen);

        // 3 km away: past the window, so the frame is a RESET.
        harness.Sessions.SetViewpoint(session, new Vector3D(3000d, 0d, 0d));
        var before = seen.Groups.Count(g => g.Kind == SourceStatus.GroupComplete);
        Run(harness, session, 4, seen);
        Assert.Multiple(() =>
        {
            Assert.That(seen.Groups.Count(g => g.Kind == SourceStatus.GroupComplete), Is.EqualTo(before + 1), "completed again after the RESET");
            Assert.That(ShardsHeld(harness, session), Is.EqualTo(3));
            Assert.That(seen.Answers, Has.Count.EqualTo(1), "and the request was answered once");
        });
    }

    [Test]
    [VerifiesRule("SUB-33")]
    public void ASetWithNoSelectorIsRefused()
    {
        var registry = new SubscriptionsRegistry(Options());
        registry.Groups("x", g => g.Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
        Assert.That(Assert.Catch(registry.Freeze).Message, Does.Contain("declares no selector"));
    }

    [Test]
    [VerifiesRule("SUB-33")]
    public void AReachOnAnExplicitSetAndNoneOnARegionSetAreRefused()
    {
        var keyed = new SubscriptionsRegistry(Options());
        keyed.Groups("x", g => g.Explicit().Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
        keyed.Profile("p", p => p.Sphere(10).Groups("x", 5));
        var region = new SubscriptionsRegistry(Options());
        region.Groups("x", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
        region.Profile("p", p => p.Sphere(10).Groups("x"));
        Assert.Multiple(() =>
        {
            Assert.That(Assert.Catch(keyed.Freeze).Message, Does.Contain("with a reach"));
            Assert.That(Assert.Catch(region.Freeze).Message, Does.Contain("with no reach"));
        });
    }

    [Test]
    public void ASharedKeyOf0AndAnEngineCodeFromTheApplicationAreRefusedAtTheCall()
    {
        var registry = new SubscriptionsRegistry(Options());
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.Groups("x", g => g.Shared(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => GroupDecision.Deny(SourceErrorCodes.Limit));
            Assert.Throws<InvalidOperationException>(() => registry.Groups("y", g => g.Shared(1).Explicit()), "one selector per set");
        });
    }

    /// <summary>The catalog lists SubscribeRequest at its reserved index when an explicit set takes requests, and the set as 'explicit'.</summary>
    [Test]
    public void TheCatalogListsSubscribeRequestAndTheExplicitSet()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(TheCatalogListsSubscribeRequestAndTheExplicitSet));
        var command = harness.Subscriptions.Catalog.Canonical.Commands.Single(c => c.Name == BuiltInCommands.SubscribeRequest);
        Assert.Multiple(() =>
        {
            Assert.That(command.Idx, Is.EqualTo(BuiltInCommands.SubscribeRequestIdx));
            Assert.That(BuiltInCommands.HasSubscribeRequestShape(command), Is.True);
            Assert.That(harness.CatalogPlan.GroupSets.Single(s => s.Name == Lists).Selector, Is.EqualTo(CatalogGroupSet.ExplicitSelector));
        });
    }
}

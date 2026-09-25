using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>A command for the acknowledgement tests: one number, so its sequence is all that matters.</summary>
[StructLayout(LayoutKind.Sequential)]
struct SelfStep
{
    public uint Step;
}

/// <summary>
/// <c>SELF</c> and <c>ACKS</c> (design/Subscriptions/11 § 2): the controlled entity's owner groups reach its session and no other, every group after a
/// <c>Control</c> change (SUB-11), the union of the groups changed while the session was skipped (SUB-03), <c>lastSeq</c> with the frame of the tick that
/// drained it, and every rejection on the session's next frame.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class SelfBlockTests : TestBase<SelfBlockTests>
{
    private long _tick;

    private FrameHarness Harness(DatabaseEngine dbe, string name)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclarePlayer(subs);
            subs.Profile("world", p => p.Detection(PushDetection.Automatic).World().Of<ProjPlayer>());
            subs.Profile("world2", p => p.Detection(PushDetection.Automatic).World().Of<ProjPlayer>());
            subs.Command<SelfStep>(c => c.Roles(SessionRole.Spectator, SessionRole.Player).Field(m => m.Step, Codec.VarUInt));
        }, name, new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxSessions = 8, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0), AllowAutomaticPushDetection = true,
        });
        harness.RunFence = true;
        _tick = 0;
        return harness;
    }

    private static EntityId Spawn(DatabaseEngine dbe, float x, long credits, int items)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = 10, MaxX = x, MaxY = 10 } };
        var wallet = new ProjWallet { Credits = credits, ItemCount = items };
        var vitals = new ProjVitals { Health = 50, MaxHealth = 100 };
        var id = tx.Spawn<ProjPlayer>(ProjPlayer.Bounds.Set(in bounds), ProjPlayer.Wallet.Set(in wallet), ProjPlayer.Vitals.Set(in vitals));
        tx.Commit();
        return id;
    }

    private static void SetWallet(DatabaseEngine dbe, EntityId entity, long? credits = null, int? items = null)
    {
        using var tx = dbe.CreateQuickTransaction();
        ref var wallet = ref tx.OpenMut(entity).Write(ProjPlayer.Wallet);
        if (credits is { } c)
        {
            wallet.Credits = c;
        }

        if (items is { } i)
        {
            wallet.ItemCount = i;
        }

        tx.Commit();
    }

    // Runs ticks, reading every frame of each session; returns each session's frames in order.
    private List<FrameLog>[] Run(FrameHarness harness, int ticks, params SessionId[] sessions)
    {
        var logs = sessions.Select(_ => new List<FrameLog>()).ToArray();
        for (var t = 0; t < ticks; t++)
        {
            harness.RunTick(++_tick);
            for (var s = 0; s < sessions.Length; s++)
            {
                while (harness.Read(sessions[s]) is { } log)
                {
                    logs[s].Add(log);
                }
            }
        }

        return logs;
    }

    private static byte[] Commands(CatalogPlan plan, uint clientTick, params ushort[] seqs)
    {
        var list = seqs.Select(seq => (plan.CommandByName(nameof(SelfStep)), seq, new RecordValues { ["Step"] = FieldValue.Of(seq) })).ToList();
        var buffer = new byte[4096];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, clientTick, list);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// The owner's session receives its entity's owner groups — every one on its first <c>SELF</c> — and the session beside it, which controls nothing,
    /// receives no <c>SELF</c> at all: owner values never travel anywhere else (SUB-11). A later change sends only the group that changed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void APrivateFieldReachesItsOwnerAndNoOneElse()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var mine = Spawn(dbe, 10, credits: 1234, items: 3);
        Spawn(dbe, 20, credits: 99, items: 7);
        using var harness = Harness(dbe, nameof(APrivateFieldReachesItsOwnerAndNoOneElse));
        var sessions = harness.OpenSessions(2, "world");
        harness.Sessions.SetControlled(sessions[0], mine);

        var first = Run(harness, 3, sessions);
        var netId = harness.NetIdOf(mine);
        SetWallet(dbe, mine, items: 4);
        var later = Run(harness, 2, sessions);

        Assert.Multiple(() =>
        {
            var selves = first[0].SelectMany(f => f.Selves).ToArray();
            Assert.That(selves, Has.Length.EqualTo(1), "one SELF: nothing changed after the first");
            Assert.That(selves[0], Is.EqualTo(("ProjPlayer", netId, (ushort)0, (byte)0b11)), "the first SELF names the entity and carries every owner group");
            var values = first[0].Single(f => f.Selves.Count > 0).SelfNumbers;
            Assert.That(values["credits"], Is.EqualTo(1234));
            Assert.That(values["items"], Is.EqualTo(3));

            var changed = later[0].SelectMany(f => f.Selves).ToArray();
            Assert.That(changed, Has.Length.EqualTo(1));
            var bag = Array.IndexOf(harness.CatalogPlan.ArchetypeByName("ProjPlayer").OwnerGroups, "bag");
            Assert.That(changed[0].Mask, Is.EqualTo((byte)(1 << bag)), "only the group that changed");
            Assert.That(later[0].Single(f => f.Selves.Count > 0).SelfNumbers["items"], Is.EqualTo(4));

            Assert.That(first[1].Concat(later[1]).SelectMany(f => f.Selves), Is.Empty, "the other session is never sent anyone's owner state");
        });
    }

    /// <summary>
    /// SUB-11: a control switch to another entity sends that entity's every owner group, once, even though none of its values changes afterwards; a release
    /// sends netId 0, once (W17′).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void AControlChangeSendsEveryOwnerGroupOnceAndAReleaseSaysSo()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var a = Spawn(dbe, 10, credits: 1, items: 1);
        var b = Spawn(dbe, 20, credits: 2, items: 2);
        using var harness = Harness(dbe, nameof(AControlChangeSendsEveryOwnerGroupOnceAndAReleaseSaysSo));
        var session = harness.OpenSessions(1, "world")[0];
        harness.Sessions.SetControlled(session, a);
        Run(harness, 3, session);

        harness.Sessions.SetControlled(session, b);
        var switched = Run(harness, 3, session)[0];
        harness.Sessions.SetControlled(session, EntityId.Null);
        var released = Run(harness, 3, session)[0];

        Assert.Multiple(() =>
        {
            var selves = switched.SelectMany(f => f.Selves).ToArray();
            Assert.That(selves, Has.Length.EqualTo(1), "once, and never again while nothing changes");
            Assert.That(selves[0].NetId, Is.EqualTo(harness.NetIdOf(b)));
            Assert.That(selves[0].Mask, Is.EqualTo((byte)0b11), "every owner group of the new entity");
            Assert.That(switched.Single(f => f.Selves.Count > 0).SelfNumbers["credits"], Is.EqualTo(2));

            var none = released.SelectMany(f => f.Selves).ToArray();
            Assert.That(none, Is.EqualTo(new[] { ((string)null, 0u, (ushort)0, (byte)0) }), "one SELF with netId 0, then silence");
        });
    }

    /// <summary>
    /// <c>SELF</c> follows <c>Control</c>, not geometry: a session whose disc is far from its controlled entity — it holds none of its records — still
    /// receives the entity's owner groups.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void AControlledEntityOutsideTheSessionsGeometryStillSendsItsOwnerState()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var mine = Spawn(dbe, 10, credits: 77, items: 1);
        var cellM = ProjectionTestSchema.ReplicationCellFor(30);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclarePlayer(subs);
            subs.Profile("near", p => p.Detection(PushDetection.Automatic).Sphere(30).Of<ProjPlayer>());
        }, nameof(AControlledEntityOutsideTheSessionsGeometryStillSendsItsOwnerState), new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxSessions = 4, ReplicationCellM = cellM, AllowAutomaticPushDetection = true,
        });
        harness.RunFence = true;
        _tick = 0;
        var session = harness.OpenSessions(1, "near")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(4000, 4000, 0));
        harness.Sessions.SetControlled(session, mine);

        var frames = Run(harness, 3, session)[0];

        Assert.Multiple(() =>
        {
            Assert.That(frames.SelectMany(f => f.Enters), Is.Empty, "the disc holds nothing");
            var self = frames.SelectMany(f => f.Selves).Single();
            Assert.That(self.NetId, Is.EqualTo(harness.NetIdOf(mine)));
            Assert.That(frames.Single(f => f.Selves.Count > 0).SelfNumbers["credits"], Is.EqualTo(77));
        });
    }

    /// <summary>
    /// SUB-03's clause: owner groups changed on ticks the session was skipped — its frames never drained, so the hand-off refuses it — all reach it on its
    /// next frame, with their current values.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-03")]
    public void OwnerGroupsChangedWhileSkippedArriveAsTheirUnion()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var mine = Spawn(dbe, 10, credits: 5, items: 5);
        using var harness = Harness(dbe, nameof(OwnerGroupsChangedWhileSkippedArriveAsTheirUnion));
        var session = harness.OpenSessions(1, "world")[0];
        harness.Sessions.SetControlled(session, mine);
        Run(harness, 3, session);

        // Undrained: the first two frames fill the hand-off's slots, the rest are skipped.
        SetWallet(dbe, mine, credits: 6);
        harness.RunTick(++_tick);
        SetWallet(dbe, mine, items: 6);
        harness.RunTick(++_tick);
        SetWallet(dbe, mine, credits: 7);
        harness.RunTick(++_tick);
        SetWallet(dbe, mine, items: 8);
        harness.RunTick(++_tick);
        var skipped = harness.StateOf(session).FramesSkipped;
        var backlog = new List<FrameLog>();
        while (harness.Read(session) is { } log)
        {
            backlog.Add(log);
        }

        var caughtUp = Run(harness, 1, session)[0];

        Assert.Multiple(() =>
        {
            Assert.That(skipped, Is.GreaterThan(0), "the fixture must actually skip the session");
            var self = caughtUp.SelectMany(f => f.Selves).Single();
            Assert.That(self.Mask, Is.EqualTo((byte)0b11), "the union of the groups changed while skipped");
            var values = caughtUp.Single(f => f.Selves.Count > 0).SelfNumbers;
            Assert.That(values["credits"], Is.EqualTo(7));
            Assert.That(values["items"], Is.EqualTo(8));
        });
    }

    /// <summary>
    /// <c>lastSeq</c> reaches the client in the frame of the tick that drained its command — with an owner-less SELF for a spectator that controls nothing
    /// (W17′) — and a rejection reaches it too, even across a skip.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-03")]
    public void LastSeqAndRejectionsReachTheClient()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 10, credits: 1, items: 1);
        using var harness = Harness(dbe, nameof(LastSeqAndRejectionsReachTheClient));
        harness.RunIngress = true;
        var session = harness.OpenSessions(1, "world")[0];
        Run(harness, 2, session);

        var commands = harness.Subscriptions.Commands;
        harness.AfterIngress = _ =>
        {
            foreach (ref readonly var c in commands.Commands<SelfStep>())
            {
                if (c.Value.Step % 2 == 0)
                {
                    commands.Reject(in c, 42);
                }
            }
        };

        harness.Subscriptions.Ingress.OnCommands(session, Commands(harness.CatalogPlan, 1, 1, 2, 3));
        var acked = Run(harness, 1, session)[0];

        // A rejection on a tick the session is skipped: two undrained frames first.
        harness.Subscriptions.Ingress.OnCommands(session, Commands(harness.CatalogPlan, 2, 4));
        harness.RunTick(++_tick);
        harness.Subscriptions.Ingress.OnCommands(session, Commands(harness.CatalogPlan, 3, 5));
        harness.RunTick(++_tick);
        harness.Subscriptions.Ingress.OnCommands(session, Commands(harness.CatalogPlan, 4, 6));
        harness.RunTick(++_tick);
        var all = new List<FrameLog>();
        while (harness.Read(session) is { } log)
        {
            all.Add(log);
        }

        all.AddRange(Run(harness, 1, session)[0]);

        Assert.Multiple(() =>
        {
            var self = acked.SelectMany(f => f.Selves).Single();
            Assert.That(self, Is.EqualTo(((string)null, 0u, (ushort)3, (byte)0)), "a spectator's acknowledgement: netId 0, lastSeq 3");
            Assert.That(acked.SelectMany(f => f.Acks), Is.EqualTo(new[] { ((ushort)2, (byte)42) }), "seq 2 was rejected");

            Assert.That(all.SelectMany(f => f.Acks).Select(x => x.Seq), Is.EqualTo(new ushort[] { 4, 6 }), "every rejection, the skipped tick's included");
            Assert.That(all.SelectMany(f => f.Selves).Last().LastSeq, Is.EqualTo(6));
            Assert.That(harness.Assembler.AckWindowsLost + harness.Assembler.AcksOverflowed, Is.Zero);
        });
    }

    /// <summary>
    /// SUB-11 across a flip: Control A → B → A between two published frames (the session skipped). A's changes made while B was controlled were routed to
    /// nobody, so the return to A must send every owner group — comparing entities at the frame would find A again and send nothing.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void AControlFlipBetweenTwoFramesStillSendsTheOwnerGroups()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var a = Spawn(dbe, 10, credits: 1, items: 1);
        var b = Spawn(dbe, 20, credits: 2, items: 2);
        using var harness = Harness(dbe, nameof(AControlFlipBetweenTwoFramesStillSendsTheOwnerGroups));
        var session = harness.OpenSessions(1, "world")[0];
        harness.Sessions.SetControlled(session, a);
        Run(harness, 3, session);

        // Keep the hand-off full, so every tick below is skipped for this session.
        SetWallet(dbe, a, credits: 10);
        harness.RunTick(++_tick);
        SetWallet(dbe, a, credits: 11);
        harness.RunTick(++_tick);
        harness.Sessions.SetControlled(session, b);
        SetWallet(dbe, a, credits: 12);
        harness.RunTick(++_tick);
        harness.Sessions.SetControlled(session, a);
        harness.RunTick(++_tick);
        Assert.That(harness.StateOf(session).FramesSkipped, Is.GreaterThan(0), "the fixture must skip the flip");
        while (harness.Read(session) is not null)
        {
        }

        var after = Run(harness, 8, session)[0];

        Assert.Multiple(() =>
        {
            var self = after.SelectMany(f => f.Selves).First();
            Assert.That(self.NetId, Is.EqualTo(harness.NetIdOf(a)));
            Assert.That(self.Mask, Is.EqualTo((byte)0b11), "every group: a Control change happened, even though it came back");
            Assert.That(after.First(f => f.Selves.Count > 0).SelfNumbers["credits"], Is.EqualTo(12));
        });
    }

    /// <summary>Two sessions controlling one entity each receive its owner changes (the reverse map's chain).</summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void TwoSessionsControllingOneEntityBothReceiveItsChanges()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var shared = Spawn(dbe, 10, credits: 1, items: 1);
        using var harness = Harness(dbe, nameof(TwoSessionsControllingOneEntityBothReceiveItsChanges));
        var sessions = harness.OpenSessions(3, "world");
        harness.Sessions.SetControlled(sessions[0], shared);
        harness.Sessions.SetControlled(sessions[1], shared);
        Run(harness, 3, sessions);

        SetWallet(dbe, shared, items: 9);
        var frames = Run(harness, 2, sessions);

        Assert.Multiple(() =>
        {
            for (var s = 0; s < 2; s++)
            {
                var self = frames[s].SelectMany(f => f.Selves).Single();
                Assert.That(frames[s].Single(f => f.Selves.Count > 0).SelfNumbers["items"], Is.EqualTo(9), $"session {s}");
                Assert.That(self.NetId, Is.EqualTo(harness.NetIdOf(shared)));
            }

            Assert.That(frames[2].SelectMany(f => f.Selves), Is.Empty, "the third controls nothing");
        });
    }

    /// <summary>A destroyed controlled entity: its session is told once, with netId 0 (W17′), and not again while Control still names it.</summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void ADestroyedControlledEntityIsReportedGoneOnce()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var mine = Spawn(dbe, 10, credits: 1, items: 1);
        Spawn(dbe, 20, credits: 2, items: 2);
        using var harness = Harness(dbe, nameof(ADestroyedControlledEntityIsReportedGoneOnce));
        var session = harness.OpenSessions(1, "world")[0];
        harness.Sessions.SetControlled(session, mine);
        Run(harness, 3, session);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(mine);
            tx.Commit();
        }

        var after = Run(harness, 6, session)[0];

        Assert.That(after.SelectMany(f => f.Selves), Is.EqualTo(new[] { ((string)null, 0u, (ushort)0, (byte)0) }),
            "one SELF with netId 0: the entity it named cannot be located any more");
    }

    /// <summary>A RESET (here, a profile switch) clears the client's owner state, so the frame after it carries every owner group again.</summary>
    [Test]
    [VerifiesRule("SUB-11")]
    public void AResetResendsEveryOwnerGroup()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var mine = Spawn(dbe, 10, credits: 3, items: 4);
        using var harness = Harness(dbe, nameof(AResetResendsEveryOwnerGroup));
        var session = harness.OpenSessions(1, "world")[0];
        harness.Sessions.SetControlled(session, mine);
        Run(harness, 3, session);

        Assert.That(harness.Sessions.SetProfile(session, "world2"), Is.True);
        var after = Run(harness, 3, session)[0];

        Assert.Multiple(() =>
        {
            var reset = after.First(f => (f.Flags & TickFlags.Reset) != 0);
            Assert.That(reset.Selves, Has.Count.EqualTo(1), "the RESET frame carries the SELF");
            Assert.That(reset.Selves[0].Mask, Is.EqualTo((byte)0b11));
            Assert.That(reset.SelfNumbers["credits"], Is.EqualTo(3));
        });
    }

    /// <summary>A rejection whose tick left the acknowledgement history before the session's next frame is counted, never silently dropped.</summary>
    [Test]
    public void ARejectionOlderThanTheHistoryIsCountedAsLost()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var mine = Spawn(dbe, 10, credits: 1, items: 1);
        using var harness = Harness(dbe, nameof(ARejectionOlderThanTheHistoryIsCountedAsLost));
        harness.RunIngress = true;
        var session = harness.OpenSessions(1, "world")[0];
        harness.Sessions.SetControlled(session, mine);
        Run(harness, 2, session);
        var commands = harness.Subscriptions.Commands;
        harness.AfterIngress = tick =>
        {
            foreach (ref readonly var c in commands.Commands<SelfStep>())
            {
                commands.Reject(in c, 9);
            }

            // Something to say every tick, so the undrained frames fill the hand-off and the session is skipped.
            SetWallet(dbe, mine, credits: tick);
        };

        // Two undrained frames, then a rejection on a skipped tick, then more skipped ticks than the history holds.
        harness.RunTick(++_tick);
        harness.RunTick(++_tick);
        harness.Subscriptions.Ingress.OnCommands(session, Commands(harness.CatalogPlan, 1, 1));
        for (var i = 0; i < 12; i++)
        {
            harness.RunTick(++_tick);
        }

        while (harness.Read(session) is not null)
        {
        }

        // The skip run degraded the session's rate class, so its next frame may be a few ticks away.
        var next = Run(harness, 8, session)[0];

        Assert.Multiple(() =>
        {
            Assert.That(next, Is.Not.Empty, "the session is served again");
            Assert.That(next.SelectMany(f => f.Acks), Is.Empty, "the rejection's tick is gone");
            Assert.That(harness.Assembler.AckWindowsLost, Is.GreaterThan(0), "and the window's loss is counted");
            Assert.That(next.SelectMany(f => f.Selves).Last().LastSeq, Is.EqualTo(1), "lastSeq still settles it");
        });
    }
}

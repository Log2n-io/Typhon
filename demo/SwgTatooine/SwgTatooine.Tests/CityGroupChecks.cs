using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A session standing in a city is served that city's whole structure set, not only the part of it inside its sphere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Completion is the assertion, because completion is a stronger statement than any count.</b> <c>GROUP_COMPLETE</c>
/// means every member carrying that key has entered the session — so a city whose radius exceeds the player's sphere
/// cannot be complete unless structures outside the sphere were delivered. Counting held buildings and comparing against
/// a geometric expectation would assert the same thing less exactly, and would need the netIds mapped back to entities.
/// </para>
/// <para>
/// <b>The premise is asserted first.</b> If every city were smaller than a player's sphere the set would deliver nothing
/// a sphere does not already carry, and this case would pass while proving nothing. The world's own city radii are
/// checked against <see cref="TatooineReplication.PlayerRadiusM"/> before the session is looked at.
/// </para>
/// <para>
/// The player is parked at the city's centre rather than left where the generator put it: 66 % of players spawn in a
/// city, so an unparked session would usually be in one and occasionally not, which is a flake rather than a test.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class CityGroupChecks : ServedChecks
{
    private SimConfig Config()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>A player in a city is told that city's set is complete, which means it holds every structure the city has.</summary>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public void APlayerInACityIsServedTheWholeCityAndToldWhenItIsComplete()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();

        // The premise. A city no larger than the sphere would make this case vacuous.
        var city = Worlds.WidestCity(sim);
        var widest = city.Index;
        Assert.That(
            (double)city.Radius,
            Is.GreaterThan(TatooineReplication.PlayerRadiusM),
            "the premise: the widest city must be larger than a player's sphere, or the group set delivers nothing a sphere does not");

        Harness = new SessionHarness(sim);
        var link = Harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the possession to be claimed by a tick");
        Harness.Ticks(2);

        Worlds.Park(sim, link.Session, city.X, city.Z);

        var sink = new CitySink { Completed = [], Released = [], Held = [] };
        var frame = default(RealmFrame);
        var read = 0;

        void Drain()
        {
            var sent = link.Sent;
            for (; read < sent.Length; read++)
            {
                var message = sent[read];
                if (message.Length > 0 && message[0] == MessageTypes.Tick)
                {
                    TickReader.Read(message, link.Plan, ref frame, ref sink);
                }
            }
        }

        var set = SetIndexOf(link.Plan, TatooineReplication.CitySet);
        SessionHarness.Until(
            () =>
            {
                Harness.Ticks(1);
                Drain();
                return sink.Completed.Contains((set, widest + 1));
            },
            $"the '{TatooineReplication.CitySet}' set to complete for city {widest} (key {widest + 1}), "
            + $"a {city.Radius:F0} m city around a {TatooineReplication.PlayerRadiusM:F0} m sphere");

        // The geometry, and not only the completion. GROUP_COMPLETE is the stronger statement and it is what this case
        // waits for, but it rests on the engine's own definition of complete: one structure measurably outside the
        // sphere says the same thing in the case's own terms, and fails loudly rather than by timeout if the set ever
        // starts completing without delivering.
        var beyond = 0;
        var positions = sink.Positions;
        Assert.That(positions, Is.Not.Null, "no structure carried a position, so the geometry cannot be checked");
        foreach (var at in positions.Values)
        {
            var dx = at.X - city.X;
            var dz = at.Z - city.Z;
            if (Math.Sqrt((dx * dx) + (dz * dz)) > TatooineReplication.PlayerRadiusM)
            {
                beyond++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(sink.Held, Is.Not.Empty, "a complete city delivered no structures at all");
            Assert.That(
                beyond, Is.GreaterThan(0),
                $"every structure delivered was inside the {TatooineReplication.PlayerRadiusM:F0} m sphere, so the group set proved nothing");
            Assert.That(sink.Released, Does.Not.Contain((set, widest + 1)), "the city was released again while the player stood in it");
        });
    }

    /// <summary>The set's index in the catalog, which is what a frame's group records name it by.</summary>
    /// <param name="plan">The session's catalog plan.</param>
    /// <param name="name">The set's declared name.</param>
    /// <returns>Its index.</returns>
    private static uint SetIndexOf(CatalogPlan plan, string name)
    {
        for (var i = 0; i < plan.GroupSets.Length; i++)
        {
            if (plan.GroupSets[i].Name == name)
            {
                return (uint)i;
            }
        }

        Assert.Fail($"the catalog declares no group set named '{name}'");
        return 0;
    }

    /// <summary>Records the group lifecycle records a session's frames carried, and the structures it holds.</summary>
    private struct CitySink : ITickSink
    {
        /// <summary>Every <c>(set, key)</c> completed, in arrival order.</summary>
        public List<(uint Set, long Key)> Completed;

        /// <summary>Every <c>(set, key)</c> released.</summary>
        public List<(uint Set, long Key)> Released;

        /// <summary>The netIds of the structures currently held.</summary>
        public HashSet<uint> Held;

        private bool _structures;

        public void Group(byte kind, uint set, long key)
        {
            if (kind == SourceStatus.GroupComplete)
            {
                Completed.Add((set, key));
            }
            else if (kind == SourceStatus.GroupReleased)
            {
                Released.Add((set, key));
            }
        }

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
            if ((flags & TickFlags.Reset) != 0)
            {
                Held.Clear();
            }
        }

        public void BeginEntities(ArchetypePlan archetype) => _structures = archetype.Name == nameof(WorldObject);

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
            if (!_structures)
            {
                return;
            }

            Held.Add(netId);
            if (position.Length >= 2)
            {
                (Positions ??= [])[netId] = (position[0], position[1]);
            }
        }

        /// <summary>Where each held structure is, so a case can assert on the geometry rather than only on the count.</summary>
        /// <remarks>Created on first entry: this is a struct, so a field initialiser here would need a constructor it has no other use for.</remarks>
        public Dictionary<uint, (double X, double Z)> Positions;

        public void Leave(uint netId) => Held.Remove(netId);

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
        }

        public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
        {
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }

        public void Realm(RealmFrame frame)
        {
        }

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
        }

        public void State(uint netId, byte groupMask)
        {
        }

        public void Event(MessagePlan type)
        {
        }

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
        {
        }

        public void Ack(ushort seq, byte reason)
        {
        }

        public void Source(ushort requestId, byte status, ushort code)
        {
        }

        public void BeginAggregate(CatalogGrid grid, bool reset)
        {
        }

        public void Aggregate(int channel, int tile, double value)
        {
        }

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
        {
        }

        public void Metric(MetricPlan metric, int instance, double value)
        {
        }

        public void Debug(byte kind, scoped ReadOnlySpan<byte> payload)
        {
        }

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload)
        {
        }

        public void UnknownBlock(byte blockType)
        {
        }

        public void EndTick()
        {
        }
    }
}

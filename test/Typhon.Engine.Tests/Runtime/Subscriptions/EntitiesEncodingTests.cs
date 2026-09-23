using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-13b — the bytes the engine's <c>ENTITIES</c> encoder puts on the wire, checked where it matters: against the decoder a client actually runs, and
/// against a committed vector the other SDKs read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The .NET check is the whole store, not the decoder.</b> <c>Typhon.Client</c>'s <see cref="Typhon.Client.FrameApplier"/> decodes a frame straight into
/// a <see cref="Typhon.Client.WorldStore"/>, so asserting on the store asserts that the bytes carried the right values for the right entities in the right
/// order — the same property AC-12 asks of the golden vectors, reached from the other end.
/// </para>
/// <para>
/// <b>The vector is self-contained</b>, and deliberately so. <c>stream-kitchen-sink</c> names a companion catalog vector; this one cannot, because the
/// engine's own catalog vector (<c>catalog-engine</c>) is emitted from declarations that include a <c>ClientRegion</c> observer, and a runtime refuses to
/// start with one — so no runtime can both produce frames and emit that catalog. The stream therefore opens with the <c>WELCOME</c> that carries its
/// own catalog, exactly as a TCP client sees it, and a reader of the file needs nothing else.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class EntitiesEncodingTests : TestBase<EntitiesEncodingTests>
{
    /// <summary>The vector this fixture regenerates under <c>TYPHON_UPDATE_GOLDEN=1</c>, and the one the TypeScript SDK reads back.</summary>
    private const string GoldenCase = "stream-engine";

    private const string GoldenDescription =
        "The engine's own ENTITIES encoder, as a TCP-framed stream (u32 length LE + message, W31): a WELCOME carrying the catalog these frames decode "
        + "against, then one TICK per tick of a driven Engine-Subscriptions track over a moving archetype with a packed enum and a fraction, an archetype "
        + "with an owner section, and a static one. The frames cover the initial fill (VIEW_COMPLETE), motion segments, state records with a group mask, "
        + "leaves after enters in the same frame, and a RESET refill. The expectation is the sequence of calls a decoder makes into its sink, per frame.";

    private const string Profile = "god-world";
    private const string OtherProfile = "god-world-again";

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("player");
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>().Of<ProjPlayer>().Of<ProjRock>());
        subs.Profile(OtherProfile, p => p.World().Of<ProjCreature>().Of<ProjPlayer>().Of<ProjRock>());
    }

    private static SubscriptionsOptions Options() =>
        new()
        {
            MaxSessions = 16,
            StatePoolBudgetBytes = 16L * 1024 * 1024,
            FramePoolBudgetBytes = 16L * 1024 * 1024,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
        };

    private FrameHarness Create()
    {
        var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, "EntitiesEncodingTests", Options());

        // The fence publishes the structure marks the engine's own pushes ride: a spawn, a destroy, a spatial write.
        harness.RunFence = true;
        return harness;
    }

    // ── The decoder a client runs ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every frame the assembler produced decodes in <c>Typhon.Client</c>'s applier, and the replica it fills equals the values the engine holds.
    /// </summary>
    [Test]
    public void TheProducedBytesDecodeIntoTheClientsReplica()
    {
        using var harness = Create();
        SpawnCreatures(harness, 6);
        SpawnRocks(harness, 3);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.RunTick(1);
        Assert.That(harness.Deliver(session), Is.EqualTo(1), "the fill frame decoded");

        SetLevels(harness, 4242);
        harness.RunTick(2);
        Assert.That(harness.Deliver(session), Is.EqualTo(1), "the update frame decoded");

        var replica = harness.Replica(session);
        var creature = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        var rock = harness.CatalogPlan.ArchetypeByName(nameof(ProjRock)).Idx;

        Assert.Multiple(() =>
        {
            Assert.That(replica.Store.Anomalies, Is.Zero, "a well-formed stream produces no anomaly");
            Assert.That(replica.NetIds(creature), Has.Length.EqualTo(6));
            Assert.That(replica.NetIds(rock), Has.Length.EqualTo(3), "a static archetype enters like any other");

            foreach (var netId in replica.NetIds(creature))
            {
                Assert.That(replica.Value(creature, netId, "level"), Is.EqualTo(4242d), "the state record carried the new value");
                Assert.That(replica.Value(creature, netId, "template"), Is.EqualTo(7d), "an onEnter field arrived with the enter and was not resent");
                Assert.That(replica.Value(creature, netId, "hp"), Is.EqualTo(5d / 10d).Within(1d / 255d), "a fraction survives its 8-bit codec");
            }

            foreach (var netId in replica.NetIds(rock))
            {
                var position = replica.Position(rock, netId);
                Assert.That(position[1], Is.EqualTo(-50d).Within(0.01d), "a static archetype's position travels in its enter record");
            }
        });
    }

    /// <summary>
    /// A static archetype's enter carries its position and its block carries no segment at all — <c>nSegment</c> non-zero there is a 1007 (03 § 5).
    /// </summary>
    [Test]
    public void AStaticArchetypeCarriesItsPositionOnEnterAndNoSegment()
    {
        using var harness = Create();
        SpawnRocks(harness, 4);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.RunTick(1);
        var frame = harness.Read(session);

        // Moving a static archetype is a leave and a later enter, never a segment, so nothing this fixture does can produce one — and the decoder would
        // refuse the block if it did. Reaching here at all is the assertion.
        Assert.Multiple(() =>
        {
            Assert.That(frame.Blocks, Is.EqualTo(new[] { nameof(ProjRock) }));
            Assert.That(frame.Enters.Count, Is.EqualTo(4));
            Assert.That(frame.Segments, Is.Empty, "a static archetype never carries a segment");
            Assert.That(frame.States, Is.Empty, "and never a state record: every field folds into its enter");
        });
    }

    // ── The vector ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine's encoder against its committed vector: the exact bytes, and the exact sequence of calls decoding them makes.
    /// </summary>
    /// <remarks>
    /// Regenerated only under <c>TYPHON_UPDATE_GOLDEN=1</c>, for the reason <c>Typhon.Protocol.Tests</c>' helper gives: a self-computed expectation follows
    /// the bug. The scheme is that helper's and deliberately not a reference to it — that type is internal to another test assembly, and the vector has to
    /// live in its directory because the TypeScript SDK reads the whole directory from there.
    /// </remarks>
    [Test]
    public void TheEngineStreamMatchesItsGoldenVector()
    {
        using var harness = Create();
        var creatures = SpawnCreatures(harness, 5);
        SpawnPlayers(harness, 2);
        SpawnRocks(harness, 3);
        var session = harness.OpenSessions(1, Profile)[0];

        var stream = new List<byte>();
        var expectation = new JsonArray();

        // The WELCOME a TCP client receives first: the catalog every frame below decodes against, and the tick period a client rebuilds segment times from.
        var welcome = new byte[harness.Subscriptions.Catalog.Utf8.Length + 256];
        var writer = new WireWriter(welcome);
        new WelcomeMessage
        {
            SessionId = 1,
            ResumeToken = new byte[16],
            Tick = 1,
            TickPeriodUs = harness.Subscriptions.NominalTickPeriodUs,
            CatalogHash = harness.Subscriptions.Catalog.Hash,
            CatalogJson = harness.Subscriptions.Catalog.Utf8,
        }.Write(ref writer);
        Append(stream, writer.Written);

        // 1 — the initial fill: every archetype, VIEW_COMPLETE.
        // 2 — motion: three creatures teleport, which the motion rule turns into segments.
        // 3 — state: every creature's vitals group changes, and nothing else does.
        // 4 — churn: two creatures leave and one enters, in one frame.
        // 5 — RESET: a profile switch, so the whole view is re-sent.
        for (var tick = 1; tick <= 5; tick++)
        {
            switch (tick)
            {
                case 2:
                    MoveCreatures(harness, 3);
                    break;
                case 3:
                    SetLevels(harness, 777);
                    break;
                case 4:
                    Destroy(harness, creatures.Take(2));
                    SpawnCreatures(harness, 1);
                    break;
                case 5:
                    Assert.That(harness.Sessions.SetProfile(session, OtherProfile), Is.True);
                    break;
            }

            harness.RunTick(tick);
            foreach (var frame in harness.Collect(session))
            {
                Append(stream, frame);
                var log = new FrameLog();
                log.Decode(frame, harness.CatalogPlan);
                expectation.Add(Render(log));
            }
        }

        Assert.That(expectation.Count, Is.EqualTo(5), "one frame per tick, so the vector's shape is stated rather than discovered");
        AssertGolden(stream.ToArray(), expectation, harness.Subscriptions.Catalog.Hash);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Append(List<byte> stream, ReadOnlySpan<byte> message)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)message.Length);
        stream.AddRange(length.ToArray());
        stream.AddRange(message.ToArray());
    }

    private static JsonObject Render(FrameLog log)
    {
        var calls = new JsonArray();
        foreach (var call in log.Calls)
        {
            calls.Add(call);
        }

        return new JsonObject { ["tick"] = log.TickNumber, ["flags"] = (int)log.Flags, ["calls"] = calls };
    }

    private static void AssertGolden(byte[] bytes, JsonArray frames, ulong catalogHash)
    {
        var directory = Path.Combine(CatalogBuilderTests.RepositoryRoot(), "test", "Typhon.Protocol.Tests", "Golden");
        var binPath = Path.Combine(directory, GoldenCase + ".bin");
        var jsonPath = Path.Combine(directory, GoldenCase + ".json");

        var expectation = new JsonObject
        {
            ["description"] = GoldenDescription,
            ["catalogHash"] = CatalogSerializer.ToHex(catalogHash),
            ["frames"] = frames,
        };

        // "\n", not Environment.NewLine: regenerating on Windows and on Linux must produce the same file.
        var json = expectation.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("TYPHON_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(binPath, bytes);
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
            return;
        }

        Assert.That(File.Exists(binPath), Is.True, $"missing golden vector {GoldenCase}.bin - regenerate with TYPHON_UPDATE_GOLDEN=1 and commit it");

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexString(bytes), Is.EqualTo(Convert.ToHexString(File.ReadAllBytes(binPath))), $"{GoldenCase}.bin drifted from the vector");
            Assert.That(json, Is.EqualTo(File.ReadAllText(jsonPath).ReplaceLineEndings("\n")), $"{GoldenCase}.json drifted from the vector");
        });
    }

    private static EntityId[] SpawnCreatures(FrameHarness harness, int count)
    {
        var entities = new EntityId[count];
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = At(10f + (i * 3f), 10f);
            var ai = new ProjAi { Template = 7, Level = 100, Mode = ProjAiMode.Wander, Alerted = 1 };
            var vitals = new ProjVitals { Health = 5, MaxHealth = 10 };
            entities[i] = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        }

        tx.Commit();
        return entities;
    }

    private static void SpawnPlayers(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = At(600f + (i * 4f), 600f);
            var vitals = new ProjVitals { Health = 7, MaxHealth = 10 };
            var wallet = new ProjWallet { Credits = 1234 + i, ItemCount = 3 + i };
            tx.Spawn<ProjPlayer>(ProjPlayer.Bounds.Set(in bounds), ProjPlayer.Vitals.Set(in vitals), ProjPlayer.Wallet.Set(in wallet));
        }

        tx.Commit();
    }

    private static void SpawnRocks(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = At(-200f + (i * 5f), -50f);
            var ai = new ProjAi { Template = (byte)(i + 1) };
            tx.Spawn<ProjRock>(ProjRock.Bounds.Set(in bounds), ProjRock.Ai.Set(in ai));
        }

        tx.Commit();
    }

    private static void Destroy(FrameHarness harness, IEnumerable<EntityId> entities)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        foreach (var entity in entities)
        {
            tx.Destroy(entity);
        }

        tx.Commit();
    }

    private static void MoveCreatures(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var bounds = cluster.GetSpan(ProjCreature.Bounds);
#pragma warning restore TYPHON009
            var moved = 0;
            while (occupancy != 0 && moved < count)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                bounds[slot] = At(1500f + (slot * 9f), 1500f);
                moved++;
            }

            break;
        }

        accessor.Dispose();
        tx.Commit();
    }

    /// <summary>Writes every creature's level through the span path, and pushes each slot it wrote, as an explicit profile's system must.</summary>
    private static void SetLevels(FrameHarness harness, ushort level)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
            while (occupancy != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                ai[slot].Level = level;
                harness.Subscriptions.Commands.Replicate(in cluster, slot);
            }
        }

        accessor.Dispose();
        tx.Commit();
    }

    private static ProjBounds At(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f }, Speed = 1f };
}

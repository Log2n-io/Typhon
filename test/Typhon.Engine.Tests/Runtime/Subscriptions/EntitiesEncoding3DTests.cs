using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Typhon.Engine.Tests.Runtime.Subscriptions;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <c>stream-engine-3d</c> (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 9, 1.5.5): the engine's own encoder over a deep grid, as a
/// TCP-framed stream both SDKs decode — 3D movers (<c>pos3</c>, <c>vel3</c>) beside 2D walkers on the plane z = 0 and a static archetype, served to a
/// sphere by the deep implementation.
/// </summary>
/// <remarks>
/// Regenerated only under <c>TYPHON_UPDATE_GOLDEN=1</c>, like <c>stream-engine</c>. <c>Typhon.Client.Tests</c> and the TypeScript SDK read the same two
/// files, so the vector pins the deep path's bytes in three places.
/// </remarks>
[TestFixture]
[NonParallelizable]
class EntitiesEncoding3DTests : TestBase<EntitiesEncoding3DTests>
{
    private const string GoldenCase = "stream-engine-3d";

    private const string GoldenDescription =
        "The engine's ENTITIES encoder over a deep replication grid (a 2 km cube at c = 256 m, the deep implementation), as a TCP-framed stream "
        + "(u32 length LE + message, W31): a WELCOME carrying the catalog, then one TICK per tick of a driven track serving a 1 000 m sphere at the origin. "
        + "3D movers (pos3, vel3) beside 2D walkers on the plane z = 0 and a static archetype; the frames cover the fill (VIEW_COMPLETE), 3D motion "
        + "segments, state records, leaves after enters in one frame, and a RESET refill. The expectation is the sequence of calls a decoder makes.";

    private const string Profile = "deep-sphere";
    private const string OtherProfile = "deep-sphere-again";

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("player");
        subs.Archetype<ProjFlyer>(a => a
            .Motion(ProjFlyer.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps))
            .OnEnter(ProjFlyer.Ai, x => x.Template, Codec.U8, name: "template")
            .Field(ProjFlyer.Ai, x => x.Level, Codec.U16, name: "level", group: "vitals"));
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.Sphere(1000).Of<ProjFlyer>().Of<ProjCreature>().Of<ProjRock>());
        subs.Profile(OtherProfile, p => p.Sphere(1000).Of<ProjFlyer>().Of<ProjCreature>().Of<ProjRock>());
    }

    [Test]
    public void TheDeepEngineStreamMatchesItsGoldenVector()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), Declare, nameof(EntitiesEncoding3DTests),
            new SubscriptionsOptions
            {
                MaxSessions = 16,
                StatePoolBudgetBytes = 16L * 1024 * 1024,
                FramePoolBudgetBytes = 16L * 1024 * 1024,
                ReplicationCellM = 256,
            });
        harness.RunFence = true;
        Assert.That(harness.Subscriptions.Push.Deep, Is.True, "a volumetric world is served by the deep implementation");

        var flyers = SpawnFlyers(harness, 5);
        SpawnCreatures(harness, 3);
        SpawnRocks(harness, 2);
        var session = harness.OpenSessions(1, Profile)[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0, 0, 0)), Is.True);

        var stream = new List<byte>();
        var expectation = new JsonArray();
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

        // 1 — the fill; 2 — three flyers jump across the cube, in 3D; 3 — every flyer's vitals; 4 — two flyers leave, one enters; 5 — RESET.
        for (var tick = 1; tick <= 5; tick++)
        {
            switch (tick)
            {
                case 2:
                    MoveFlyers(harness, 3);
                    break;
                case 3:
                    SetLevels(harness, 777);
                    break;
                case 4:
                    Destroy(harness, flyers.Take(2));
                    SpawnFlyers(harness, 1);
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
                var calls = new JsonArray();
                foreach (var call in log.Calls)
                {
                    calls.Add(call);
                }

                expectation.Add(new JsonObject { ["tick"] = log.TickNumber, ["flags"] = (int)log.Flags, ["calls"] = calls });
            }
        }

        Assert.That(expectation.Count, Is.EqualTo(5), "one frame per tick");
        AssertGolden(stream.ToArray(), expectation, harness.Subscriptions.Catalog.Hash);
    }

    private static void Append(List<byte> stream, ReadOnlySpan<byte> message)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)message.Length);
        stream.AddRange(length.ToArray());
        stream.AddRange(message.ToArray());
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

    private static EntityId[] SpawnFlyers(FrameHarness harness, int count)
    {
        var entities = new EntityId[count];
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = At3(10f + (i * 3f), 10f, 20f + (i * 5f));
            var ai = new ProjAi { Template = 9, Level = 100 };
            entities[i] = tx.Spawn<ProjFlyer>(ProjFlyer.Bounds.Set(in bounds), ProjFlyer.Ai.Set(in ai));
        }

        tx.Commit();
        return entities;
    }

    private static void SpawnCreatures(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = -30.5f + (i * 4f), MinY = 39.5f, MaxX = -29.5f + (i * 4f), MaxY = 40.5f }, Speed = 1f };
            var ai = new ProjAi { Template = 7, Level = 100, Mode = ProjAiMode.Wander, Alerted = 1 };
            var vitals = new ProjVitals { Health = 5, MaxHealth = 10 };
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        }

        tx.Commit();
    }

    private static void SpawnRocks(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = -200.5f + (i * 5f), MinY = -50.5f, MaxX = -199.5f + (i * 5f), MaxY = -49.5f }, Speed = 1f };
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

    // A jump well past the declared speed, so the motion rule starts a new epoch: three axes on the wire.
    private static void MoveFlyers(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjFlyer>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            var moved = 0;
            while (occupancy != 0 && moved < count)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                cluster.WriteSpatial(ProjFlyer.Bounds, slot, At3(300f + (slot * 9f), 300f, -200f));
                moved++;
            }

            break;
        }

        accessor.Dispose();
        tx.Commit();
    }

    private static void SetLevels(FrameHarness harness, ushort level)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjFlyer>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var ai = cluster.GetSpan(ProjFlyer.Ai);
#pragma warning restore TYPHON009
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                ai[slot].Level = level;
                harness.Subscriptions.Commands.Replicate(in cluster, slot);
            }

            cluster.MarkDirty(ProjFlyer.Ai);
        }

        accessor.Dispose();
        tx.Commit();
    }

    private static ProjBounds3 At3(float x, float y, float z) => new()
    {
        Bounds = new AABB3F { MinX = x - 0.5f, MinY = y - 0.5f, MinZ = z - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f, MaxZ = z + 0.5f },
        Speed = 1f,
    };
}

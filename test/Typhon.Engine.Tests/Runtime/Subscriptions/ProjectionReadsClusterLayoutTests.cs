using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// SUB-01 — replication reads component values only through the archetype's cluster layout.
/// </summary>
/// <remarks>
/// <para>
/// Two halves, because the rule has two. The <b>behavioural</b> half writes a component through
/// <see cref="ClusterRef{TArch}.GetSpan{T}"/>, projects it through the compiled plan and asserts every projected value equals what
/// <see cref="ClusterRef{TArch}.GetReadOnlySpan{T}"/> reports: an offset that addressed the wrong field, or a stride that addressed the wrong slot, produces a
/// different number here and nowhere else. The <b>structural</b> half asserts the plan holds no component segment and no entity-location structure, so there
/// is no second path it could have read through.
/// </para>
/// <para>
/// <b>The write goes through <c>GetSpan</c> on purpose.</b> That is the path that sets no dirty bit and signals nothing, so a replication design that keyed on
/// dirtiness rather than on the values would pass a test that wrote through <c>EntityRef.Write</c> and fail here.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ProjectionReadsClusterLayoutTests : TestBase<ProjectionReadsClusterLayoutTests>
{
    private const int EntityCount = 8;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds At(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f }, Speed = 1f };

    /// <summary>
    /// Values projected through the compiled plan equal the values the cluster's own read-only spans report, field by field and slot by slot.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-01")]
    public void ProjectedValuesEqualClusterRefSpans()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));

        // Spawn into one cell so every entity lands in one cluster, then give each slot a distinct value through the span path.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                var bounds = At(10f, 10f);
                var ai = new ProjAi();
                var vitals = new ProjVitals { Health = 1, MaxHealth = 1 };
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjCreature>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                // TYPHON009: the un-barriered mutable span write. It is the point of the case — it sets no dirty bit, so nothing but the values themselves
                // tells replication anything changed.
#pragma warning disable TYPHON009
                var ai = cluster.GetSpan(ProjCreature.Ai);
                var vitals = cluster.GetSpan(ProjCreature.Vitals);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ai[slot].Template = (byte)(200 + slot);
                    ai[slot].Mode = (ProjAiMode)(slot % 5);
                    ai[slot].Alerted = (byte)(slot & 1);
                    ai[slot].Level = (ushort)(1000 + (slot * 7));
                    ai[slot].ThinkCooldown = slot * 31;
                    vitals[slot].Health = slot;
                    vitals[slot].MaxHealth = 10;
                }

                cluster.MarkDirty(ProjCreature.Ai);
                cluster.MarkDirty(ProjCreature.Vitals);
            }

            accessor.Dispose();
            tx.Commit();
        }

        var codes = new uint[64];
        var expected = new uint[64];
        var visited = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjCreature>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var slots = cluster.OccupancyBits;
                if (slots == 0)
                {
                    continue;
                }

                // The two columns the projection reads, taken through the archetype's own accessor. The compiled field supplies the stride and the field
                // offset; if either disagreed with the layout these spans were carved from, the codes below would not match.
                var ai = cluster.GetReadOnlySpan(ProjCreature.Ai);
                var vitals = cluster.GetReadOnlySpan(ProjCreature.Vitals);
                var aiColumn = MemoryMarshal.AsBytes(ai);
                var vitalsColumn = MemoryMarshal.AsBytes(vitals);

                Expect(slots, expected, ai, vitals, "level");
                AssertColumn(plan, "level", aiColumn, slots, codes, expected);
                Expect(slots, expected, ai, vitals, "mode");
                AssertColumn(plan, "mode", aiColumn, slots, codes, expected);
                Expect(slots, expected, ai, vitals, "alerted");
                AssertColumn(plan, "alerted", aiColumn, slots, codes, expected);
                Expect(slots, expected, ai, vitals, "template");
                AssertColumn(plan, "template", aiColumn, slots, codes, expected);
                Expect(slots, expected, ai, vitals, "hp");
                AssertColumn(plan, "hp", vitalsColumn, slots, codes, expected);

                visited += BitOperations.PopCount(slots);
            }

            accessor.Dispose();
        }

        Assert.That(visited, Is.EqualTo(EntityCount), "every spawned entity was projected");
    }

    /// <summary>
    /// A slot bit past the column's own slot count is dropped, not followed: the walk masks, so nothing reads past the column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readers index with <c>Unsafe.Add</c> and bound nothing, so before the mask any set bit of the caller's <c>ulong</c> became a read at
    /// <c>slot × stride</c> bytes into memory the column does not own. That mask is not hypothetical bookkeeping: a watched mask outlives its cluster for one
    /// tick by design (the <c>EntityId</c> re-initialization path), and a cluster that shrank between the mask being taken and the column being walked is
    /// exactly this input.
    /// </para>
    /// <para>
    /// The backing array is deliberately longer than the column and its tail is filled with <c>0xFF</c>, so an unmasked walk would not merely be undefined —
    /// it would produce a specific wrong answer this test can name.
    /// </para>
    /// </remarks>
    [Test]
    public void SlotsPastTheColumnsEndAreMaskedOffRatherThanRead()
    {
        const int stride = 4;
        const int slotCount = 8;

        var backing = new byte[256];
        for (var i = 0; i < backing.Length; i++)
        {
            backing[i] = 0xFF;
        }

        for (var slot = 0; slot < slotCount; slot++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(backing.AsSpan(slot * stride), (uint)(slot + 1));
        }

        var field = new CompiledField
        {
            Name = "synthetic",
            ComponentSize = stride,
            FieldOffsetInComponent = 0,
            RatioOffsetInComponent = -1,
            SourceType = ProjectionSourceType.UInt32,
            CodecKind = CodecKind.U32,
            CodeMin = 0d,
            CodeMax = uint.MaxValue,
        };

        var codes = new uint[64];
        var column = new ProjectionColumn(backing.AsSpan(0, slotCount * stride), stride, 0);
        var covered = column.SlotCount;
        ProjectionColumnWalk.Quantize(field, column, ulong.MaxValue, codes);

        Assert.Multiple(() =>
        {
            Assert.That(covered, Is.EqualTo(slotCount));
            for (var slot = 0; slot < slotCount; slot++)
            {
                Assert.That(codes[slot], Is.EqualTo((uint)(slot + 1)), $"slot {slot} is inside the column and is read");
            }

            for (var slot = slotCount; slot < codes.Length; slot++)
            {
                Assert.That(codes[slot], Is.Zero, $"slot {slot} is past the column and must be dropped, not read as 0xFFFFFFFF");
            }
        });
    }

    /// <summary>The ratio walk masks the same way: its two reads are two chances to leave the column, not one.</summary>
    [Test]
    public void TheRatioWalkMasksItsSlotsToo()
    {
        const int stride = 8;
        const int slotCount = 4;

        var backing = new byte[256];
        for (var i = 0; i < backing.Length; i++)
        {
            backing[i] = 0xFF;
        }

        for (var slot = 0; slot < slotCount; slot++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(backing.AsSpan(slot * stride), slot + 1);
            BinaryPrimitives.WriteInt32LittleEndian(backing.AsSpan((slot * stride) + 4), 4);
        }

        var field = new CompiledField
        {
            Name = "ratio",
            ComponentSize = stride,
            FieldOffsetInComponent = 0,
            RatioOffsetInComponent = 4,
            SourceType = ProjectionSourceType.Int32,
            CodecKind = CodecKind.Unorm,
            CodecBits = 8,
        };

        var codes = new uint[64];
        var column = new ProjectionColumn(backing.AsSpan(0, slotCount * stride), stride, 0);
        ProjectionColumnWalk.Quantize(field, column, ulong.MaxValue, codes);

        Assert.Multiple(() =>
        {
            Assert.That(codes[3], Is.EqualTo(WireMath.EncodeUnorm(1d, 8)), "the last slot in the column is a full bar");
            for (var slot = slotCount; slot < codes.Length; slot++)
            {
                Assert.That(codes[slot], Is.Zero, $"slot {slot} is past the column and must be dropped");
            }
        });
    }

    /// <summary>
    /// The offsets the plan carries are the engine's own, taken from the archetype's cluster layout rather than computed by replication.
    /// </summary>
    [Test]
    public void TheOffsetsAreTheClusterLayoutsOwn()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));
        var meta = ArchetypeRegistry.GetMetadata(plan.ArchetypeCatalogId);
        var layout = meta.ClusterLayout;

        foreach (var field in plan.Fields)
        {
            Assert.That(field.ComponentOffsetInCluster, Is.EqualTo(layout.ComponentOffset(field.ComponentSlot)));
            Assert.That(field.ComponentSize, Is.EqualTo(layout.ComponentSize(field.ComponentSlot)));
            Assert.That(field.FieldOffsetInComponent, Is.LessThan(field.ComponentSize), "a field sits inside its component");
        }

        Assert.That(plan.Position.ComponentOffsetInCluster, Is.EqualTo(layout.ComponentOffset(plan.Position.ComponentSlot)));
    }

    /// <summary>
    /// The plan names the cluster layout and nothing else of the engine's storage: no component segment, no entity-location structure, no archetype state.
    /// </summary>
    /// <remarks>
    /// A scope-symbol assertion rather than a comment, because the rule's failure mode is a later slice quietly adding "just one" reference to a segment or an
    /// entity map in order to reach something faster — after which replication is a second reader of the storage with its own idea of where a value lives.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-01")]
    public void ThePlanHoldsNoSegmentOrEntityLocationReference()
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "ComponentSegment", "ChunkBasedSegment", "VariableSizedBufferSegmentBase", "ComponentTable", "ArchetypeEngineState", "ArchetypeClusterState",
            "ArchetypeMetadata", "DBComponentDefinition", "Field", "EntityRef", "EntityMap", "ClusterLocation", "Transaction", "DatabaseEngine",
            "SpatialGrid", "ChangeSet",
        };

        var planTypes = new[]
        {
            typeof(CompiledProjectionPlan), typeof(CompiledField), typeof(CompiledGroup), typeof(CompiledSection), typeof(CompiledPosition),
        };

        var sawClusterLayout = false;
        foreach (var type in planTypes)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var property in type.GetProperties(all))
            {
                sawClusterLayout |= Check(property.PropertyType, $"{type.Name}.{property.Name}", forbidden);
            }

            foreach (var field in type.GetFields(all))
            {
                sawClusterLayout |= Check(field.FieldType, $"{type.Name}.{field.Name}", forbidden);
            }
        }

        Assert.That(sawClusterLayout, Is.True,
            "the plan does hold the archetype's ArchetypeClusterInfo — that is the one engine structure SUB-01 allows, and losing it would make this test "
          + "vacuous");
    }

    private static bool Check(Type type, string where, HashSet<string> forbidden)
    {
        var seen = false;
        while (type != null && (type.IsArray || type.IsByRef || type.IsPointer))
        {
            type = type.GetElementType();
        }

        if (type == null)
        {
            return false;
        }

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }

        Assert.That(forbidden.Contains(name), Is.False,
            $"{where} is a '{name}': replication reads component values only through the archetype's cluster layout (SUB-01)");

        if (type == typeof(ArchetypeClusterInfo))
        {
            seen = true;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                seen |= Check(argument, where, forbidden);
            }
        }

        return seen;
    }

    /// <summary>
    /// Fills <paramref name="expected"/> from the cluster's own read-only spans. A local function would be shorter; a <c>ReadOnlySpan&lt;T&gt;</c> cannot be
    /// captured by a lambda, and reading the values any other way would stop the comparison being against <c>GetReadOnlySpan</c>.
    /// </summary>
    private static void Expect(ulong slots, uint[] expected, ReadOnlySpan<ProjAi> ai, ReadOnlySpan<ProjVitals> vitals, string name)
    {
        Array.Clear(expected);
        var bits = slots;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            expected[slot] = name switch
            {
                "level" => ai[slot].Level,
                "mode" => (uint)ai[slot].Mode,
                "alerted" => ai[slot].Alerted,
                "template" => ai[slot].Template,
                "hp" => WireMath.EncodeUnorm((double)vitals[slot].Health / vitals[slot].MaxHealth, 8),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no expectation for this field"),
            };
        }
    }

    private static void AssertColumn(CompiledProjectionPlan plan, string name, ReadOnlySpan<byte> componentColumn, ulong slots, uint[] codes,
        uint[] expected)
    {
        var field = ProjectionTestSchema.FieldNamed(plan.Fields, name);
        Assert.That(field.Name, Is.EqualTo(name), $"the plan holds a field named '{name}'");

        Array.Clear(codes);
        var column = ProjectionColumn.Over(componentColumn, field);
        ProjectionColumnWalk.Quantize(field, column, slots, codes);

        var bits = slots;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            Assert.That(codes[slot], Is.EqualTo(expected[slot]), $"'{name}' at slot {slot} projects what the cluster's own span reports");
        }
    }
}

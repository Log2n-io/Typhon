using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Components whose CLR size exceeds the extent of their fields (#816) ───────────────────────────────────────────────────────────────────────────────────
//
// The schema derives a component's field extent from `lastField.Offset + lastField.Size`, which cannot see the padding the compiler appends to keep a struct's
// alignment inside an array. A cluster column strided by the extent instead of by `sizeof(T)` mis-addresses every slot after the first — `Span<T>[i]` lands at
// `i * sizeof(T)` while the slot lives at `i * extent` — so reads alias the wrong slot and run off the end of the column, and writes stamp the neighbour.

// The padding below is the point of the fixture, so TYPHON010 — which exists to talk everyone else out of it — is suppressed for these two declarations only.
#pragma warning disable TYPHON010

/// <summary>Natural tail padding: an 8-byte field followed by a 4-byte one. Field extent 12, CLR size 16 (alignment 8). No attribute involved.</summary>
[Component("Typhon.Test.Pad.Tail", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PadTail
{
    public long Owner;
    public int Weight;
}

/// <summary>Explicit oversize, the shape #816 was found on (SpaceBattle's <c>Vitals</c>): field extent 4, CLR size 8.</summary>
[Component("Typhon.Test.Pad.Explicit", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Size = 8)]
struct PadExplicit
{
    public int Hp;
}

#pragma warning restore TYPHON010

/// <summary>
/// The opt-out: <c>Size</c> pinned to the field extent shrinks the CLR size onto it, so no padding is stored. Extent 12, CLR size 12. <c>Pack = 4</c> is the
/// preferred form (it follows the fields); <c>Size</c> is kept here because it is the variant a component with data already on disk must use — it moves no
/// interior offset — and this fixture is where that form has to keep working.
/// </summary>
[Component("Typhon.Test.Pad.Tight", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Size = 12)]
struct PadTight
{
    public long Owner;
    public int Weight;
}

/// <summary>Unpadded neighbour. Its column sits next to the padded ones, so a mis-strided write into theirs lands in this one's bytes.</summary>
[Component("Typhon.Test.Pad.Guard", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PadGuard
{
    public int Sentinel;
}

[Archetype]
partial class PadUnit : Archetype<PadUnit>
{
    public static readonly Comp<PadTail> Tail = Register<PadTail>();
    public static readonly Comp<PadExplicit> Vitals = Register<PadExplicit>();
    public static readonly Comp<PadTight> Tight = Register<PadTight>();
    public static readonly Comp<PadGuard> Guard = Register<PadGuard>();
}

/// <summary>
/// #816: a component's storage stride must be the stride its accessors assume. Covers both faces — the schema-side size, and the cluster SoA round-trip that
/// silently corrupted itself when the two disagreed.
/// </summary>
[TestFixture]
[NonParallelizable]
class ClusterPaddedComponentTests : TestBase<ClusterPaddedComponentTests>
{
    private const int EntityCount = 40;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PadTail>();
        dbe.RegisterComponentFromAccessor<PadExplicit>();
        dbe.RegisterComponentFromAccessor<PadTight>();
        dbe.RegisterComponentFromAccessor<PadGuard>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Schema side — the stride the layout is built from
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("SCHEMA-06")]
    public void ComponentStorageSize_IsTheClrSize_NotTheFieldExtent()
    {
        var defs = new DatabaseDefinitions();

        Assert.Multiple(() =>
        {
            // Field extent 12, but an array of PadTail steps by 16 — so the column must too.
            Assert.That(defs.CreateFromAccessor(typeof(PadTail), null).ComponentStorageSize,
                Is.EqualTo(Unsafe.SizeOf<PadTail>()).And.EqualTo(16), "PadTail: natural tail padding");

            // The #816 shape: one 4-byte field inside an 8-byte struct.
            Assert.That(defs.CreateFromAccessor(typeof(PadExplicit), null).ComponentStorageSize,
                Is.EqualTo(Unsafe.SizeOf<PadExplicit>()).And.EqualTo(8), "PadExplicit: explicit Size");

            // Opt-out: extent and CLR size coincide, so nothing is padded and nothing is wasted.
            Assert.That(defs.CreateFromAccessor(typeof(PadTight), null).ComponentStorageSize,
                Is.EqualTo(Unsafe.SizeOf<PadTight>()).And.EqualTo(12), "PadTight: Size pinned to the extent");

            // An already-tight component is unaffected by the change.
            Assert.That(defs.CreateFromAccessor(typeof(PadGuard), null).ComponentStorageSize,
                Is.EqualTo(Unsafe.SizeOf<PadGuard>()).And.EqualTo(4), "PadGuard: no padding either way");
        });
    }

    [Test]
    public void ReflectedAndGeneratedPaths_ProduceTheSameFieldOffsets()
    {
        // Sizes cannot disagree between the two paths — both take them from the CLR type in DBComponentDefinition.Build, so asserting on them proves nothing.
        // OFFSETS can: the generated spec measures them against a stack probe (managed layout) while reflection reads Marshal.OffsetOf (marshalled layout),
        // and those two part company for bool and char. This is the assertion that would catch the generator regressing to Marshal.OffsetOf.
        AssertOffsetsAgree<PadTail>();
        AssertOffsetsAgree<PadTight>();
        AssertOffsetsAgree<PadGuard>();
    }

    /// <summary>Builds one component through both schema paths — the generic overload dispatches to the source-generated spec, the Type overload reflects —
    /// and requires every field to land at the same offset.</summary>
    private static void AssertOffsetsAgree<T>() where T : unmanaged
    {
        var reflected = new DatabaseDefinitions().CreateFromAccessor(typeof(T), null);
        var generated = new DatabaseDefinitions().CreateFromAccessor<T>(null);

        Assert.That(generated.FieldsByName, Has.Count.EqualTo(reflected.FieldsByName.Count), $"{typeof(T).Name}: field count");
        foreach (var field in reflected.FieldsByName.Values)
        {
            Assert.That(generated.FieldsByName[field.Name].OffsetInComponentStorage,
                Is.EqualTo(field.OffsetInComponentStorage), $"{typeof(T).Name}.{field.Name} offset");
        }
    }

    [Test]
    public void ClusterLayout_ColumnStride_MatchesSizeofForEveryComponent()
    {
        using var dbe = SetupEngine();
        var meta = ArchetypeRegistry.GetMetadata<PadUnit>();
        var layout = dbe._archetypeStates[meta.ArchetypeId].ClusterState.Layout;

        Assert.Multiple(() =>
        {
            Assert.That(layout.ComponentSize(meta.GetSlot(PadUnit.Tail._componentTypeId)), Is.EqualTo(Unsafe.SizeOf<PadTail>()), "Tail column");
            Assert.That(layout.ComponentSize(meta.GetSlot(PadUnit.Vitals._componentTypeId)), Is.EqualTo(Unsafe.SizeOf<PadExplicit>()), "Vitals column");
            Assert.That(layout.ComponentSize(meta.GetSlot(PadUnit.Tight._componentTypeId)), Is.EqualTo(Unsafe.SizeOf<PadTight>()), "Tight column");
            Assert.That(layout.ComponentSize(meta.GetSlot(PadUnit.Guard._componentTypeId)), Is.EqualTo(Unsafe.SizeOf<PadGuard>()), "Guard column");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cluster side — the corruption #816 actually reported
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("SCHEMA-06")]
    public void ClusterSpan_PaddedComponents_EverySlotReadsBackWhatWasSpawned()
    {
        using var dbe = SetupEngine();
        var expected = SpawnUnits(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var seen = 0;
        foreach (var cluster in tx.GetClusterEnumerator<PadUnit>())
        {
            var tails = cluster.GetReadOnlySpan(PadUnit.Tail);
            var vitals = cluster.GetReadOnlySpan(PadUnit.Vitals);
            var tights = cluster.GetReadOnlySpan(PadUnit.Tight);
            var guards = cluster.GetReadOnlySpan(PadUnit.Guard);

            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var i = expected[cluster.GetEntityId(slotIndex)];
                seen++;

                // Sequential rather than Assert.Multiple: a span cannot be captured by the lambda (CS8175).
                AssertTailMatches(tails[slotIndex], i, slotIndex);
                Assert.That(vitals[slotIndex].Hp, Is.EqualTo(HpOf(i)), $"Vitals.Hp at slot {slotIndex}");
                Assert.That(tights[slotIndex].Owner, Is.EqualTo(OwnerOf(i)), $"Tight.Owner at slot {slotIndex}");
                Assert.That(tights[slotIndex].Weight, Is.EqualTo(WeightOf(i)), $"Tight.Weight at slot {slotIndex}");
                Assert.That(guards[slotIndex].Sentinel, Is.EqualTo(SentinelOf(i)), $"Guard.Sentinel at slot {slotIndex}");
            }
        }

        Assert.That(seen, Is.EqualTo(EntityCount), "every spawned entity was visited exactly once");
    }

    [Test]
    [VerifiesRule("SCHEMA-06")]
    public void ClusterSpan_WritingAPaddedColumn_LeavesTheNeighbouringColumnsIntact()
    {
        using var dbe = SetupEngine();
        var expected = SpawnUnits(dbe);

        // Rewrite every slot of the two padded columns through the span API — a whole-struct store, which is what spills at a wrong stride.
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.GetClusterEnumerator<PadUnit>())
            {
                var tails = cluster.GetSpan(PadUnit.Tail);
                var vitals = cluster.GetSpan(PadUnit.Vitals);
                for (var slotIndex = 0; slotIndex < cluster.ClusterSize; slotIndex++)
                {
                    tails[slotIndex] = new PadTail { Owner = -1, Weight = -2 };
                    vitals[slotIndex] = new PadExplicit { Hp = -3 };
                }
                cluster.MarkDirty(PadUnit.Tail);
                cluster.MarkDirty(PadUnit.Vitals);
            }
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        foreach (var cluster in read.GetClusterEnumerator<PadUnit>())
        {
            var tights = cluster.GetReadOnlySpan(PadUnit.Tight);
            var guards = cluster.GetReadOnlySpan(PadUnit.Guard);

            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var i = expected[cluster.GetEntityId(slotIndex)];

                Assert.That(tights[slotIndex].Owner, Is.EqualTo(OwnerOf(i)), $"Tight.Owner at slot {slotIndex} survived the Tail/Vitals writes");
                Assert.That(tights[slotIndex].Weight, Is.EqualTo(WeightOf(i)), $"Tight.Weight at slot {slotIndex} survived the Tail/Vitals writes");
                Assert.That(guards[slotIndex].Sentinel, Is.EqualTo(SentinelOf(i)), $"Guard.Sentinel at slot {slotIndex} survived the Tail/Vitals writes");
            }
        }
    }

    [Test]
    public void EntityRef_PaddedComponent_WholeStructWriteDoesNotDisturbTheNextSlot()
    {
        // The per-entity path addresses with the layout stride but hands back a `ref T` that is sizeof(T) wide, so a whole-struct assignment writes past a
        // too-short slot into the next one. Same invariant, other accessor.
        using var dbe = SetupEngine();
        var expected = SpawnUnits(dbe);
        var ids = new EntityId[EntityCount];
        foreach (var kvp in expected)
        {
            ids[kvp.Value] = kvp.Key;
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i += 2)
            {
                var e = tx.OpenMut(ids[i]);
                e.Write(PadUnit.Tail) = new PadTail { Owner = -1, Weight = -2 };
            }
            tx.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        for (var i = 1; i < EntityCount; i += 2)
        {
            var e = read.Open(ids[i]);
            ref readonly var tail = ref e.Read(PadUnit.Tail);
            ref readonly var guard = ref e.Read(PadUnit.Guard);

            Assert.That(tail.Owner, Is.EqualTo(OwnerOf(i)), $"untouched entity {i} kept its Owner");
            Assert.That(tail.Weight, Is.EqualTo(WeightOf(i)), $"untouched entity {i} kept its Weight");
            Assert.That(guard.Sentinel, Is.EqualTo(SentinelOf(i)), $"untouched entity {i} kept its Sentinel");
        }
    }

    [Test]
    [RuleMutant("SCHEMA-06")]
    public void Mutant_ColumnAddressedAtTheFieldExtent_IsRejectedByTheRoundTripAssertion()
    {
        // The genuineness proof for SCHEMA-06. Post-fix the engine cannot produce a mis-strided column, so the violating input is constructed: take the REAL
        // column bytes and re-address them at the field extent (12) instead of sizeof(PadTail) (16), then run the verifier's own per-slot assertion over the
        // result. It has to fail, or the verifier proves nothing.
        //
        // Note the direction: #816 read a 12-byte-strided column with a 16-byte step, this reads a 16-byte-strided column with a 12-byte step. Mirror images
        // of the same defect — the stride the storage uses and the stride the accessor uses disagree — and either way slot 0 aliases correctly and everything
        // after it does not, which is what makes the assertion fail from slot 1 onward rather than everywhere.
        using var dbe = SetupEngine();
        var expected = SpawnUnits(dbe);

        RuleMutants.AssertDetects("SCHEMA-06", TailMismatchMarker, () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            foreach (var cluster in tx.GetClusterEnumerator<PadUnit>())
            {
                var columnBytes = MemoryMarshal.AsBytes(cluster.GetReadOnlySpan(PadUnit.Tail));
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var misStrided = MemoryMarshal.Read<PadTail>(columnBytes[(slotIndex * PadTailFieldExtent)..]);
                    AssertTailMatches(misStrided, expected[cluster.GetEntityId(slotIndex)], slotIndex);
                }
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Where <c>PadTail</c>'s fields end — the stride a pre-#816 column was laid out at, and what the mutant re-reads the column with.</summary>
    private const int PadTailFieldExtent = 12;

    /// <summary>
    /// Distinctive substring of the round-trip assertion's own failure message. <see cref="RuleMutants.AssertDetects"/> requires the mutant's rejection to
    /// carry it, so that what proves the verifier can fail is the verifier's assertion and not some incidental exception on the way there.
    /// </summary>
    private const string TailMismatchMarker = "PadTail at slot";

    /// <summary>The per-slot check shared by the verifier and its mutant — the single assertion path both drive, one with a correct value and one with a
    /// value read at the wrong stride.</summary>
    private static void AssertTailMatches(in PadTail tail, int i, int slotIndex)
    {
        Assert.That(tail.Owner, Is.EqualTo(OwnerOf(i)), $"{TailMismatchMarker} {slotIndex}: Owner");
        Assert.That(tail.Weight, Is.EqualTo(WeightOf(i)), $"{TailMismatchMarker} {slotIndex}: Weight");
    }

    private static long OwnerOf(int i) => 1_000_000L + i;
    private static int WeightOf(int i) => 100 + i;
    private static int HpOf(int i) => 200 + i;
    private static int SentinelOf(int i) => 0x5EED_0000 + i;

    /// <summary>Spawns <see cref="EntityCount"/> units with per-entity distinct values in every column; returns entity id → ordinal.</summary>
    private static Dictionary<EntityId, int> SpawnUnits(DatabaseEngine dbe)
    {
        var map = new Dictionary<EntityId, int>(EntityCount);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var tail = new PadTail { Owner = OwnerOf(i), Weight = WeightOf(i) };
            var vitals = new PadExplicit { Hp = HpOf(i) };
            var tight = new PadTight { Owner = OwnerOf(i), Weight = WeightOf(i) };
            var guard = new PadGuard { Sentinel = SentinelOf(i) };
            var id = tx.Spawn<PadUnit>(
                PadUnit.Tail.Set(in tail),
                PadUnit.Vitals.Set(in vitals),
                PadUnit.Tight.Set(in tight),
                PadUnit.Guard.Set(in guard));
            map.Add(id, i);
        }
        tx.Commit();
        return map;
    }
}

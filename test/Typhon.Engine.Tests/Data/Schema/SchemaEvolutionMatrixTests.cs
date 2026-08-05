using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Indexed SingleVersion pair — gives the oracle something to check

[Component("Typhon.Schema.UnitTest.EvoMxIdx", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMxIdxV1
{
    [Index]
    public int Key;

    public float B;
    public EvoMxIdxV1(int key, float b) { Key = key; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoMxIdx", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMxIdxV2
{
    [Index]
    public int Key;

    public float B;
    public long C;
    public EvoMxIdxV2(int key, float b, long c) { Key = key; B = b; C = c; }
}

[Archetype]
class EvoMxIdxArch : Archetype<EvoMxIdxArch>
{
    public static readonly Comp<EvoMxIdxV1> Comp = Register<EvoMxIdxV1>();
}

[Archetype]
class EvoMxIdxV2Arch : Archetype<EvoMxIdxV2Arch>
{
    public static readonly Comp<EvoMxIdxV2> Comp = Register<EvoMxIdxV2>();
}

#endregion

/// <summary>
/// Schema evolution across the option matrix, driven by <see cref="EngineAxes"/> rather than by hand-picked combinations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why 200 entities and not one.</b> The four hand-written tests in <c>SchemaEvolutionStorageModeTests</c> each migrate a SINGLE entity, which makes the
/// placement half of the re-cluster a tautology: with one entity the only correct destination slot is slot 0, so code that pins the destination to 0 passes.
/// That is not hypothetical — it was mutation-proven on this branch: pinning <c>oldPos.SlotIndex → 0</c> in <c>CopyPreMigrationSlot</c> left the whole suite
/// green. 200 entities span more than one cluster at any <c>ClusterSize</c> in [8,64], so <c>ClaimSlot</c> and the EntityMap-rewrite loop are genuinely
/// exercised and every entity's payload is distinctive enough that a slot mix-up shows up as the wrong value rather than as a plausible one.
/// </para>
/// <para>
/// Same lesson <c>ClusterChainRootResolutionTests</c> already learned for chain roots: sharing a cluster is the PRECONDITION for the bug, so the fixture has to
/// guarantee it rather than hope for it.
/// </para>
/// <para>
/// <b>Why a covering array.</b> Storage shape × durability × index kind × reopen kind is multiplicative; example-based fixtures are additive. Pairwise
/// covering keeps the case count near the number of axis VALUES rather than their product, on the empirical result that most defects are triggered by one
/// parameter or an interaction of two. See <see cref="EngineAxes"/>.
/// </para>
/// <para>
/// <b>Axes deliberately not covered here, stated rather than silently skipped.</b> The shape axis is restricted to the three compositions this fixture has
/// schema pairs for (<c>PureSv</c>, <c>PureSv</c>+unique index, <c>SvPlusVersioned</c>); pure-Transient has no persisted bytes to migrate, and the remaining
/// compositions need component pairs nobody has written yet. <see cref="ReopenKind.None"/> is excluded because a migration IS a reopen. Widening the shape
/// axis is C.3's job, and it needs new component pairs, not new test logic.
/// </para>
/// </remarks>
class SchemaEvolutionMatrixTests : TestBase<SchemaEvolutionMatrixTests>
{
    private const int EntityCount = 200;

    /// <summary>The (shape, index) pairs this fixture can actually build a workload for.</summary>
    private static bool Supported(Cell c) =>
        c.Reopen is ReopenKind.Clean or ReopenKind.CleanThenCrash
        && ((c.Shape == StorageShape.PureSv && c.Index is IndexShape.None or IndexShape.Unique)
            || (c.Shape == StorageShape.SvPlusVersioned && c.Index == IndexShape.None));

    public static IEnumerable<TestCaseData> Cases() => EngineAxes.PairwiseWhere(Supported);

    [TestCaseSource(nameof(Cases))]
    public void Migration_PreservesEveryEntity(Cell cell)
    {
        var ids = new EntityId[EntityCount];

        // ── Session 1: seed under V1, clean close so the EntityMap / cluster SPIs become durable ──────────────────────────────────────────────────────────
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterV1(dbe, cell);
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(cell.Durability);
            for (var i = 0; i < EntityCount; i++)
            {
                ids[i] = SpawnV1(t, cell, i);
            }

            t.Commit();
        }

        // ── Optional extra crash before the migration, so the migrating open also has a crash-recovery history behind it ───────────────────────────────────
        if (cell.Reopen == ReopenKind.CleanThenCrash)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterV1(dbe, cell);
            dbe.InitializeArchetypes();
            dbe.SimulateHardCrash();
        }

        // ── Session N: declare V2, migrate, and read every entity back ────────────────────────────────────────────────────────────────────────────────────
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterV2(dbe, cell);
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                for (var i = 0; i < EntityCount; i++)
                {
                    AssertV2(t, cell, ids[i], i);
                }
            });

            if (cell.Index != IndexShape.None)
            {
                IndexDataOracle.AssertIndexAgreesWithData<EvoMxIdxV2Arch>(dbe, $"after migrating {EntityCount} entities ({cell})");
            }
        }
    }

    // ── Per-shape workload dispatch ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Payloads are per-entity distinctive on EVERY field: a slot mix-up during the re-cluster then surfaces as another entity's value rather than as something
    // that happens to look right. `i * 7 + 1` and `i * 1.5f` differ for every i and differ from each other, so a field-offset error is visible too.

    private static void RegisterV1(DatabaseEngine dbe, Cell cell)
    {
        switch (cell.Shape, cell.Index)
        {
            case (StorageShape.PureSv, IndexShape.Unique):
                dbe.RegisterComponentFromAccessor<EvoMxIdxV1>();
                break;
            case (StorageShape.SvPlusVersioned, _):
                dbe.RegisterComponentFromAccessor<EvoMixSvV1>();
                dbe.RegisterComponentFromAccessor<EvoMixVer>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<EvoSmAddV1>();
                break;
        }
    }

    private static void RegisterV2(DatabaseEngine dbe, Cell cell)
    {
        switch (cell.Shape, cell.Index)
        {
            case (StorageShape.PureSv, IndexShape.Unique):
                dbe.RegisterComponentFromAccessor<EvoMxIdxV2>();
                break;
            case (StorageShape.SvPlusVersioned, _):
                dbe.RegisterComponentFromAccessor<EvoMixSvV2>();
                dbe.RegisterComponentFromAccessor<EvoMixVer>();
                break;
            default:
                dbe.RegisterComponentFromAccessor<EvoSmAddV2>();
                break;
        }
    }

    private static EntityId SpawnV1(Transaction t, Cell cell, int i) =>
        (cell.Shape, cell.Index) switch
        {
            (StorageShape.PureSv, IndexShape.Unique) => t.Spawn<EvoMxIdxArch>(EvoMxIdxArch.Comp.Set(new EvoMxIdxV1(i * 7 + 1, i * 1.5f))),
            (StorageShape.SvPlusVersioned, _) => t.Spawn<EvoMixArch>(
                EvoMixArch.Sv.Set(new EvoMixSvV1(i * 7 + 1, i * 1.5f)),
                EvoMixArch.Ver.Set(new EvoMixVer(i * 1000L + 3))),
            _ => t.Spawn<EvoSmAddArch>(EvoSmAddArch.Comp.Set(new EvoSmAddV1(i * 7 + 1, i * 1.5f))),
        };

    private static void AssertV2(Transaction t, Cell cell, EntityId id, int i)
    {
        switch (cell.Shape, cell.Index)
        {
            case (StorageShape.PureSv, IndexShape.Unique):
            {
                var got = t.Open(id).Read(EvoMxIdxV2Arch.Comp);
                Assert.That(got.Key, Is.EqualTo(i * 7 + 1), $"entity {i}: indexed SV key must survive the re-cluster");
                Assert.That(got.B, Is.EqualTo(i * 1.5f).Within(0.0001f), $"entity {i}: SV float must survive");
                Assert.That(got.C, Is.EqualTo(0L), $"entity {i}: added field zero-fills");
                break;
            }

            case (StorageShape.SvPlusVersioned, _):
            {
                var e = t.Open(id);
                var sv = e.Read(EvoMixV2Arch.Sv);
                var ver = e.Read(EvoMixV2Arch.Ver);
                Assert.That(sv.A, Is.EqualTo(i * 7 + 1), $"entity {i}: SV survives via the cluster-to-cluster copy");
                Assert.That(sv.B, Is.EqualTo(i * 1.5f).Within(0.0001f), $"entity {i}: SV float survives");
                Assert.That(sv.C, Is.EqualTo(0L), $"entity {i}: added SV field zero-fills");
                Assert.That(ver.V, Is.EqualTo(i * 1000L + 3), $"entity {i}: the untouched Versioned neighbour is refilled from its chain at the NEW slot");
                break;
            }

            default:
            {
                var got = t.Open(id).Read(EvoSmAddV2Arch.Comp);
                Assert.That(got.A, Is.EqualTo(i * 7 + 1), $"entity {i}: surviving int must carry across the re-cluster");
                Assert.That(got.B, Is.EqualTo(i * 1.5f).Within(0.0001f), $"entity {i}: surviving float must carry across");
                Assert.That(got.C, Is.EqualTo(0L), $"entity {i}: added field zero-fills");
                break;
            }
        }
    }
}

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The storage-mode axis of schema evolution (#671). SchemaEvolutionTests has ZERO occurrences of StorageMode — every one of its tests runs a Versioned-by-
// default component, which is why a subsystem that could not migrate a SingleVersion cluster looked healthy for months. Versioned itself is covered there
// (and is cluster-backed now that every archetype is), so what these add is the mode that has NO second copy of its data: SingleVersion lives only in the
// cluster slot, and a migration changes component sizes, which changes ClusterSize, which moves every offset in the cluster.
//
// Field sizes here are deliberately NON-UNIFORM and the payloads deliberately distinctive. Uniform sizes let a mis-addressed read land inside the right
// slot anyway and report success; a mix of int/float/long/short with recognisable values makes an off-by-one field or slot visible in the assertion.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Add a field

[Component("Typhon.Schema.UnitTest.EvoSmAdd", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmAddV1
{
    public int A;
    public float B;
    public EvoSmAddV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoSmAdd", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmAddV2
{
    public int A;
    public float B;
    public long C;
    public EvoSmAddV2(int a, float b, long c) { A = a; B = b; C = c; }
}

[Archetype]
class EvoSmAddArch : Archetype<EvoSmAddArch>
{
    public static readonly Comp<EvoSmAddV1> Comp = Register<EvoSmAddV1>();
}

[Archetype]
class EvoSmAddV2Arch : Archetype<EvoSmAddV2Arch>
{
    public static readonly Comp<EvoSmAddV2> Comp = Register<EvoSmAddV2>();
}

#endregion

#region Widen a field

[Component("Typhon.Schema.UnitTest.EvoSmWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmWidenV1
{
    public short A;
    public int B;
    public EvoSmWidenV1(short a, int b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoSmWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct EvoSmWidenV2
{
    public long A;
    public int B;
    public EvoSmWidenV2(long a, int b) { A = a; B = b; }
}

[Archetype]
class EvoSmWidenArch : Archetype<EvoSmWidenArch>
{
    public static readonly Comp<EvoSmWidenV1> Comp = Register<EvoSmWidenV1>();
}

[Archetype]
class EvoSmWidenV2Arch : Archetype<EvoSmWidenV2Arch>
{
    public static readonly Comp<EvoSmWidenV2> Comp = Register<EvoSmWidenV2>();
}

#endregion

#region Remove a field

[Component("Typhon.Schema.UnitTest.EvoSmDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct EvoSmDropV1
{
    public int A;
    public long Doomed;
    public float C;
    public EvoSmDropV1(int a, long doomed, float c) { A = a; Doomed = doomed; C = c; }
}

[Component("Typhon.Schema.UnitTest.EvoSmDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmDropV2
{
    public int A;
    public float C;
    public EvoSmDropV2(int a, float c) { A = a; C = c; }
}

[Archetype]
class EvoSmDropArch : Archetype<EvoSmDropArch>
{
    public static readonly Comp<EvoSmDropV1> Comp = Register<EvoSmDropV1>();
}

[Archetype]
class EvoSmDropV2Arch : Archetype<EvoSmDropV2Arch>
{
    public static readonly Comp<EvoSmDropV2> Comp = Register<EvoSmDropV2>();
}

#endregion

#region Mixed archetype: SingleVersion alongside Versioned

[Component("Typhon.Schema.UnitTest.EvoMixSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMixSvV1
{
    public int A;
    public float B;
    public EvoMixSvV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoMixSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMixSvV2
{
    public int A;
    public float B;
    public long C;
    public EvoMixSvV2(int a, float b, long c) { A = a; B = b; C = c; }
}

// Versioned companion, unchanged across the migration. Its cluster HEAD is rebuilt from its revision chain, so it must survive a migration driven entirely
// by the SV component next to it — a different recovery route in the same rebuild pass.
[Component("Typhon.Schema.UnitTest.EvoMixVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMixVer
{
    public long V;
    public EvoMixVer(long v) { V = v; }
}

[Archetype]
class EvoMixArch : Archetype<EvoMixArch>
{
    public static readonly Comp<EvoMixSvV1> Sv = Register<EvoMixSvV1>();
    public static readonly Comp<EvoMixVer> Ver = Register<EvoMixVer>();
}

[Archetype]
class EvoMixV2Arch : Archetype<EvoMixV2Arch>
{
    public static readonly Comp<EvoMixSvV2> Sv = Register<EvoMixSvV2>();
    public static readonly Comp<EvoMixVer> Ver = Register<EvoMixVer>();
}

#endregion

#region Versioned enabled state across a migration (#846)

// Own component pair rather than EvoMix's: ArchetypeMetadata is process-global, so a fixture sharing another's archetype inherits its schema version (#720).
[Component("Typhon.Schema.UnitTest.Evo846Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct Evo846SvV1
{
    public int A;
    public Evo846SvV1(int a) { A = a; }
}

[Component("Typhon.Schema.UnitTest.Evo846Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct Evo846SvV2
{
    public int A;
    public long B;
    public Evo846SvV2(int a, long b) { A = a; B = b; }
}

// The Versioned component whose enabled state is under test. It does not change; the SV neighbour's migration is what routes the open through
// RebuildClusterFromChains.
[Component("Typhon.Schema.UnitTest.Evo846Ver", 1)]
[StructLayout(LayoutKind.Sequential)]
struct Evo846Ver
{
    public long V;
    public Evo846Ver(long v) { V = v; }
}

[Archetype]
class Evo846Arch : Archetype<Evo846Arch>
{
    public static readonly Comp<Evo846SvV1> Sv = Register<Evo846SvV1>();
    public static readonly Comp<Evo846Ver> Ver = Register<Evo846Ver>();
}

[Archetype]
class Evo846V2Arch : Archetype<Evo846V2Arch>
{
    public static readonly Comp<Evo846SvV2> Sv = Register<Evo846SvV2>();
    public static readonly Comp<Evo846Ver> Ver = Register<Evo846Ver>();
}

// Pure-Versioned twin: no SingleVersion slot, so the migration keeps no pre-migration cluster, and the component under test is the one that migrates.
[Component("Typhon.Schema.UnitTest.Evo846PvKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct Evo846PvKey
{
    public long K;
    public Evo846PvKey(long k) { K = k; }
}

[Component("Typhon.Schema.UnitTest.Evo846PvVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct Evo846PvVerV1
{
    public long V;
    public Evo846PvVerV1(long v) { V = v; }
}

[Component("Typhon.Schema.UnitTest.Evo846PvVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct Evo846PvVerV2
{
    public long V;
    public long W;
    public Evo846PvVerV2(long v, long w) { V = v; W = w; }
}

[Archetype]
class Evo846PvArch : Archetype<Evo846PvArch>
{
    public static readonly Comp<Evo846PvKey> Key = Register<Evo846PvKey>();
    public static readonly Comp<Evo846PvVerV1> Ver = Register<Evo846PvVerV1>();
}

[Archetype]
class Evo846PvV2Arch : Archetype<Evo846PvV2Arch>
{
    public static readonly Comp<Evo846PvKey> Key = Register<Evo846PvKey>();
    public static readonly Comp<Evo846PvVerV2> Ver = Register<Evo846PvVerV2>();
}

#endregion

/// <summary>
/// Schema evolution across the storage-mode axis (#671). Each test seeds under the V1 schema, closes cleanly, then reopens declaring V2 and asserts the data
/// survived the re-cluster — the migration changes component sizes, so every entity lands at a different <c>(clusterChunkId, slotIndex)</c>.
/// </summary>
class SchemaEvolutionStorageModeTests : TestBase<SchemaEvolutionStorageModeTests>
{
    /// <summary>
    /// A migrating reopen keeps every Versioned component in the state the caller left it: present, disabled, or absent (#846).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RebuildClusterFromChains</c> re-places every entity on a migrating open, and it used to set a Versioned slot's enabled bit whenever the entity had a
    /// chain head. A head proves the component is present, not that it is enabled — <c>Disable</c> keeps the payload, so a disabled component has a head
    /// too, and every migration silently re-enabled it. STAGE-02 forbids exactly that derivation.
    /// </para>
    /// <para>
    /// Seventy entities, so the re-cluster fills more than one cluster at any size and the old-cluster bit has to be read at each entity's own position; the
    /// three states rotate so a position mix-up lands an entity on a neighbour's state. The SV field added by the migration zero-fills, which proves the
    /// open took the migrating path — the one that runs the rebuild.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("STAGE-02")]
    public void Migration_KeepsAVersionedComponentsEnabledState_PresentDisabledAndAbsent()
    {
        const int count = 70;
        var ids = new EntityId[count];

        // State by i % 3: 0 = supplied then disabled, 1 = supplied and enabled, 2 = never supplied.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<Evo846SvV1>();
            dbe.RegisterComponentFromAccessor<Evo846Ver>();
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < count; i++)
                {
                    ids[i] = i % 3 == 2
                        ? t.Spawn<Evo846Arch>(Evo846Arch.Sv.Set(new Evo846SvV1(i)))
                        : t.Spawn<Evo846Arch>(Evo846Arch.Sv.Set(new Evo846SvV1(i)), Evo846Arch.Ver.Set(new Evo846Ver(i * 1000L + 7)));
                }

                t.Commit();
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < count; i += 3)
                {
                    t.OpenMut(ids[i]).Disable(Evo846Arch.Ver);
                }

                t.Commit();
            }
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<Evo846SvV2>();
            dbe.RegisterComponentFromAccessor<Evo846Ver>();
            dbe.InitializeArchetypes();

            // Resolved through the entities' routing id, not Archetype<Evo846V2Arch>: the migrated entities live in the state the persisted archetype maps to.
            var meta = dbe.GetMetaByRouting(ids[0].ArchetypeId);
            Assert.That(meta, Is.Not.Null, "premise: the migrated entities' routing id resolves to an archetype");
            var verSlot = meta.GetSlot(Evo846V2Arch.Ver._componentTypeId);

            using (var t = dbe.CreateQuickTransaction())
            {
                Assert.Multiple(() =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        var entity = t.Open(ids[i]);
                        var sv = entity.Read(Evo846V2Arch.Sv);
                        Assert.That(sv.A, Is.EqualTo(i), $"entity {i}: premise — the SV value must survive the re-cluster");
                        Assert.That(sv.B, Is.EqualTo(0L), $"entity {i}: premise — the field the migration added zero-fills, so the open migrated");

                        var enabled = i % 3 == 1;
                        Assert.That(entity.IsEnabled(Evo846V2Arch.Ver), Is.EqualTo(enabled), $"entity {i} (state {i % 3}): the record's enabled bit");
                        Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, ids[i], verSlot), Is.EqualTo(enabled),
                            $"entity {i} (state {i % 3}): the cluster copy of the enabled bit");
                        if (enabled)
                        {
                            Assert.That(entity.Read(Evo846V2Arch.Ver).V, Is.EqualTo(i * 1000L + 7), $"entity {i}: the enabled value survives");
                        }
                    }
                });
            }

            // Disabled kept its value and re-enables without one; absent has no value and refuses.
            for (var i = 0; i < count; i++)
            {
                if (i % 3 == 1)
                {
                    continue;
                }

                using var t = dbe.CreateQuickTransaction();
                var entity = t.OpenMut(ids[i]);
                if (i % 3 == 0)
                {
                    entity.Enable(Evo846V2Arch.Ver);
                    t.Commit();
                    using var read = dbe.CreateQuickTransaction();
                    Assert.That(read.Open(ids[i]).Read(Evo846V2Arch.Ver).V, Is.EqualTo(i * 1000L + 7),
                        $"entity {i}: a component disabled before the migration keeps its value across it");
                }
                else
                {
                    var refused = false;
                    try
                    {
                        entity.Enable(Evo846V2Arch.Ver);
                    }
                    catch (System.InvalidOperationException)
                    {
                        refused = true;
                    }

                    Assert.That(refused, Is.True, $"entity {i}: a component never supplied must stay absent across the migration");
                }
            }
        }
    }

    /// <summary>
    /// The same three states across a migration of the Versioned component ITSELF, in an archetype with no SingleVersion slot (#846).
    /// </summary>
    /// <remarks>
    /// A migration keeps the pre-migration cluster only when the archetype has a SingleVersion slot — its bytes have no other copy — so a fix that read the
    /// enabled bit from that cluster left every pure-Versioned archetype re-enabling its disabled components. The bit is read from the pre-migration EntityMap
    /// record instead, which exists for every archetype and whose layout does not depend on the component sizes the migration changes.
    /// </remarks>
    [Test]
    [VerifiesRule("STAGE-02")]
    public void Migration_OfTheVersionedComponentItself_KeepsItsEnabledState_WithoutASingleVersionSlot()
    {
        const int count = 70;
        var ids = new EntityId[count];

        // State by i % 3: 0 = supplied then disabled, 1 = supplied and enabled, 2 = never supplied. Key is always supplied, so every entity keeps a chain.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<Evo846PvKey>();
            dbe.RegisterComponentFromAccessor<Evo846PvVerV1>();
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < count; i++)
                {
                    ids[i] = i % 3 == 2
                        ? t.Spawn<Evo846PvArch>(Evo846PvArch.Key.Set(new Evo846PvKey(i)))
                        : t.Spawn<Evo846PvArch>(Evo846PvArch.Key.Set(new Evo846PvKey(i)), Evo846PvArch.Ver.Set(new Evo846PvVerV1(i * 1000L + 7)));
                }

                t.Commit();
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < count; i += 3)
                {
                    t.OpenMut(ids[i]).Disable(Evo846PvArch.Ver);
                }

                t.Commit();
            }
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<Evo846PvKey>();
            dbe.RegisterComponentFromAccessor<Evo846PvVerV2>();
            dbe.InitializeArchetypes();

            var meta = dbe.GetMetaByRouting(ids[0].ArchetypeId);
            Assert.That(meta, Is.Not.Null, "premise: the migrated entities' routing id resolves to an archetype");
            var verSlot = meta.GetSlot(Evo846PvV2Arch.Ver._componentTypeId);

            using (var t = dbe.CreateQuickTransaction())
            {
                Assert.Multiple(() =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        var entity = t.Open(ids[i]);
                        Assert.That(entity.Read(Evo846PvV2Arch.Key).K, Is.EqualTo(i), $"entity {i}: premise — the entity survives the re-cluster");

                        var enabled = i % 3 == 1;
                        Assert.That(entity.IsEnabled(Evo846PvV2Arch.Ver), Is.EqualTo(enabled), $"entity {i} (state {i % 3}): the record's enabled bit");
                        Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, ids[i], verSlot), Is.EqualTo(enabled),
                            $"entity {i} (state {i % 3}): the cluster copy of the enabled bit");
                        if (enabled)
                        {
                            var ver = entity.Read(Evo846PvV2Arch.Ver);
                            Assert.That(ver.V, Is.EqualTo(i * 1000L + 7), $"entity {i}: the enabled value survives its own migration");
                            Assert.That(ver.W, Is.EqualTo(0L), $"entity {i}: premise — the field the migration added zero-fills, so the open migrated");
                        }
                    }
                });
            }

            // Disabled kept its (migrated) value and re-enables without one; absent has no value and refuses.
            for (var i = 0; i < count; i++)
            {
                if (i % 3 == 1)
                {
                    continue;
                }

                using var t = dbe.CreateQuickTransaction();
                var entity = t.OpenMut(ids[i]);
                if (i % 3 == 0)
                {
                    entity.Enable(Evo846PvV2Arch.Ver);
                    t.Commit();
                    using var read = dbe.CreateQuickTransaction();
                    Assert.That(read.Open(ids[i]).Read(Evo846PvV2Arch.Ver).V, Is.EqualTo(i * 1000L + 7),
                        $"entity {i}: a component disabled before its own migration keeps its value across it");
                }
                else
                {
                    var refused = false;
                    try
                    {
                        entity.Enable(Evo846PvV2Arch.Ver);
                    }
                    catch (System.InvalidOperationException)
                    {
                        refused = true;
                    }

                    Assert.That(refused, Is.True, $"entity {i}: a component never supplied must stay absent across the migration");
                }
            }
        }
    }

    [Test]
    public void SingleVersion_AddField_PreservesDataAndZeroFillsNewField()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmAddV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoSmAddArch>(EvoSmAddArch.Comp.Set(new EvoSmAddV1(0x5EED, 2.5f)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmAddV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoSmAddV2Arch.Comp);
            Assert.Multiple(() =>
            {
                Assert.That(got.A, Is.EqualTo(0x5EED), "surviving int must carry across the re-cluster");
                Assert.That(got.B, Is.EqualTo(2.5f).Within(0.0001f), "surviving float must carry across the re-cluster");
                Assert.That(got.C, Is.EqualTo(0L), "field added by the migration must be zero-filled, not garbage from the old layout");
            });
        }
    }

    [Test]
    public void SingleVersion_WidenField_PreservesValue()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmWidenV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoSmWidenArch>(EvoSmWidenArch.Comp.Set(new EvoSmWidenV1(-1234, 77)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmWidenV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoSmWidenV2Arch.Comp);
            Assert.Multiple(() =>
            {
                // Negative on purpose: a widening that zero-extends instead of sign-extending turns -1234 into 64302, which a positive probe value would hide.
                Assert.That(got.A, Is.EqualTo(-1234L), "short -> long must SIGN-extend through the field map");
                Assert.That(got.B, Is.EqualTo(77), "the field after the widened one must not be dragged out of position");
            });
        }
    }

    [Test]
    public void SingleVersion_RemoveField_KeepsSurvivorsAtTheirNewOffsets()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmDropV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoSmDropArch>(EvoSmDropArch.Comp.Set(new EvoSmDropV1(11, long.MaxValue, 6.25f)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmDropV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoSmDropV2Arch.Comp);
            Assert.Multiple(() =>
            {
                Assert.That(got.A, Is.EqualTo(11), "field before the removed one keeps its offset");
                // C moves from offset 12 to offset 4. The 12 comes from V1's Pack = 4, which places Doomed at 4 rather than 8 (#816, TYPHON010) — under the
                // natural layout C sat at 16. Reading C at its OLD offset would pick up bytes 12-15 of a V1 record, which is where Doomed's tail sits, so
                // this is the assertion that actually proves the field map drove the copy rather than a blind memcpy.
                Assert.That(got.C, Is.EqualTo(6.25f).Within(0.0001f), "field AFTER the removed one must be re-addressed to its new offset");
            });
        }
    }

    [Test]
    public void MixedArchetype_SvMigrates_AndVersionedNeighbourSurvives()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoMixSvV1>();
            dbe.RegisterComponentFromAccessor<EvoMixVer>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoMixArch>(EvoMixArch.Sv.Set(new EvoMixSvV1(7, 1.5f)), EvoMixArch.Ver.Set(new EvoMixVer(0xABCDEF)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoMixSvV2>();
            dbe.RegisterComponentFromAccessor<EvoMixVer>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var e = t.Open(id);
            var sv = e.Read(EvoMixV2Arch.Sv);
            var ver = e.Read(EvoMixV2Arch.Ver);
            Assert.Multiple(() =>
            {
                Assert.That(sv.A, Is.EqualTo(7), "SV survives via the cluster-to-cluster copy");
                Assert.That(sv.B, Is.EqualTo(1.5f).Within(0.0001f), "SV survives via the cluster-to-cluster copy");
                Assert.That(sv.C, Is.EqualTo(0L), "added SV field zero-fills");
                // The two modes recover by DIFFERENT routes in the same pass — SV is copied from the old cluster, Versioned is refilled from its chain. This
                // asserts the SV copy did not overwrite the neighbour's slot, and that re-placing entities kept both slots pointing at the same entity.
                Assert.That(ver.V, Is.EqualTo(0xABCDEF), "the untouched Versioned neighbour must be refilled from its chain at the entity's NEW cluster slot");

                // Review §5 C.4: the canonical migration fixture also has to say the migration left the FILE consistent, not just the data readable. A
                // migration replaces the cluster, the EntityMap and (when an index is added) the index segment; each replacement used to abandon the old
                // segment's pages (M9). MigrationSegmentReclaimTests covers the shapes; this is the guard on the fixture everyone edits.
                Assert.That(dbe.RunStorageIntegrityCheck().IsHealthy, Is.True, "the migrating open must not leave pages allocated to no segment");
            });
        }
    }

    /// <summary>
    /// An engine builds its cluster state from the layout IT computed, never from what the process-wide metadata holds a moment later.
    /// </summary>
    /// <remarks>
    /// <see cref="ArchetypeMetadata"/> is one object per archetype for the whole process, and every engine's <c>InitializeArchetypes</c> writes its
    /// <c>ClusterLayout</c>. The engine used to read it back to size its cluster segment and build its state, so another engine opening another schema
    /// version of the same archetype in between — <c>SchemaEvolutionMatrixTests</c> running beside this fixture — handed it THAT version's layout: each
    /// SingleVersion value stored at the wrong offset and read back as zeros after reopen (<c>SV 0 != 7</c> in
    /// <see cref="MixedArchetype_SvMigrates_AndVersionedNeighbourSurvives"/>). The hook stands in for that other engine, deterministically.
    /// </remarks>
    [Test]
    public void InitializeArchetypes_BuildsClusterStateFromItsOwnLayout_NotFromTheSharedMetadata()
    {
        var meta = Archetype<EvoMixArch>.Metadata;
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EvoMixSvV1>();
        dbe.RegisterComponentFromAccessor<EvoMixVer>();

        ArchetypeClusterInfo own = null;
        dbe.AfterClusterLayoutPublishedForTest = m =>
        {
            if (!ReferenceEquals(m, meta))
            {
                return;
            }

            own = m.ClusterLayout;
            m.ClusterLayout = ArchetypeClusterInfo.Compute(m.ComponentCount, [64, 64], 0, m.VersionedSlotMask, m.TransientSlotMask);
        };

        try
        {
            dbe.InitializeArchetypes();

            Assert.That(own, Is.Not.Null, "precondition: the hook never ran for the archetype under test");
            Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.Layout, Is.SameAs(own),
                "the cluster state was built from a layout another engine published, not from the one this engine computed");
        }
        finally
        {
            if (own != null)
            {
                meta.ClusterLayout = own;
            }
        }
    }
}

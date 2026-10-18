using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.SparseToggle", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SpToggleDense
{
    [Index(AllowMultiple = true)] public int Group;
    public int Code;

    public SpToggleDense(int group, int code) { Group = group; Code = code; }
}

[Component("Typhon.Schema.UnitTest.SparseToggle", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SpToggleSparse
{
    [Index(AllowMultiple = true, Sparse = true)] public int Group;
    public int Code;

    public SpToggleSparse(int group, int code) { Group = group; Code = code; }
}

[Archetype]
class SpToggleDenseArch : Archetype<SpToggleDenseArch>
{
    public static readonly Comp<SpToggleDense> Comp = Register<SpToggleDense>();
}

[Archetype]
class SpToggleSparseArch : Archetype<SpToggleSparseArch>
{
    public static readonly Comp<SpToggleSparse> Comp = Register<SpToggleSparse>();
}

[Component("Typhon.Schema.UnitTest.MultiToggle", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SpToggleUnique
{
    [Index] public int Code;
    public int Payload;

    public SpToggleUnique(int code, int payload) { Code = code; Payload = payload; }
}

[Component("Typhon.Schema.UnitTest.MultiToggle", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SpToggleMulti
{
    [Index(AllowMultiple = true)] public int Code;
    public int Payload;

    public SpToggleMulti(int code, int payload) { Code = code; Payload = payload; }
}

[Archetype]
class SpToggleUniqueArch : Archetype<SpToggleUniqueArch>
{
    public static readonly Comp<SpToggleUnique> Comp = Register<SpToggleUnique>();
}

[Archetype]
class SpToggleMultiArch : Archetype<SpToggleMultiArch>
{
    public static readonly Comp<SpToggleMulti> Comp = Register<SpToggleMulti>();
}

/// <summary>
/// Turning <c>Sparse</c> on or off on a populated index rebuilds it from the data at open (IX-09). Loaded instead, a tree built dense keeps key-0 entries the
/// sparse one never removes, and one built sparse lacks the 0s a dense one must find — the schema diff reported the change and nothing acted on it.
/// </summary>
[TestFixture]
class SparseSchemaToggleTests : TestBase<SparseSchemaToggleTests>
{
    private static readonly int[] Groups = [0, 0, 0, 1, 1, 2, 0, 2];

    [TestCase(true)]
    [TestCase(false)]
    [VerifiesRule("IX-09")]
    public void TogglingSparse_OnAPopulatedIndex_RebuildsItAtOpen(bool toSparse)
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            if (toSparse)
            {
                dbe.RegisterComponentFromAccessor<SpToggleDense>();
            }
            else
            {
                dbe.RegisterComponentFromAccessor<SpToggleSparse>();
            }

            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Groups.Length; i++)
            {
                if (toSparse)
                {
                    t.Spawn<SpToggleDenseArch>(SpToggleDenseArch.Comp.Set(new SpToggleDense(Groups[i], i)));
                }
                else
                {
                    t.Spawn<SpToggleSparseArch>(SpToggleSparseArch.Comp.Set(new SpToggleSparse(Groups[i], i)));
                }
            }

            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            if (toSparse)
            {
                dbe.RegisterComponentFromAccessor<SpToggleSparse>();
            }
            else
            {
                dbe.RegisterComponentFromAccessor<SpToggleDense>();
            }

            dbe.InitializeArchetypes();

            // The entities live in the archetype they were spawned as; its tree now follows the reopened definition.
            var meta = toSparse ? Archetype<SpToggleDenseArch>.Metadata : Archetype<SpToggleSparseArch>.Metadata;
            var tree = dbe._archetypeStates[meta.ArchetypeId].ClusterState.IndexSlots[0].Fields[0].Index;
            var distinct = new HashSet<int>(Groups);
            if (toSparse)
            {
                distinct.Remove(0);
            }

            Assert.That(tree.Sparse, Is.EqualTo(toSparse), "the tree follows the reopened schema");
            Assert.That(tree.EntryCount, Is.EqualTo(distinct.Count), toSparse ? "groups 1 and 2, no 0" : "0, 1 and 2");
            IndexDataOracle.AssertIndexAgreesWithData(dbe, meta, "after the reopen");
        }

        // The change was written back: the catalog now says what the trees are, so the next open sees no change and the integrity check reads the right
        // kind — a dense-to-sparse flip left the catalog dense, and IDX-04 then reported every entity holding 0 as missing from the index.
        var bundle = ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BundleDirectory;
        var field = DatabaseSchema.Inspect(bundle).Components.Single(c => c.Name == "Typhon.Schema.UnitTest.SparseToggle").Fields.Single(f => f.Name == "Group");
        Assert.That(field.IndexSparse, Is.EqualTo(toSparse), "the catalog records the reopened kind");

        using (var source = new OfflineBundlePageSource(bundle))
        {
            var report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Deep });
            Assert.That(report.Findings.Where(f => f.Code != null && f.Code.StartsWith("IDX")).Select(f => $"{f.Code}: {f.Summary}"), Is.Empty,
                "no index finding after the toggle");
        }
    }

    /// <summary>
    /// Flipping <c>AllowMultiple</c> on a populated Versioned component: the element-id tail it adds or removes changes the stride, so the clusters are
    /// migrated and the index is rebuilt from the migrated data. (A SingleVersion one is refused at open, #671: its old cluster tail cannot be
    /// reconstructed.)
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void FlippingAllowMultiple_OnAPopulatedIndex_MigratesAndRebuilds(bool toMulti)
    {
        const int rows = 200;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            if (toMulti)
            {
                dbe.RegisterComponentFromAccessor<SpToggleUnique>();
            }
            else
            {
                dbe.RegisterComponentFromAccessor<SpToggleMulti>();
            }

            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < rows; i++)
                {
                    if (toMulti)
                    {
                        t.Spawn<SpToggleUniqueArch>(SpToggleUniqueArch.Comp.Set(new SpToggleUnique(1_000 + i, i)));
                    }
                    else
                    {
                        t.Spawn<SpToggleMultiArch>(SpToggleMultiArch.Comp.Set(new SpToggleMulti(1_000 + i, i)));
                    }
                }

                t.Commit();
            }

        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            if (toMulti)
            {
                dbe.RegisterComponentFromAccessor<SpToggleMulti>();
            }
            else
            {
                dbe.RegisterComponentFromAccessor<SpToggleUnique>();
            }

            dbe.InitializeArchetypes();
            var meta = toMulti ? Archetype<SpToggleUniqueArch>.Metadata : Archetype<SpToggleMultiArch>.Metadata;
            var tree = dbe._archetypeStates[meta.ArchetypeId].ClusterState.IndexSlots[0].Fields[0].Index;
            Assert.That(tree.AllowMultiple, Is.EqualTo(toMulti), "the tree follows the reopened schema");
            Assert.That(tree.EntryCount, Is.EqualTo(rows), "one key per entity, every code distinct");
            IndexDataOracle.AssertIndexAgreesWithData(dbe, meta, "after the reopen");
        }
    }
}

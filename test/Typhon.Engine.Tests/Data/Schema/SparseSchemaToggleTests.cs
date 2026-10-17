using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
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
    }
}

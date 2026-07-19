using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Feature #514 D4 — component-TYPE rename hatch (symmetric with the archetype hatch). A component's durable identity is [Component("Name", rev)] (or the CLR type
// name when unnamed). [Component(PreviousName="Old")] re-matches a database created under the old name on reopen — data preserved, name carried forward — so a
// component's schema name can change without orphaning its data. (Before D4, ComponentAttribute.PreviousName existed but was NOT consumed.)
[Component("Comp.TypeRename.New", 1, PreviousName = "Comp.TypeRename.Old")]
[StructLayout(LayoutKind.Sequential)]
struct TypeRenameVal
{
    public int V;
    public int W; // padding — a component's storage must total >= 8 bytes
    public TypeRenameVal(int v) { V = v; W = 0; }
}

[Archetype]
partial class TypeRenameArch : Archetype<TypeRenameArch>
{
    public static readonly Comp<TypeRenameVal> Val = Register<TypeRenameVal>();
}

[TestFixture]
[NonParallelizable]
class ComponentRenameTests : TestBase<ComponentRenameTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // D4 — reopen after a component rename: the persisted ComponentR1 (created under the old name) is re-matched via PreviousName, its data segment loads, the
    // pre-rename entity resolves to the live value, and the durable component name is carried forward on disk (so PreviousName can be dropped later).
    [Test]
    public void Reopen_AfterComponentRename_MatchesByPreviousName_DataSurvives_AndCarriesNameForward()
    {
        EntityId id;

        // Session 1 — create the DB under the component's current name ("Comp.TypeRename.New"); spawn an entity; dispose cleanly.
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewSession(scope))
        {
            using var t = dbe.CreateQuickTransaction();
            id = t.Spawn<TypeRenameArch>(TypeRenameArch.Val.Set(new TypeRenameVal(55)));
            Assert.That(t.Commit(), Is.True);
        }

        // Session 1.5 — simulate a database created by an OLDER build where this component was named "Comp.TypeRename.Old": rewrite the persisted ComponentR1.Name.
        RewritePersistedComponentName(from: "Comp.TypeRename.New", to: "Comp.TypeRename.Old");

        // Session 2 — reopen. TypeRenameVal declares [Component(PreviousName = "Comp.TypeRename.Old")], so registration must match the old-named row, load its data
        // segment, and resolve the pre-rename entity.
        using (var scope2 = ServiceProvider.CreateScope())
        using (var dbe2 = NewSession(scope2))
        {
            using var t2 = dbe2.CreateReadOnlyTransaction();
            var e = t2.Open(id);
            Assert.That(e.IsValid, Is.True, "an entity written under the old component name must still resolve after the rename");
            Assert.That(e.Read(TypeRenameArch.Val).V, Is.EqualTo(55), "component data survives the rename");
        }

        // Carry-forward: the durable component name on disk must now be "Comp.TypeRename.New" — the next reopen matches by Name directly, PreviousName no longer needed.
        var names = ReadPersistedComponentNames();
        Assert.That(names, Does.Contain("Comp.TypeRename.New"), "the durable component name must be carried forward on disk after a PreviousName match");
        Assert.That(names, Does.Not.Contain("Comp.TypeRename.Old"), "the former component name must be gone after carry-forward");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private DatabaseEngine NewSession(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TypeRenameVal>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // Rewrite the persisted ComponentR1.Name of the row currently named <paramref name="from"/> to <paramref name="to"/> — fabricates the "database created under
    // the old component name" state. Opens its own bare session (system schema loads in the engine ctor; no InitializeArchetypes needed).
    private void RewritePersistedComponentName(string from, string to)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var table = dbe.GetComponentTable<ComponentR1>();
        Assert.That(table, Is.Not.Null, "ComponentR1 system table must be loaded on reopen");

        var cs = dbe.MMF.CreateChangeSet();
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }
            if (SystemCrud.Read(table, chunkId, out ComponentR1 comp, dbe.EpochManager) && comp.Name.AsString == from)
            {
                comp.Name.AsString = to;
                SystemCrud.Update(table, chunkId, ref comp, dbe.EpochManager, cs);
                break;
            }
        }
        cs.SaveChanges();
    }

    private List<string> ReadPersistedComponentNames()
    {
        var names = new List<string>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var table = dbe.GetComponentTable<ComponentR1>();
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }
            if (SystemCrud.Read(table, chunkId, out ComponentR1 comp, dbe.EpochManager))
            {
                names.Add(comp.Name.AsString);
            }
        }
        return names;
    }
}

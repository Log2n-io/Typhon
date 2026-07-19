using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Feature #514 D4 — archetype rename hatch. The durable per-DB identity is [Archetype(Name=...)] (defaulting to the CLR type's simple name), NOT the C# type name.
// Renaming the type is safe two ways: (1) keep a stable [Archetype(Name="X")] so the class can be renamed freely, and (2) [Archetype(PreviousName="Old")] so an
// existing database created under the old name is re-matched on reopen (routing id restored, data preserved) and the name is carried forward on disk.
[Component("Typhon.Test.Rename.Val", 1)]
[StructLayout(LayoutKind.Sequential)]
struct RenameVal
{
    public int V;
    public int W; // padding — a component's storage must total >= 8 bytes
    public RenameVal(int v) { V = v; W = 0; }
}

[Component("Typhon.Test.Override.Val", 1)]
[StructLayout(LayoutKind.Sequential)]
struct OverrideVal
{
    public int V;
    public int W;
    public OverrideVal(int v) { V = v; W = 0; }
}

// Durable name "Rename.New" is deliberately different from the CLR type name — it doubles as a Name-override case. PreviousName is the reopen rename hint.
[Archetype(Name = "Rename.New", PreviousName = "Rename.Old")]
partial class RenameArch : Archetype<RenameArch>
{
    public static readonly Comp<RenameVal> Val = Register<RenameVal>();
}

[Archetype(Name = "Custom.Override.Durable.Name")]
partial class OverrideArch : Archetype<OverrideArch>
{
    public static readonly Comp<OverrideVal> Val = Register<OverrideVal>();
}

[TestFixture]
[NonParallelizable]
class ArchetypeRenameTests : TestBase<ArchetypeRenameTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // D4 — the attribute's Name/PreviousName flow into the runtime metadata (Name defaults to the CLR type name when not overridden).
    [Test]
    public void Metadata_CarriesDurableNameAndPreviousName()
    {
        Assert.That(Archetype<RenameArch>.Metadata.Name, Is.EqualTo("Rename.New"), "explicit [Archetype(Name=...)] override is the durable name");
        Assert.That(Archetype<RenameArch>.Metadata.PreviousName, Is.EqualTo("Rename.Old"));
        Assert.That(Archetype<OverrideArch>.Metadata.Name, Is.EqualTo("Custom.Override.Durable.Name"));
        Assert.That(Archetype<OverrideArch>.Metadata.PreviousName, Is.Null, "no rename declared");
        // A default (no-Name) archetype's durable name is still its CLR type name — the pre-D4 on-disk identity for existing databases.
        Assert.That(Archetype<CompAArch>.Metadata.Name, Is.EqualTo("CompAArch"));
    }

    // D4 — [Archetype(Name=...)] decouples the durable identity from the C# type name: the persisted name is the override, so the class can be renamed freely.
    [Test]
    public void NameOverride_PersistsOverrideName_NotClrTypeName()
    {
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            dbe.RegisterComponentFromAccessor<OverrideVal>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction();
            t.Spawn<OverrideArch>(OverrideArch.Val.Set(new OverrideVal(9)));
            Assert.That(t.Commit(), Is.True);
        }

        var names = ReadPersistedArchetypeNames();
        Assert.That(names, Does.Contain("Custom.Override.Durable.Name"), "durable name is the [Archetype(Name=...)] override");
        Assert.That(names, Does.Not.Contain("OverrideArch"), "the CLR type name is NOT the persisted identity when Name is overridden");
    }

    // D4 — reopen after a rename: the persisted row (created under the old name) is re-matched via PreviousName, the routing id is restored so pre-rename EntityIds
    // still resolve, the component data survives, and the durable name is carried forward on disk (so PreviousName can be dropped in a later release).
    [Test]
    public void Reopen_AfterRename_MatchesByPreviousName_RestoresRouting_AndCarriesNameForward()
    {
        EntityId idA;
        ushort routing;

        // Session 1 — create the DB under RenameArch's current durable name ("Rename.New"); spawn an entity; dispose cleanly (persists the row + routing id).
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewSession(scope))
        {
            routing = dbe.RoutingIdOf(Archetype<RenameArch>.Metadata);
            using var t = dbe.CreateQuickTransaction();
            idA = t.Spawn<RenameArch>(RenameArch.Val.Set(new RenameVal(77)));
            Assert.That(t.Commit(), Is.True);
        }

        // Session 1.5 — simulate a database created by an OLDER build where this archetype was named "Rename.Old": rewrite the persisted name back to it.
        // (Same direct-tamper technique as SchemaVersioningTests.Persist_ThenTamper_* — the only way to fabricate the pre-rename on-disk state with one class.)
        RewritePersistedArchetypeName(from: "Rename.New", to: "Rename.Old");

        // Session 2 — reopen. RenameArch declares [Archetype(PreviousName = "Rename.Old")], so the reopen must match the persisted "Rename.Old" row via
        // PreviousName, restore the SAME routing id, and resolve the pre-rename EntityId to the live data.
        using (var scope2 = ServiceProvider.CreateScope())
        using (var dbe2 = NewSession(scope2))
        {
            Assert.That(dbe2.RoutingIdOf(Archetype<RenameArch>.Metadata), Is.EqualTo(routing),
                "routing id must be restored via PreviousName across a rename, so existing EntityIds keep resolving");

            using var t2 = dbe2.CreateReadOnlyTransaction();
            var e = t2.Open(idA);
            Assert.That(e.IsValid, Is.True, "an EntityId spawned under the old name must still resolve after the rename");
            Assert.That(e.Read(RenameArch.Val).V, Is.EqualTo(77), "component data survives the rename");
        }

        // Carry-forward: the durable name on disk must now be "Rename.New" — PersistNewArchetypes re-stamped it, so the next reopen matches by Name directly and
        // the PreviousName hint is no longer load-bearing.
        var names = ReadPersistedArchetypeNames();
        Assert.That(names, Does.Contain("Rename.New"), "the durable name must be carried forward on disk after a PreviousName match");
        Assert.That(names, Does.Not.Contain("Rename.Old"), "the former name must be gone after carry-forward");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private DatabaseEngine NewSession(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RenameVal>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // Rewrite the persisted ArchetypeR1.Name of the row currently named <paramref name="from"/> to <paramref name="to"/>, without touching any other field —
    // fabricates the "database created under the old name" state for the rename test. Opens its own session and does not call InitializeArchetypes.
    private void RewritePersistedArchetypeName(string from, string to)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RenameVal>();
        if (dbe.GetComponentTable<ArchetypeR1>() == null)
        {
            dbe.RegisterComponentFromAccessor<ArchetypeR1>();
        }

        var table = dbe.GetComponentTable<ArchetypeR1>();
        var cs = dbe.MMF.CreateChangeSet();
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }
            if (SystemCrud.Read(table, chunkId, out ArchetypeR1 arch, dbe.EpochManager) && arch.Name.AsString == from)
            {
                arch.Name.AsString = to;
                SystemCrud.Update(table, chunkId, ref arch, dbe.EpochManager, cs);
                break;
            }
        }
        cs.SaveChanges();
    }

    private List<string> ReadPersistedArchetypeNames()
    {
        var names = new List<string>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        if (dbe.GetComponentTable<ArchetypeR1>() == null)
        {
            dbe.RegisterComponentFromAccessor<ArchetypeR1>();
        }

        var table = dbe.GetComponentTable<ArchetypeR1>();
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var segment = table.ComponentSegment;
        for (var chunkId = 1; chunkId < segment.ChunkCapacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }
            if (SystemCrud.Read(table, chunkId, out ArchetypeR1 arch, dbe.EpochManager))
            {
                names.Add(arch.Name.AsString);
            }
        }
        return names;
    }
}

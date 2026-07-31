using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Feature #615 (F2) — renames are journalled into the SchemaHistoryR1 audit trail at the moment they are carried forward on disk.
//
// The rename hatch itself (#514 D4) is deliberately transient: [Component(PreviousName=…)] re-matches a database created under the old name, the engine re-keys
// the row to the new name, and the attribute is then expected to be deleted from source. After that the old name exists NOWHERE — not in source, not in the
// database — while old profiling captures still refer to it. These fixtures pin the one moment at which both names are known.
//
// The chain below is built from three real opens with no tampering: each session registers a different CLR type whose PreviousName points at the previous
// session's name. That is exactly the shape a codebase produces when a component is renamed twice over two releases.

[Component("Comp.Hist.Old", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HistOldVal
{
    public int V;
    public int W; // padding — a component's storage must total >= 8 bytes
}

[Component("Comp.Hist.Mid", 1, PreviousName = "Comp.Hist.Old")]
[StructLayout(LayoutKind.Sequential)]
struct HistMidVal
{
    public int V;
    public int W;
}

[Component("Comp.Hist.Final", 1, PreviousName = "Comp.Hist.Mid")]
[StructLayout(LayoutKind.Sequential)]
struct HistFinalVal
{
    public int V;
    public int W;
}

// A rename that arrives together with a field change — the new type declares one more field than the persisted row has.
[Component("Comp.HistWiden.Old", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HistWidenOldVal
{
    public int V;
    public int W;
}

[Component("Comp.HistWiden.New", 1, PreviousName = "Comp.HistWiden.Old")]
[StructLayout(LayoutKind.Sequential)]
struct HistWidenNewVal
{
    public int V;
    public int W;
    public int X;
}

[Component("Typhon.Test.HistArch.Val", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HistArchVal
{
    public int V;
    public int W;
}

[Archetype(Name = "Hist.Arch.New", PreviousName = "Hist.Arch.Old")]
partial class HistArch : Archetype<HistArch>
{
    public static readonly Comp<HistArchVal> Val = Register<HistArchVal>();
}

[TestFixture]
[NonParallelizable]
class SchemaRenameHistoryTests : TestBase<SchemaRenameHistoryTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // ── AC4 · component rename ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ComponentRename_RecordsARenameRow()
    {
        OpenAndRegister<HistOldVal>();   // database created under the old name
        OpenAndRegister<HistMidVal>();   // reopened by a build that renamed it

        var renames = ReadRenames();

        Assert.That(renames, Has.Count.EqualTo(1), "one hop, one row");
        Assert.Multiple(() =>
        {
            Assert.That(renames[0].PreviousName.AsString, Is.EqualTo("Comp.Hist.Old"));
            Assert.That(renames[0].ComponentName.AsString, Is.EqualTo("Comp.Hist.Mid"));
            Assert.That(renames[0].Target, Is.EqualTo(SchemaObjectKind.Component));
            Assert.That(renames[0].FromRevision, Is.EqualTo(1));
            Assert.That(renames[0].ToRevision, Is.EqualTo(1));
            Assert.That(renames[0].Timestamp, Is.GreaterThan(0));
        });
    }

    // ── AC8 · recorded once ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void SecondReopen_AddsNoFurtherRow()
    {
        OpenAndRegister<HistOldVal>();
        OpenAndRegister<HistMidVal>();
        Assert.That(ReadRenames(), Has.Count.EqualTo(1), "precondition: the first reopen journalled the rename");

        // The name has been carried forward, so this open matches by Name and never reaches the rename branch. A second row here would mean the trail grows
        // by one every time the database is opened — the audit trail would become a log of openings rather than of changes.
        OpenAndRegister<HistMidVal>();

        Assert.That(ReadRenames(), Has.Count.EqualTo(1), "a reopen after carry-forward is not a rename");
    }

    // ── AC6 · the chain ──────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void RenameChain_ResolvesTheOriginalNameToTheCurrentOne()
    {
        OpenAndRegister<HistOldVal>();
        OpenAndRegister<HistMidVal>();     // Old → Mid
        OpenAndRegister<HistFinalVal>();   // Mid → Final

        var renames = ReadRenames();
        Assert.That(renames, Has.Count.EqualTo(2), "two hops, two rows");

        // The operation F5's rename bridge performs: take a name out of an old capture and walk the trail forward to whatever the database calls it now. One
        // hop is not enough — a component renamed twice needs the chain, which is why the row records the previous name rather than just a 'renamed' flag.
        Assert.That(ResolveForward("Comp.Hist.Old", renames), Is.EqualTo("Comp.Hist.Final"));
        Assert.That(ResolveForward("Comp.Hist.Mid", renames), Is.EqualTo("Comp.Hist.Final"), "walking from a mid-chain name works too");
        Assert.That(ResolveForward("Comp.Hist.Final", renames), Is.EqualTo("Comp.Hist.Final"), "a current name resolves to itself");
        Assert.That(ResolveForward("Comp.Never.Existed", renames), Is.EqualTo("Comp.Never.Existed"), "an unknown name passes through unchanged");
    }

    // ── AC7 · a rename alongside a field change ──────────────────────────────────────────────────────────────

    [Test]
    public void RenameWithAFieldChange_RecordsBothRows()
    {
        OpenAndRegister<HistWidenOldVal>();
        OpenAndRegister<HistWidenNewVal>();   // renamed AND gained a field in the same open

        var history = ReadHistory();
        var renames = history.Where(h => h.Kind == SchemaChangeKind.Rename).ToList();
        var changes = history.Where(h => h.Kind != SchemaChangeKind.Rename && h.ComponentName.AsString.StartsWith("Comp.HistWiden")).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(renames, Has.Count.EqualTo(1), "the rename is its own row");
            Assert.That(renames[0].PreviousName.AsString, Is.EqualTo("Comp.HistWiden.Old"));
            Assert.That(renames[0].FieldsAdded, Is.Zero, "field counts belong to the schema-change row, not the rename row");

            Assert.That(changes, Has.Count.EqualTo(1), "…and the field change is its own row");
            Assert.That(changes[0].FieldsAdded, Is.EqualTo(1));
            Assert.That(changes[0].PreviousName.AsString, Is.Empty, "a schema-change row names no previous name");
        });
    }

    // ── AC1 · non-rename rows stay clean ─────────────────────────────────────────────────────────────────────

    [Test]
    public void NonRenameRows_CarryAnEmptyPreviousName()
    {
        OpenAndRegister<HistWidenOldVal>();
        OpenAndRegister<HistWidenNewVal>();

        var history = ReadHistory();
        Assert.That(history, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var row in history.Where(h => h.Kind != SchemaChangeKind.Rename))
            {
                Assert.That(row.PreviousName.AsString, Is.Empty,
                    $"'{row.ComponentName.AsString}' ({row.Kind}) is not a rename, so PreviousName must be empty rather than stale");
            }
        });
    }

    // ── AC5 · archetype rename ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ArchetypeRename_RecordsARenameRow()
    {
        // Session 1 — the database is created under the archetype's current durable name.
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewArchetypeSession(scope))
        {
            using var t = dbe.CreateQuickTransaction();
            t.Spawn<HistArch>(HistArch.Val.Set(new HistArchVal { V = 9 }));
            Assert.That(t.Commit(), Is.True);
        }

        // Session 1.5 — fabricate the "created under the old name" state. An archetype's durable name comes from a fixed attribute, so unlike the component
        // chain above there is no way to reach this state with a second class; the same direct tamper ArchetypeRenameTests uses is the only route.
        RewritePersistedArchetypeName("Hist.Arch.New", "Hist.Arch.Old");

        // Session 2 — reopen; PersistNewArchetypes matches PreviousName, carries the name forward, and journals the hop.
        using (var scope2 = ServiceProvider.CreateScope())
        using (var dbe2 = NewArchetypeSession(scope2))
        {
            Assert.That(dbe2.RoutingIdOf(Archetype<HistArch>.Metadata), Is.GreaterThanOrEqualTo(1));
        }

        var renames = ReadRenames();
        Assert.That(renames, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(renames[0].PreviousName.AsString, Is.EqualTo("Hist.Arch.Old"));
            Assert.That(renames[0].ComponentName.AsString, Is.EqualTo("Hist.Arch.New"));
            Assert.That(renames[0].Target, Is.EqualTo(SchemaObjectKind.Archetype),
                "the discriminator is what stops an archetype rename being read as a component rename — the two name spaces can overlap");
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a session, registers one component, closes. No archetypes — the component rename hatch lives entirely in RegisterComponentFromAccessor.</summary>
    private void OpenAndRegister<T>() where T : unmanaged
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<T>();
    }

    private DatabaseEngine NewArchetypeSession(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<HistArchVal>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private List<SchemaHistoryR1> ReadHistory()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        return [.. dbe.GetSchemaHistory()];
    }

    private List<SchemaHistoryR1> ReadRenames() => ReadHistory().Where(h => h.Kind == SchemaChangeKind.Rename).ToList();

    /// <summary>
    /// Walks the rename trail forward — the resolution F5 needs to turn a name recorded in an old capture into the name the database uses today. Rows are in
    /// insertion order (chunk ids ascend), so a single forward pass follows a chain of any length; an unknown name resolves to itself.
    /// </summary>
    private static string ResolveForward(string name, IEnumerable<SchemaHistoryR1> renames)
    {
        var current = name;
        foreach (var row in renames)
        {
            if (row.PreviousName.AsString == current)
            {
                current = row.ComponentName.AsString;
            }
        }
        return current;
    }

    private void RewritePersistedArchetypeName(string from, string to)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<HistArchVal>();
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
}

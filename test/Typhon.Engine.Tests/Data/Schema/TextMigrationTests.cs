using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// #1094 — schema evolution into and out of String1024. A String1024 is a handle to an out-of-line text, so a migration that produces one has to write the
// text into a chunk (a widened String64, or a migration function's MigrationContext.SetText), and one that drops one has to give the chunk back once the
// migration has committed.
// ═══════════════════════════════════════════════════════════════════════════════

#region Widen String64 -> String1024 (SingleVersion: the cluster pass)

[Component("Typhon.Schema.UnitTest.TxmSvWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxmSvWidenV1
{
    public String64 Name;
    public int After;
}

[Component("Typhon.Schema.UnitTest.TxmSvWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxmSvWidenV2
{
    public String1024 Name;
    public int After;
}

[Archetype]
class TxmSvWidenArch : Archetype<TxmSvWidenArch>
{
    public static readonly Comp<TxmSvWidenV1> Comp = Register<TxmSvWidenV1>();
}

[Archetype]
class TxmSvWidenV2Arch : Archetype<TxmSvWidenV2Arch>
{
    public static readonly Comp<TxmSvWidenV2> Comp = Register<TxmSvWidenV2>();
}

#endregion

#region Widen String64 -> String1024 (Versioned: the segment migration)

[Component("Typhon.Schema.UnitTest.TxmVWiden", 1)]
[StructLayout(LayoutKind.Sequential)]
struct TxmVWidenV1
{
    public String64 Name;
    public int After;
}

[Component("Typhon.Schema.UnitTest.TxmVWiden", 1)]
[StructLayout(LayoutKind.Sequential)]
struct TxmVWidenV2
{
    public String1024 Name;
    public int After;
}

[Archetype]
class TxmVWidenArch : Archetype<TxmVWidenArch>
{
    public static readonly Comp<TxmVWidenV1> Comp = Register<TxmVWidenV1>();
}

[Archetype]
class TxmVWidenV2Arch : Archetype<TxmVWidenV2Arch>
{
    public static readonly Comp<TxmVWidenV2> Comp = Register<TxmVWidenV2>();
}

#endregion

#region A migration function that sets a text (both storage homes)

[Component("Typhon.Schema.UnitTest.TxmFnSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxmFnSvV1
{
    public String64 Name;
    public int Number;
}

[Component("Typhon.Schema.UnitTest.TxmFnSv", 2, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxmFnSvV2
{
    public String1024 Name;     // String64 -> String1024 alone would be a compatible widening; the narrowed Number below makes the change breaking
    public short Number;
}

[Archetype]
class TxmFnSvArch : Archetype<TxmFnSvArch>
{
    public static readonly Comp<TxmFnSvV1> Comp = Register<TxmFnSvV1>();
}

[Archetype]
class TxmFnSvV2Arch : Archetype<TxmFnSvV2Arch>
{
    public static readonly Comp<TxmFnSvV2> Comp = Register<TxmFnSvV2>();
}

[Component("Typhon.Schema.UnitTest.TxmFnV", 1)]
[StructLayout(LayoutKind.Sequential)]
struct TxmFnVV1
{
    public String64 Name;
    public int Number;
}

[Component("Typhon.Schema.UnitTest.TxmFnV", 2)]
[StructLayout(LayoutKind.Sequential)]
struct TxmFnVV2
{
    public String1024 Name;     // String64 -> String1024 alone would be a compatible widening; the narrowed Number below makes the change breaking
    public short Number;
}

[Archetype]
class TxmFnVArch : Archetype<TxmFnVArch>
{
    public static readonly Comp<TxmFnVV1> Comp = Register<TxmFnVV1>();
}

[Archetype]
class TxmFnVV2Arch : Archetype<TxmFnVV2Arch>
{
    public static readonly Comp<TxmFnVV2> Comp = Register<TxmFnVV2>();
}

#endregion

#region Drop a String1024 field

[Component("Typhon.Schema.UnitTest.TxmDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxmDropV1
{
    public int Keep;
    public String1024 Doomed;
}

[Component("Typhon.Schema.UnitTest.TxmDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TxmDropV2
{
    public int Keep;
}

[Archetype]
class TxmDropArch : Archetype<TxmDropArch>
{
    public static readonly Comp<TxmDropV1> Comp = Register<TxmDropV1>();
}

[Archetype]
class TxmDropV2Arch : Archetype<TxmDropV2Arch>
{
    public static readonly Comp<TxmDropV2> Comp = Register<TxmDropV2>();
}

#endregion

[TestFixture]
[NonParallelizable]
class TextMigrationTests : TestBase<TextMigrationTests>
{
    private const int Population = 20;

    private static string NameOf(int i) => $"name-{i}-" + new string('x', i * 2);

    private static int LiveTextChunks(DatabaseEngine dbe)
    {
        var segment = dbe.TextSegment;
        return segment == null ? 0 : segment.AllocatedChunkCount - segment.QuarantinedFreeCount - 1;   // chunk 0 is the null sentinel
    }

    [Test]
    [VerifiesRule("TXT-04")]
    public void SingleVersion_WidenString64ToString1024_KeepsEveryValue()
    {
        var ids = new EntityId[Population];
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmSvWidenV1>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids[i] = t.Spawn<TxmSvWidenArch>(TxmSvWidenArch.Comp.Set(new TxmSvWidenV1 { Name = (String64)(i % 5 == 0 ? "" : NameOf(i)), After = i }));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmSvWidenV2>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < Population; i++)
            {
                var e = t.Open(ids[i]);
                var v = e.Read(TxmSvWidenV2Arch.Comp);
                Assert.That(v.After, Is.EqualTo(i), $"{i}: the field after the widened one");
                Assert.That(e.ReadText(TxmSvWidenV2Arch.Comp, in v.Name), Is.EqualTo(i % 5 == 0 ? "" : NameOf(i)), $"{i}: the widened text");
                Assert.That(v.Name.IsEmpty, Is.EqualTo(i % 5 == 0), $"{i}: an empty String64 costs no chunk");
            }

            Assert.That(LiveTextChunks(dbe), Is.EqualTo(Population - Population / 5));
        }

        // And it is durable: the next open reads the migrated texts from the file.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmSvWidenV2>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction();
            var e = t.Open(ids[3]);
            var v = e.Read(TxmSvWidenV2Arch.Comp);
            Assert.That(e.ReadText(TxmSvWidenV2Arch.Comp, in v.Name), Is.EqualTo(NameOf(3)));
        }
    }

    [Test]
    public void Versioned_WidenString64ToString1024_KeepsEveryValue()
    {
        var ids = new EntityId[Population];
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmVWidenV1>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Population; i++)
                {
                    ids[i] = t.Spawn<TxmVWidenArch>(TxmVWidenArch.Comp.Set(new TxmVWidenV1 { Name = (String64)NameOf(i), After = i }));
                }

                Assert.That(t.Commit(), Is.True);
            }

            // A second revision per entity: the migration keeps HEAD only, so the older revision's text is the sweep's.
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Population; i++)
                {
                    t.OpenMut(ids[i]).Set(TxmVWidenArch.Comp, new TxmVWidenV1 { Name = (String64)("v2-" + NameOf(i)), After = i });
                }

                Assert.That(t.Commit(), Is.True);
            }
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmVWidenV2>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < Population; i++)
            {
                var e = t.Open(ids[i]);
                var v = e.Read(TxmVWidenV2Arch.Comp);
                Assert.That(v.After, Is.EqualTo(i));
                Assert.That(e.ReadText(TxmVWidenV2Arch.Comp, in v.Name), Is.EqualTo("v2-" + NameOf(i)), $"{i}: HEAD's text");
            }

            Assert.That(LiveTextChunks(dbe), Is.EqualTo(Population), "one text per entity: the dropped revisions' are swept after the commit");
        }
    }

    [Test]
    public void SingleVersion_MigrationFunction_SetsATextThroughItsContext() =>
        RunFunctionMigration<TxmFnSvV1, TxmFnSvV2, TxmFnSvArch, TxmFnSvV2Arch>(TxmFnSvArch.Comp, TxmFnSvV2Arch.Comp,
            (f, n) => new TxmFnSvV1 { Name = (String64)f, Number = n },
            (ref TxmFnSvV1 old, out TxmFnSvV2 n, MigrationContext ctx) =>
            {
                n = default;
                ctx.SetText(ref n.Name, old.Name.AsString + " #" + old.Number);
                n.Number = (short)old.Number;
            },
            v => (v.Name, v.Number));

    [Test]
    public void Versioned_MigrationFunction_SetsATextThroughItsContext() =>
        RunFunctionMigration<TxmFnVV1, TxmFnVV2, TxmFnVArch, TxmFnVV2Arch>(TxmFnVArch.Comp, TxmFnVV2Arch.Comp,
            (f, n) => new TxmFnVV1 { Name = (String64)f, Number = n },
            (ref TxmFnVV1 old, out TxmFnVV2 n, MigrationContext ctx) =>
            {
                n = default;
                ctx.SetText(ref n.Name, old.Name.AsString + " #" + old.Number);
                n.Number = (short)old.Number;
            },
            v => (v.Name, v.Number));

    private void RunFunctionMigration<TOld, TNew, TOldArch, TNewArch>(Comp<TOld> oldComp, Comp<TNew> newComp, System.Func<string, int, TOld> make,
        ContextMigrationFunc<TOld, TNew> migrate, System.Func<TNew, (String1024 Text, int Number)> read)
        where TOld : unmanaged where TNew : unmanaged where TOldArch : Archetype<TOldArch> where TNewArch : Archetype<TNewArch>
    {
        var ids = new EntityId[Population];
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TOld>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < Population; i++)
            {
                ids[i] = t.Spawn<TOldArch>(oldComp.Set(make($"first{i}", i)));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterMigration(migrate);
            dbe.RegisterComponentFromAccessor<TNew>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < Population; i++)
            {
                var e = t.Open(ids[i]);
                var (text, number) = read(e.Read(newComp));
                Assert.That(e.ReadText(newComp, in text), Is.EqualTo($"first{i} #{i}"), $"{i}: the text the function set");
                Assert.That(number, Is.EqualTo(i), $"{i}: the scalar the function set");
            }

            Assert.That(LiveTextChunks(dbe), Is.EqualTo(Population));
        }
    }

    [Test]
    [VerifiesRule("TXT-04")]
    public void DroppingAString1024Field_GivesItsTextsBackOnceTheMigrationCommits()
    {
        var ids = new EntityId[Population];
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmDropV1>();
            dbe.InitializeArchetypes();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Population; i++)
                {
                    ids[i] = t.Spawn<TxmDropArch>(TxmDropArch.Comp.Set(new TxmDropV1 { Keep = i }));
                }

                Assert.That(t.Commit(), Is.True);
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Population; i++)
                {
                    var e = t.OpenMut(ids[i]);
                    var v = e.Read(TxmDropArch.Comp);
                    e.SetText(TxmDropArch.Comp, ref v, ref v.Doomed, NameOf(i));
                }

                Assert.That(t.Commit(), Is.True);
            }

            Assert.That(LiveTextChunks(dbe), Is.EqualTo(Population));
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TxmDropV2>();
            dbe.InitializeArchetypes();
            Assert.That(LiveTextChunks(dbe), Is.Zero, "no row names a text any more");
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Open(ids[7]).Read(TxmDropV2Arch.Comp).Keep, Is.EqualTo(7));
        }
    }
}

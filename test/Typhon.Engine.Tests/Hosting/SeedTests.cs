using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Seed.Item", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SeedItem
{
    public int Value;
    public int Pad;
    public SeedItem(int value) { Value = value; Pad = 0; }
}

[Archetype(507)]
partial class SeedItemArch : Archetype<SeedItemArch>
{
    public static readonly Comp<SeedItem> Item = Register<SeedItem>();
}

/// <summary>
/// Tests for <see cref="TyphonOptions.Seed"/> (#506 item 4 follow-up): revision-tagged seed steps applied in ascending order,
/// each in its own durable transaction. A fresh database runs every step; an existing one runs only the steps it has not
/// applied yet (bringing instances up to date). Crash-safe: a step whose transaction never commits re-runs on the next open.
/// </summary>
[NonParallelizable]
public class SeedTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(SeedTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string DbPath => Path.Combine(_dir, "seed.typhon");

    // Common config: register the seed item schema, turn FUA off (no synchronous fsync per commit), and give a comfortable
    // cache so the reopen/rebuild path doesn't hit the 2 MiB-default page-cache backpressure.
    private static TyphonOptions Common(TyphonOptions o) => o
        .Register<SeedItem>()
        .ConfigureEngine(e => e.Wal.UseFUA = false)
        .ConfigureStorage(s => s.DatabaseCacheSize = 64UL * 1024 * 1024);

    private static void Spawn(Transaction tx, int value) => tx.Spawn<SeedItemArch>(SeedItemArch.Item.Set(new SeedItem(value)));

    private int CountItems(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Query<SeedItemArch>().Count();
    }

    [Test]
    public void Seed_AppliesAllStepsOnCreate_SkipsOnReopen()
    {
        int s1 = 0, s2 = 0;

        using (var dbe = DatabaseEngine.Open(DbPath, o => Common(o)
            .Seed(1, tx => { s1++; Spawn(tx, 1); })
            .Seed(2, tx => { s2++; Spawn(tx, 2); })))
        {
            Assert.That((s1, s2), Is.EqualTo((1, 1)), "both steps must run on a fresh database");
            Assert.That(CountItems(dbe), Is.EqualTo(2));
        }

        // Reopen with the same steps: nothing new to apply.
        using (var dbe = DatabaseEngine.Open(DbPath, o => Common(o)
            .Seed(1, tx => { s1++; Spawn(tx, 1); })
            .Seed(2, tx => { s2++; Spawn(tx, 2); })))
        {
            Assert.That((s1, s2), Is.EqualTo((1, 1)), "already-applied steps must not run again on reopen");
            Assert.That(CountItems(dbe), Is.EqualTo(2), "reopen must not re-seed / duplicate data");
        }
    }

    [Test]
    public void Seed_AppliesOnlyNewStepsOnReopen()
    {
        // Ship revision 1 first.
        using (var dbe = DatabaseEngine.Open(DbPath, o => Common(o).Seed(1, tx => Spawn(tx, 1))))
        {
            Assert.That(CountItems(dbe), Is.EqualTo(1));
        }

        // Later the app adds revision 2. Reopen: step 1 is already applied, only step 2 must run.
        int s1 = 0, s2 = 0;
        using (var dbe = DatabaseEngine.Open(DbPath, o => Common(o)
            .Seed(1, tx => { s1++; Spawn(tx, 1); })
            .Seed(2, tx => { s2++; Spawn(tx, 2); })))
        {
            Assert.That(s1, Is.EqualTo(0), "an already-applied step must not re-run");
            Assert.That(s2, Is.EqualTo(1), "a newly-added step must be applied on reopen");
            Assert.That(CountItems(dbe), Is.EqualTo(2), "exactly the two distinct steps' data");
        }
    }

    [Test]
    public void Seed_ReRunsStepThatNeverCommitted()
    {
        // First open: step 1 commits, step 2 throws mid-step. Step 2's transaction never commits, so its data + revision advance
        // roll back — the database is left at committed revision 1.
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseEngine.Open(DbPath, o => Common(o)
                .Seed(1, tx => Spawn(tx, 1))
                .Seed(2, tx => { Spawn(tx, 2); throw new InvalidOperationException("boom mid-step"); })));

        // Reopen with both steps working: step 1 is already applied (skipped), step 2 must RE-RUN and complete.
        int s1 = 0, s2 = 0;
        using var dbe = DatabaseEngine.Open(DbPath, o => Common(o)
            .Seed(1, tx => { s1++; Spawn(tx, 1); })
            .Seed(2, tx => { s2++; Spawn(tx, 2); }));

        Assert.That(s1, Is.EqualTo(0), "the committed step 1 must not re-run");
        Assert.That(s2, Is.EqualTo(1), "the rolled-back step 2 must re-run on the next open (crash-safety)");
        Assert.That(CountItems(dbe), Is.EqualTo(2), "step 1 (from the first open) + step 2 (re-run) — no duplicate, no loss");
    }

    [Test]
    public void Seed_DuplicateRevision_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DatabaseEngine.Open(DbPath, o => Common(o)
                .Seed(1, tx => Spawn(tx, 1))
                .Seed(1, tx => Spawn(tx, 2))));
        Assert.That(ex.Message, Does.Contain("revision 1"));
    }
}

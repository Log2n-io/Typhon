using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// CK-05 protected-pair invariant (design 08 A1.10), proven via the <see cref="ChaosPageIO"/> write-log: across many persist cycles the
/// current-VALID slot of a protected pair (the meta pair AND a segment-directory pair) is <b>never physically written</b> — every write goes
/// to the alternate slot, then flips. This is the property that makes a torn write survivable: the only good copy is never in-flight.
/// <para>
/// Distinct from <see cref="MetaPairTests"/> / <see cref="DirectoryPairTests"/>, which assert the <i>outcomes</i> (torn current slot → reopen
/// selects the sibling (a); both corrupt → loud fail (d); generation monotonic (c)). Those don't observe the physical write stream, so they
/// can't prove the current slot was never touched — only that recovery coped. Here the write-log makes the protocol itself falsifiable: a
/// regression that wrote the current slot (even if it later recovered) would be caught at the write, not just at reopen.
/// </para>
/// </summary>
[TestFixture]
public class ProtectedPairTests : AllocatorTestBase
{
    private EpochManager _epochManager;
    private ManagedPagedMMFOptions _options;
    private ManagedPagedMMF _mmf;

    private static string DbName => $"T_ProtPair_{(uint)TestContext.CurrentContext.Test.Name.GetHashCode():X8}";

    public override void Setup()
    {
        base.Setup();
        _epochManager = new EpochManager("ProtPairEpoch", AllocationResource);
        _options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = TestDatabaseDir,
            DatabaseName = DbName,
            // 512 pages (4 MiB) — deliberately below the 8 MiB floor to force pair over-grow/cycling; TestMode bypasses the floor.
            DatabaseCacheSize = 512 * PagedMMF.PageSize,
            TestMode = true,
        };
    }

    public override void TearDown()
    {
        _mmf?.Dispose();
        _mmf = null;
        try
        {
            _options.EnsureFileDeleted();
        }
        catch
        {
            // best-effort cleanup
        }

        base.TearDown();
    }

    private ManagedPagedMMF Open(bool fresh)
    {
        if (fresh)
        {
            _options.EnsureFileDeleted();
        }

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        return new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, _options, AllocationResource, "ProtPairMMF", logger);
    }

    private ulong ReadSlotGeneration(int filePageIndex)
    {
        var buf = new byte[PagedMMF.PageSize];
        _mmf.ReadPageDirect(filePageIndex, buf);
        return PageBaseHeader.ReadPairGeneration(buf);
    }

    private int TwinOf(int primary)
    {
        foreach (var (p, twin) in _mmf.DirectoryPairs)
        {
            if (p == primary)
            {
                return twin;
            }
        }

        return 0;
    }

    // ── A1.10(b)/(c): META pair — the current-valid slot is never written; generation strictly increments — over a seeded cycle count. ──

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void MetaPair_CurrentValidSlotNeverWritten_OverCycles([Values(2, 5, 11)] int cycles)
    {
        _mmf = Open(fresh: true);

        // Wire AFTER genesis so only our explicit watermark writes are recorded. Each UpdateCheckpointLsn drives exactly one PersistMetaNow
        // (one WritePageDirect to the alternate meta slot + one fsync barrier).
        var chaos = new ChaosPageIO();
        chaos.WireTo(_mmf);

        var seen = 0;
        for (var i = 1; i <= cycles; i++)
        {
            var currentValidSlot = _mmf.MetaCurrentSlotForTest;   // the slot holding the good copy BEFORE this write
            var genBefore = _mmf.MetaGenerationForTest;

            DurabilityWatermarks.UpdateCheckpointLsn(_mmf, i * 10L);

            var newWrites = chaos.WrittenPages.Skip(seen).ToArray();
            seen = chaos.WrittenPages.Count;

            Assert.That(newWrites.Length, Is.EqualTo(1), $"cycle {i}: exactly one physical meta write");
            Assert.That(newWrites[0], Is.Not.EqualTo(currentValidSlot),
                $"cycle {i}: the meta write must target the ALTERNATE slot — the current-valid slot {currentValidSlot} is never overwritten (CK-05)");
            Assert.That(_mmf.MetaCurrentSlotForTest, Is.EqualTo(newWrites[0]), $"cycle {i}: current flips to the just-written slot");
            Assert.That(_mmf.MetaGenerationForTest, Is.EqualTo(genBefore + 1), $"cycle {i}: generation strictly increments by 1");
        }

        var fsyncs = chaos.FlushBarrierCount;
        Assert.That(fsyncs, Is.GreaterThanOrEqualTo(cycles), "each protected write is fsync'd before the slot flip (write→fsync→flip)");

        // The two slots end one generation apart, with the highest (current) on the last-written slot — the durable proof that writes alternated.
        var genCur = ReadSlotGeneration(_mmf.MetaCurrentSlotForTest);
        var genAlt = ReadSlotGeneration(_mmf.MetaCurrentSlotForTest == 0 ? 1 : 0);
        Assert.That(genCur - genAlt, Is.EqualTo(1UL).Or.EqualTo(genCur), "the current slot holds the newest generation; the alternate trails by exactly one (or 0 if never written)");
    }

    // ── A1.10(b): DIRECTORY pair — the per-pair write-log strictly alternates (⟺ the current-valid slot is never written) across grows. ──

    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("CK-05")]
    public void DirectoryPair_CurrentValidSlotNeverWritten_OverGrows([Values(2, 5, 9)] int grows)
    {
        _mmf = Open(fresh: true);

        // Establish the directory pair (root + twin) with a first persist, THEN wire chaos so the write-log isolates the grow writes.
        var cs0 = _mmf.CreateChangeSet();
        var seg = _mmf.AllocateSegment(PageBlockType.None, 2, cs0);
        cs0.SaveChanges();

        var root = seg.RootPageIndex;
        var twin = TwinOf(root);
        Assert.That(twin, Is.GreaterThan(0), "the segment root is a directory page and must have a twin");

        // The slot holding the good copy right now (highest generation) — the first grow write must NOT target it.
        var preCurrentSlot = ReadSlotGeneration(root) >= ReadSlotGeneration(twin) ? root : twin;

        var chaos = new ChaosPageIO();
        chaos.WireTo(_mmf);

        var length = 2;
        for (var i = 1; i <= grows; i++)
        {
            length += 2;
            var cs = _mmf.CreateChangeSet();
            seg.Grow(length, true, cs);
            cs.SaveChanges();                                     // each grow rewrites the root directory → one protected-pair persist to the alternate slot
        }

        // The write-log restricted to the pair's two physical slots must strictly alternate: after a persist writes slot S (now current), the
        // next persist targets the OTHER slot — so S, the current-valid copy, is never the immediately-following target. That IS the protected
        // -pair guarantee, observed at the physical write layer.
        var dirWrites = chaos.WrittenPages.Where(p => p == root || p == twin).ToList();
        Assert.That(dirWrites.Count, Is.EqualTo(grows), "each grow performs exactly one directory-pair write (to the alternate slot)");
        Assert.That(dirWrites[0], Is.Not.EqualTo(preCurrentSlot),
            $"the first grow write must target the alternate of the current-valid slot {preCurrentSlot} — never the good copy (CK-05)");

        for (var i = 1; i < dirWrites.Count; i++)
        {
            Assert.That(dirWrites[i], Is.Not.EqualTo(dirWrites[i - 1]),
                $"directory write {i} ({dirWrites[i]}) must flip to the other slot — writing the same slot twice would overwrite the just-made-current copy (CK-05)");
        }

        // Durable cross-check: the two slots end one generation apart (consecutive), the current holding the newest.
        var genRoot = ReadSlotGeneration(root);
        var genTwin = ReadSlotGeneration(twin);
        Assert.That(System.Math.Abs((long)genRoot - (long)genTwin), Is.EqualTo(1L), "after the alternating grows the two slots hold consecutive generations");
    }
}

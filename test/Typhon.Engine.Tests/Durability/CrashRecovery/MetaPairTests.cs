using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// CK-05 meta-pair A/B slot alternation (P1.3 Increment C1): the meta page (root header + bootstrap) occupies two
/// physical slots (file pages 0–1); each write goes to the alternate slot with a bumped generation + CRC, so the
/// current-valid slot is never in-flight. A torn write can never brick the database — reopen selects the valid sibling.
/// </summary>
[TestFixture]
public class MetaPairTests : AllocatorTestBase
{
    private EpochManager _epochManager;
    private ManagedPagedMMFOptions _options;
    private ManagedPagedMMF _mmf;

    private static string DbName => $"T_MetaPair_{(uint)TestContext.CurrentContext.Test.Name.GetHashCode():X8}";

    public override void Setup()
    {
        base.Setup();
        _epochManager = new EpochManager("MetaPairEpoch", AllocationResource);
        _options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = TestDatabaseDir,
            DatabaseName = DbName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
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
        return new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, _options, AllocationResource, "MetaPairMMF", logger);
    }

    private ulong ReadSlotGeneration(int slot)
    {
        var buf = new byte[PagedMMF.PageSize];
        _mmf.ReadPageDirect(slot, buf);
        return PageBaseHeader.ReadPairGeneration(buf);
    }

    private static byte[] GarbagePage()
    {
        var g = new byte[PagedMMF.PageSize];
        g.AsSpan().Fill(0xFF);   // all-0xFF → stored CRC ≠ computed CRC → slot is invalid
        return g;
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void MetaFlip_AlternatesSlots_GenerationMonotonic()
    {
        _mmf = Open(fresh: true);

        var prevGen = _mmf.MetaGenerationForTest;
        var prevSlot = _mmf.MetaCurrentSlotForTest;

        for (var i = 1; i <= 4; i++)
        {
            DurabilityWatermarks.UpdateCheckpointLsn(_mmf, i * 10);
            Assert.That(_mmf.MetaGenerationForTest, Is.EqualTo(prevGen + 1), "each meta write bumps the generation by 1");
            Assert.That(_mmf.MetaCurrentSlotForTest, Is.Not.EqualTo(prevSlot), "each meta write flips to the other slot");
            prevGen = _mmf.MetaGenerationForTest;
            prevSlot = _mmf.MetaCurrentSlotForTest;
        }

        var genA = ReadSlotGeneration(0);
        var genB = ReadSlotGeneration(1);
        Assert.That(Math.Abs((long)genA - (long)genB), Is.EqualTo(1L), "the two slots hold consecutive generations");
        Assert.That(Math.Max(genA, genB), Is.EqualTo(_mmf.MetaGenerationForTest), "the current slot holds the highest generation");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void TornCurrentSlot_ReopenSelectsSibling()
    {
        _mmf = Open(fresh: true);
        DurabilityWatermarks.UpdateCheckpointLsn(_mmf, 50);    // the sibling will hold CheckpointLSN 50
        DurabilityWatermarks.UpdateCheckpointLsn(_mmf, 100);   // the current slot holds CheckpointLSN 100
        var currentSlot = _mmf.MetaCurrentSlotForTest;

        // Tear the current (newest) slot — simulate a torn write that never completed.
        _mmf.WritePageDirect(currentSlot, GarbagePage());
        _mmf.FlushToDisk();
        _mmf.Dispose();
        _mmf = null;

        // Reopen — LoadMeta must select the valid sibling (CheckpointLSN 50), never fail.
        _mmf = Open(fresh: false);
        Assert.That(DurabilityWatermarks.ReadCheckpointLsn(_mmf), Is.EqualTo(50L), "reopen falls back to the valid sibling slot, not the torn current slot");
        Assert.That(_mmf.MetaCurrentSlotForTest, Is.Not.EqualTo(currentSlot), "the torn slot is not selected as current");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void BothSlotsCorrupt_OpenFailsLoudly()
    {
        _mmf = Open(fresh: true);
        DurabilityWatermarks.UpdateCheckpointLsn(_mmf, 100);

        _mmf.WritePageDirect(0, GarbagePage());
        _mmf.WritePageDirect(1, GarbagePage());
        _mmf.FlushToDisk();
        _mmf.Dispose();
        _mmf = null;

        // Both slots invalid → open must fail loudly, never silently fall back. (The MMF wraps the LoadMeta failure in a
        // "Virtual Disk Manager initialization error" — assert the loud meta-pair diagnostic on the inner exception.)
        Assert.That(() => _mmf = Open(fresh: false),
            Throws.Exception.With.InnerException.TypeOf<InvalidOperationException>()
                .And.InnerException.Message.Contains("Both meta-pair slots"));
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DurabilityWatermarks_SurviveReopen()
    {
        _mmf = Open(fresh: true);
        DurabilityWatermarks.UpdateCheckpointLsn(_mmf, 4242);
        DurabilityWatermarks.SetCleanShutdown(_mmf, true);
        _mmf.Dispose();
        _mmf = null;

        _mmf = Open(fresh: false);
        Assert.That(DurabilityWatermarks.ReadCheckpointLsn(_mmf), Is.EqualTo(4242L), "CheckpointLSN survives reopen via the watermark block");
        Assert.That(DurabilityWatermarks.ReadCleanShutdown(_mmf), Is.True, "CleanShutdown survives reopen, packed alongside CheckpointLSN");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void FreshDatabase_WatermarksReadAsZeroAndClean()
    {
        // Genesis writes NO watermark entry (M12: the durability layer owns it). An absent key must read as the
        // correct fresh-database state — (CheckpointLSN=0, CleanShutdown=false) — so deleting the genesis init is safe.
        _mmf = Open(fresh: true);

        Assert.That(DurabilityWatermarks.ReadCheckpointLsn(_mmf), Is.EqualTo(0L), "a fresh database has no checkpoint watermark yet");
        Assert.That(DurabilityWatermarks.ReadCleanShutdown(_mmf), Is.False, "a fresh database is not marked clean-shutdown");
    }
}

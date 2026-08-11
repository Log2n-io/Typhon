using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// CK-05 regression: the structural flush path must never write the meta pair.
/// </summary>
/// <remarks>
/// <para>
/// The page-0 meta pair exists so a torn write can never destroy the only copy of the root metadata: writes alternate
/// between two physical slots, and the current slot is never overwritten in place. That guarantee holds only while
/// <c>PersistMetaNow</c> is the <b>sole</b> writer of those two slots.
/// </para>
/// <para>
/// It was not. <c>SavePages</c> — the structural ChangeSet flush — writes whatever pages the ChangeSet carries, and its
/// checksum-stamping step is guarded by <c>FilePageIndex &gt; 0</c> while the <i>write</i> was not. A flush that happened
/// to carry logical page 0 therefore overwrote meta slot 0 with an image whose stored checksum no longer matched its
/// content. Nothing complained, because the other slot still opened the database — the pair had silently degraded to a
/// single copy. The failure only surfaces later, when that surviving slot tears too and both slots read invalid, at which
/// point the database is permanently unopenable. <c>CollectDirtyMemPageIndices</c> had the exclusion all along; this path
/// is fed by a ChangeSet rather than by that scan, so it never applied.
/// </para>
/// <para>
/// Found by the offline integrity scanner on its first run against a healthy database, which is the argument for having an
/// independent detector: nothing inside the engine could notice, because every individual structure was well-formed.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class MetaPairStructuralFlushTests
{
    private string _root;

    private static string DbName => "Tmps_" + TestSeed.StableHash(TestContext.CurrentContext.Test.Name).ToString("X8");

    private string DbDir => Path.Combine(_root, "db");

    private string BundlePath => Path.Combine(DbDir, $"{DbName}.typhon");

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(MetaPairStructuralFlushTests), DbName);
        Directory.CreateDirectory(DbDir);
        Directory.CreateDirectory(Path.Combine(_root, "wal"));
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// After a full engine lifecycle both meta slots must be individually checksum-valid. "At least one is valid" is the
    /// weaker property that lets the database open; it is not the property the pair exists to provide.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-05")]
    public void FullEngineLifecycle_LeavesBothMetaSlotsValid()
    {
        var writes = RunLifecycle();

        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        var slot0 = ReadPage(dataPath, 0);
        var slot1 = ReadPage(dataPath, 1);

        var report = Describe(slot0, 0) + "\n" + Describe(slot1, 1);

        Assert.Multiple(() =>
        {
            Assert.That(IsValid(slot0), Is.True, "meta slot 0 must be checksum-valid:\n" + report);
            Assert.That(IsValid(slot1), Is.True, "meta slot 1 must be checksum-valid:\n" + report);
        });

        // The generations must differ by exactly one: strict alternation is what makes the pair a pair. Two writes landing
        // on the same slot in a row is the signature of a second writer, which is the bug this test guards.
        var gen0 = PageBaseHeader.ReadPairGeneration(slot0);
        var gen1 = PageBaseHeader.ReadPairGeneration(slot1);
        Assert.That(Math.Abs((long)gen0 - (long)gen1), Is.EqualTo(1), "meta generations must be consecutive:\n" + report);

        // And no slot may be written twice in a row during shutdown.
        for (var i = 1; i < writes.Count; i++)
        {
            Assert.That(writes[i], Is.Not.EqualTo(writes[i - 1]),
                $"meta-pair writes must strictly alternate; observed [{string.Join(", ", writes)}]");
        }
    }

    /// <summary>
    /// The genuineness proof for <see cref="FullEngineLifecycle_LeavesBothMetaSlotsValid"/>: a database with only one
    /// valid slot must FAIL that test's assertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the verifier above could be wired so that it cannot fail — asserting on the wrong buffer, or with
    /// a matcher that accepts anything — and it would stay green in the same build as the bug it exists to catch. That
    /// is not hypothetical for this rule: the violation it guards survived for months precisely because every other
    /// CK-05 test checked the pair's read selection or its write protocol, and the database opened fine from the one
    /// surviving slot.
    /// </para>
    /// <para>
    /// The mutant is the exact state the bug produced — one slot whose stored checksum no longer matches its content —
    /// and it drives the verifier's real assertion path rather than a re-implementation of it.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("CK-05")]
    public void Mutant_OneValidSlotDoesNotSatisfyThePairProperty()
    {
        RunLifecycle();

        var dataPath = Path.Combine(BundlePath, IntegrityConstants.DataFileName);
        ClobberSlot(dataPath, 1);

        RuleMutants.AssertDetects(
            "CK-05",
            "meta slot 1 must be checksum-valid",
            () =>
            {
                var slot0 = ReadPage(dataPath, 0);
                var slot1 = ReadPage(dataPath, 1);
                var report = Describe(slot0, 0) + "\n" + Describe(slot1, 1);

                Assert.That(IsValid(slot0), Is.True, "meta slot 0 must be checksum-valid:\n" + report);
                Assert.That(IsValid(slot1), Is.True, "meta slot 1 must be checksum-valid:\n" + report);
            });
    }

    /// <summary>Overwrites a range inside one meta slot, leaving the page parseable but checksum-invalid.</summary>
    private static void ClobberSlot(string dataPath, int slot)
    {
        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek((long)slot * IntegrityConstants.PageSize + 200, SeekOrigin.Begin);
        fs.Write(new byte[64]);
        fs.Flush(true);
    }

    /// <summary>Runs a representative lifecycle and returns the physical meta-pair page indices written during shutdown.</summary>
    private List<int> RunLifecycle()
    {
        var writes = new List<int>();
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DbName;
                opts.DatabaseDirectory = DbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = Path.Combine(_root, "wal"),
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1
                };
            });

        using var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            for (var i = 0; i < 64; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 1, i, i);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                tx.Commit();
            }

            uow.Flush();
        }

        dbe.ForceCheckpoint();

        // Record shutdown-time writes: this is where the structural flush and the clean-shutdown metadata write interleave.
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        mmf.PageWriteInterceptor = idx =>
        {
            if (idx <= 1)
            {
                lock (writes)
                {
                    writes.Add(idx);
                }
            }
        };

        scope.Dispose();
        return writes;
    }

    private static string Describe(byte[] page, int slot) =>
        $"  slot {slot}: generation={PageBaseHeader.ReadPairGeneration(page)} stored=0x{StoredCrc(page):X8} "
        + $"computed=0x{ComputedCrc(page):X8} valid={IsValid(page)} modificationCounter={BitConverter.ToInt32(page, 12)}";

    private static bool IsValid(byte[] page) => StoredCrc(page) == ComputedCrc(page);

    private static byte[] ReadPage(string path, int index)
    {
        var page = new byte[IntegrityConstants.PageSize];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(index * (long)IntegrityConstants.PageSize, SeekOrigin.Begin);
        fs.ReadExactly(page);
        return page;
    }

    private static uint StoredCrc(byte[] page) => BitConverter.ToUInt32(page, PageBaseHeader.PageChecksumOffset);

    private static uint ComputedCrc(byte[] page) => Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
}

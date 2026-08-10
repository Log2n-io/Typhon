using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.Engine;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// Format revision 7: the chunk stride is on the page, so a reader with no schema assembly can do chunk arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes is narrow and total. An offline reader could already find a segment, read its directory and
/// classify its pages — <c>LogicalSegmentHeader.Kind</c> made the segment self-describing in that sense — but it could
/// not locate chunk <i>n</i> inside any of them, because stride arrives as a constructor argument derived from a CLR
/// type and was never written down. Every cross-structure check needs that one integer before it can read a single
/// engine-defined chunk header.
/// </para>
/// <para>
/// These tests read the bytes with no engine at all. A test that asked the engine for the stride would be asking the
/// thing that already knew it.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SegmentGeometryPersistenceTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void EveryChunkSegmentPageRecordsItsStride()
    {
        var expected = BuildAndRecordStrides();
        Assert.That(expected, Is.Not.Empty, "precondition: the fixture must produce chunk-based segments");

        var data = File.ReadAllBytes(DamageKit.DataPath(BundlePath));

        var mismatches = new List<string>();
        foreach (var (page, stride) in expected)
        {
            var recorded = SegmentGeometryProbe.ReadStride(data, page);
            if (recorded != stride)
            {
                mismatches.Add($"page {page}: recorded {recorded}, expected {stride}");
            }
        }

        Assert.That(mismatches, Is.Empty,
            "every page of a chunk-based segment must carry its stride:\n  " + string.Join("\n  ", mismatches));
    }

    /// <summary>
    /// The stride survives a full lifecycle with checkpoints — which is also the collision detector for the byte range.
    /// </summary>
    /// <remarks>
    /// The geometry lives in free bytes of the page header, alongside the CK-05 pair generation and the sector-footer
    /// declaration. If anything else ever writes there, this test is what notices: the value would be intact right
    /// after page init and wrong after the page had been through a real write path.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void StrideSurvivesCheckpointsAndReopen()
    {
        var expected = BuildAndRecordStrides();

        // Reopen, write more, checkpoint again, close. Every one of those paths rewrites pages.
        using (var provider = ReopenProvider())
        {
            using var scope = provider.CreateScope();
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 64; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(5000 + i, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();
        }

        var data = File.ReadAllBytes(DamageKit.DataPath(BundlePath));
        var mismatches = expected
            .Where(kv => SegmentGeometryProbe.ReadStride(data, kv.Key) != kv.Value)
            .Select(kv => $"page {kv.Key}: recorded {SegmentGeometryProbe.ReadStride(data, kv.Key)}, expected {kv.Value}")
            .ToArray();

        Assert.That(mismatches, Is.Empty,
            "the recorded stride must survive checkpoints and a reopen; a mismatch here means something else is "
            + "writing into the bytes it occupies:\n  " + string.Join("\n  ", mismatches));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADatabaseStillScansSoundWithTheGeometryRecorded()
    {
        // The geometry sits inside the CRC-covered region, so a stamp that landed anywhere unexpected would show up as
        // a checksum failure rather than as a quiet wrong number.
        BuildHealthyDatabase();

        DamageKit.Baseline(BundlePath);
    }

    [Test]
    [CancelAfter(30_000)]
    public void NonChunkPagesRecordNoStride()
    {
        // Zero means "not a chunk-based page". A reader must be able to tell "no chunks here" from "stride unknown",
        // and conflating them is how a walker starts doing arithmetic on a page that has none.
        BuildHealthyDatabase();
        var data = File.ReadAllBytes(DamageKit.DataPath(BundlePath));

        // The meta pair is not a chunk segment.
        Assert.That(SegmentGeometryProbe.ReadStride(data, 0), Is.Zero, "meta slot 0 holds no chunks");
        Assert.That(SegmentGeometryProbe.ReadStride(data, 1), Is.Zero, "meta slot 1 holds no chunks");
    }

    /// <summary>
    /// A bundle recording an older format revision is refused, naming both revisions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pre-alpha carries no compatibility obligation, so revision 7 does not read revision 6 — and the point of this
    /// test is that it does not read it <i>loudly</i>. A silent misread is the failure worth guarding: the geometry
    /// bytes this revision introduces are zero on a v6 page, so a reader that ignored the revision would conclude
    /// "this segment has no chunks" about a segment full of them, and every check downstream would agree with it.
    /// </para>
    /// <para>
    /// The forgery is genuine rather than approximate: the revision is patched in both meta slots and each page is
    /// re-stamped with the engine's own <c>StampPageForWrite</c>, so the file that reaches the open path is
    /// checksum-valid in every respect except the one under test. Patching the byte alone would have produced a CRC
    /// failure, and the test would have passed while proving something else entirely.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ABundleFromAnOlderRevisionIsRefusedByTheVersionGate()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        DamageKit.ForgeFormatRevision(BundlePath, 6);

        var ex = Assert.Catch<Exception>(() =>
        {
            using var provider = ReopenProvider();
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        });

        // Positive evidence, not the absence of a word. The first version of this assertion required the message not
        // to mention "checksum" — and matched the bundle path, which contains the test's own name. An assertion that
        // can be satisfied or broken by what a test is called is not measuring the product.
        var message = Flatten(ex);
        Assert.That(message, Does.Contain("Incompatible database format"),
            "the open must be refused by the version gate rather than by a corrupted page:\n" + message);
        Assert.That(message, Does.Contain("file version 6").And.Contain("engine version 7"),
            "the refusal must name the revision found and the one expected, so an operator knows which build to use:\n" + message);
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
        {
            parts.Add(e.Message);
        }

        return string.Join(" | ", parts);
    }

    /// <summary>Builds a database and returns the stride the engine believes each chunk-segment page has.</summary>
    private Dictionary<int, int> BuildAndRecordStrides()
    {
        var expected = new Dictionary<int, int>();

        using (var scope = Provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 128; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();

            foreach (var seg in dbe.EnumerateStorageSegments())
            {
                if (seg.Stride <= 0)
                {
                    continue;
                }

                foreach (var page in seg.Pages.Span)
                {
                    expected[page] = seg.Stride;
                }
            }
        }

        CloseEngine();
        return expected;
    }
}

/// <summary>Reads the persisted geometry out of a raw data-file image, with no engine involved.</summary>
internal static class SegmentGeometryProbe
{
    /// <summary>Byte offset of the stride within a page. Mirrors <c>SegmentGeometry.StrideOffset</c>.</summary>
    private const int StrideOffset = 54;

    /// <summary>Reads the recorded chunk stride for a physical page; <c>0</c> means the page records none.</summary>
    internal static int ReadStride(byte[] data, int filePageIndex)
    {
        var at = filePageIndex * IntegrityConstants.PageSize + StrideOffset;
        return at + sizeof(ushort) > data.Length ? -1 : System.BitConverter.ToUInt16(data, at);
    }
}

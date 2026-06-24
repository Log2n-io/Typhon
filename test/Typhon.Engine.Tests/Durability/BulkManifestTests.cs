using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Verifies the binary layout, size, and field offsets of <see cref="BulkManifestHeader"/> and <see cref="BulkPageRange"/>, plus the new
/// <see cref="WalChunkType"/> enum values <see cref="WalChunkType.BulkBegin"/> / <see cref="WalChunkType.BulkEnd"/>. Catches accidental struct changes that
/// would break the on-disk WAL format for BulkLoad manifests.
/// </summary>
/// <remarks>
/// <para>Source-of-truth: <c>claude/design/Durability/BulkLoad/04-manifest-format.md</c>.</para>
/// <para>The <see cref="BulkManifestHeader.Lsn"/> field MUST sit at body offset 0 per <b>WP-07</b> (<c>claude/rules/durability.md</c>) —
/// <c>WalSegmentReader</c> extracts the chunk LSN via the generic <c>body[0..8]</c> convention.</para>
/// </remarks>
[TestFixture]
public class BulkManifestTests
{
    #region WalChunkType — new values

    [Test]
    public void WalChunkType_BulkBegin_Is5() =>
        Assert.That((int)WalChunkType.BulkBegin, Is.EqualTo(5));

    [Test]
    public void WalChunkType_BulkEnd_Is6() =>
        Assert.That((int)WalChunkType.BulkEnd, Is.EqualTo(6));

    [Test]
    public void WalChunkType_ExistingValues_Unchanged()
    {
        // Defensive: the existing chunk types must keep their numeric values for backward compat.
        Assert.That((int)WalChunkType.Transaction, Is.EqualTo(1));
        Assert.That((int)WalChunkType.TickFence, Is.EqualTo(3));
        Assert.That((int)WalChunkType.ClusterTickFence, Is.EqualTo(4));
    }

    #endregion

    #region BulkManifestHeader — size + WP-07

    [Test]
    public void BulkManifestHeader_SizeOf_Is56Bytes()
    {
        Assert.That(Unsafe.SizeOf<BulkManifestHeader>(), Is.EqualTo(56));
        Assert.That(Unsafe.SizeOf<BulkManifestHeader>(), Is.EqualTo(BulkManifestHeader.SizeInBytes));
    }

    [Test]
    public void BulkManifestHeader_LsnIsAtOffset0_WP07()
    {
        // WP-07: chunk body's first 8 bytes must be the chunk's LSN. WalSegmentReader reads body[0..8]
        // unconditionally. Lsn at offset 0 keeps BulkBegin/BulkEnd chunks WP-07-compliant.
        Assert.That(
            Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.Lsn)).ToInt32(),
            Is.EqualTo(0));
    }

    #endregion

    #region BulkManifestHeader — field offsets

    [Test]
    public void BulkManifestHeader_FieldOffsets()
    {
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.Lsn)).ToInt32(),
            Is.EqualTo(0));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.BulkSessionId)).ToInt32(),
            Is.EqualTo(8));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.BulkBeginLsn)).ToInt32(),
            Is.EqualTo(16));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.SegmentCount)).ToInt32(),
            Is.EqualTo(24));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.PageRangeCount)).ToInt32(),
            Is.EqualTo(28));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.EntitiesSpawned)).ToInt32(),
            Is.EqualTo(32));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.EntitiesUpdated)).ToInt32(),
            Is.EqualTo(40));
        Assert.That(Marshal.OffsetOf<BulkManifestHeader>(nameof(BulkManifestHeader.EntitiesDestroyed)).ToInt32(),
            Is.EqualTo(48));
    }

    #endregion

    #region BulkPageRange — size + offsets

    [Test]
    public void BulkPageRange_SizeOf_Is16Bytes()
    {
        Assert.That(Unsafe.SizeOf<BulkPageRange>(), Is.EqualTo(16));
        Assert.That(Unsafe.SizeOf<BulkPageRange>(), Is.EqualTo(BulkPageRange.SizeInBytes));
    }

    [Test]
    public void BulkPageRange_FieldOffsets()
    {
        Assert.That(Marshal.OffsetOf<BulkPageRange>(nameof(BulkPageRange.SegmentRootPageId)).ToInt32(),
            Is.EqualTo(0));
        Assert.That(Marshal.OffsetOf<BulkPageRange>(nameof(BulkPageRange.FirstPageId)).ToInt32(),
            Is.EqualTo(4));
        Assert.That(Marshal.OffsetOf<BulkPageRange>(nameof(BulkPageRange.PageCount)).ToInt32(),
            Is.EqualTo(8));
        Assert.That(Marshal.OffsetOf<BulkPageRange>(nameof(BulkPageRange.Reserved)).ToInt32(),
            Is.EqualTo(12));
    }

    #endregion

    #region Round-trip — header + page-range list to byte buffer

    [Test]
    public unsafe void RoundTrip_HeaderAndPageRanges_ThroughByteBuffer()
    {
        // Build a manifest matching the 04-manifest-format.md worked example:
        // - 2 segments, 3 page ranges (segment 10 = [100..149], [200..249]; segment 20 = [500..599])
        // - LSN = 1500, BulkSessionId = 0x0100FFEEDDCCBBAA, BulkBeginLsn = 1000 (cross-ref)
        var headerIn = new BulkManifestHeader
        {
            Lsn               = 1500L,
            BulkSessionId     = 0x0100FFEEDDCCBBAA,
            BulkBeginLsn      = 1000L,
            SegmentCount      = 2,
            PageRangeCount    = 3,
            EntitiesSpawned   = 250_000L,
            EntitiesUpdated   = 0L,
            EntitiesDestroyed = 0L,
        };

        var rangesIn = new[]
        {
            new BulkPageRange { SegmentRootPageId = 10, FirstPageId = 100, PageCount = 50, Reserved = 0 },
            new BulkPageRange { SegmentRootPageId = 10, FirstPageId = 200, PageCount = 50, Reserved = 0 },
            new BulkPageRange { SegmentRootPageId = 20, FirstPageId = 500, PageCount = 100, Reserved = 0 },
        };

        // Serialise: 56 bytes of header + 3 × 16 bytes of page-range entries = 104 bytes total.
        var bodyLen = BulkManifestHeader.SizeInBytes + rangesIn.Length * BulkPageRange.SizeInBytes;
        var buffer = new byte[bodyLen];

        fixed (byte* p = buffer)
        {
            *(BulkManifestHeader*)p = headerIn;
            var rangePtr = (BulkPageRange*)(p + BulkManifestHeader.SizeInBytes);
            for (int i = 0; i < rangesIn.Length; i++)
            {
                rangePtr[i] = rangesIn[i];
            }
        }

        // Deserialise.
        BulkManifestHeader headerOut;
        var rangesOut = new BulkPageRange[rangesIn.Length];
        fixed (byte* p = buffer)
        {
            headerOut = *(BulkManifestHeader*)p;
            var rangePtr = (BulkPageRange*)(p + BulkManifestHeader.SizeInBytes);
            for (int i = 0; i < rangesIn.Length; i++)
            {
                rangesOut[i] = rangePtr[i];
            }
        }

        // Header equality (field by field — clearer failure messages than struct equality).
        Assert.That(headerOut.Lsn, Is.EqualTo(headerIn.Lsn));
        Assert.That(headerOut.BulkSessionId, Is.EqualTo(headerIn.BulkSessionId));
        Assert.That(headerOut.BulkBeginLsn, Is.EqualTo(headerIn.BulkBeginLsn));
        Assert.That(headerOut.SegmentCount, Is.EqualTo(headerIn.SegmentCount));
        Assert.That(headerOut.PageRangeCount, Is.EqualTo(headerIn.PageRangeCount));
        Assert.That(headerOut.EntitiesSpawned, Is.EqualTo(headerIn.EntitiesSpawned));
        Assert.That(headerOut.EntitiesUpdated, Is.EqualTo(headerIn.EntitiesUpdated));
        Assert.That(headerOut.EntitiesDestroyed, Is.EqualTo(headerIn.EntitiesDestroyed));

        // Page-range list.
        for (int i = 0; i < rangesIn.Length; i++)
        {
            Assert.That(rangesOut[i].SegmentRootPageId, Is.EqualTo(rangesIn[i].SegmentRootPageId), $"range {i} SegmentRootPageId");
            Assert.That(rangesOut[i].FirstPageId, Is.EqualTo(rangesIn[i].FirstPageId), $"range {i} FirstPageId");
            Assert.That(rangesOut[i].PageCount, Is.EqualTo(rangesIn[i].PageCount), $"range {i} PageCount");
            Assert.That(rangesOut[i].Reserved, Is.EqualTo(rangesIn[i].Reserved), $"range {i} Reserved");
        }

        // WP-07 sanity: bytes [0..8) of the body equal headerIn.Lsn.
        var lsnFromBytes = System.BitConverter.ToInt64(buffer, 0);
        Assert.That(lsnFromBytes, Is.EqualTo(headerIn.Lsn),
            "body[0..8] must equal the chunk's LSN (WP-07) — extracted by WalSegmentReader unconditionally");
    }

    #endregion
}

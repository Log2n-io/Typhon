using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// WA-5 (BKP-10): the archiver reads a sealed segment while a released checkpoint may reclaim it. Its handle shares delete, so the reclaim deletes the
/// file instead of failing the cycle under the list lock, and the handle keeps reading it. Windows only: a POSIX unlink never fails on an open file.
/// </summary>
[TestFixture]
[Platform("Win")]
internal sealed class ArchiveShareDeleteTests
{
    private const string Marker = "BKP-10: reclaim failed on a segment the archiver held";
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(TestStorage.Root, nameof(ArchiveShareDeleteTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("BKP-10")]
    public void AReclaim_OfASegmentTheArchiverHolds_DeletesIt_AndTheHandleStillReadsIt()
    {
        using var io = new WalFileIO();
        using var manager = new WalSegmentManager(io, _dir, 64 * 1024, 1, false);
        manager.Initialize(0, 1);
        var sealedPath = manager.ActiveSegment.Path;
        manager.RotateSegment(11, 10);

        using var handle = io.OpenSegmentForArchive(sealedPath);
        Assert.That(ReclaimAll(manager), Is.Null, Marker);
        Assert.That(File.Exists(sealedPath), Is.False);

        var header = new byte[4096];
        io.ReadAligned(handle, 0, header);
        Assert.That(MemoryMarshal.Read<WalSegmentHeader>(header).Validate(), Is.True, "the deleted segment is still read through the archiver's handle");
    }

    /// <summary>A plain read handle (no delete sharing): the reclaim throws inside the checkpoint's step 8. The verifier must see it.</summary>
    [Test]
    [CancelAfter(10_000)]
    [RuleMutant("BKP-10")]
    public void Mutant_AReadHandleWithoutDeleteSharing_FailsTheReclaim()
    {
        using var io = new WalFileIO();
        using var manager = new WalSegmentManager(io, _dir, 64 * 1024, 1, false);
        manager.Initialize(0, 1);
        var sealedPath = manager.ActiveSegment.Path;
        manager.RotateSegment(11, 10);

        using var handle = io.OpenSegmentForRead(sealedPath);
        RuleMutants.AssertDetects("BKP-10", Marker, () => Assert.That(ReclaimAll(manager), Is.Null, Marker));
    }

    private static Exception ReclaimAll(WalSegmentManager manager)
    {
        try
        {
            manager.MarkReclaimable(long.MaxValue);
            return null;
        }
        catch (IOException e)
        {
            return e;
        }
    }
}

using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// Review items 17-19 on the pack reader and writer: a failed IndexCRC stays failed, no forged offset wraps past a range check, an incremental names
/// its base, the writer frees what it allocated when it cannot allocate the rest, refuses a tail segment it could not copy whole, and the index entry
/// keeps its on-disk size.
/// </summary>
[TestFixture]
internal sealed class PackHardeningTests
{
    private const string Dir = "/s/packs";

    private static InMemorySparseFileSystem NewFs()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory(Dir);
        return fs;
    }

    private static string WriteFull(InMemorySparseFileSystem fs, BackupTestImage image) =>
        PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Full, 1), Enumerable.Range(0, image.PageCount), withTail: false).PackPath;

    [Test]
    public void AnIndexThatFailedItsCrc_KeepsFailing_WhenTheCallerRetries()
    {
        var fs = NewFs();
        var image = BackupTestImages.Clean;
        var path = WriteFull(fs, image);
        using (var reader = PackReader.Open(fs, path))
        {
            // One bit of the first entry's PageCrc: no per-entry check reads it, only the IndexCRC catches it.
            var crcField = reader.Footer.IndexOffset + 16;
            fs.Poke(path, crcField, [(byte)(fs.ReadAll(path)[crcField] ^ 0x01)]);
        }

        using var again = PackReader.Open(fs, path);
        using var stream = again.OpenIndexStream(PackFormat.PageEntrySize * 4);
        var ex = Assert.Throws<BackupPackCorruptException>(() =>
        {
            while (stream.TryNext(out _))
            {
            }
        });
        Assert.That(ex.Message, Does.Contain("IndexCRC"));
        Assert.Throws<BackupPackCorruptException>(() => stream.TryNext(out _), "a retry read on from the buffer as if the index were sound");
    }

    [TestCase(long.MinValue)]
    [TestCase(long.MinValue + 2)]
    [TestCase(-4096)]
    [TestCase(0)]
    [TestCase(PackFormat.HeaderSize)]
    public void AnIndexEntryWhoseOffsetWouldWrap_IsRefused(long forged)
    {
        var fs = NewFs();
        var image = BackupTestImages.Clean;
        var path = WriteFull(fs, image);
        PackFooter footer;
        int victim;
        using (var reader = PackReader.Open(fs, path))
        {
            footer = reader.Footer;
            victim = Array.FindIndex(reader.LoadIndex(), e => e.StoredLength > 0);
        }

        // The forged entry, then the IndexCRC and the footer made to match it: only the range check is left to catch it.
        var pack = fs.ReadAll(path);
        var entryAt = footer.IndexOffset + ((long)victim * PackFormat.PageEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(pack.AsSpan((int)entryAt + 8), forged);
        footer.IndexCrc = Crc32CUtil.Compute(pack.AsSpan((int)footer.IndexOffset, footer.PageCount * PackFormat.PageEntrySize));
        footer.WriteTo(pack.AsSpan(pack.Length - PackFormat.FooterSize));
        fs.Poke(path, 0, pack);

        using var forgedPack = PackReader.Open(fs, path);
        var ex = Assert.Throws<BackupPackCorruptException>(() => forgedPack.LoadIndex());
        Assert.That(ex.Message, Does.Contain("outside the data region"));
    }

    [TestCase(long.MinValue)]
    [TestCase(long.MinValue + 2)]
    [TestCase(-4096)]
    [TestCase(PackFormat.HeaderSize)]
    public void ATailBlockWhoseOffsetWouldWrap_IsRefused(long forged)
    {
        var fs = NewFs();
        var image = BackupTestImages.WithShortTail(BackupTestImages.Unclean);
        var path = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Full, 1), Enumerable.Range(0, image.PageCount),
            withTail: true).PackPath;
        PackFooter footer;
        using (var reader = PackReader.Open(fs, path))
        {
            footer = reader.Footer;
        }

        // The first block entry's DataOffset forged, the TailDirCRC and the footer made to match: only the range check is left to catch it.
        var pack = fs.ReadAll(path);
        var tailDir = (int)footer.TailDirOffset;
        var firstBlock = tailDir + PackFormat.SectionPrefixSize + (footer.TailSegmentCount * PackFormat.TailSegmentEntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(pack.AsSpan(firstBlock), forged);
        footer.TailDirCrc = Crc32CUtil.Compute(pack.AsSpan(tailDir, (int)(footer.SuspectOffset - footer.TailDirOffset)));
        footer.WriteTo(pack.AsSpan(pack.Length - PackFormat.FooterSize));
        fs.Poke(path, 0, pack);

        using var forgedPack = PackReader.Open(fs, path);
        var ex = Assert.Throws<BackupPackCorruptException>(() => forgedPack.LoadTailDirectory());
        Assert.That(ex.Message, Does.Contain("block entry 0 is inconsistent"));
    }

    [TestCase(PackFormat.HeaderSize + PackFormat.FramePrefixSize, 10, 1000, true)]
    [TestCase(PackFormat.HeaderSize + PackFormat.FramePrefixSize - 1, 10, 1000, false)]
    [TestCase(990, 10, 1000, true)]
    [TestCase(991, 10, 1000, false)]
    [TestCase(long.MinValue + 2, 10, 1000, false)]
    [TestCase(long.MaxValue - 10, 10, long.MaxValue, true)]
    [TestCase(long.MaxValue - 4, 10, long.MaxValue, false)]
    [TestCase(long.MaxValue - 4, 10, long.MaxValue - 5, false)]
    public void TheDataRegionCheck_NeverWraps(long offset, long length, long end, bool inside) =>
        Assert.That(PackReader.InDataRegion(offset, length, end), Is.EqualTo(inside));

    [Test]
    public void AnIncrementalThatNamesNoBase_IsRefused()
    {
        var fs = NewFs();
        var image = BackupTestImages.Clean;
        var path = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Incremental, 2, parent: 1, baseId: 0), [0, 1, 2],
            withTail: false).PackPath;

        var ex = Assert.Throws<BackupPackCorruptException>(() => PackReader.Open(fs, path).Dispose());
        Assert.That(ex.Message, Does.Contain("inconsistent identity"));
    }

    [Test]
    public void ATailSegmentLargerThanAnIntCanCount_IsRefused()
    {
        var fs = NewFs();
        var image = BackupTestImages.Clean;
        using var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Full, 1));
        writer.SetImage(image.PageCount, image.CheckpointLsn, 1);

        var ex = Assert.Throws<InvalidOperationException>(() => writer.BeginTailSegment(1, 1, (uint)int.MaxValue + 1));
        Assert.That(ex.Message, Does.Contain("more than a segment can hold"));
    }

    [Test]
    public void AWriterThatCannotAllocateItsSecondBuffer_FreesTheFirst()
    {
        using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = nameof(PackHardeningTests) });
        using var real = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = nameof(PackHardeningTests) });
        var allocator = new SecondAllocationFails(real);
        var fs = NewFs();
        var image = BackupTestImages.Clean;
        using var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Full, 1),
            new PackWriterOptions { Owner = new BufferOwner(allocator, registry.Allocation) });

        Assert.Throws<OutOfMemoryException>(() => writer.SetImage(image.PageCount, image.CheckpointLsn, 1));
        Assert.That(registry.Allocation.Children.Where(c => c.Id.StartsWith("Backup.Pack", StringComparison.Ordinal)), Is.Empty,
            "the first buffer was left allocated with nobody to free it");
    }

    [Test]
    public void AnIndexEntry_IsTheOnDiskSize() => Assert.That(Unsafe.SizeOf<PackPageEntry>(), Is.EqualTo(PackFormat.PageEntrySize));

    /// <summary>The real allocator, except that its second pinned allocation fails.</summary>
    private sealed class SecondAllocationFails(IMemoryAllocator real) : IMemoryAllocator
    {
        private int _calls;

        public MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false, ushort sourceTag = 0) =>
            real.AllocateArray(id, parent, size, zeroed, sourceTag);

        public PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0, ushort sourceTag = 0) =>
            ++_calls == 2 ? throw new OutOfMemoryException("second buffer") : real.AllocatePinned(id, parent, size, zeroed, alignment, sourceTag);

        public LargePinnedMemoryBlock AllocateLargePinned(string id, IResource parent, long size, int alignment, LargeBlockContents contents,
            ushort sourceTag = 0) => real.AllocateLargePinned(id, parent, size, alignment, contents, sourceTag);
    }
}

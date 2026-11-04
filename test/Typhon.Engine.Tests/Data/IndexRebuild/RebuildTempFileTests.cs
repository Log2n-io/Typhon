using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The sorted index rebuild's temp file (#1588, RB-10): runs round-trip at every stride, every corruption of a block is detected with its own reason
/// before a record of it is used, and the file leaves nothing behind.
/// </summary>
[TestFixture]
[VerifiesRule("RB-10")]
internal sealed unsafe class RebuildTempFileTests
{
    private string _dir;
    private readonly List<IntPtr> _native = [];

    [SetUp]
    public void SetUp() => _dir = Path.Combine(TestStorage.ForFixture(nameof(RebuildTempFileTests)), TestContext.CurrentContext.Test.Name);

    [TearDown]
    public void TearDown()
    {
        foreach (var p in _native)
        {
            NativeMemory.Free((void*)p);
        }

        _native.Clear();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [TestCase(1)]
    [TestCase(5_000)]
    [TestCase(30_000)]
    public void ARun_RoundTrips_AtEveryStride(int count)
    {
        RoundTrip<int>(count, i => i * 7);
        RoundTrip<long>(count, i => (long)i << 33);
        RoundTrip<String64>(count, i => S64(i));
    }

    [Test]
    public void AFlippedPayloadByte_IsDetected()
        => AssertCorrupt(file => Flip(file, RebuildTempFile.BlockSize + RebuildTempFile.HeaderSize + 100), "payload CRC");

    [Test]
    public void AWrongVersion_IsDetected() => AssertCorrupt(file => RewriteHeader(file, 1, version: 2), "version");

    [Test]
    public void AFlippedHeaderByte_IsDetected() => AssertCorrupt(file => Flip(file, RebuildTempFile.BlockSize + 13), "header CRC");

    [Test]
    public void ABadMagic_IsDetected() => AssertCorrupt(file => Flip(file, RebuildTempFile.BlockSize), "magic");

    [Test]
    public void SwappedBlocks_AreDetected() =>
        AssertCorrupt(file =>
        {
            var a = new byte[RebuildTempFile.BlockSize];
            var b = new byte[RebuildTempFile.BlockSize];
            file.Read(RebuildTempFile.BlockSize, a);
            file.Read(2L * RebuildTempFile.BlockSize, b);
            file.PatchForTest(RebuildTempFile.BlockSize, b);
            file.PatchForTest(2L * RebuildTempFile.BlockSize, a);
        }, "sequence");

    [Test]
    public void AWrongOwner_IsDetected() => AssertCorrupt(file => RewriteHeader(file, 1, ownerDelta: 1), "owner");

    [Test]
    public void AWrongStride_IsDetected() => AssertCorrupt(file => RewriteHeader(file, 1, strideDelta: 4), "stride");

    [Test]
    public void ACountAboveCapacity_IsDetected() => AssertCorrupt(file => RewriteHeader(file, 1, count: RebuildTempFile.Capacity(12) + 1), "count");

    [Test]
    public void AWrongKind_IsDetected() => AssertCorrupt(file => RewriteHeader(file, 1, kind: (ushort)RebuildBlockKind.Pairs), "kind");

    [Test]
    public void ATruncatedFile_IsAShortRead()
    {
        using var file = RebuildTempFile.Create(_dir, "t.tmp");
        var run = WriteRun<long>(file, 20_000, i => i);
        var longer = new RunDescriptor(run.Offset, run.Blocks + 1, run.Owner, run.Entries + RebuildTempFile.Capacity(12));
        var ex = Assert.Throws<IndexRebuildTempCorruptException>(() => Drain<long>(file, longer, 1));
        Assert.That(ex.Check, Does.Contain("short read"));
    }

    [Test]
    public void ARunMissingEntries_IsDetectedAtItsEnd()
    {
        using var file = RebuildTempFile.Create(_dir, "t.tmp");
        var run = WriteRun<long>(file, 20_000, i => i);
        var claimed = new RunDescriptor(run.Offset, run.Blocks, run.Owner, run.Entries + 1);
        var ex = Assert.Throws<IndexRebuildTempCorruptException>(() => Drain<long>(file, claimed, 2));
        Assert.That(ex.Check, Does.Contain("ends after"));
    }

    [Test]
    public void TheFile_IsGoneOnceDisposed_AndTheSweepDeletesLeftovers()
    {
        var file = RebuildTempFile.Create(_dir, "ix-1.tmp");
        WriteRun<int>(file, 100, i => i);
        file.Dispose();
        Assert.That(File.Exists(Path.Combine(_dir, "ix-1.tmp")), Is.False, "delete-on-close (Windows) or unlinked at open (elsewhere)");

        File.WriteAllBytes(Path.Combine(_dir, "ix-2.tmp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "keep.bin"), [1]);
        Assert.Multiple(() =>
        {
            Assert.That(RebuildTempFile.Sweep(_dir), Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(_dir, "ix-2.tmp")), Is.False);
            Assert.That(File.Exists(Path.Combine(_dir, "keep.bin")), Is.True, "only temp files are swept");
        });
    }

    [Test]
    public void APieceWiseRun_ReadsAsOne()
    {
        using var file = RebuildTempFile.Create(_dir, "t.tmp");
        var capacity = RebuildTempFile.Capacity(12);
        var total = 3 * capacity + 17;
        var offset = file.Reserve(RebuildTempFile.BlocksFor(total, 12));
        var records = Native<StagedEntry<long>>(total);
        for (var i = 0; i < total; i++)
        {
            records[i] = new StagedEntry<long>(i, i);
        }

        var staging = new byte[RebuildTempFile.BlockSize];
        file.WriteBlocks(offset, 0, new ReadOnlySpan<byte>(records, 2 * capacity * 12), 12, 2 * capacity, RebuildBlockKind.Run, 9, staging);
        file.WriteBlocks(offset, 2, new ReadOnlySpan<byte>(records + 2 * capacity, (capacity + 17) * 12), 12, capacity + 17, RebuildBlockKind.Run, 9,
            staging);

        var got = Drain<long>(file, new RunDescriptor(offset, 4, 9, total), 3);
        Assert.That(got, Has.Count.EqualTo(total));
        for (var i = 0; i < total; i++)
        {
            Assert.That(got[i].Location, Is.EqualTo(i));
        }
    }

    private void RoundTrip<TKey>(int count, Func<int, TKey> key) where TKey : unmanaged
    {
        using var file = RebuildTempFile.Create(_dir, $"rt-{typeof(TKey).Name}-{count}.tmp");
        var first = WriteRun(file, count, key, owner: 3);
        var second = WriteRun(file, count / 2 + 1, key, owner: 4);
        foreach (var (run, n) in new[] { (first, count), (second, count / 2 + 1) })
        {
            var got = Drain<TKey>(file, run, 2);
            Assert.That(got, Has.Count.EqualTo(n), $"{typeof(TKey).Name} × {count}");
            for (var i = 0; i < n; i++)
            {
                Assert.That(got[i].Location, Is.EqualTo(i));
                Assert.That(StagedOrder<TKey>.CompareKeys(got[i], new StagedEntry<TKey>(key(i), i)), Is.Zero);
            }
        }
    }

    private RunDescriptor WriteRun<TKey>(RebuildTempFile file, int count, Func<int, TKey> key, int owner = 1) where TKey : unmanaged
    {
        var records = Native<StagedEntry<TKey>>(count);
        for (var i = 0; i < count; i++)
        {
            records[i] = new StagedEntry<TKey>(key(i), i);
        }

        var stride = StagedOrder<TKey>.Stride;
        return file.Write(new ReadOnlySpan<byte>(records, count * stride), stride, count, RebuildBlockKind.Run, owner, new byte[2 * RebuildTempFile.BlockSize]);
    }

    private List<StagedEntry<TKey>> Drain<TKey>(RebuildTempFile file, RunDescriptor run, int bufferBlocks) where TKey : unmanaged
    {
        var buffer = (byte*)Native<byte>(bufferBlocks * RebuildTempFile.BlockSize);
        var cursor = new RunCursor<TKey>(file, in run, buffer, bufferBlocks * RebuildTempFile.BlockSize);
        var got = new List<StagedEntry<TKey>>();
        while (cursor.HasCurrent)
        {
            got.Add(cursor.Current);
            cursor.Advance();
        }

        return got;
    }

    /// <summary>A three-block run of long entries, corrupted in its second block, must fail with <paramref name="reason"/> before the second block's first
    /// entry is returned.</summary>
    private void AssertCorrupt(Action<RebuildTempFile> corrupt, string reason)
    {
        using var file = RebuildTempFile.Create(_dir, "c.tmp");
        var capacity = RebuildTempFile.Capacity(12);
        var run = WriteRun<long>(file, 3 * capacity, i => i);
        Assert.That(run.Blocks, Is.EqualTo(3), "premise");
        corrupt(file);

        var buffer = (byte*)Native<byte>(RebuildTempFile.BlockSize);
        var cursor = new RunCursor<long>(file, in run, buffer, RebuildTempFile.BlockSize);
        var served = 0;
        var ex = Assert.Throws<IndexRebuildTempCorruptException>(() =>
        {
            while (cursor.HasCurrent)
            {
                served++;
                cursor.Advance();
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(ex.Check, Does.Contain(reason));
            Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.IndexRebuildTempCorrupt));
            Assert.That(served, Is.EqualTo(capacity), "only the first, intact block was served");
        });
    }

    private static void Flip(RebuildTempFile file, long offset)
    {
        var b = new byte[1];
        var block = new byte[RebuildTempFile.BlockSize];
        file.Read(offset - offset % RebuildTempFile.BlockSize, block);
        b[0] = (byte)(block[offset % RebuildTempFile.BlockSize] ^ 0x5A);
        file.PatchForTest(offset, b);
    }

    /// <summary>Rewrites block <paramref name="sequence"/>'s header with a field changed and a VALID header CRC, so the field's own check must catch
    /// it.</summary>
    private static void RewriteHeader(RebuildTempFile file, int sequence, int ownerDelta = 0, int strideDelta = 0, int count = -1, ushort kind = 0,
        ushort version = 0)
    {
        var block = new byte[RebuildTempFile.BlockSize];
        var offset = (long)sequence * RebuildTempFile.BlockSize;
        file.Read(offset, block);
        var span = block.AsSpan();
        if (version != 0)
        {
            MemoryMarshal.Write(span.Slice(4), in version);
        }

        if (kind != 0)
        {
            MemoryMarshal.Write(span.Slice(6), in kind);
        }

        MemoryMarshal.Write(span.Slice(8), MemoryMarshal.Read<int>(span.Slice(8)) + ownerDelta);
        if (count >= 0)
        {
            MemoryMarshal.Write(span.Slice(16), in count);
        }

        MemoryMarshal.Write(span.Slice(20), MemoryMarshal.Read<int>(span.Slice(20)) + strideDelta);
        MemoryMarshal.Write(span.Slice(28), Crc32CUtil.Compute(span.Slice(0, 28)));
        file.PatchForTest(offset, block.AsSpan(0, RebuildTempFile.HeaderSize));
    }

    private T* Native<T>(int count) where T : unmanaged
    {
        var p = (T*)NativeMemory.AllocZeroed((nuint)Math.Max(1, count), (nuint)sizeof(T));
        _native.Add((IntPtr)p);
        return p;
    }

    private static String64 S64(int i)
    {
        var bytes = new byte[64];
        var text = System.Text.Encoding.ASCII.GetBytes($"k{i:D8}");
        text.CopyTo(bytes, 0);
        fixed (byte* p = bytes)
        {
            return new String64(p, 64);
        }
    }
}

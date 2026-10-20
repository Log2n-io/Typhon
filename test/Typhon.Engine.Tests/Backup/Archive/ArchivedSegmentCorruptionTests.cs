using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// T7 for the archive (BKP-10: every byte of a <c>.twal</c> is under a CRC that verify checks): flip each byte outside the data region and every 8th
/// inside it, and the full check refuses every one; a decode either refuses or hands back the exact bytes, never wrong ones.
/// </summary>
[TestFixture]
internal sealed class ArchivedSegmentCorruptionTests
{
    private const string Root = "/s";
    private const string Marker = "BKP-10: a damaged archived segment";

    private InMemorySparseFileSystem _fs;
    private string _path;
    private byte[] _pristine;
    private byte[] _segment;

    [OneTimeSetUp]
    public void Build()
    {
        _fs = new InMemorySparseFileSystem();
        _fs.CreateDirectory("/");
        _fs.CreateDirectory(Root);
        _segment = ArchiveTestKit.Segment(9, 2_000, 16 * 1024);
        ArchiveTestKit.Publish(_fs, Root, _segment, 2_100, ArchiveTestKit.Samples(2_000, 2_100, 20));
        _path = new WalArchivePaths(Root).Segment(ArchiveTestKit.Timeline, 9);
        _pristine = _fs.ReadAll(_path);
        Assert.That(ArchiveTestKit.FullCheck(_fs, _path, _segment), Is.Null, "premise: the pristine file is sound");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-10")]
    public void EveryFlippedByte_IsRefusedByTheFullCheck_AndADecodeNeverReturnsWrongBytes()
    {
        var flips = 0;
        foreach (var offset in Offsets())
        {
            Flip(offset, () =>
            {
                Assert.That(ArchiveTestKit.FullCheck(_fs, _path), Is.Not.Null, $"{Marker} at byte {offset} passed every check");
                AssertDecodeExactOrRefused(offset, DecodeChecked);
            });
            flips++;
        }

        Assert.That(flips, Is.GreaterThan(500));
    }

    /// <summary>A decode that skips the raw CRC: some flip in the data region decodes to other bytes of the same length. The verifier must see it.</summary>
    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("BKP-10")]
    public void Mutant_ADecodeWithoutTheRawCrc_IsReported()
    {
        RuleMutants.AssertDetects("BKP-10", Marker, () =>
        {
            foreach (var offset in Offsets())
            {
                Flip(offset, () => AssertDecodeExactOrRefused(offset, DecodeWithoutRawCrc));
            }
        });
    }

    private IEnumerable<long> Offsets()
    {
        using var reader = ArchivedSegmentReader.Open(_fs, _path);
        var dataStart = (long)ArchiveFormat.HeaderSize;
        var dataEnd = reader.Footer.BlockDirOffset;
        for (long i = 0; i < _pristine.Length; i++)
        {
            if (i < dataStart || i >= dataEnd || i % 8 == 0)
            {
                yield return i;
            }
        }
    }

    private void Flip(long offset, Action check)
    {
        _fs.Poke(_path, offset, [(byte)(_pristine[offset] ^ 0x5A)]);
        try
        {
            check();
        }
        finally
        {
            _fs.Poke(_path, offset, [_pristine[offset]]);
        }
    }

    private void AssertDecodeExactOrRefused(long offset, Func<byte[]> decode)
    {
        byte[] raw;
        try
        {
            raw = decode();
        }
        catch (BackupException)
        {
            return;
        }

        Assert.That(raw.AsSpan().SequenceEqual(_segment), Is.True, $"{Marker} at byte {offset} decoded to wrong bytes");
    }

    private byte[] DecodeChecked()
    {
        using var reader = ArchivedSegmentReader.Open(_fs, _path);
        var raw = new byte[reader.Footer.ArchiveLength];
        reader.Decode(raw);
        return raw;
    }

    /// <summary>The mutant: the block walk of <see cref="ArchivedSegmentReader.Decode"/> with its RawCrc and WAL-header checks left out.</summary>
    private byte[] DecodeWithoutRawCrc()
    {
        using var reader = ArchivedSegmentReader.Open(_fs, _path);
        var raw = new byte[reader.Footer.ArchiveLength];
        long offset = 0;
        using var file = _fs.OpenRead(_path);
        foreach (var block in reader.ReadBlockDirectory())
        {
            var stored = new byte[block.StoredLength];
            file.ReadExactly(stored, block.DataOffset);
            if (!TailBlockCodec.Decode(stored, raw.AsSpan((int)offset, block.RawLength)))
            {
                throw new BackupPackCorruptException(_path, "Data", -1, "does not decode");
            }

            offset += block.RawLength;
        }

        return raw;
    }
}

using NUnit.Framework;
using System;
using System.Text;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="Crc32CUtil"/> CRC32C implementation.
/// Verifies against known test vectors and edge cases.
/// </summary>
[TestFixture]
public class Crc32CUtilTests
{
    #region Known Test Vectors

    [Test]
    public void Compute_EmptySpan_ReturnsZero()
    {
        var result = Crc32CUtil.Compute(ReadOnlySpan<byte>.Empty);

        Assert.That(result, Is.EqualTo(0x00000000u));
    }

    [Test]
    public void Compute_123456789_ReturnsCanonicalCrc32C()
    {
        // The canonical CRC32C test vector: ASCII "123456789" → 0xE3069283
        // This is the universally published reference for the Castagnoli polynomial.
        var data = Encoding.ASCII.GetBytes("123456789");

        var result = Crc32CUtil.Compute(data);

        Assert.That(result, Is.EqualTo(0xE3069283u));
    }

    [Test]
    public void Compute_AllZeros32Bytes_Deterministic()
    {
        var data = new byte[32];

        var result1 = Crc32CUtil.Compute(data);
        var result2 = Crc32CUtil.Compute(data);

        Assert.That(result1, Is.EqualTo(result2));
        Assert.That(result1, Is.Not.EqualTo(0u));
    }

    [Test]
    public void Compute_AllOnes32Bytes_Deterministic()
    {
        var data = new byte[32];
        Array.Fill(data, (byte)0xFF);

        var result1 = Crc32CUtil.Compute(data);
        var result2 = Crc32CUtil.Compute(data);

        Assert.That(result1, Is.EqualTo(result2));
        Assert.That(result1, Is.Not.EqualTo(0u));
    }

    [Test]
    public void Compute_SingleByte_NonZero()
    {
        var result = Crc32CUtil.Compute(new byte[] { 0x00 });

        Assert.That(result, Is.Not.EqualTo(0u));
    }

    [Test]
    public void Compute_IscsiTestVector_ReturnsKnownCrc()
    {
        // RFC 3720 section B.4: 48 bytes of specific iSCSI data → 0xD9963A56
        var data = new byte[]
        {
            0x01, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00,
            0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x18,
            0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        var result = Crc32CUtil.Compute(data);

        Assert.That(result, Is.EqualTo(0xD9963A56u));
    }

    #endregion

    #region ComputeSkipping

    [Test]
    public void ComputeSkipping_SkipsSpecifiedRegion()
    {
        // Create data with a non-zero region that should be skipped
        var data = new byte[48];
        for (int i = 0; i < 48; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Compute with skipping bytes [36..40) (the CRC field position in WalRecordHeader)
        var skipOffset = 36;
        var skipLength = 4;
        var crcWithSkip = Crc32CUtil.ComputeSkipping(data, skipOffset, skipLength);

        // Compute reference: same data but with the skip region zeroed
        var dataWithZeros = (byte[])data.Clone();
        Array.Clear(dataWithZeros, skipOffset, skipLength);
        var crcReference = Crc32CUtil.Compute(dataWithZeros);

        Assert.That(crcWithSkip, Is.EqualTo(crcReference));
    }

    [Test]
    public void ComputeSkipping_SkipAtStart_MatchesZeroedReference()
    {
        var data = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            data[i] = (byte)(i + 1);
        }

        var crcWithSkip = Crc32CUtil.ComputeSkipping(data, 0, 4);

        var dataWithZeros = (byte[])data.Clone();
        Array.Clear(dataWithZeros, 0, 4);
        var crcReference = Crc32CUtil.Compute(dataWithZeros);

        Assert.That(crcWithSkip, Is.EqualTo(crcReference));
    }

    [Test]
    public void ComputeSkipping_SkipAtEnd_MatchesZeroedReference()
    {
        var data = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            data[i] = (byte)(i + 1);
        }

        var crcWithSkip = Crc32CUtil.ComputeSkipping(data, 28, 4);

        var dataWithZeros = (byte[])data.Clone();
        Array.Clear(dataWithZeros, 28, 4);
        var crcReference = Crc32CUtil.Compute(dataWithZeros);

        Assert.That(crcWithSkip, Is.EqualTo(crcReference));
    }

    [Test]
    public void ComputeSkipping_EntireSpan_MatchesAllZeros()
    {
        var data = new byte[16];
        Array.Fill(data, (byte)0xAA);

        var crcWithSkip = Crc32CUtil.ComputeSkipping(data, 0, 16);

        var zeros = new byte[16];
        var crcReference = Crc32CUtil.Compute(zeros);

        Assert.That(crcWithSkip, Is.EqualTo(crcReference));
    }

    [Test]
    public void ComputeSkipping_PageCrcUseCase_SkipsChecksumField()
    {
        // Simulate an 8192-byte page with random data, skipping PageChecksum at offset 8 (4 bytes)
        var page = new byte[8192];
        var rng = new Random(99);
        rng.NextBytes(page);

        var crcSkipping = Crc32CUtil.ComputeSkipping(
            page,
            PageBaseHeader.PageChecksumOffset,
            PageBaseHeader.PageChecksumSize);

        // Reference: same page with the checksum field zeroed
        var pageZeroed = (byte[])page.Clone();
        Array.Clear(pageZeroed, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        var crcReference = Crc32CUtil.Compute(pageZeroed);

        Assert.That(crcSkipping, Is.EqualTo(crcReference));
        Assert.That(crcSkipping, Is.Not.EqualTo(0u));
    }

    #endregion

    #region Edge Cases

    [Test]
    public void Compute_LargeBuffer_Deterministic()
    {
        // 8KB page-sized buffer (typical FPI size)
        var data = new byte[8192];
        var rng = new Random(42);
        rng.NextBytes(data);

        var crc1 = Crc32CUtil.Compute(data);
        var crc2 = Crc32CUtil.Compute(data);

        Assert.That(crc1, Is.EqualTo(crc2));
        Assert.That(crc1, Is.Not.EqualTo(0u));
    }

    [Test]
    public void Compute_UnalignedLength_Works()
    {
        // Test lengths that are not multiples of 8 (exercises the tail byte loop)
        for (int len = 1; len <= 17; len++)
        {
            var data = new byte[len];
            Array.Fill(data, (byte)0x42);

            var crc = Crc32CUtil.Compute(data);

            Assert.That(crc, Is.Not.EqualTo(0u), $"Length {len} produced zero CRC");
        }
    }

    [Test]
    public void Compute_DifferentDataProducesDifferentCrc()
    {
        var data1 = Encoding.UTF8.GetBytes("Hello, World!");
        var data2 = Encoding.UTF8.GetBytes("Hello, World?");

        var crc1 = Crc32CUtil.Compute(data1);
        var crc2 = Crc32CUtil.Compute(data2);

        Assert.That(crc1, Is.Not.EqualTo(crc2));
    }

    [Test]
    public void Compute_WalRecordHeaderSize_Works()
    {
        // Verify CRC works with typical WAL header sizes
        var header = new byte[WalRecordHeader.SizeInBytes]; // 48 bytes
        for (int i = 0; i < header.Length; i++)
        {
            header[i] = (byte)i;
        }

        var crc = Crc32CUtil.Compute(header);

        Assert.That(crc, Is.Not.EqualTo(0u));
    }

    #endregion
}

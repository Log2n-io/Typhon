using System;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The wire primitives of 03-wire-protocol § 2: every encoding at its boundaries, and every way a decoder must refuse input rather than read past it.
/// </summary>
[TestFixture]
public class WireTests
{
    [TestCase(0u, new byte[] { 0x00 })]
    [TestCase(127u, new byte[] { 0x7F })]
    [TestCase(128u, new byte[] { 0x80, 0x01 })]
    [TestCase(16383u, new byte[] { 0xFF, 0x7F })]
    [TestCase(16384u, new byte[] { 0x80, 0x80, 0x01 })]
    [TestCase(uint.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void VaruIsMinimalLeb128(uint value, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[8];
        var w = new WireWriter(buffer);
        w.WriteVaru(value);
        var written = w.Written.ToArray();
        var r = new WireReader(written);
        var decoded = r.ReadVaru();

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(expected));
            Assert.That(WireWriter.VaruSize(value), Is.EqualTo(expected.Length));
            Assert.That(decoded, Is.EqualTo(value));
        });
    }

    [TestCase(0, new byte[] { 0x00 })]
    [TestCase(-1, new byte[] { 0x01 })]
    [TestCase(1, new byte[] { 0x02 })]
    [TestCase(-64, new byte[] { 0x7F })]
    [TestCase(64, new byte[] { 0x80, 0x01 })]
    [TestCase(int.MinValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void VariIsZigzag(int value, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[8];
        var w = new WireWriter(buffer);
        w.WriteVari(value);
        var written = w.Written.ToArray();
        var r = new WireReader(written);
        var decoded = r.ReadVari();

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(expected));
            Assert.That(decoded, Is.EqualTo(value));
        });
    }

    /// <summary>A varint whose fifth byte carries bits above 31 does not fit 32 bits; one longer than five bytes is not a varint at all.</summary>
    [TestCase(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 })]
    [TestCase(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 })]
    [TestCase(new byte[] { 0x80 })]
    public void AMalformedVaruIsRefused(byte[] bytes) => AssertMalformed(bytes, r => r.ReadVaru());

    [Test]
    public void AnOverlongVaruIsAcceptedLeniently()
    {
        var r = new WireReader(new byte[] { 0x80, 0x00 });
        Assert.That(r.ReadVaru(), Is.Zero);
    }

    [Test]
    public void I24SignExtendsFromBit23()
    {
        Span<byte> buffer = stackalloc byte[3];
        var w = new WireWriter(buffer);
        w.WriteU24(unchecked((uint)-2));
        var written = w.Written.ToArray();
        var r = new WireReader(written);
        var decoded = r.ReadI24();

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(new byte[] { 0xFE, 0xFF, 0xFF }));
            Assert.That(decoded, Is.EqualTo(-2));
        });
    }

    [Test]
    public void AStringOverItsCapIsRefusedOnBothSides()
    {
        var buffer = new byte[64];
        Assert.Throws<ArgumentException>(() =>
        {
            var w = new WireWriter(buffer);
            w.WriteStr("12345", 4);
        });

        AssertMalformed([0x05, (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5'], r => r.ReadStr(4));
    }

    [Test]
    public void InvalidUtf8IsMalformed() => AssertMalformed([0x02, 0xC3, 0x28], r => r.ReadStrUtf8(8));

    [Test]
    public void AShortReadIsMalformedNotAnIndexFault() => AssertMalformed([0x01, 0x02], r => r.ReadU32());

    /// <summary>The length prefix is patched minimal once the content is known, so a 127-byte and a 128-byte block differ by exactly one byte.</summary>
    [TestCase(0)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(20000)]
    public void LengthPrefixesArePatchedMinimal(int contentLength)
    {
        var buffer = new byte[contentLength + 8];
        var w = new WireWriter(buffer);
        var mark = w.BeginLengthPrefixed();
        for (var i = 0; i < contentLength; i++)
        {
            w.WriteU8((byte)i);
        }

        w.EndLengthPrefixed(mark);
        var position = w.Position;
        var r = new WireReader(w.Written);
        var length = r.ReadVaru();
        var lastByteOk = contentLength == 0 || r.ReadBytes(contentLength)[contentLength - 1] == (byte)(contentLength - 1);

        Assert.Multiple(() =>
        {
            Assert.That(position, Is.EqualTo(WireWriter.VaruSize((uint)contentLength) + contentLength));
            Assert.That(length, Is.EqualTo((uint)contentLength));
            Assert.That(lastByteOk, Is.True);
        });
    }

    [Test]
    public void AKickReasonIsTruncatedBetweenCodePoints()
    {
        var reason = new string('a', 122) + "€";

        Assert.Multiple(() =>
        {
            Assert.That(KickMessage.TruncateUtf8(reason, 123), Is.EqualTo(new string('a', 122)), "the 3-byte euro sign does not fit in the last byte");
            Assert.That(KickMessage.TruncateUtf8("😀x", 4), Is.EqualTo("😀"), "a surrogate pair is kept whole");
        });
    }

    [Test]
    public void ByeRefusesACodeABrowserWouldReject()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CloseCodes.IsValidClientCode(1000), Is.True);
            Assert.That(CloseCodes.IsValidClientCode(4100), Is.True);
            Assert.That(CloseCodes.IsValidClientCode(1001), Is.False);
            Assert.That(CloseCodes.IsValidClientCode(3000), Is.False, "3000–3999 is registered, not private use");
        });
    }

    private static void AssertMalformed(byte[] bytes, Func<WireReaderBox, object> read)
    {
        var ex = Assert.Throws<WireFormatException>(() => read(new WireReaderBox(bytes)));
        Assert.That(ex.CloseCode, Is.EqualTo(CloseCodes.MalformedPayload));
    }

    /// <summary>A class wrapper so a lambda can drive a ref struct reader.</summary>
    internal sealed class WireReaderBox(byte[] bytes)
    {
        private int _position;

        public uint ReadVaru() => Run((ref WireReader r) => r.ReadVaru());

        public uint ReadU32() => Run((ref WireReader r) => r.ReadU32());

        public string ReadStr(int max) => Run((ref WireReader r) => r.ReadStr(max));

        public int ReadStrUtf8(int max) => Run((ref WireReader r) => r.ReadStrUtf8(max).Length);

        private delegate T Reader<out T>(ref WireReader r);

        private T Run<T>(Reader<T> action)
        {
            var r = new WireReader(bytes);
            r.Skip(_position);
            var result = action(ref r);
            _position = r.Position;
            return result;
        }
    }
}

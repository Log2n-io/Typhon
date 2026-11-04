using System.Text;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Round-trip and boundary tests for the UTF-8 string encodings: the inline <see cref="String64"/>, and the encoding a <see cref="String1024"/> text gets
/// before it is stored out of line (<see cref="TextChunks.Encode"/>, #1094). The String1024 cases keep the coverage of the old inline setter's bugs — a
/// 63-byte destination cap copy-pasted from String64, and an unbounded stackalloc on the truncation path — against the encoder that replaced it.
/// </summary>
[TestFixture]
internal class String64Tests
{
    [Test]
    public void String64_Roundtrip_ShortString()
    {
        var s = new String64 { AsString = "hello" };
        Assert.That(s.AsString, Is.EqualTo("hello"));
    }

    [Test]
    public void String64_Roundtrip_63Bytes_ExactFit()
    {
        var value = new string('a', 63);
        var s = new String64 { AsString = value };
        Assert.That(s.AsString, Is.EqualTo(value));
    }

    [Test]
    public void String64_Truncates_64BytesAndBeyond()
    {
        var s = new String64 { AsString = new string('a', 64) };
        Assert.That(s.AsString, Is.EqualTo(new string('a', 63)));
    }

    private static string RoundTrip1024(string value)
    {
        var buffer = new byte[String1024.MaxUtf8Bytes];
        var n = TextChunks.Encode(value, buffer);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    [Test]
    public void String1024_Roundtrip_ShortString() => Assert.That(RoundTrip1024("hello"), Is.EqualTo("hello"));

    // The old setter's regression range: 64–1023 UTF-8 bytes fits but exceeded its erroneous 63-byte GetBytes cap.
    [TestCase(64)]
    [TestCase(100)]
    [TestCase(500)]
    [TestCase(1023)]
    public void String1024_Roundtrip_MidRangeAscii(int length)
    {
        var value = new string('x', length);
        Assert.That(RoundTrip1024(value), Is.EqualTo(value));
    }

    [Test]
    public void String1024_Roundtrip_MultiByteUtf8_MidRange()
    {
        var value = new string('é', 400); // 800 UTF-8 bytes — in the regression range, multi-byte encoding
        Assert.That(Encoding.UTF8.GetByteCount(value), Is.EqualTo(800));
        Assert.That(RoundTrip1024(value), Is.EqualTo(value));
    }

    [Test]
    public void String1024_Truncates_1024Bytes() => Assert.That(RoundTrip1024(new string('a', 1024)), Is.EqualTo(new string('a', 1023)));

    [Test]
    public void String1024_Truncates_OnACodePointBoundary()
    {
        // 1 022 ASCII bytes and a 2-byte 'é' that straddles the limit: the 'é' is dropped whole, by both encoders.
        var value = new string('a', 1022) + "é";
        Assert.That(RoundTrip1024(value), Is.EqualTo(new string('a', 1022)));
        Assert.That(TextChunks.FitUtf8(Encoding.UTF8.GetBytes(value)), Is.EqualTo(1022));
        Assert.That(TextChunks.FitUtf8("ab\0cd"u8), Is.EqualTo(2), "a NUL ends the text");
    }

    [Test]
    public void String1024_LargeString_TruncatesWithoutStackOverflow()
    {
        // The old inline setter's truncation path did stackalloc byte[sizeRequired] — a 5 MB assignment would blow the stack.
        Assert.That(RoundTrip1024(new string('x', 5_000_000)), Is.EqualTo(new string('x', 1023)));
    }
}

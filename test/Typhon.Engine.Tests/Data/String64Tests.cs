using System.Text;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Round-trip and boundary tests for the fixed-size inline UTF-8 string structs (<see cref="String64"/>, <see cref="String1024"/>).
/// Regression coverage for the String1024 setter bug where the fast path passed a 63-byte destination cap (copy-pasted from String64),
/// making any 64–1023-byte assignment throw, and for the unbounded stackalloc on the truncation path.
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

    [Test]
    public void String1024_Roundtrip_ShortString()
    {
        var s = new String1024 { AsString = "hello" };
        Assert.That(s.AsString, Is.EqualTo("hello"));
    }

    // The regression range: 64–1023 UTF-8 bytes fits the buffer but exceeded the erroneous 63-byte GetBytes cap.
    [TestCase(64)]
    [TestCase(100)]
    [TestCase(500)]
    [TestCase(1023)]
    public void String1024_Roundtrip_MidRangeAscii(int length)
    {
        var value = new string('x', length);
        var s = new String1024 { AsString = value };
        Assert.That(s.AsString, Is.EqualTo(value));
    }

    [Test]
    public void String1024_Roundtrip_MultiByteUtf8_MidRange()
    {
        var value = new string('é', 400); // 800 UTF-8 bytes — in the regression range, multi-byte encoding
        Assert.That(Encoding.UTF8.GetByteCount(value), Is.EqualTo(800));
        var s = new String1024 { AsString = value };
        Assert.That(s.AsString, Is.EqualTo(value));
    }

    [Test]
    public void String1024_Truncates_1024Bytes()
    {
        var s = new String1024 { AsString = new string('a', 1024) };
        Assert.That(s.AsString, Is.EqualTo(new string('a', 1023)));
    }

    [Test]
    public void String1024_LargeString_TruncatesWithoutStackOverflow()
    {
        // Pre-fix the truncation path did stackalloc byte[sizeRequired] — a 5 MB assignment would blow the stack.
        var s = new String1024 { AsString = new string('x', 5_000_000) };
        Assert.That(s.AsString, Is.EqualTo(new string('x', 1023)));
    }
}

using NUnit.Framework;
using System;
using System.Text;
using Typhon.Protocol;

namespace Typhon.Protocol.Tests;

/// <summary>
/// The inline text a message struct carries a <c>str</c> in: it holds bytes, it refuses what will not fit, and it never
/// silently shortens anything.
/// </summary>
[TestFixture]
public sealed class Utf8TextTests
{
    [Test]
    public void ItHoldsTheUtf8OfWhatItWasGiven()
    {
        const string Spoken = "Utinni! ça va, señor? こんにちは";
        var text = Utf8Text256.From(Spoken);

        Assert.Multiple(() =>
        {
            Assert.That(text.Utf8.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes(Spoken)));
            Assert.That(text.Length, Is.EqualTo(Encoding.UTF8.GetByteCount(Spoken)), "the length is BYTES, not characters");
            Assert.That(text.Length, Is.GreaterThan(Spoken.Length), "the sample has multi-byte characters, or it proves nothing");
            Assert.That(text.ToString(), Is.EqualTo(Spoken));
            Assert.That(text.IsEmpty, Is.False);
        });
    }

    [Test]
    public void ADefaultIsEmptyRatherThanInvalid()
    {
        var text = default(Utf8Text256);

        Assert.Multiple(() =>
        {
            Assert.That(text.IsEmpty, Is.True);
            Assert.That(text.Length, Is.Zero);
            Assert.That(text.Utf8.IsEmpty, Is.True);
            Assert.That(text.ToString(), Is.Empty);
            Assert.That(Utf8Text256.From(null).IsEmpty, Is.True);
            Assert.That(Utf8Text256.From(string.Empty).IsEmpty, Is.True);
        });
    }

    [Test]
    public void ItHoldsExactlyItsCapacity()
    {
        var text = Utf8Text256.From(new string('x', Utf8Text256.Capacity));
        Assert.That(text.Length, Is.EqualTo(Utf8Text256.Capacity), "the cap is inclusive");
    }

    /// <summary>
    /// One byte past the capacity is refused, not truncated.
    /// </summary>
    /// <remarks>
    /// <b>Truncating is the behaviour this type exists to prevent.</b> Cutting UTF-8 to fit can split a character, and a
    /// shortened message reaches its reader as a shorter message rather than as an error — so the caller that meant to
    /// shorten it never finds out it did not choose to.
    /// </remarks>
    [Test]
    public void OneBytePastTheCapacityIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => Utf8Text256.From(new string('x', Utf8Text256.Capacity + 1)),
                Throws.ArgumentException.With.Message.Contains(Utf8Text256.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Counted in BYTES: 128 two-byte characters are 256 bytes and fit exactly; 129 do not, though the string is
            // less than half the capacity in characters.
            Assert.That(() => Utf8Text256.From(new string('é', 128)), Throws.Nothing);
            Assert.That(() => Utf8Text256.From(new string('é', 129)), Throws.ArgumentException);
            Assert.That(() => Utf8Text256.FromUtf8(new byte[Utf8Text256.Capacity + 1]), Throws.ArgumentException);
        });
    }

    [Test]
    public void TwoValuesAreEqualWhenTheirBytesAre()
    {
        var a = Utf8Text256.From("ça va");
        var b = Utf8Text256.FromUtf8(Encoding.UTF8.GetBytes("ça va"));
        var c = Utf8Text256.From("ca va");

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a, Is.Not.EqualTo(c), "the cedilla is two bytes and 'c' is one: these are different strings");
            Assert.That(a != c, Is.True);
        });
    }

    /// <summary>An unpaired surrogate is refused, not quietly turned into a replacement character.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Encoding.UTF8"/> carries a REPLACEMENT fallback: it encodes a lone surrogate as the three bytes of
    /// U+FFFD and reports success. So the type that documents "it refuses rather than truncating" was committing, one
    /// level down, exactly the silent corruption that sentence exists to forbid — a message reaching the reader altered
    /// rather than rejected.
    /// </para>
    /// <para>
    /// Not a hypothetical input: a JavaScript string sliced by length cuts between the two halves of an astral character,
    /// and that is the ordinary way a client shortens a chat line to fit.
    /// </para>
    /// </remarks>
    [Test]
    public void AnUnpairedSurrogateIsRefused()
    {
        Assert.Multiple(() =>
        {
            // A high surrogate with nothing after it.
            var lone = Assert.Throws<ArgumentException>(() => Utf8Text256.From("hi \ud83d"));
            Assert.That(lone!.Message, Does.Contain("surrogate"));

            // A low surrogate with nothing before it.
            Assert.Throws<ArgumentException>(() => Utf8Text256.From("\ude00 there"));

            // And the PAIR is fine — the guard must refuse invalid text, not astral text.
            var pair = Utf8Text256.From("hi \ud83d\ude00");
            Assert.That(pair.ToString(), Is.EqualTo("hi \ud83d\ude00"));
            Assert.That(pair.Length, Is.EqualTo(7), "three ASCII plus a four-byte emoji");
        });
    }

    /// <summary>A copy carries its own bytes: the buffer is inline, so assigning the value is the copy.</summary>
    [Test]
    public void ACopyIsIndependent()
    {
        var original = Utf8Text256.From("first");
        var copy = original;
        original = Utf8Text256.From("second");

        Assert.Multiple(() =>
        {
            Assert.That(copy.ToString(), Is.EqualTo("first"));
            Assert.That(original.ToString(), Is.EqualTo("second"));
        });
    }
}

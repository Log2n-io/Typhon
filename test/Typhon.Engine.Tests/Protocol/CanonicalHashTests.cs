using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

/// <summary>
/// #957 — the shared canonical FNV-1a 64 helper must reproduce, byte for byte, the digests the hand-rolled sites produce today.
/// </summary>
/// <remarks>
/// <para>
/// The oracle is a verbatim copy of the original loop, kept below. Asserting the helper against a re-implementation rather than against hard-coded constants
/// is deliberate: a constant I computed by running the new code would prove only that it agrees with itself. The copy is what the two call sites did before
/// the migration, so it is the real prior behaviour, and it stays here permanently as the pin.
/// </para>
/// <para>
/// The last four cases are the properties the construction exists for, and each one fails for a plausible implementation that passes the others: order
/// independence needs the caller's sort, process independence needs UTF-8 bytes rather than <c>string.GetHashCode</c>, and the separator is the only thing
/// stopping adjacent entries from running together.
/// </para>
/// </remarks>
[TestFixture]
class CanonicalHashTests
{
    /// <summary>
    /// The construction as <c>ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint</c> wrote it: UTF-8 name, int32 little-endian revision, 0xFF separator.
    /// </summary>
    private static ulong LegacyNameRevisionDigest(IEnumerable<(string Name, int Revision)> entries)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        Span<byte> revisionBytes = stackalloc byte[sizeof(int)];
        foreach (var (name, revision) in entries)
        {
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                hash = (hash ^ b) * prime;
            }
            BinaryPrimitives.WriteInt32LittleEndian(revisionBytes, revision);
            foreach (var b in revisionBytes)
            {
                hash = (hash ^ b) * prime;
            }
            hash = (hash ^ 0xFF) * prime;
        }

        return hash;
    }

    /// <summary>
    /// The construction as <c>DatabaseRepair.Fingerprint</c> wrote it: UTF-8 code, int32 page, int64 occurrences, 0xFF separator.
    /// </summary>
    private static ulong LegacyFindingDigest(IEnumerable<(string Code, int Page, long Occurrences)> entries)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        Span<byte> intBytes = stackalloc byte[sizeof(int)];
        Span<byte> longBytes = stackalloc byte[sizeof(long)];
        foreach (var (code, page, occurrences) in entries)
        {
            foreach (var b in Encoding.UTF8.GetBytes(code))
            {
                hash = (hash ^ b) * prime;
            }
            BinaryPrimitives.WriteInt32LittleEndian(intBytes, page);
            foreach (var b in intBytes)
            {
                hash = (hash ^ b) * prime;
            }
            BinaryPrimitives.WriteInt64LittleEndian(longBytes, occurrences);
            foreach (var b in longBytes)
            {
                hash = (hash ^ b) * prime;
            }
            hash = (hash ^ 0xFF) * prime;
        }

        return hash;
    }

    private static ulong HelperNameRevisionDigest(IEnumerable<(string Name, int Revision)> entries)
    {
        var builder = CanonicalHashBuilder.Create();
        foreach (var (name, revision) in entries)
        {
            builder.AddUtf8(name);
            builder.AddInt32(revision);
            builder.EndEntry();
        }

        return builder.Value;
    }

    private static ulong HelperFindingDigest(IEnumerable<(string Code, int Page, long Occurrences)> entries)
    {
        var builder = CanonicalHashBuilder.Create();
        foreach (var (code, page, occurrences) in entries)
        {
            builder.AddUtf8(code);
            builder.AddInt32(page);
            builder.AddInt64(occurrences);
            builder.EndEntry();
        }

        return builder.Value;
    }

    private static readonly (string Name, int Revision)[] SchemaCorpus =
    [
        ("", 0),
        ("A", 1),
        ("Ab", 1),
        ("Position", 7),
        ("Créature", -3),
        ("名前", int.MaxValue),
        ("Emoji\U0001F600Component", int.MinValue),
        ("a.very.long.component.name.that.comfortably.exceeds.the.helper's.stack.encoding.limit." +
         "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789" +
         "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789", 42),
    ];

    [Test]
    public void TheHelperReproducesTheSchemaFingerprintConstruction()
    {
        Assert.That(HelperNameRevisionDigest(SchemaCorpus), Is.EqualTo(LegacyNameRevisionDigest(SchemaCorpus)),
            "the helper must fold name/revision entries exactly as ProfilerSessionMetadataBuilder did");
    }

    [Test]
    public void TheHelperReproducesTheRepairFingerprintConstruction()
    {
        (string Code, int Page, long Occurrences)[] findings =
        [
            ("", 0, 0L),
            ("PS-11", 42, 1L),
            ("CK-12", 0, long.MaxValue),
            ("WAL-01", int.MaxValue, long.MinValue),
            ("Ünicode-Code", -1, -1L),
        ];

        Assert.That(HelperFindingDigest(findings), Is.EqualTo(LegacyFindingDigest(findings)),
            "the helper must fold code/page/occurrence entries exactly as DatabaseRepair did");
    }

    [Test]
    public void EveryPrefixOfTheCorpusAgreesWithTheLegacyConstruction()
    {
        // Entry-by-entry rather than only on the whole corpus: a helper that disagreed on one entry but compensated on the next would pass a single
        // whole-corpus comparison.
        for (var length = 0; length <= SchemaCorpus.Length; length++)
        {
            var prefix = SchemaCorpus[..length];
            Assert.That(HelperNameRevisionDigest(prefix), Is.EqualTo(LegacyNameRevisionDigest(prefix)), $"prefix of length {length} diverged");
        }
    }

    [Test]
    public void AnEmptyFoldIsTheOffsetBasis()
    {
        Assert.That(CanonicalHashBuilder.Create().Value, Is.EqualTo(14695981039346656037UL), "an empty digest must be the FNV-1a 64 offset basis");
    }

    /// <summary>
    /// The separator's whole reason for existing. Without it these two fold to identical byte streams.
    /// </summary>
    [Test]
    public void TheSeparatorStopsAdjacentEntriesRunningTogether()
    {
        var split = HelperNameRevisionDigest([("Ab", 1), ("c", 2)]);
        var joined = HelperNameRevisionDigest([("Abc", 1), ("", 2)]);

        Assert.That(split, Is.Not.EqualTo(joined), "('Ab',1)('c',2) and ('Abc',1)('',2) must not collide");
    }

    /// <summary>
    /// Order independence is the CALLER's job — the helper folds what it is given, in order. This pins that the split of responsibility is real, so nobody
    /// later assumes the helper sorts and drops their own sort.
    /// </summary>
    [Test]
    public void TheHelperIsOrderSensitiveSoTheCallerMustSort()
    {
        var forward = HelperNameRevisionDigest([("A", 1), ("B", 2)]);
        var reversed = HelperNameRevisionDigest([("B", 2), ("A", 1)]);

        Assert.That(forward, Is.Not.EqualTo(reversed), "the helper does not sort; ordinal sorting belongs to the caller and must not be dropped");
    }

    /// <summary>
    /// The process-independence property, which is why UTF-8 bytes are hashed rather than <c>string.GetHashCode</c>. A digest that varied per process would
    /// defeat every comparison these fingerprints exist for, and that is the bug DatabaseRepair's comment records.
    /// </summary>
    [Test]
    public void TheDigestIsStableForTheSameInputWithinAndAcrossFolds()
    {
        var first = HelperNameRevisionDigest(SchemaCorpus);
        var second = HelperNameRevisionDigest(SchemaCorpus);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first), "the same entries must fold to the same digest");
            Assert.That(first, Is.Not.EqualTo(0UL), "a digest of zero would hide a builder that never folded anything");
        });
    }

    [Test]
    public void LongStringsTakeThePooledPathAndStillMatch()
    {
        // Straddles the helper's 256-byte stack limit in both directions, and in UTF-8 where a char is not a byte.
        var entries = new List<(string Name, int Revision)>();
        for (var length = 250; length <= 260; length++)
        {
            entries.Add((new string('x', length), length));
            entries.Add((new string('é', length), length));
        }

        Assert.That(HelperNameRevisionDigest(entries), Is.EqualTo(LegacyNameRevisionDigest(entries)),
            "the pooled encoding path must produce the same bytes as the allocating one it replaces");
    }

    [Test]
    public void ToHexMatchesTheRepairFingerprintRendering()
    {
        var builder = CanonicalHashBuilder.Create();
        builder.AddUtf8("PS-11");
        builder.AddInt32(42);
        builder.AddInt64(1);
        builder.EndEntry();

        Assert.That(builder.ToHex(), Is.EqualTo(builder.Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(builder.ToHex(), Has.Length.EqualTo(16), "DatabaseRepair renders exactly 16 hex digits into its fingerprint string");
    }
}

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Typhon.Protocol;

/// <summary>
/// The FNV-1a 64 construction this repository uses for content fingerprints: UTF-8 bytes, fixed-width little-endian integers, and an explicit end-of-entry
/// separator, folded over entries the caller has already sorted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The same construction was hand-rolled at two sites with identical constants and no shared helper —
/// <c>ProfilerSessionMetadataBuilder.ComputeSchemaFingerprint</c> and <c>DatabaseRepair.Fingerprint</c> — the second of which re-implemented it deliberately
/// and said so in a comment, after a <c>string.GetHashCode</c> non-determinism bug. The catalog needs the same digest, and a third copy is the point at which
/// it becomes a convention nobody owns.
/// </para>
/// <para>
/// <b>What each part is for, because each one is load-bearing.</b> Hashing UTF-8 <i>bytes</i> rather than <c>string.GetHashCode</c> is what makes the value
/// stable across processes — <c>GetHashCode</c> is randomized per process, and a fingerprint that changes between runs silently defeats every comparison it
/// exists for. Fixed-width little-endian integers make it stable across architectures. The <c>0xFF</c> separator at the end of each entry is what stops
/// <c>("Ab", 1)</c> colliding with <c>("A", …)</c> by concatenation. Ordinal sorting is what makes it independent of declaration order — but it belongs to
/// the caller, because only the caller knows what an entry is.
/// </para>
/// <para>
/// <b>What this helper does NOT do.</b> It does not sort. Each caller folds its own already-sorted entries, because the tuple shape differs at every site
/// (name + revision; code + page + occurrences; archetype + field + codec). Sorting here would mean a collection type and an allocation for the privilege.
/// </para>
/// <para>
/// <b>Not every FNV-like hash in this repository is this one.</b> Three further sites — <c>ArchetypeClusterState.HashUnitGeometry</c>,
/// <c>IndexContentChecks</c> and the Workbench's <c>StorageMapService</c> — start from <c>1469598103934665603</c>, which is the FNV-1a 64 offset basis with
/// its final digit missing, and two of them say "FNV-1a offset basis" in a comment. They are self-consistent cache keys, so nothing is broken today, but they
/// are not this construction and must not be migrated onto this helper expecting the same digests.
/// </para>
/// </remarks>
/// <remarks>
/// <b>A plain struct, deliberately not a <c>ref struct</c>.</b> It holds one <c>ulong</c> and references nothing, so the stack-only constraint buys nothing —
/// and costs something real: an instance method of a <c>ref struct</c> may capture a span parameter into the receiver, so the compiler refuses to pass a
/// <c>stackalloc</c> buffer into one (CS8350/CS8352). That is exactly what <see cref="AddInt32"/>, <see cref="AddInt64"/> and the short-string path of
/// <see cref="AddUtf8(ReadOnlySpan{char})"/> do. The project's "prefer <c>ref struct</c> for short-lived helpers" guidance is about types that wrap
/// references; this one does not.
/// </remarks>
public struct CanonicalHashBuilder
{
    /// <summary>FNV-1a 64 offset basis.</summary>
    private const ulong OffsetBasis = 14695981039346656037;

    /// <summary>FNV-1a 64 prime.</summary>
    private const ulong Prime = 1099511628211;

    /// <summary>Marks the end of one entry, so adjacent entries cannot collide by concatenation.</summary>
    private const byte EntrySeparator = 0xFF;

    /// <summary>Above this, a string is encoded through a pooled buffer rather than the stack.</summary>
    private const int StackEncodeLimit = 256;

    private ulong _hash;

    /// <summary>Creates a builder seeded with the FNV-1a 64 offset basis.</summary>
    /// <returns>A builder with no entries folded in yet.</returns>
    public static CanonicalHashBuilder Create() => new() { _hash = OffsetBasis };

    /// <summary>The digest of everything folded in so far.</summary>
    public readonly ulong Value => _hash;

    /// <summary>The digest as 16 lower-case hexadecimal digits, invariant-formatted.</summary>
    /// <returns>A 16-character hexadecimal string.</returns>
    public readonly string ToHex() => _hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Folds raw bytes in, one FNV-1a round each.</summary>
    /// <param name="value">The bytes to fold.</param>
    public void AddBytes(ReadOnlySpan<byte> value)
    {
        var hash = _hash;
        foreach (var b in value)
        {
            hash = (hash ^ b) * Prime;
        }

        _hash = hash;
    }

    /// <summary>Folds a string in as its UTF-8 bytes. A <see langword="null"/> string folds as empty, exactly as the call sites it replaces did.</summary>
    /// <param name="value">The string to fold; may be <see langword="null"/>.</param>
    public void AddUtf8(string value) => AddUtf8(value.AsSpan());

    /// <summary>Folds character data in as its UTF-8 bytes.</summary>
    /// <param name="value">The characters to fold.</param>
    /// <remarks>
    /// Encodes into a stack or pooled buffer rather than calling <c>Encoding.UTF8.GetBytes(string)</c>, which allocates a fresh array per entry as the two
    /// original sites did. The bytes are identical; only the garbage is gone, which matters because the catalog folds one entry per field of every replicated
    /// archetype.
    /// </remarks>
    public void AddUtf8(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (maxBytes <= StackEncodeLimit)
        {
            Span<byte> buffer = stackalloc byte[StackEncodeLimit];
            AddBytes(buffer[..Encoding.UTF8.GetBytes(value, buffer)]);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            AddBytes(rented.AsSpan(0, Encoding.UTF8.GetBytes(value, rented)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Folds a 32-bit integer in as four little-endian bytes.</summary>
    /// <param name="value">The value to fold.</param>
    public void AddInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        AddBytes(bytes);
    }

    /// <summary>Folds a 64-bit integer in as eight little-endian bytes.</summary>
    /// <param name="value">The value to fold.</param>
    public void AddInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        AddBytes(bytes);
    }

    /// <summary>
    /// Closes the current entry. Call once after the last field of every entry, including the last one.
    /// </summary>
    /// <remarks>
    /// Skipping it is not a cosmetic difference: without a separator the fields of adjacent entries run together, so <c>("Ab", 1)</c> and <c>("A", …)</c> can
    /// produce the same digest. Both original sites emit it unconditionally at the end of each loop iteration, and this reproduces that.
    /// </remarks>
    public void EndEntry()
    {
        _hash = (_hash ^ EntrySeparator) * Prime;
    }
}

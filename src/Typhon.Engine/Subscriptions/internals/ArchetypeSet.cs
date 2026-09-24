using System.Runtime.CompilerServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// The archetypes a profile observes, as a set of plan indices: 256 bits, so every plan index the registry admits has its own bit (09 § 12).
/// </summary>
/// <remarks>
/// A plan index runs over every declared projection, observed or not, and the registry refuses the 256th (<see cref="ProtocolConstants.MaxArchetypes"/>).
/// The 64-bit mask this replaces aliased any index past 63 onto <c>index mod 64</c> — a shift count is masked to six bits — so one archetype's events
/// reached another's sessions.
/// </remarks>
[InlineArray(Words)]
internal struct ArchetypeSet
{
    /// <summary>The number of 64-bit words: 256 bits.</summary>
    public const int Words = 4;

    /// <summary>The number of plan indices the set can hold.</summary>
    public const int Capacity = Words * 64;

    private ulong _word;

    /// <summary>Whether plan index <paramref name="archetype"/> is in the set.</summary>
    /// <param name="archetype">A plan index below <see cref="Capacity"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(int archetype) =>
        ((Unsafe.Add(ref Unsafe.As<ArchetypeSet, ulong>(ref Unsafe.AsRef(in this)), (archetype >> 6) & (Words - 1)) >> (archetype & 63)) & 1UL) != 0;

    /// <summary>Adds plan index <paramref name="archetype"/>.</summary>
    /// <param name="archetype">A plan index below <see cref="Capacity"/>.</param>
    public void Add(int archetype) => this[archetype >> 6] |= 1UL << (archetype & 63);
}

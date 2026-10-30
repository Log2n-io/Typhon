using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Marks a type whose instances hold an engine resource only their <c>Dispose</c> releases — pinned pages and their chunk-writer registrations, an epoch
/// pin, an open transaction. The analyzer reports such a value created and never disposed, discarded, or used once as a receiver and dropped (TYPHON004),
/// and a type that holds one in a field without disposing it (TYPHON005–007).
/// </summary>
/// <remarks>
/// <para>
/// The analyzer also <i>infers</i> it: a type with a <c>Dispose</c> that holds a marked type in a field, by value, owns that resource too. TYPHON014
/// requires the marker wherever the inference finds an owner, so ownership is stated at the declaration and reaches every caller — including through a
/// class, whose private fields a reference assembly drops. A leak this would have caught: an <c>ArchetypeAccessor</c> that owned three
/// <c>ChunkAccessor</c>s, never disposed by a caller, held the checkpoint back for 13 hours (#1538).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false)]
internal sealed class MustDisposeAttribute : Attribute
{
    /// <summary>What a value never disposed costs, added to the diagnostic message.</summary>
    public string Reason { get; set; }
}

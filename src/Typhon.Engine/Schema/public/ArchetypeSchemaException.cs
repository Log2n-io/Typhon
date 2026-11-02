using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>Why an archetype's persisted schema could not be opened by this build (#1394).</summary>
[PublicAPI]
public enum ArchetypeSchemaErrorKind
{
    /// <summary>The component set or its order changed, but the effective <c>[Archetype(Revision)]</c> did not increase (R1).</summary>
    RevisionNotBumped,

    /// <summary>The runtime revision is lower than the persisted one: the database was written by a newer build (R3).</summary>
    Downgrade,

    /// <summary>A persisted component is no longer declared. Removal deletes data and is not supported yet (R4).</summary>
    ComponentRemoved,

    /// <summary>The persisted component list (<see cref="ArchetypeR1.ComponentNames"/>) is missing or inconsistent with the row (R5).</summary>
    NamesUnreadable,

    /// <summary>A persisted slot names a component the catalog holds no row for, so the stored layout cannot be reconstructed (R6).</summary>
    CatalogRowMissing,

    /// <summary>An added component carries a unique, non-sparse index on a populated archetype (R7).</summary>
    UniqueIndexOnAddedComponent,

    /// <summary>Two consumed archetypes share one durable name at one revision (R8).</summary>
    DuplicateName,

    /// <summary>An added Versioned component already has revision chains carrying the archetype's routing id (R9).</summary>
    StaleChains,

    /// <summary>A component changes layout while a persisted archetype holding it is not opened by this process (R10).</summary>
    UnopenedHolder,

    /// <summary>The pre-migration cluster cannot be loaded, or fails validation (R11).</summary>
    OldClusterUnreadable,

    /// <summary>
    /// An added component carries the archetype's spatial field or its realm key, and the archetype is populated: every existing entity would be placed at
    /// position 0, or in realm 0 (R12).
    /// </summary>
    PlacementOnAddedComponent,
}

/// <summary>
/// Thrown when an archetype's persisted schema (its <see cref="ArchetypeR1"/> row) cannot be opened under the runtime declaration. Every refusal is raised
/// before any byte of the archetype moves.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/>, which is what a schema refusal at <c>InitializeArchetypes</c> has always thrown, so existing
/// handlers keep catching it. See <c>claude/design/Schema/07-archetype-set-evolution.md</c> §8 for each kind.
/// </remarks>
[PublicAPI]
public sealed class ArchetypeSchemaException : InvalidOperationException
{
    /// <summary>The durable name of the archetype (or, for <see cref="ArchetypeSchemaErrorKind.UnopenedHolder"/>, of the unopened holder).</summary>
    public string ArchetypeName { get; }

    /// <summary>Which rule refused the open.</summary>
    public ArchetypeSchemaErrorKind Kind { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="archetypeName">Durable name of the archetype concerned.</param>
    /// <param name="kind">Which rule refused the open.</param>
    /// <param name="message">The full, user-facing message.</param>
    public ArchetypeSchemaException(string archetypeName, ArchetypeSchemaErrorKind kind, string message) : base(message)
    {
        ArchetypeName = archetypeName;
        Kind = kind;
    }
}

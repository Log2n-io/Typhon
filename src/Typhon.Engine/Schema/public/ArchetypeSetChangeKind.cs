using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>What differs between an archetype's stored row and this build's declaration, when the open would rewrite the row (#1394).</summary>
[PublicAPI]
public enum ArchetypeSetChangeKind
{
    /// <summary>The component set or its order changed, with a revision increase: every entity is re-placed into the new cluster geometry.</summary>
    SetChange,

    /// <summary>Same set, higher revision: the row's revision is rewritten, no data moves.</summary>
    RevisionOnly,

    /// <summary>Same set and revision, but the stored component names predate a component rename: the row's names are refreshed, no data moves.</summary>
    NamesRefreshed,

    /// <summary>
    /// An open that consumed the archetype REFUSES it (R1 a set change with no revision increase, R3 a downgrade, R4 a removed component, R5 an unreadable
    /// stored list, R6 a stored component with no catalog row): under <see cref="ArchetypeSetMigrationPolicy.Refuse"/> the archetype is skipped rather
    /// than the whole open refused, and the same is reported for an archetype skipped for a component this engine does not register.
    /// <see cref="ArchetypeSetChange.Changes"/> carries the refusal's message, remedy included.
    /// </summary>
    Refused,
}

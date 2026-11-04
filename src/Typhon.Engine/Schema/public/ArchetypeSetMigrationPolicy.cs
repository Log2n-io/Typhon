using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// What an open does with an archetype whose persisted component set differs from this build's (#1394, design 07 D4). Set through
/// <see cref="DatabaseEngineOptions.ArchetypeSetMigration"/>.
/// </summary>
[PublicAPI]
public enum ArchetypeSetMigrationPolicy
{
    /// <summary>
    /// Migrate at open (the default): every existing entity is re-placed into the new cluster geometry, every surviving component carried across by name,
    /// every added one absent; a revision-only bump or stale stored names rewrite the archetype row alone. All of it commits in the open's schema commit.
    /// </summary>
    Apply,

    /// <summary>
    /// Leave the file untouched: an archetype that would migrate (a set change, a revision-only bump, or stale stored names) is skipped instead. It gets no
    /// storage in this engine, its row and segments stay as they are, and it is listed in <see cref="DatabaseEngine.RefusedArchetypeSetChanges"/>; the rest
    /// of the database opens. For inspectors (the Workbench), which must never rewrite a file they only read; <c>tsh schema-validate</c> does not open at
    /// all, it previews (an open under this policy still commits the component migrations its registration staged). An archetype the build's
    /// declaration refuses (R1, R3-R6) is skipped too and listed as <see cref="ArchetypeSetChangeKind.Refused"/>, the refusal in its
    /// <see cref="ArchetypeSetChange.Changes"/>, so the rest of the database still opens. What concerns more than one archetype still refuses the open
    /// (<see cref="ArchetypeSchemaException"/>): two classes of one name at one revision (R8), a component migrating under a skipped archetype (R10).
    /// </summary>
    Refuse,
}

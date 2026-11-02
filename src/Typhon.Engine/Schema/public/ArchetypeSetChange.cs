using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// How one archetype's stored row differs from this build's declaration (#1394): what an open under <see cref="ArchetypeSetMigrationPolicy.Refuse"/>
/// skipped instead of applying (<see cref="DatabaseEngine.RefusedArchetypeSetChanges"/>), and what a dry run of the next open reports.
/// </summary>
/// <param name="ArchetypeName">The archetype's durable name.</param>
/// <param name="FromRevision">The revision the archetype is stored at.</param>
/// <param name="ToRevision">The revision this build declares.</param>
/// <param name="Kind">What an open under <see cref="ArchetypeSetMigrationPolicy.Apply"/> would do.</param>
/// <param name="Changes">What changed, one line: <c>+Gear at slot 2; Vitals 2-&gt;3</c>; empty for a metadata-only change; for
/// <see cref="ArchetypeSetChangeKind.Refused"/>, the refusal's message.</param>
[PublicAPI]
public sealed record ArchetypeSetChange(string ArchetypeName, int FromRevision, int ToRevision, ArchetypeSetChangeKind Kind, string Changes);

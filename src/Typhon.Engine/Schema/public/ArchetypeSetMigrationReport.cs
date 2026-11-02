using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// One archetype whose component set the last open migrated (#1394): its existing entities re-placed into the new cluster geometry, every surviving
/// component carried across by name, every added one absent. Read through <see cref="DatabaseEngine.LastOpenArchetypeSetMigrations"/>.
/// </summary>
/// <param name="ArchetypeName">The archetype's durable name.</param>
/// <param name="FromRevision">The revision the archetype was stored at.</param>
/// <param name="ToRevision">The revision this build declares.</param>
/// <param name="Changes">What changed, one line: <c>+Gear at slot 2; Vitals 2-&gt;3</c>.</param>
/// <param name="EntitiesMigrated">The entities re-placed.</param>
/// <param name="ElapsedMilliseconds">Wall-clock time of the re-placement and of the rebuilds over it (HEADs, indexes, spatial layer).</param>
[PublicAPI]
public sealed record ArchetypeSetMigrationReport(string ArchetypeName, int FromRevision, int ToRevision, string Changes, int EntitiesMigrated,
    double ElapsedMilliseconds);

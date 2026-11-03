using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>How an open reconciles the schema the application registers with the one the database holds (#1485).</summary>
[PublicAPI]
public enum SchemaMatch
{
    /// <summary>
    /// The application's schema wins: a component whose layout changed migrates, a new component or archetype is added, the timeline forks, and the
    /// assembly manifest takes the identity of the build that opened it. Every ordinary open.
    /// </summary>
    Migrate = 0,

    /// <summary>
    /// The open reproduces the database as written: the registered components and archetypes, and the assemblies declaring them, must be exactly the ones
    /// the catalog and its manifest hold. Any difference is refused with <see cref="SchemaMismatchException"/> before it is written — nothing migrates,
    /// nothing is added, the timeline does not fork. Drills and restore settles open this way, so a point is never proven, or settled, with another build.
    /// </summary>
    Exact,
}

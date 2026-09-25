using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// An archetype replicated by attributes (design/Subscriptions/11 § 5). Implemented by the Typhon source generator on every <c>[Replicated]</c> archetype,
/// never by hand: <see cref="DeclareReplication"/> is the builder calls its attributes compile to, and
/// <see cref="SubscriptionsRegistry.Archetype{TArchetype}()"/> is what applies them.
/// </summary>
[PublicAPI]
public interface IReplicatedArchetype
{
    /// <summary>Whether <c>[Replicated(Static = true)]</c> declared it static: sent once on enter, never updated.</summary>
    static abstract bool ReplicatedStatic { get; }

    /// <summary>Declares the archetype's projection as its attributes say.</summary>
    /// <param name="archetype">The builder.</param>
    static abstract void DeclareReplication(ArchetypeProjectionBuilder archetype);
}

namespace Typhon.Engine;

/// <summary>Who tells the push path that an entity changed (<c>design/Subscriptions/research/push-model.md</c>).</summary>
public enum PushDetection
{
    /// <summary>
    /// The application: a system calls <see cref="SubscriptionsCommands.Replicate{TArchetype}(in ClusterRef{TArchetype}, int)"/> after a write. The engine
    /// adds its own pushes (spawn, destroy, <c>WriteSpatial</c>, migration, motion refits). An entity changed without either is not re-sent.
    /// </summary>
    Explicit,

    /// <summary>
    /// EXPERIMENTAL, refused unless <see cref="SubscriptionsOptions.AllowAutomaticPushDetection"/> is set: the engine projects and compares every live entity
    /// of the archetype every tick and pushes those whose wire bytes changed. Nothing to forget, at the cost of encoding the whole archetype each tick.
    /// </summary>
    Automatic,
}

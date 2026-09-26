using Typhon.Protocol;

namespace Typhon.Engine.Internals;

internal sealed unsafe partial class FrameAssembler
{
    /// <summary>
    /// Whether <paramref name="session"/> holds <paramref name="entity"/>, which <paramref name="netId"/> names — its controlled entity, or one its COMMITTED
    /// geometry names (SUB-16): what its client was last told, so what it may name in a command (SUB-26, design/Subscriptions/11 § 3).
    /// </summary>
    /// <param name="session">The session that sent the reference.</param>
    /// <param name="netId">The identity the reference carried.</param>
    /// <param name="entity">The entity the identity is bound to.</param>
    /// <returns><see langword="true"/> when the entity is live, still carries that identity, and the session holds it.</returns>
    /// <remarks>
    /// <para>
    /// <b>Committed, and safe to read from a system.</b> Commands are applied by application systems, which run before the fence; the committed geometry is
    /// written only by the frame stage and v̂ only by the projection, both after it (SUB-05). Committed is also the right answer: an entity that left this
    /// tick was still on the client's screen when it sent the command. The profile judged is the one the last published frame was built against — a switch
    /// is not what the client holds until its RESET frame is published.
    /// </para>
    /// <para>
    /// <b>Live, and still this identity.</b> The entity is located through the EntityMap (a destroyed one is refused at once, not only after the next
    /// projection unbinds its identity) and its replica must still carry <paramref name="netId"/>, so a missed unbind can never resolve.
    /// </para>
    /// <para><b>An entity the client learned from an event is not held</b> (11 § 3, Q3): events inform, they do not grant reach.</para>
    /// </remarks>
    internal bool Holds(SessionId session, uint netId, EntityId entity)
    {
        if (entity.IsNull || Push == null)
        {
            return false;
        }

        using var epoch = EpochGuard.Enter(Engine.EpochManager);
        var locator = new BoundViewpoint(Engine);
        try
        {
            if (!locator.TryLocate(entity, out var clusters, out var chunk, out var slot)
                || !Push.TryReplicaAt(clusters, chunk, slot, entity, out var archetype, out var block, out var carried)
                || carried != netId)
            {
                return false;
            }

            if (_sessions.ControlledOf(session) == entity)
            {
                return true;
            }

            var state = StateOf(session);
            var profile = state != null && state.Generation == session.Generation ? state.CommittedProfile : -1;
            if (profile < 0 || Profiles == null || !Profiles.SetOf(profile).Contains(archetype))
            {
                return false;
            }

            // The entity's realm's replication (SUB-28): its frame decodes the entity's v̂, and its geometry is the session's only if the session is placed in
            // that realm — a session of another realm reads an unbound slot there and holds nothing, whatever the local coordinates.
            var owner = Push.Hub == null ? Push : Push.Hub.For(((ReplicationBlockHeader*)block)->Realm);
            if (owner == null)
            {
                return false;
            }

            owner.DecodeVisibility(archetype, block, slot, out var x, out var y, out var z);
            var shape = Profiles.IsWorld(profile) ? PushShape.World : Profiles.RegionOf(profile) ? PushShape.Region : PushShape.Sphere;
            return owner.HoldsCommitted(session, shape, x, y, z);
        }
        finally
        {
            locator.Dispose();
        }
    }

    /// <summary>Whether <paramref name="entity"/> is live and its replica still carries <paramref name="netId"/> — <c>TryResolveAny</c>'s test.</summary>
    internal bool IsLive(uint netId, EntityId entity)
    {
        if (entity.IsNull || Push == null)
        {
            return false;
        }

        using var epoch = EpochGuard.Enter(Engine.EpochManager);
        var locator = new BoundViewpoint(Engine);
        try
        {
            return locator.TryLocate(entity, out var clusters, out var chunk, out var slot)
                   && Push.TryReplicaAt(clusters, chunk, slot, entity, out _, out _, out var carried) && carried == netId;
        }
        finally
        {
            locator.Dispose();
        }
    }
}

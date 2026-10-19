using Typhon.Protocol;

namespace Typhon.Engine.Internals;

internal sealed unsafe partial class FrameAssembler
{
    /// <summary>
    /// Whether <paramref name="session"/> holds <paramref name="entity"/> AND knows it by <paramref name="netId"/> — its controlled entity, or one its
    /// COMMITTED geometry names (SUB-16): what its client was last told, so what it may name in a command (SUB-26, design/Subscriptions/11 § 3).
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
        => TryHeldNetId(session, entity, out var carried) && carried == netId;

    /// <summary>
    /// The identity <paramref name="session"/> knows <paramref name="entity"/> by, when it holds it at all: its controlled entity, one its committed
    /// geometry names, or a member of a group its committed frame holds (14 § 3) — positionless ones included (14 § 5) — and, of those, one it sees (14 § 6).
    /// </summary>
    /// <param name="session">The session asking.</param>
    /// <param name="entity">The entity to name.</param>
    /// <param name="netId">The identity that session's client was last told, or <c>0</c>.</param>
    /// <returns><see langword="true"/> when the entity is live, replicated, and held by that session.</returns>
    /// <remarks>
    /// <para>
    /// <b>The inverse of <see cref="Holds"/>, and literally the same walk.</b> That method already had to read the entity's identity out
    /// of its replica in order to compare it, so returning it instead of comparing it costs nothing and removes the second copy of the
    /// reach rules. <c>Holds</c> is now the comparison on top of this.
    /// </para>
    /// <para>
    /// <b>Session-scoped on purpose, although a netId is engine-wide.</b> The number is global — one allocator across every replicated
    /// archetype — so an unscoped lookup would be easy to write and easy to misuse: handing a session an identity it was never shown
    /// lets it name something outside its view, which is exactly what <c>SUB-26</c> forbids and what <c>TryResolve</c> enforces coming
    /// the other way. Asking "what does THIS session call it" cannot be misused that way, because the answer is no when it cannot see it.
    /// </para>
    /// </remarks>
    internal bool TryHeldNetId(SessionId session, EntityId entity, out uint netId)
    {
        netId = NetIdAllocator.NoNetId;
        if (entity.IsNull || Push == null)
        {
            return false;
        }

        using var epoch = EpochGuard.Enter(Engine.EpochManager);
        var locator = new EntityLocator(Engine);
        try
        {
            if (!locator.TryLocate(entity, out var chunk, out var slot)
                || !Push.TryReplicaAt(Engine._stateByRouting[entity.ArchetypeId].ClusterState, chunk, slot, entity, out var archetype, out var block,
                    out var carried)
                || carried == NetIdAllocator.NoNetId)
            {
                return false;
            }

            if (_sessions.ControlledOf(session) == entity)
            {
                netId = carried;
                return true;
            }

            var state = StateOf(session);
            var profile = state != null && state.Generation == session.Generation ? state.CommittedProfile : -1;
            if (profile < 0 || Profiles == null)
            {
                return false;
            }

            // The entity's realm's replication (SUB-28): its frame decodes the entity's v̂, and its geometry and groups are the session's only if the session
            // is placed in that realm — a session of another realm reads an unbound slot there and holds nothing, whatever the local coordinates. A
            // positionless member has no realm of its own (14 § 5): it is held through the keys of whichever realm the session is in.
            var positionless = Push.IsUnplaced(archetype);
            var owner = Push.Hub == null ? Push
                : positionless ? Push.Hub.PlacedFor(session)
                : Push.Hub.For(((ReplicationBlockHeader*)block)->Realm);
            if (owner == null)
            {
                return false;
            }

            var held = false;
            if (!positionless && Profiles.SetOf(profile).Contains(archetype))
            {
                owner.DecodeVisibility(archetype, block, slot, out var x, out var y, out var z);
                var shape = Profiles.IsWorld(profile) ? PushShape.World : Profiles.RegionOf(profile) ? PushShape.Region : PushShape.Sphere;
                held = owner.HoldsCommitted(session, shape, x, y, z);
            }

            if (!held && !owner.GroupHoldsCommitted(session, archetype, block, slot, carried))
            {
                return false;
            }

            // Held, but owed its enter (15 § 4.5): the client was never given it, so it cannot name it.
            if (state.OwedEnters > 0 && state.OwesEnter(carried))
            {
                return false;
            }

            // Visibility (14 § 6): a session that does not see the entity was never sent it, whatever its geometry.
            if (!owner.CommittedSees(session, owner.MaskOf(archetype, block, slot)))
            {
                return false;
            }

            netId = carried;
            return true;
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
        var locator = new EntityLocator(Engine);
        try
        {
            return locator.TryLocate(entity, out var chunk, out var slot)
                   && Push.TryReplicaAt(Engine._stateByRouting[entity.ArchetypeId].ClusterState, chunk, slot, entity, out _, out _, out var carried)
                   && carried == netId;
        }
        finally
        {
            locator.Dispose();
        }
    }
}

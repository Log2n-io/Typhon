using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// S1 — the per-block pass that turns one tick's pushed entities into per-entity replication state and one push event each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once per entity, never once per session.</b> Every session that will receive an entity this tick reads the same hot entry and copies the same bytes;
/// nothing below is parameterised by a session, and no session is reachable from here (02 § 4).
/// </para>
/// <para>
/// <b>Column by column, then slot by slot.</b> The first half walks each projected field's column across every live watched slot of the cluster, producing one
/// wire CODE per (field, slot) through <see cref="ProjectionColumnWalk"/> — one contiguous run of cache lines per field instead of one line per field per
/// entity. The second half walks the slots, encodes each change group's body from those codes, compares it with the copy the entry already holds, and stamps
/// the group's tick only where the bytes differ.
/// </para>
/// <para>
/// <b>The comparison is on codes, and an entity is quantized exactly once</b> (SUB-10). A value is read from the column, quantized, and the resulting code is
/// what is encoded, what is compared, and what is stored — it is never decoded and re-quantized, so the value that decided "this changed" is bit-for-bit the
/// value that reaches the wire. WHICH entities are projected is the push set (ADR-067): the application's <c>Replicate</c> calls and the engine's own pushes.
/// Whether a pushed entity's bytes changed is still decided here, by the comparison, so a redundant push costs an encode and never a byte on the wire.
/// </para>
/// <para>
/// <b>Group bodies are stored zero-padded to their widest form, and that is what makes a byte comparison exact.</b> A section's
/// <see cref="CompiledSection.MaxBodyBytes"/> is an upper bound — a <c>varu</c> reserves five bytes and usually spends one — so the stored copy is the encoded
/// body followed by zeros out to that bound. Canonical varints are minimal, so no two different values zero-pad to the same bytes, and a fixed-width
/// <c>memcmp</c> is therefore exactly a comparison of the values.
/// </para>
/// <para>
/// <b>Where the motion rule sits.</b> This pass quantizes the position, compares it with the stored copy and sets <see cref="FlagPositionChanged"/>; whether
/// that becomes a motion SEGMENT is <see cref="MotionTracker"/>'s decision (P1-10), and it owns the hot entry's segment bytes and the motion tick in
/// <c>GroupTicks[0]</c> outright. A position that changed is not a segment.
/// </para>
/// <para>
/// <b>What this pass deliberately does not do.</b> It projects owner fields into the block's owner entry and records which owner groups changed, but emits no
/// owner record: which entity a session controls is session state this pass cannot see.
/// </para>
/// </remarks>
internal static unsafe class ProjectionPass
{
    /// <summary>The most slots a cluster holds, and therefore the stride of one field's row in the code scratch.</summary>
    public const int MaxSlots = 64;

    /// <summary>The most change groups either side can declare — the wire's <c>u8</c> mask (W14).</summary>
    public const int MaxGroups = 8;

    /// <summary>Length of <c>ReplicationHotEntry.GroupTicks</c>; the stamps the change mask reads.</summary>
    private const int MaxGroupTicks = 4;

    /// <summary><see cref="ReplicationHotEntry.Flags"/> bits 0-7: the owner groups whose body changed this tick.</summary>
    public const int OwnerChangedMaskShift = 0;

    /// <summary><see cref="ReplicationHotEntry.Flags"/> bit 8: the entry was initialized this tick, so its record is an enter.</summary>
    public const ushort FlagInitializedThisTick = 1 << 8;

    /// <summary><see cref="ReplicationHotEntry.Flags"/> bit 9: the quantized position differs from the one the entry held.</summary>
    public const ushort FlagPositionChanged = 1 << 9;

    /// <summary>
    /// Projects one block's pushed slots: releases the identities of slots that stopped being occupied, (re-)initializes the entries that need it, compares
    /// every other pushed entity's projection with what it held, and records one push event per slot.
    /// </summary>
    /// <param name="plan">The archetype's compiled plan.</param>
    /// <param name="archetypeIndex">The plan's index in the runtime's plan list, carried on every record it produces.</param>
    /// <param name="state">The archetype's replication state: the leases the identities come from, the scratch arenas, and the counters.</param>
    /// <param name="worker">The chunk index, which is also the index of the scratch arena and the identity lease this call owns exclusively.</param>
    /// <param name="block">The block describing the cluster.</param>
    /// <param name="clusterBase">The cluster chunk's base address in the persistent store.</param>
    /// <param name="transientBase">
    /// The same cluster's base in the transient store, or <see langword="null"/> when the archetype has no transient component.
    /// </param>
    /// <param name="tick">The tick being projected.</param>
    /// <param name="references">
    /// The worker's reference reader, opened for its share, when the plan projects a reference field (13 § 5); <see langword="null"/> otherwise — a
    /// reference then reads as 0.
    /// </param>
    public static void ProjectBlock(CompiledProjectionPlan plan, int archetypeIndex, ArchetypeReplicationState state, int worker,
        ReplicationBlockHeader* block, byte* clusterBase, byte* transientBase, uint tick, ReferenceResolver references = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);

        if (block == null || clusterBase == null)
        {
            return;
        }

        // ── Dormant clusters are not re-projected (ADR: build on the engine's own per-tick state) ──────────────────────────────────────────────────
        //
        // The engine already answers this question every tick and replication has simply never asked it. `DormancySweep` runs at the fence, advances a
        // per-cluster counter of consecutive clean ticks, and moves a cluster to `Sleeping` once it passes `SleepThresholdTicks`. `TyphonRuntime` then
        // skips sleeping clusters when it dispatches systems — which is what makes this SOUND rather than merely plausible: nothing is dispatched over a
        // sleeping cluster, so no system can write to one, so every byte this method would encode is already the byte the block holds.
        //
        // <b>Cost when the application never enables dormancy: one field read.</b> `SleepingClusterCount` is zero until a cluster actually sleeps, which
        // is the same zero-overhead guard `TyphonRuntime.OnParallelQueryPrepare` uses for the same reason. `SleepThresholdTicks` defaults to 0 — dormancy
        // is opt-in — so an application that wants none pays a predictable-not-taken branch per block and nothing else.
        //
        // <b>The precondition, stated plainly:</b> an application that writes into a sleeping cluster through a path that raises no dirty bit
        // (`ClusterRef.GetSpan` outside a dispatched system) gets a stale entity here. That is not a new contract — the same write is already lost by the
        // WAL and already fails to wake the cluster, so it is a pre-existing requirement of using dormancy at all, not one replication introduces.
        // A referrer encodes its targets' identities too, which change with no write to its cluster (13 § 5): a slot re-pushed for that — its target
        // released, or not identified yet — is taken out of ProjectedWatchedMask by whoever re-pushed it, so the test below projects it even asleep.
        var clusterState = state.ClusterState;
        if (clusterState != null && clusterState.SleepingClusterCount > 0)
        {
            var sleepStates = clusterState.SleepStates;
            var chunkId = block->ChunkId;
            // Two things a sleeping cluster can still change under us, and neither raises a dirty bit:
            //   - a newly PUSHED slot needs its identity minted, and only this pass mints one (ProjectedWatchedMask);
            //   - a DESTROY clears an occupancy bit, which is detected nowhere else in the engine (ProjectedOccupancy). No destroy path wakes a cluster,
            //     so without this the identity is never released, the block goes on describing a dead entity, and a respawn into that slot reaches
            //     clients as the OLD entity under the OLD netId — which the frame stage's reuse check cannot see, because it compares against the block's
            //     own stale id.
            // The occupancy word is the cluster's own, at offset 0, and the slot loop below has to load it anyway.
            if (sleepStates != null && (uint)chunkId < (uint)sleepStates.Length && sleepStates[chunkId] == ClusterSleepState.Sleeping
                && (block->WatchedMask & ~block->ProjectedWatchedMask) == 0
                && *(ulong*)clusterBase == block->ProjectedOccupancy)
            {
                state.NoteBlockDormant();
                return;
            }
        }

        var layout = plan.BlockLayout;
        var clusterLayout = plan.ClusterLayout;
        var slotCount = plan.SlotCount;
        var slotMask = slotCount >= 64 ? ulong.MaxValue : (1UL << slotCount) - 1;

        // The occupancy word is the cluster's own, at offset 0 — the engine's single answer to "is this slot live". Reading it here, once per block, is what
        // makes the destroy path free: there is no destroy hook anywhere, and a slot that stopped being occupied is detected by this AND.
        var occupancy = *(ulong*)clusterBase & slotMask;
        var watched = block->WatchedMask & slotMask;

        if (watched == 0)
        {
            return;
        }

        var entityIds = (long*)(clusterBase + clusterLayout.EntityIdsOffset);
        var blockBytes = (byte*)block;
        var leases = state.NetIdLeases;
        var arena = state.Scratch[worker];

        // A push-served archetype is projected only where pushed, and every slot projected here becomes one event the frame stage fans out.
        // The replication of the block's realm (R4.1): a block describes one cluster, and a cluster is in one realm from its claim to its drain. The hub
        // only ever pushes blocks of realms it serves.
        var realm = block->Realm;
        var push = state.Push;
        var hub = push?.Hub;
        if (hub != null)
        {
            push = hub.For(realm);
            if (push == null)
            {
                // Never listed (PrepareBlocks routes by realm): projecting it into another realm's replication would break SUB-28 and SUB-30 silently.
                Debug.Assert(false, $"block of unserved realm {realm} was listed for projection");
                return;
            }
        }

        var pushIndex = state.PushArchetypeIndex;

        // The frame of the block's realm (R4.2, SUB-30): a block describes one cluster, and a cluster is in one realm from its claim to its drain, so the
        // frame is hoisted per block.
        var frame = push?.CodecsFor(realm)?.ByPlan[pushIndex] ?? plan.Position?.Frame;

        // ── 1. Slots that stopped being occupied give their identities back ─────────────────────────────────────────────────────────────────────────────
        var released = 0;
        var gone = watched & ~occupancy;
        while (gone != 0)
        {
            var slot = BitOperations.TrailingZeroCount(gone);
            gone &= gone - 1;
            var hot = (ReplicationHotEntry*)(blockBytes + layout.HotOffset + (slot * layout.HotStride));
            if (hot->NetId != NetIdAllocator.NoNetId)
            {
                if (push != null)
                {
                    EmitPushLeave(push, pushIndex, worker, block, blockBytes, layout, slot, hot->NetId, leases, hot->Entity);
                }

                // A controlled entity that leaves is news to its session: every owner group pending, so its next frame fails to locate it and says netId 0.
                state.Self?.Notice(hot->Entity, -1);

                state.EntityIndex?.Unbind(hot->NetId);
                leases.Release(worker, hot->NetId);
                released++;
            }

            ClearEntry(state, blockBytes, layout, slot, worker);
        }

        var live = watched & occupancy;
        if (live == 0)
        {
            state.NoteProjected(blocks: 1, slots: 0, records: 0, releases: released);
            return;
        }

        // ── 2. Which entries have to be (re-)initialized ─────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // Two causes, one answer: the entry does not describe the entity in the slot (a spawn, or a slot the engine reused), or it holds no identity.
        ulong initializing = 0;

        var bits = live;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            var hot = (ReplicationHotEntry*)(blockBytes + layout.HotOffset + (slot * layout.HotStride));
            var entity = (ulong)entityIds[slot];
            if (hot->Entity.RawValue != entity)
            {
                if (hot->NetId != NetIdAllocator.NoNetId)
                {
                    if (push != null)
                    {
                        EmitPushLeave(push, pushIndex, worker, block, blockBytes, layout, slot, hot->NetId, leases, hot->Entity);
                    }

                    // A controlled entity that leaves is news to its session: every owner group pending, so its next frame fails to locate it and says netId 0.
                    state.Self?.Notice(hot->Entity, -1);

                    state.EntityIndex?.Unbind(hot->NetId);
                    leases.Release(worker, hot->NetId);
                    released++;
                }

                ClearEntry(state, blockBytes, layout, slot, worker);
                initializing |= 1UL << slot;
                continue;
            }

            // An entity is projected only when pushed, so "not projected last tick" is its normal state and says nothing about what a client holds — the
            // geometric known-set does. Only an entry with no identity is (re-)initialized.
            if (hot->NetId == NetIdAllocator.NoNetId)
            {
                initializing |= 1UL << slot;
            }
        }

        // The slots an entity was carried into this tick. Their event is never a no-op: the entity's latest event must name the slot it is
        // in now, and a migration that changed no byte would otherwise leave it naming the one it left. A bit left over from a tick that returned early only
        // costs one event with no record.
        var pushArrived = push != null ? Volatile.Read(ref block->ArrivedSlots) : 0UL;

        // ── 3. One column walk per projected field, over the live watched slots ─────────────────────────────────────────────────────────────────────────
        var fields = plan.Fields;
        var ownerFields = plan.OwnerFields;
        var codeRows = fields.Length + ownerFields.Length;
        var codes = arena.Codes(Math.Max(1, codeRows) * MaxSlots);
        var unresolved = Quantize(state, fields, 0, clusterLayout, clusterBase, transientBase, slotCount, live, codes, references);
        unresolved |= Quantize(state, ownerFields, fields.Length, clusterLayout, clusterBase, transientBase, slotCount, live, codes, references);

        // A reference to an entity that has no identity yet sends 0 this tick; its referrer is pushed again, and resolves once the target has one.
        if (unresolved != 0)
        {
            hub?.Repush(pushIndex, block->ChunkId, unresolved);
        }

        var referenceIndex = plan.ReferenceCount > 0 ? state.References : null;

        // Collections (13 § 6.5): the worker's reader, its batch carved for this block. A slot whose element named an entity with no identity yet is
        // pushed again, as a top-level reference's is.
        var collections = plan.Collections.Length > 0 ? state.CollectionContextFor(worker) : null;
        collections?.Bind(arena, plan);
        var collectionIndex = collections != null ? state.References : null;
        var collectionUnresolved = 0UL;

        // ── 4. Scratch, carved once per block ────────────────────────────────────────────────────────────────────────────────────────────────────────────
        var packBytes = MaxPackBytes(plan);
        // Sized by what a section can put on the wire, not by what its entry stores: a wide section is encoded whole before it is compared (13 § 6.3).
        var stateBytes = plan.MaxStateBodyBytes;
        var ownerBytes = plan.MaxOwnerBodyBytes;
        var enterBytes = plan.OnEnter.MaxBodyBytes;
        var wideBodies = state.WideBodies;
        var scratch = arena.Scratch(packBytes + stateBytes + ownerBytes + enterBytes + 8);
        var pack = new Span<byte>(scratch, packBytes);
        var groupScratch = scratch + packBytes;
        var ownerScratch = groupScratch + stateBytes;
        var enterScratch = ownerScratch + ownerBytes;

        Span<int> groupLength = stackalloc int[MaxGroups];
        Span<int> groupOffset = stackalloc int[MaxGroups];
        Span<int> ownerLength = stackalloc int[MaxGroups];
        Span<int> ownerOffset = stackalloc int[MaxGroups];
        Fill(plan.Groups, groupOffset);
        Fill(plan.OwnerGroups, ownerOffset);

        var position = plan.Position;
        var positionBytes = layout.PrevPositionBytes;

        // v̂ (09 § 2, SUB-20): kept apart from the previous position only when the archetype's slack is above zero. It moves to the entity's position at
        // an initialization, on a teleport, or when the position is more than h_A from it; otherwise the geometry keeps reading where it was.
        var ownVisibility = push != null && layout.VisibilityPositionBytes > 0;
        var slack = plan.VisibilitySlackM;
        var slackSquared = slack * slack;

        // A pointer as well as a span over the same stack bytes: the position is quantized through the span and read by the motion rule through the pointer,
        // and taking the pointer here rather than per slot is what keeps a `fixed` region off the per-entity path.
        var quantizedBuffer = stackalloc byte[24];
        var quantizedPosition = new Span<byte>(quantizedBuffer, 24);

        // A moving position is quantized for the whole block before the slots are visited: one pass over the column, the frame's constants and the spatial
        // type resolved once rather than per slot and axis. Slot s's code is at s × positionBytes.
        var positionCodes = stackalloc byte[position != null && positionBytes > 0 ? MaxSlots * positionBytes : 0];
        if (position != null && positionBytes > 0)
        {
            QuantizePositions(position, frame, clusterBase, transientBase, clusterLayout, live, positionCodes, positionBytes);
        }

        // ── The motion rule's per-block setup (P1-10) ────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // Everything the rule needs that is a property of the ARCHETYPE rather than of the entity: the tolerance and teleport thresholds pre-squared, the
        // heartbeat in ticks, and the four offsets a segment is written at. The scratch is carved once for the whole block, so the per-slot call allocates no
        // stack of its own and stays inlinable.
        var motion = MotionPolicy.For(position, frame, layout, state.TickPeriodSeconds);
        byte* velocityColumn = null;
        if (motion.Enabled && motion.VelocityDeclared)
        {
            velocityColumn = StoreFor(clusterLayout, transientBase, clusterBase, position.VelocityComponentSlot) + position.VelocityComponentOffsetInCluster;
        }

        Span<double> motionScratch = stackalloc double[MotionTracker.ScratchDoubles];
        var segmentsEmitted = 0;
        var shadowSegments = 0;

        var visited = 0;
        var records = 0;

        // The slots this block pushes again next tick, marked once after the loop: the hub's repush words are shared by every worker, eight clusters to a
        // line, so a mark per slot made workers on neighbouring clusters write the same line over and over.
        var repush = 0UL;
        bits = live;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            visited++;

            var hotBytes = blockBytes + layout.HotOffset + (slot * layout.HotStride);
            var coldBytes = blockBytes + layout.ColdOffset + (slot * layout.ColdStride);
            var hot = (ReplicationHotEntry*)hotBytes;
            var initialize = (initializing & (1UL << slot)) != 0;
            var takeIdentity = false;

            if (initialize)
            {
                // An identity is taken only when the entry HAS none. The three causes of an initialization are not alike: a new or reused slot arrived here
                // with its entry cleared and therefore needs one, while an entity that was simply unwatched for a tick never left and keeps the identity it
                // had. Taking one unconditionally would strand the old number — live in the allocator, named by nothing — which is the leak AC-9 counts.
                //
                // Checked here, TAKEN after the sections are stored: an archetype's wide bodies can fail to find room (13 § 6.2), and an identity taken
                // before that would have to be given back — a release that only reaches the allocator next tick, so a lasting shortage would drain the
                // lease for every other entity of the block.
                if (hot->NetId == NetIdAllocator.NoNetId)
                {
                    if (!leases.HasAny(worker))
                    {
                        // The lease ran dry. The entity stays watched and its entry stays uninitialized, so the next tick — whose refill has seen this tick's
                        // demand — initializes it. Deferring an entity by a tick is the only failure available here that neither allocates on a worker nor
                        // hands two entities one identity; it is counted so a lease that is chronically too small is visible rather than inferred.
                        leases.Take(worker);
                        state.NoteNetIdStarvation();
                        repush |= 1UL << slot;
                        continue;
                    }

                    takeIdentity = true;
                }

                // Published with a release, after the zeroing that preceded it: a reference resolver on another worker that sees this entity must not see
                // the netId of the entry's previous holder (13 § 5). The netId itself is written later, once the bodies are stored.
                Volatile.Write(ref Unsafe.AsRef<long>(hot), entityIds[slot]);
                hot->Flags = FlagInitializedThisTick;
            }
            else
            {
                hot->Flags = 0;
            }

            // Where the entity was when last projected, and where it is now — the decoded wire positions.
            float pushOldX = 0f, pushOldY = 0f, pushOldZ = 0f, pushNewX = 0f, pushNewY = 0f, pushNewZ = 0f;
            var pushFlags = (byte)0;

            // ── Position: quantized, compared, stored; then the motion rule decides whether it becomes a SEGMENT (P1-10) ──────────────────────────────────
            if (position != null && positionBytes > 0)
            {
                var quantizedSlot = positionCodes + (slot * positionBytes);
                var quantized = new ReadOnlySpan<byte>(quantizedSlot, positionBytes);
                var stored = coldBytes + layout.PrevPositionOffsetInColdEntry;
                var visibility = coldBytes + layout.VisibilityPositionOffsetInColdEntry;
                if (push != null)
                {
                    if (!initialize)
                    {
                        push.Decode(pushIndex, visibility, out pushOldX, out pushOldY, out pushOldZ);
                        pushFlags |= PushEvent.HasOld;
                    }

                    push.Decode(pushIndex, quantizedSlot, out pushNewX, out pushNewY, out pushNewZ);
                    pushFlags |= PushEvent.HasNew;
                }
                var moved = initialize || !new ReadOnlySpan<byte>(stored, positionBytes).SequenceEqual(quantized);
                var epochBefore = ownVisibility && motion.Enabled ? hotBytes[motion.SegmentOffset + motion.SegmentEpochOffset] : (byte)0;

                // BEFORE the previous position is overwritten, because the rule's teleport and run-departure tests are about this tick's STEP, which only
                // exists while both positions are still there. The rule owns the whole of the hot entry's segment region and the motion tick in
                // GroupTicks[0]: a position that changed is not a segment, and stamping the tick for every change would send one to every client every tick,
                // which is the traffic segments exist to remove.
                MotionTracker.Update(in motion, hot, coldBytes, quantizedSlot, velocityColumn, slot, initialize, tick, motionScratch, ref segmentsEmitted,
                    ref shadowSegments);

                if (moved)
                {
                    quantized.CopyTo(new Span<byte>(stored, positionBytes));
                    hot->Flags |= FlagPositionChanged;
                }

                if (ownVisibility)
                {
                    var dx = pushNewX - pushOldX;
                    var dy = pushNewY - pushOldY;
                    var dz = pushNewZ - pushOldZ;
                    var teleported = motion.Enabled && hotBytes[motion.SegmentOffset + motion.SegmentEpochOffset] != epochBefore;
                    if (initialize || teleported || (dx * dx) + (dy * dy) + (dz * dz) > slackSquared)
                    {
                        quantized.CopyTo(new Span<byte>(visibility, positionBytes));
                    }
                    else
                    {
                        // v̂ stays: the event carries it on both sides, so it is dropped unless a segment or a group made it one.
                        pushNewX = pushOldX;
                        pushNewY = pushOldY;
                        pushNewZ = pushOldZ;
                    }
                }

                // A client dead-reckons a mover until told it stopped, so a slot still extrapolating is pushed by the engine next tick —
                // the one push a developer cannot be asked to make, because nothing the application writes marks a stop.
                if (push != null && motion.Enabled && MotionTracker.IsExtrapolating(in motion, hotBytes))
                {
                    repush |= 1UL << slot;
                }
            }
            else if (position != null && initialize && layout.EnterPositionBytes > 0)
            {
                // A static position: no previous copy is kept and no segment is reserved, because it never changes and nothing extrapolates from it. It is
                // read once, here, into the cold entry's enter cache — the one place an enter record can find it on a later tick.
                QuantizePosition(position, frame, clusterBase, transientBase, clusterLayout, slot, quantizedPosition);
                var staticBytes = layout.EnterPositionBytes;
                quantizedPosition[..staticBytes].CopyTo(new Span<byte>(coldBytes + layout.EnterPositionOffsetInColdEntry, staticBytes));
                if (push != null)
                {
                    push.Decode(pushIndex, coldBytes + layout.EnterPositionOffsetInColdEntry, out pushNewX, out pushNewY, out pushNewZ);
                    pushFlags |= PushEvent.HasNew;
                }
            }
            else if (push != null && position != null && layout.EnterPositionBytes > 0)
            {
                // A static entity pushed again is where it always was.
                push.Decode(pushIndex, coldBytes + layout.EnterPositionOffsetInColdEntry, out pushNewX, out pushNewY, out pushNewZ);
                pushOldX = pushNewX;
                pushOldY = pushNewY;
                pushOldZ = pushNewZ;
                pushFlags |= PushEvent.HasOld | PushEvent.HasNew;
            }

            // ── Headings: the deadband, before the groups compare (09 § 15) ────────────────────────────────────────────────────────────────────────────────
            if (layout.HeadingBytes > 0)
            {
                ApplyHeadingDeadband(fields, codes, slot, coldBytes + layout.HeadingOffsetInColdEntry, initialize);
            }

            // ── Groups: encode, compare, stamp ──────────────────────────────────────────────────────────────────────────────────────────────────────────
            var text = new TextSource(state, clusterLayout, clusterBase, transientBase, slot, collections, references);
            var wideFailed = false;
            var named = new CollectionNames(collectionIndex, worker, hot->Entity.RawValue);
            collections?.Unresolved = false;

            var changed = EncodeAndCompare(plan.Groups, fields, 0, codes, slot, pack, groupScratch, hotBytes + layout.PackedStateOffsetInHotEntry,
                groupLength, groupOffset, hot, tick, initialize, in text, wideBodies, ref wideFailed, state.CollectionReferenceSections, in named);

            var ownerChanged = 0;
            if (ownerFields.Length > 0)
            {
                ownerChanged = EncodeAndCompare(plan.OwnerGroups, ownerFields, fields.Length, codes, slot, pack, ownerScratch,
                    blockBytes + layout.OwnerOffset + (slot * layout.OwnerEntrySize), ownerLength, ownerOffset, hot, tick, initialize, in text, wideBodies,
                    ref wideFailed, state.CollectionReferenceOwnerSections, in named, stampTicks: false);
            }

            if (collections is { Unresolved: true })
            {
                collectionUnresolved |= 1UL << slot;
                repush |= 1UL << slot;
            }

            // ── The enter cache, before the event: a failure to store it has to be able to take the initialization back ───────────────────────────────
            //
            // Written at an initialization, and again for a described entity whose wide onEnter body is missing — an entry a pass left before storing
            // it, which every later enter record would otherwise have nothing to copy.
            if (layout.EnterBodyBytes > 0 && (initialize || (plan.OnEnter.Wide
                    && Unsafe.ReadUnaligned<uint>(coldBytes + layout.EnterBodyOffsetInColdEntry + plan.OnEnter.StoredOffset) == 0)))
            {
                var onEnterLength = EncodeSection(fields, plan.OnEnter, codes, 0, slot, pack, new Span<byte>(enterScratch, enterBytes), in text);

                // 03 § 5's body(onEnter). An onEnter field appears in no state record and no group body, so a session that first sees this entity on a later
                // tick would otherwise have no enter body to be sent, and the frame stage would have to re-encode one per session from the columns.
                // Zero-padded to the section's widest form, exactly as a stored group body is, so the frame stage recovers the real length by walking the
                // section rather than by storing one — or, for a wide section, kept out of line with its length.
                var cache = coldBytes + layout.EnterBodyOffsetInColdEntry + plan.OnEnter.StoredOffset;
                if (plan.OnEnter.Wide)
                {
                    wideFailed |= !StoreWide(wideBodies, cache, new ReadOnlySpan<byte>(enterScratch, onEnterLength));
                }
                else
                {
                    var inline = new Span<byte>(cache, layout.EnterBodyBytes);
                    inline.Clear();
                    new ReadOnlySpan<byte>(enterScratch, onEnterLength).CopyTo(inline);
                }
            }

            if (wideFailed)
            {
                // The arena's budget is spent (13 § 6.2). An entity already described keeps the bytes it had — the failed section was neither replaced nor
                // stamped — and is pushed again next tick. A NEW one cannot be described at all, so its initialization is taken back whole, as a lease
                // that ran dry would have left it: no identity, no event, no record, and tried again next tick.
                state.NoteWideDeferral();
                repush |= 1UL << slot;
                if (initialize)
                {
                    // No identity was taken yet, so there is none to give back.
                    ClearEntry(state, blockBytes, layout, slot, worker);
                    continue;
                }
            }

            if (initialize)
            {
                if (takeIdentity)
                {
                    // Cannot be refused: the lease was found non-empty above, and only this worker spends it.
                    var netId = leases.Take(worker);
                    hot->NetId = netId;
                    hot->Generation = state.NetIds.GenerationOf(netId);
                }

                state.EntityIndex?.Bind(hot->NetId, hot->Entity);
            }

            // What the entity's bodies now name: each reference that resolved differently moves its count in the reverse index (13 § 5). After the
            // deferral above: an inline section is always stored, but a wide one may have kept the body it had, so on a deferral a reference in a wide
            // section keeps its held netId until a pass stores it — the entity is pushed again meanwhile.
            if (referenceIndex != null)
            {
                var held = coldBytes + layout.ReferenceOffsetInColdEntry;
                var referrer = hot->Entity.RawValue;
                NoteReferences(referenceIndex, worker, fields, plan.Groups, 0, codes, slot, held, referrer, wideFailed);
                NoteReferences(referenceIndex, worker, ownerFields, plan.OwnerGroups, fields.Length, codes, slot, held, referrer, wideFailed);
            }

            if (ownerChanged != 0)
            {
                hot->Flags |= (ushort)(ownerChanged << OwnerChangedMaskShift);

                // To the sessions that control this entity, as a pending mask their next published frame's SELF carries (11 § 2.2).
                state.Self?.Notice(hot->Entity, ownerChanged);
            }

            if (push != null)
            {
                // The stamp is the tick of the entity's last EVENT, written by AddEvent when it records one. The sweep, the cell delivery and the push
                // log's catch-up all read it as "the push step owns this entity from that tick on".
                if ((pushArrived & (1UL << slot)) != 0)
                {
                    pushFlags |= PushEvent.Arrived;
                }

                push.AddEvent(worker, pushIndex, block, slot, hot, hot->NetId, pushFlags, pushOldX, pushOldY, pushOldZ, pushNewX, pushNewY, pushNewZ);
            }

            if (initialize)
            {
                records++;
            }
            else if (changed != 0)
            {
                records++;
            }
        }

        if (repush != 0)
        {
            hub?.Repush(pushIndex, block->ChunkId, repush);
        }
        // ── Arrivals, consumed ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // An entity carried in from another cluster since this block was last projected. Read above (pushArrived) to flag its event; cleared here.
        // Exchanged rather than read-then-cleared: the fence's migration slices are the other writer and they run in parallel with each other.
        Interlocked.Exchange(ref block->ArrivedSlots, 0UL);

        // A referrer still waiting on its target's identity is "not projected" for the dormant skip, which then lets its re-push through.
        block->ProjectedWatchedMask = watched & ~(unresolved | collectionUnresolved);
        block->ProjectedOccupancy = *(ulong*)clusterBase;
        state.NoteProjected(blocks: 1, slots: visited, records: records, releases: released);
        state.NoteSegments(segmentsEmitted, shadowSegments);
    }

    // ── Column walk ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    // Returns the slots whose reference named an entity with no identity yet.
    private static ulong Quantize(ArchetypeReplicationState state, CompiledField[] fields, int rowBase, ArchetypeClusterInfo clusterLayout, byte* clusterBase,
        byte* transientBase, int slotCount, ulong slots, ulong* codes, ReferenceResolver references)
    {
        var walkFrom = FrameAssembler.PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        var unresolved = 0UL;
        for (var i = 0; i < fields.Length; i++)
        {
            // A packed field is read exactly like any other: it is packed on the WIRE, not absent from the projection.
            ref readonly var field = ref fields[i];
            var storeBase = StoreFor(clusterLayout, transientBase, clusterBase, field.ComponentSlot);
            if (field.Path == ColumnPath.EntityRef)
            {
                unresolved |= Resolve(field, storeBase, slots, codes + ((rowBase + i) * MaxSlots), references);
                continue;
            }

            var column = new ReadOnlySpan<byte>(storeBase + field.ComponentOffsetInCluster, slotCount * field.ComponentSize);
            var row = new Span<ulong>(codes + ((rowBase + i) * MaxSlots), MaxSlots);
            var clamps = ProjectionColumnWalk.Quantize(field, ProjectionColumn.Over(column, field), slots, row);
            if (clamps != 0)
            {
                // A declared narrowing that bit: counted per field, so "my balance shows 4 294 967 295" has a number behind it (13 § 2.3).
                state.NoteClamps(rowBase + i, clamps);
            }
        }

        if (walkFrom != 0L)
        {
            Interlocked.Add(ref SubscriptionsProjectExecSystem.ProjectWalkTicks, Stopwatch.GetTimestamp() - walkFrom);
        }

        return unresolved;
    }

    /// <summary>
    /// A reference column's codes: the netId each slot's <see cref="EntityId"/> names, through the worker's resolver (13 § 5). Returns the slots whose
    /// target has no identity yet.
    /// </summary>
    private static ulong Resolve(in CompiledField field, byte* storeBase, ulong slots, ulong* row, ReferenceResolver references)
    {
        var unresolved = 0UL;
        var column = storeBase + field.ComponentOffsetInCluster + field.FieldOffsetInComponent;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var target = EntityId.FromRaw(Unsafe.ReadUnaligned<long>(column + (slot * field.ComponentSize)));
            var pending = false;
            row[slot] = references?.Resolve(target, out pending) ?? NetIdAllocator.NoNetId;
            if (pending)
            {
                unresolved |= 1UL << slot;
            }
        }

        return unresolved;
    }

    /// <summary>
    /// Compares each reference field's netId with the one the entry last held for it and logs the change to the reverse index: −1 for the netId the
    /// bodies stop naming, +1 for the one they name now.
    /// </summary>
    private static void NoteReferences(ReferenceIndex index, int worker, CompiledField[] fields, CompiledGroup[] groups, int rowBase, ulong* codes, int slot,
        byte* held, ulong referrer, bool skipWide)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            ref readonly var field = ref fields[i];

            // A reference is never in onEnter (refused at Start), so its group is its section's.
            if (field.Path != ColumnPath.EntityRef || (skipWide && groups[field.GroupBit].Section.Wide))
            {
                continue;
            }

            // Cold-entry memory: native, engine-owned, at whatever alignment the regions before it leave.
            var at = held + (field.ReferenceSlot * sizeof(uint));
            var was = Unsafe.ReadUnaligned<uint>(at);
            var now = (uint)codes[((rowBase + i) * MaxSlots) + slot];
            if (was == now)
            {
                continue;
            }

            if (was != NetIdAllocator.NoNetId)
            {
                index.Note(worker, was, referrer, -1);
            }

            if (now != NetIdAllocator.NoNetId)
            {
                index.Note(worker, now, referrer, +1);
            }

            Unsafe.WriteUnaligned(at, now);
        }
    }

    /// <summary>
    /// The store a component's column lives in. A mixed archetype keeps its transient components in a second segment whose clusters share the persistent
    /// layout exactly, so the offset is the same and only the base differs — which is the rule <c>ClusterRef.ResolveBase</c> already applies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* StoreFor(ArchetypeClusterInfo clusterLayout, byte* transientBase, byte* clusterBase, byte componentSlot) =>
        transientBase != null && (clusterLayout.TransientSlotMask & (1 << componentSlot)) != 0 ? transientBase : clusterBase;

    /// <summary>
    /// A heading's deadband in code space (09 § 15, SUB-10): a new code within the tolerance of the one the client holds is replaced by the held one, so the
    /// group body the comparison sees is unchanged and nothing is sent; past it, the new code becomes the held one. The distance is taken modulo the angle's
    /// full turn, so the wrap at ±π is no special case — and the compare is still the encode: what is compared is what would be sent.
    /// </summary>
    private static void ApplyHeadingDeadband(CompiledField[] fields, ulong* codes, int slot, byte* held, bool initialize)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            ref readonly var field = ref fields[i];
            if (field.HeadingPlusOne == 0)
            {
                continue;
            }

            // An angle's code is at most 32 bits wide, so the held copy stays a uint and the arithmetic stays in 32 bits.
            var code = codes + (i * MaxSlots) + slot;
            var kept = (uint*)(held + (4 * (field.HeadingPlusOne - 1)));
            var current = (uint)*code;
            if (!initialize)
            {
                var mask = field.CodecBits >= 32 ? uint.MaxValue : (1u << field.CodecBits) - 1;
                var diff = (current - *kept) & mask;
                var distance = Math.Min(diff, (mask - diff) + 1);
                if (distance <= field.HeadingToleranceCodes)
                {
                    *code = *kept;
                    continue;
                }
            }

            *kept = current;
        }
    }

    // ── Sections ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes each group's body from the codes, compares it with the copy the entry holds and stamps the group's tick where they differ.
    /// </summary>
    /// <returns>The mask of groups whose body changed.</returns>
    private static int EncodeAndCompare(CompiledGroup[] groups, CompiledField[] fields, int rowBase, ulong* codes, int slot, Span<byte> pack, byte* scratch,
        byte* stored, Span<int> lengths, Span<int> offsets, ReplicationHotEntry* hot, uint tick, bool initialize, in TextSource text, WideBodyArena wideBodies,
        ref bool wideFailed, SectionPlan[] referenceSections, in CollectionNames named, bool stampTicks = true)
    {
        var changed = 0;
        for (var g = 0; g < groups.Length; g++)
        {
            ref readonly var group = ref groups[g];
            var max = group.Section.MaxBodyBytes;
            var destination = new Span<byte>(scratch + offsets[g], max);

            // A group whose collection's element names entities: the netIds it names now are gathered by the encode, and compared with the stored body's.
            var referencing = named.Index != null && (uint)g < (uint)referenceSections.Length ? referenceSections[g] : null;
            text.Collections?.ResetNetIds();

            // Zeroed BEFORE the encode, not after it: the padding is what makes the fixed-width comparison below exact, and clearing only the tail would
            // leave whatever the previous slot's longer body wrote there.
            destination.Clear();
            var length = EncodeSection(fields, group.Section, codes, rowBase, slot, pack, destination, in text);
            lengths[g] = length;

            var at = stored + group.Section.StoredOffset;
            if (group.Section.Wide)
            {
                // Out of line (13 § 6.3): compared on length and bytes, and replaced — re-allocated only when its size class changes — where they differ.
                if (!initialize && WideEquals(wideBodies, at, destination[..length]))
                {
                    continue;
                }

                // What the body named before it is replaced: the reverse index counts the difference, per occurrence (13 § 5).
                var before = referencing != null ? text.Collections.NamedBy(wideBodies, at, referencing) : default;
                if (!StoreWide(wideBodies, at, destination[..length]))
                {
                    wideFailed = true;
                    continue;
                }

                if (referencing != null)
                {
                    NoteCollectionReferences(in named, before, text.Collections.NetIds);
                }
            }
            else
            {
                var current = new Span<byte>(at, max);
                if (!initialize && destination.SequenceEqual(current))
                {
                    continue;
                }

                destination.CopyTo(current);
            }

            changed |= 1 << group.Bit;
            if (stampTicks && group.TickSlot >= 0)
            {
                hot->GroupTicks[group.TickSlot] = tick;
            }
        }

        return changed;
    }

    /// <summary>
    /// Encodes one section's body for one slot: its leading bit pack, then each byte-aligned field's code in wire order (03 § 5, W11/W12).
    /// </summary>
    /// <returns>Bytes written.</returns>
    private static int EncodeSection(CompiledField[] fields, in CompiledSection section, ulong* codes, int rowBase, int slot, Span<byte> pack,
        Span<byte> destination, in TextSource text)
    {
        if (section.FieldCount == 0)
        {
            return 0;
        }

        var writer = new WireWriter(destination);
        WriteSectionBody(ref writer, fields, section, codes, rowBase, slot, pack, in text);
        return writer.Position;
    }

    /// <summary>
    /// Writes one section's body into <paramref name="writer"/>: a record section's, or a collection element's — whose clamps and cuts count on the
    /// collection's row, <paramref name="clampRow"/>, rather than on a row of their own.
    /// </summary>
    private static void WriteSectionBody(ref WireWriter writer, CompiledField[] fields, in CompiledSection section, ulong* codes, int rowBase, int slot,
        Span<byte> pack, in TextSource text, int clampRow = -1)
    {
        if (section.PackBytes > 0)
        {
            var packed = pack[..section.PackBytes];
            packed.Clear();
            for (var i = 0; i < section.PackedCount; i++)
            {
                var index = section.FirstField + i;
                ref readonly var field = ref fields[index];
                var code = (uint)codes[((rowBase + index) * MaxSlots) + slot];
                var mask = field.BitCount >= 32 ? uint.MaxValue : (1u << field.BitCount) - 1;
                FieldCodec.WritePackedBits(packed, field.BitOffset, field.BitCount, code & mask);
            }

            writer.WriteBytes(packed);
        }

        for (var i = section.PackedCount; i < section.FieldCount; i++)
        {
            var index = section.FirstField + i;
            ref readonly var field = ref fields[index];
            if (field.Path == ColumnPath.Text)
            {
                if (text.Write(ref writer, field))
                {
                    // Cut at its cap, at a code point: counted on the field's row, beside the clamps (13 § 2.3).
                    text.State.NoteClamps(clampRow >= 0 ? clampRow : rowBase + index, 1);
                }

                continue;
            }

            if (field.Path == ColumnPath.Collection)
            {
                WriteCollection(ref writer, field, in text);
                continue;
            }

            WriteCode(ref writer, field, codes[((rowBase + index) * MaxSlots) + slot]);
        }
    }

    /// <summary>
    /// Writes a collection (W34, 13 § 6.5): its real count and the count sent — at most its <c>maxCount</c>, a cut counted on its row — then each element as
    /// a section of its own. Elements are read from the buffer in batches of <see cref="MaxSlots"/> and walked column by column, as a cluster's slots are;
    /// a reference is resolved per element, and the netIds named are gathered for the reverse index.
    /// </summary>
    private static void WriteCollection(ref WireWriter writer, in CompiledField field, in TextSource text)
    {
        var collection = field.Collection;
        var context = text.Collections;
        var state = text.State;
        var segments = state.CollectionSegments;
        if (context == null || (uint)collection.Index >= (uint)segments.Length)
        {
            // No reader — a fixture driving the pass bare: an empty collection, still well-formed.
            writer.WriteVaru(0);
            writer.WriteVaru(0);
            return;
        }

        var segment = segments[collection.Index];
        ref var accessor = ref context.Accessor(collection.Index);
        var bufferId = Unsafe.ReadUnaligned<int>(text.FieldAddress(field));
        var total = segment.GetElementCount(bufferId, ref accessor);
        var sent = Math.Min(total, collection.MaxCount);
        writer.WriteVaru((uint)total);
        writer.WriteVaru((uint)sent);
        if (sent < total)
        {
            state.NoteClamps(collection.Row, 1);
        }

        var size = collection.ElementSize;
        var bytes = context.ElementBytes;
        var codes = context.ElementCodes;
        var pack = new Span<byte>(context.ElementPack, context.ElementPackBytes);
        var cursor = new VariableSizedBufferSegmentBase<PersistentStore>.BufferCursor(bufferId);
        for (var done = 0; done < sent;)
        {
            var batch = Math.Min(MaxSlots, sent - done);
            var elements = new Span<byte>(bytes, batch * size);
            var read = segment.ReadElementsUnlocked(ref cursor, elements, ref accessor);
            if (read < batch)
            {
                // A buffer shorter than its count: unreachable in the exclusive window, and zeroes encode rather than whatever the scratch held.
                elements[(read * size)..].Clear();
            }

            var mask = batch >= MaxSlots ? ulong.MaxValue : (1UL << batch) - 1;
            for (var i = 0; i < collection.Fields.Length; i++)
            {
                ref readonly var element = ref collection.Fields[i];
                var row = codes + (i * MaxSlots);
                switch (element.Path)
                {
                    case ColumnPath.Text:
                    case ColumnPath.Collection:
                        continue;
                    case ColumnPath.EntityRef:
                        ResolveElements(in element, bytes, batch, row, text.References, context);
                        continue;
                }

                var clamps = ProjectionColumnWalk.Quantize(element, ProjectionColumn.Over(elements, element), mask, new Span<ulong>(row, MaxSlots));
                if (clamps != 0)
                {
                    state.NoteClamps(collection.Row, clamps);
                }
            }

            for (var e = 0; e < batch; e++)
            {
                var source = new TextSource(state, text.Layout, bytes, null, e);
                WriteSectionBody(ref writer, collection.Fields, collection.Section, codes, 0, e, pack, in source, collection.Row);
            }

            done += batch;
        }
    }

    // A reference element field's codes: the netId each element names, gathered for the reverse index; a target with no identity yet pushes the entity
    // again (13 § 5). Pointers over the context's batch (native scratch), read at e < count ≤ MaxSlots elements of field.ComponentSize: the column
    // walk's own idiom, which these rows feed.
    private static void ResolveElements(in CompiledField field, byte* elements, int count, ulong* row, ReferenceResolver references, CollectionContext context)
    {
        for (var e = 0; e < count; e++)
        {
            var target = EntityId.FromRaw(Unsafe.ReadUnaligned<long>(elements + (e * field.ComponentSize) + field.FieldOffsetInComponent));
            var pending = false;
            var netId = references?.Resolve(target, out pending) ?? NetIdAllocator.NoNetId;
            row[e] = netId;
            context.Unresolved |= pending;
            if (netId != NetIdAllocator.NoNetId)
            {
                context.AddNetId(netId);
            }
        }
    }

    /// <summary>Who names what, for a collection's references: the index, the worker's log and the referrer.</summary>
    private readonly struct CollectionNames
    {
        public CollectionNames(ReferenceIndex index, int worker, ulong referrer)
        {
            Index = index;
            Worker = worker;
            Referrer = referrer;
        }

        public ReferenceIndex Index { get; }

        public int Worker { get; }

        public ulong Referrer { get; }
    }

    // The netIds a collection body names now against those it named: each one's change in occurrences is one delta in the reverse index.
    private static void NoteCollectionReferences(in CollectionNames named, Span<uint> before, Span<uint> after)
    {
        before.Sort();
        after.Sort();
        int b = 0, a = 0;
        while (b < before.Length || a < after.Length)
        {
            var netId = b < before.Length && (a >= after.Length || before[b] <= after[a]) ? before[b] : after[a];
            var delta = 0;
            while (b < before.Length && before[b] == netId)
            {
                delta--;
                b++;
            }

            while (a < after.Length && after[a] == netId)
            {
                delta++;
                a++;
            }

            if (delta != 0)
            {
                named.Index.Note(named.Worker, netId, named.Referrer, delta);
            }
        }
    }

    /// <summary>Whether a wide section's stored body is exactly <paramref name="body"/>: the same length, then the same bytes.</summary>
    // A reference sits in block memory — native, engine-owned — at any alignment its region gives it: read and written unaligned, through the region
    // pointer the pass already holds for the entry.
    private static bool WideEquals(WideBodyArena arena, byte* reference, ReadOnlySpan<byte> body)
    {
        var handle = Unsafe.ReadUnaligned<uint>(reference);
        var length = Unsafe.ReadUnaligned<uint>(reference + sizeof(uint));
        return handle != 0 && length == (uint)body.Length && arena.Read(handle, length).SequenceEqual(body);
    }

    /// <summary>
    /// Stores a wide section's body and its length through its reference; <see langword="false"/>, with the reference untouched, on exhaustion.
    /// </summary>
    private static bool StoreWide(WideBodyArena arena, byte* reference, ReadOnlySpan<byte> body)
    {
        var handle = Unsafe.ReadUnaligned<uint>(reference);
        if (!arena.Store(ref handle, body))
        {
            return false;
        }

        Unsafe.WriteUnaligned(reference, handle);
        Unsafe.WriteUnaligned(reference + sizeof(uint), (uint)body.Length);
        return true;
    }

    /// <summary>
    /// What a section encoder needs to read a text field (13 § 2.1): the slot's cluster, and the state that counts a truncation. Text has no code row — it
    /// is read here, once per encode, straight from its column.
    /// </summary>
    private readonly struct TextSource
    {
        private readonly byte* _clusterBase;
        private readonly byte* _transientBase;
        private readonly int _slot;

        public TextSource(ArchetypeReplicationState state, ArchetypeClusterInfo layout, byte* clusterBase, byte* transientBase, int slot,
            CollectionContext collections = null, ReferenceResolver references = null)
        {
            State = state;
            Layout = layout;
            _clusterBase = clusterBase;
            _transientBase = transientBase;
            _slot = slot;
            Collections = collections;
            References = references;
        }

        public ArchetypeReplicationState State { get; }

        /// <summary>The cluster layout the columns are read through.</summary>
        public ArchetypeClusterInfo Layout { get; }

        /// <summary>The worker's collection reader, or <see langword="null"/> where no collection is read — inside an element, or a bare fixture.</summary>
        public CollectionContext Collections { get; }

        /// <summary>The worker's reference reader, for a collection's reference elements; <see langword="null"/> resolves every one to 0.</summary>
        public ReferenceResolver References { get; }

        /// <summary>Where the slot's value of <paramref name="field"/> is.</summary>
        public byte* FieldAddress(in CompiledField field) =>
            StoreFor(Layout, _transientBase, _clusterBase, field.ComponentSlot) + field.ComponentOffsetInCluster + (_slot * field.ComponentSize)
            + field.FieldOffsetInComponent;

        /// <summary>Writes the slot's text as a <c>str</c>, cut to the codec's cap at a code point; <see langword="true"/> when it was cut.</summary>
        public bool Write(ref WireWriter writer, in CompiledField field)
        {
            var at = FieldAddress(field);
            var stored = new ReadOnlySpan<byte>(at, field.TextCapacity - 1);
            var end = stored.IndexOf((byte)0);
            var utf8 = end < 0 ? stored : stored[..end];
            var cut = MessageText.ValidPrefix(utf8, field.Codec.MaxBytes);
            writer.WriteStr(cut, field.Codec.MaxBytes);
            return cut.Length < utf8.Length;
        }
    }

    /// <summary>
    /// Writes one already-quantized code in its codec's byte-aligned form.
    /// </summary>
    /// <remarks>
    /// A code, not a value: <see cref="FieldCodec.WriteNumber"/> takes the value and quantizes on the way out, which would be the second quantization of the
    /// same number and the one place a rounding difference could put different bytes on the wire from the ones the comparison above accepted. The framing is
    /// still <see cref="WireWriter"/>'s — nothing here re-spells a varint or an endianness.
    /// </remarks>
    internal static void WriteCode(ref WireWriter writer, in CompiledField field, ulong code)
    {
        switch (field.CodecKind)
        {
            case CodecKind.U8:
            case CodecKind.I8:
                writer.WriteU8((byte)code);
                break;
            case CodecKind.U16:
            case CodecKind.I16:
            case CodecKind.F16:
            case CodecKind.TickLo:
                writer.WriteU16((ushort)code);
                break;
            case CodecKind.U32:
            case CodecKind.I32:
            case CodecKind.F32:
                writer.WriteU32(unchecked((uint)code));
                break;
            case CodecKind.Varu:
            case CodecKind.EntityRef:
                writer.WriteVaru(unchecked((uint)code));
                break;
            case CodecKind.Vari:
                writer.WriteVari(unchecked((int)code));
                break;
            case CodecKind.Quant:
            case CodecKind.Unorm:
            case CodecKind.Snorm:
            case CodecKind.Angle:
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                writer.WriteBits(unchecked((uint)code), field.CodecBits);
                break;
            case CodecKind.Quat3:
                writer.WriteU32(unchecked((uint)code));
                break;
            case CodecKind.U64:
            case CodecKind.I64:
            case CodecKind.F64:
                // W32: the code is the bit pattern — the value's, the two's complement's, or the double's.
                writer.WriteU64(code);
                break;
            case CodecKind.Varu64:
                writer.WriteVaru64(code);
                break;
            case CodecKind.Vari64:
                writer.WriteVari64(unchecked((long)code));
                break;
            default:
                throw new InvalidOperationException(
                    $"Field '{field.Name}' carries codec '{CodecTokens.ToToken(field.CodecKind)}', which a column walk cannot produce a single code for. "
                  + "The compiler refuses these at Start; reaching here means one slipped through.");
        }
    }

    // ── Position ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quantizes one slot's position, axis by axis, into <paramref name="destination"/>: the archetype's <c>pos2</c>/<c>pos3</c> codec over the realm's
    /// <paramref name="frame"/>, little-endian, the same bytes a segment or an enter carries.
    /// </summary>
    private static void QuantizePosition(CompiledPosition position, PositionFrame frame, byte* clusterBase, byte* transientBase,
        ArchetypeClusterInfo clusterLayout, int slot, Span<byte> destination)
    {
        var storeBase = StoreFor(clusterLayout, transientBase, clusterBase, position.ComponentSlot);
        var value = storeBase + position.ComponentOffsetInCluster + (slot * position.ComponentSize) + position.FieldOffsetInComponent;
        var bytes = frame.AxisBytes;
        for (var axis = 0; axis < position.Dims; axis++)
        {
            var code = WireMath.EncodeQuant(Centre(position.SpatialFieldType, value, axis, position.Dims), frame.Min[axis], frame.Max[axis], frame.Bits);
            for (var i = 0; i < bytes; i++)
            {
                destination[(axis * bytes) + i] = (byte)(code >> (8 * i));
            }
        }
    }

    /// <summary>
    /// Quantizes the position of every slot in <paramref name="slots"/> into <paramref name="codes"/>, slot s at s × <paramref name="bytesPerSlot"/>, giving
    /// each the bytes <see cref="QuantizePosition"/> gives it: the same operations on the same values, with the frame's step taken from
    /// <see cref="PositionFrame.Step"/> and the spatial type chosen once for the block. One division per axis remains — a product by 1/step would move the
    /// last bit (W1).
    /// </summary>
    private static void QuantizePositions(CompiledPosition position, PositionFrame frame, byte* clusterBase, byte* transientBase,
        ArchetypeClusterInfo clusterLayout, ulong slots, byte* codes, int bytesPerSlot)
    {
        var column = StoreFor(clusterLayout, transientBase, clusterBase, position.ComponentSlot) + position.ComponentOffsetInCluster
            + position.FieldOffsetInComponent;
        switch (position.SpatialFieldType)
        {
            case SpatialFieldType.AABB2F:
            case SpatialFieldType.AABB3F:
                QuantizePositions<BoxF>(position, frame, column, slots, codes, bytesPerSlot);
                break;
            case SpatialFieldType.AABB2D:
            case SpatialFieldType.AABB3D:
                QuantizePositions<BoxD>(position, frame, column, slots, codes, bytesPerSlot);
                break;
            case SpatialFieldType.BSphere2F:
            case SpatialFieldType.BSphere3F:
                QuantizePositions<PointF>(position, frame, column, slots, codes, bytesPerSlot);
                break;
            default:
                QuantizePositions<PointD>(position, frame, column, slots, codes, bytesPerSlot);
                break;
        }
    }

    private static void QuantizePositions<TCentre>(CompiledPosition position, PositionFrame frame, byte* column, ulong slots, byte* codes, int bytesPerSlot)
        where TCentre : struct, IPositionCentre
    {
        var dims = position.Dims;
        var size = position.ComponentSize;
        var axisBytes = frame.AxisBytes;
        var top = WireMath.Pow2(frame.Bits) - 1;
        double minX = frame.Min[0], minY = frame.Min[1], minZ = dims > 2 ? frame.Min[2] : 0d;
        double stepX = frame.Step[0], stepY = frame.Step[1], stepZ = dims > 2 ? frame.Step[2] : 0d;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            var value = column + (slot * size);
            var destination = codes + (slot * bytesPerSlot);
            WriteAxis(destination, axisBytes, WireMath.EncodeQuantWithStep(TCentre.Of(value, 0, dims), minX, stepX, top));
            WriteAxis(destination + axisBytes, axisBytes, WireMath.EncodeQuantWithStep(TCentre.Of(value, 1, dims), minY, stepY, top));
            if (dims > 2)
            {
                WriteAxis(destination + (2 * axisBytes), axisBytes, WireMath.EncodeQuantWithStep(TCentre.Of(value, 2, dims), minZ, stepZ, top));
            }
        }
    }

    // One axis's code, little-endian, in its byte width.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteAxis(byte* destination, int axisBytes, uint code)
    {
        for (var i = 0; i < axisBytes; i++)
        {
            destination[i] = (byte)(code >> (8 * i));
        }
    }

    // What Centre computes for one spatial type, as a type parameter so a block's pass is specialized for its type rather than switching per slot and axis.
    // The expressions are Centre's own, operand for operand: a box's sum is a float sum, widened after.
    private interface IPositionCentre
    {
        static abstract double Of(byte* value, int axis, int dims);
    }

    private struct BoxF : IPositionCentre
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Of(byte* value, int axis, int dims) =>
            (Unsafe.ReadUnaligned<float>(ref value[axis * 4]) + Unsafe.ReadUnaligned<float>(ref value[(dims + axis) * 4])) * 0.5d;
    }

    private struct BoxD : IPositionCentre
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Of(byte* value, int axis, int dims) =>
            (Unsafe.ReadUnaligned<double>(ref value[axis * 8]) + Unsafe.ReadUnaligned<double>(ref value[(dims + axis) * 8])) * 0.5d;
    }

    private struct PointF : IPositionCentre
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Of(byte* value, int axis, int dims) => Unsafe.ReadUnaligned<float>(ref value[axis * 4]);
    }

    private struct PointD : IPositionCentre
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Of(byte* value, int axis, int dims) => Unsafe.ReadUnaligned<double>(ref value[axis * 8]);
    }

    /// <summary>The position an axis of a spatial value reports: a box's mid-point, or a sphere's centre.</summary>
    private static double Centre(SpatialFieldType type, byte* value, int axis, int dims)
    {
        switch (type)
        {
            case SpatialFieldType.AABB2F:
            case SpatialFieldType.AABB3F:
                return (Unsafe.ReadUnaligned<float>(ref value[axis * 4]) + Unsafe.ReadUnaligned<float>(ref value[(dims + axis) * 4])) * 0.5d;
            case SpatialFieldType.AABB2D:
            case SpatialFieldType.AABB3D:
                return (Unsafe.ReadUnaligned<double>(ref value[axis * 8]) + Unsafe.ReadUnaligned<double>(ref value[(dims + axis) * 8])) * 0.5d;
            case SpatialFieldType.BSphere2F:
            case SpatialFieldType.BSphere3F:
                return Unsafe.ReadUnaligned<float>(ref value[axis * 4]);
            default:
                return Unsafe.ReadUnaligned<double>(ref value[axis * 8]);
        }
    }

    /// <summary>
    /// A slot whose identity ends here — destroyed, or displaced by a reuse — leaves every session that holds it; its identity and last position are kept
    /// for the tick's events, which may still name it (09 § 11: "X killed Y").
    /// </summary>
    private static void EmitPushLeave(PushReplication push, int archetype, int worker, ReplicationBlockHeader* block, byte* blockBytes,
        in ReplicationBlockLayout layout, int slot, uint netId, NetIdLeaseSet leases, EntityId entity)
    {
        push.Decode(archetype, blockBytes + layout.ColdOffset + (slot * layout.ColdStride) + push.PositionOffset(archetype), out var x, out var y,
            out var z);
        push.AddEvent(worker, archetype, block, slot, null, netId, PushEvent.HasOld, x, y, z, 0f, 0f, 0f);
        leases.Depart(worker, entity, netId, x, y, z, push.ServedRealm);
    }

    // ── Entry helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zeroes one slot's hot and cold entries — and its owner entry when the archetype has one — so nothing of the previous occupant survives.
    /// </summary>
    /// <remarks>
    /// Called on every path that ends an entry's association with an entity: a slot that stopped being occupied, and a slot the engine handed to a different
    /// entity. Zeroing rather than merely re-stamping the identity is what makes SUB-09's "never survives slot reuse" true of the STATE and not only of the
    /// identity: a group body left behind would compare equal against the new entity's first projection and its change would go unsent.
    /// </remarks>
    private static void ClearEntry(ArchetypeReplicationState state, byte* blockBytes, in ReplicationBlockLayout layout, int slot, int worker)
    {
        // What the entry holds outside itself goes first — its wide bodies, its references' counts: zeroing it would leave them with nothing naming them.
        state.EndEntry(blockBytes, slot, worker);
        NativeMemory.Clear(blockBytes + layout.HotOffset + (slot * layout.HotStride), (nuint)layout.HotStride);
        NativeMemory.Clear(blockBytes + layout.ColdOffset + (slot * layout.ColdStride), (nuint)layout.ColdStride);
        if (layout.OwnerEntrySize > 0)
        {
            NativeMemory.Clear(blockBytes + layout.OwnerOffset + (slot * layout.OwnerEntrySize), (nuint)layout.OwnerEntrySize);
        }
    }

    private static void Fill(CompiledGroup[] groups, Span<int> offsets)
    {
        if (groups.Length > MaxGroups)
        {
            throw new InvalidOperationException(
                $"A projection declares {groups.Length} change groups; the wire's mask is a u8, so eight is the ceiling (W14). The registry refuses a ninth.");
        }

        var at = 0;
        for (var g = 0; g < groups.Length; g++)
        {
            offsets[g] = at;
            at += groups[g].Section.MaxBodyBytes;
        }
    }

    private static int MaxPackBytes(CompiledProjectionPlan plan)
    {
        var max = plan.OnEnter.PackBytes;
        for (var g = 0; g < plan.Groups.Length; g++)
        {
            max = Math.Max(max, plan.Groups[g].Section.PackBytes);
        }

        for (var g = 0; g < plan.OwnerGroups.Length; g++)
        {
            max = Math.Max(max, plan.OwnerGroups[g].Section.PackBytes);
        }

        return max;
    }
}

/// <summary>
/// Per-worker slices of the database's one identity space, so the projection pass can hand out and give back network identities from several workers at once
/// without the allocator ever seeing two callers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A lease is a slice of the single allocator, not a second allocator</b> (02 § 4). The identity space is global — an <c>entityRef</c> arrives with no
/// archetype to disambiguate it, and an event is encoded once and copied to every receiver precisely because the bytes mean the same thing to all of them —
/// so nothing here mints an identity. <see cref="BeginTick"/> takes a batch from <see cref="NetIdAllocator"/> at the track's single-threaded point and
/// <see cref="Take"/> spends it on a worker, which is what lets going global cost no parallelism.
/// </para>
/// <para>
/// <b>Releases are queued, not applied.</b> <see cref="NetIdAllocator.Release"/> bumps a generation and threads a list; running it from a worker would corrupt
/// both. A released identity therefore sits in the worker's queue until the next <see cref="BeginTick"/>, which releases it into the allocator's quarantine —
/// so an identity released during tick T becomes reissuable no earlier than T+2, one tick more conservative than SUB-06's T+1 and never less.
/// </para>
/// <para>
/// <b>The lease size follows demand, and starvation is a deferral rather than a failure.</b> A lease refills to twice what its worker spent and was refused
/// last tick, floored at <see cref="MinLease"/> and capped at <see cref="MaxLease"/>, so an initial fill converges in two or three ticks and a steady state
/// shrinks back — leased-but-unspent identities are counted in <see cref="LeasedCount"/> so a leak assertion can subtract them from the allocator's live
/// count. A worker that runs dry leaves its entity uninitialized for the tick; the entity is still watched, so the next tick initializes it.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="Take"/> and <see cref="Release"/> are safe from the worker that owns the lease and from no one else — which the
/// projection pass guarantees by partitioning over blocks, each block belonging to exactly one chunk. <see cref="BeginTick"/> and <see cref="Dispose"/> are
/// single-threaded.
/// </para>
/// </remarks>
internal sealed unsafe class NetIdLeaseSet : IDisposable
{
    /// <summary>The smallest batch a lease refills to, so a first tick of demand is not met one identity at a time.</summary>
    public const int MinLease = 32;

    /// <summary>The largest batch a lease refills to, which bounds identities held out of circulation to <c>workers x MaxLease</c>.</summary>
    public const int MaxLease = 16384;

    /// <summary>A worker's lease. Padded to a cache line: two workers spending identities must not bounce one line between them.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Lease
    {
        public uint* Ids;
        public int Count;
        public int Capacity;
        public uint* Released;
        public int ReleasedCount;
        public int ReleasedCapacity;
        public int Demand;
        public int Starved;
        public DepartedEntity* Departed;
        public int DepartedCount;
        public int DepartedCapacity;

        // Count when the lease was last filled: Ids[Count..CountAtBegin) are the identities spent since.
        public int CountAtBegin;
    }

    /// <summary>An identity released this tick with the entity it named and its last position: an event of the tick may still name it (09 § 11).</summary>
    public struct DepartedEntity
    {
        public EntityId Entity;
        public uint NetId;
        public float X;
        public float Y;
        public float Z;

        /// <summary>The realm it was last in, whose frame X, Y, Z are in.</summary>
        public ushort Realm;
    }

    /// <summary>Records, from <paramref name="worker"/>'s chunk, the entity whose identity it released and where it was last.</summary>
    public void Depart(int worker, EntityId entity, uint netId, float x, float y, float z, ushort realm = 0)
    {
        ref var lease = ref _leases[worker];
        if (lease.DepartedCount == lease.DepartedCapacity)
        {
            var capacity = lease.DepartedCapacity == 0 ? 16 : lease.DepartedCapacity * 2;
            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
            lease.Departed = (DepartedEntity*)NativeMemory.Realloc(lease.Departed, (nuint)capacity * (nuint)sizeof(DepartedEntity));
            lease.DepartedCapacity = capacity;
        }

        lease.Departed[lease.DepartedCount++] = new DepartedEntity { Entity = entity, NetId = netId, X = x, Y = y, Z = z, Realm = realm };
    }

    /// <summary>This tick's departed entities, every worker's: read serially, in the frame prologue, before the next tick's <see cref="BeginTick"/>.</summary>
    public void CollectDeparted(Dictionary<long, DepartedEntity> into)
    {
        for (var i = 0; i < _count; i++)
        {
            ref var lease = ref _leases[i];
            for (var k = 0; k < lease.DepartedCount; k++)
            {
                var d = lease.Departed[k];
                into[(long)d.Entity.RawValue] = d;
            }
        }
    }

    private Lease* _leases;
    private int _count;
    private bool _primed;
    private bool _disposed;

    /// <summary>Leases currently held, one per chunk index.</summary>
    public int Count => _count;

    /// <summary>
    /// Identities taken from the allocator and not yet spent. Subtract from <c>NetIdAllocator.LiveCount</c> for the entities actually named.
    /// </summary>
    public int LeasedCount
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _count; i++)
            {
                total += _leases[i].Count;
            }

            return total;
        }
    }

    /// <summary>Identities released by the pass and not yet handed back to the allocator. Drained by the next <see cref="BeginTick"/>.</summary>
    public int PendingReleases
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _count; i++)
            {
                total += _leases[i].ReleasedCount;
            }

            return total;
        }
    }

    /// <summary>Entities this tick's pass could not initialize because a lease ran dry. They are initialized on the next tick.</summary>
    public int Starvations
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _count; i++)
            {
                total += _leases[i].Starved;
            }

            return total;
        }
    }

    /// <summary>Native bytes the leases hold, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = (long)_count * sizeof(Lease);
            for (var i = 0; i < _count; i++)
            {
                bytes += ((long)_leases[i].Capacity + _leases[i].ReleasedCapacity) * sizeof(uint);
            }

            return bytes;
        }
    }

    /// <summary>
    /// Hands last tick's releases back to <paramref name="allocator"/> and refills every lease for the tick about to run. Single-threaded, before the dispatch.
    /// </summary>
    /// <param name="allocator">The database's identity allocator.</param>
    /// <param name="workers">The chunk count S1 is about to dispatch; leases beyond it are emptied rather than kept stocked.</param>
    /// <param name="coldEstimate">
    /// An upper bound on the identities the FIRST projected tick can need — the pushed slots. It is used once, because until a tick has run there is no
    /// demand to size a lease from and an initial fill of a large archetype would otherwise take several ticks to converge, each of them deferring
    /// entities. Afterwards the demand-driven rule takes over and the leases shrink back.
    /// </param>
    public void BeginTick(NetIdAllocator allocator, int workers, int coldEstimate = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfNegative(workers);

        EnsureLeases(workers);
        var cold = !_primed && coldEstimate > 0 && workers > 0 ? (coldEstimate + workers - 1) / workers : 0;
        _primed |= coldEstimate > 0;

        for (var i = 0; i < _count; i++)
        {
            ref var lease = ref _leases[i];

            SettleSpent(allocator, ref lease);

            for (var r = 0; r < lease.ReleasedCount; r++)
            {
                allocator.Release(lease.Released[r]);
            }

            lease.ReleasedCount = 0;
            lease.DepartedCount = 0;

            var wanted = i < workers ? Math.Clamp(Math.Max(2 * (lease.Demand + lease.Starved), cold), MinLease, MaxLease) : 0;
            while (lease.Count > wanted)
            {
                allocator.Release(lease.Ids[--lease.Count]);
            }

            if (wanted > lease.Capacity)
            {
                GrowIds(ref lease, wanted);
            }

            while (lease.Count < wanted)
            {
                var netId = allocator.Allocate();
                allocator.MarkLeased(netId);
                lease.Ids[lease.Count++] = netId;
            }

            lease.CountAtBegin = lease.Count;
            lease.Demand = 0;
            lease.Starved = 0;
        }
    }

    /// <summary>
    /// A tick with no block to project: last tick's queued releases reach the allocator and its departed entities are forgotten, as <see cref="BeginTick"/>
    /// would, with the leases themselves left as they are — so a quiet world does not keep released identities live until something is pushed again.
    /// </summary>
    public void FlushReleases(NetIdAllocator allocator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(allocator);
        for (var i = 0; i < _count; i++)
        {
            ref var lease = ref _leases[i];
            SettleSpent(allocator, ref lease);
            for (var r = 0; r < lease.ReleasedCount; r++)
            {
                allocator.Release(lease.Released[r]);
            }

            lease.ReleasedCount = 0;
            lease.DepartedCount = 0;
        }
    }

    // The identities spent since the last fill are entries' now, no longer out on a lease (NetIdAllocator.IsLeased). At the first blocks step after the
    // projection that spent them, idle or not: a spent identity's bit left set past its release could be cleared later under another lease that was
    // reissued it, and a reference to it would then resolve differently by worker order.
    private static void SettleSpent(NetIdAllocator allocator, ref Lease lease)
    {
        for (var s = lease.Count; s < lease.CountAtBegin; s++)
        {
            allocator.MarkSpent(lease.Ids[s]);
        }

        lease.CountAtBegin = lease.Count;
    }

    /// <summary>
    /// The identities <paramref name="worker"/>'s chunk released during the last projection, still queued for the allocator: what the blocks step re-pushes
    /// the referrers of before <see cref="BeginTick"/> hands them over (13 § 5).
    /// </summary>
    public ReadOnlySpan<uint> PendingReleasesOf(int worker) =>
        (uint)worker < (uint)_count && _leases[worker].Released != null
            ? new ReadOnlySpan<uint>(_leases[worker].Released, _leases[worker].ReleasedCount)
            : default;

    /// <summary>Whether <paramref name="worker"/>'s lease holds an identity to spend, without spending it.</summary>
    public bool HasAny(int worker) => _leases[worker].Count > 0;

    /// <summary>Spends one identity from the lease of <paramref name="worker"/>.</summary>
    /// <param name="worker">The chunk index.</param>
    /// <returns>The identity, or <see cref="NetIdAllocator.NoNetId"/> when the lease is empty.</returns>
    public uint Take(int worker)
    {
        ref var lease = ref _leases[worker];
        if (lease.Count == 0)
        {
            lease.Starved++;
            return NetIdAllocator.NoNetId;
        }

        lease.Demand++;
        return lease.Ids[--lease.Count];
    }

    /// <summary>Queues <paramref name="netId"/> for release at the next <see cref="BeginTick"/>.</summary>
    /// <param name="worker">The chunk index.</param>
    /// <param name="netId">The identity the entry held.</param>
    public void Release(int worker, uint netId)
    {
        if (netId == NetIdAllocator.NoNetId)
        {
            return;
        }

        ref var lease = ref _leases[worker];
        if (lease.ReleasedCount == lease.ReleasedCapacity)
        {
            var capacity = lease.ReleasedCapacity == 0 ? 32 : lease.ReleasedCapacity * 2;
            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
            lease.Released = (uint*)NativeMemory.Realloc(lease.Released, (nuint)capacity * sizeof(uint));
            lease.ReleasedCapacity = capacity;
        }

        lease.Released[lease.ReleasedCount++] = netId;
    }

    private void EnsureLeases(int workers)
    {
        if (workers <= _count)
        {
            return;
        }

        // native-alloc: per-worker lease table, sized once from the worker count and freed with the pass
        var grown = (Lease*)NativeMemory.AllocZeroed((nuint)workers, (nuint)sizeof(Lease));
        if (_leases != null)
        {
            Buffer.MemoryCopy(_leases, grown, (long)workers * sizeof(Lease), (long)_count * sizeof(Lease));
            NativeMemory.Free(_leases);
        }

        _leases = grown;
        _count = workers;
    }

    private static void GrowIds(ref Lease lease, int required)
    {
        var capacity = lease.Capacity == 0 ? MinLease : lease.Capacity;
        while (capacity < required)
        {
            capacity *= 2;
        }

        // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
        lease.Ids = (uint*)NativeMemory.Realloc(lease.Ids, (nuint)capacity * sizeof(uint));
        lease.Capacity = capacity;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The identities still in a lease are NOT handed back: this runs on the teardown path, where the allocator may already be disposed, and a throw from a
    /// Dispose is worse than a count that stays high on an object nobody will read again. The allocator is torn down with the runtime immediately afterwards.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _count; i++)
        {
            NativeMemory.Free(_leases[i].Ids);
            NativeMemory.Free(_leases[i].Released);
            NativeMemory.Free(_leases[i].Departed);
        }

        NativeMemory.Free(_leases);
        _leases = null;
        _count = 0;
    }
}

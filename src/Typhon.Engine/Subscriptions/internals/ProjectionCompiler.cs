using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Turns the declarations an application wrote into the plan the projection pass walks: every name becomes a pair of byte offsets, every codec becomes a
/// closed set of numbers, and every field takes its place in canonical wire order. Runs once, at <c>Start</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing it produces can run user code.</b> A declaration names a component handle and a selector expression; the selector was already reduced to a field
/// name when it was declared, and this compiler reduces that name to an offset taken from the schema's measured layout. No expression is compiled, no delegate
/// is kept, no reflection survives into the tick — which is what lets the per-entity pass be a loop over integers, and what keeps the path AOT-safe (#409).
/// </para>
/// <para>
/// <b>Offsets come from the archetype's cluster layout, never from a structure of replication's own</b> (SUB-01). A component's column is found with
/// <see cref="ArchetypeClusterInfo.ComponentOffset"/> at the slot <see cref="ArchetypeMetadata.GetSlot"/> resolves, and a value inside it with
/// <c>DBComponentDefinition.Field.OffsetInComponentStorage</c> — the same three numbers <see cref="ClusterRef{TArch}.GetReadOnlySpan{T}"/> uses.
/// </para>
/// <para>
/// <b>Everything it cannot compile, it refuses here, by name.</b> A component that is not on the archetype, a field that is not on the component, a motion
/// declaration with no teleport speed: each throws at <c>Start</c> naming the archetype, the field and what was expected. The alternative is a projection that
/// silently replicates the wrong bytes, which no test downstream would recognise as a declaration error.
/// </para>
/// </remarks>
internal static class ProjectionCompiler
{
    /// <summary>
    /// The finest divisor a <c>vel</c> codec may apply to the position quantum: a displacement code counts position quanta ÷ <c>quantaDiv</c> (W5), and
    /// <c>quantaDiv</c> is derived per archetype in <c>[1, 16]</c>.
    /// </summary>
    /// <remarks>
    /// Always a power of two, so a decode is exact whenever the position step is dyadic — which it is, being an extent divided by 2ᵇ. Sixteen is the ceiling
    /// rather than the value: a sixteenth of a position quantum is already far below any simulation's own precision, so buying more resolution than that
    /// would be paying wire bytes for noise.
    /// </remarks>
    internal const int MaxVelocityQuantaDiv = 16;

    /// <summary>The widths a quantizing codec may take, in ascending order (W2).</summary>
    private static readonly int[] CodecWidths = [8, 16, 24, 32];

    /// <summary>The extrapolation error that forces a new segment when an archetype declares none: 5 cm.</summary>
    private const double DefaultToleranceMetres = 0.05;

    /// <summary>The segment heartbeat when an archetype declares none: 5 s.</summary>
    private const double DefaultMaxAgeSeconds = 5.0;

    /// <summary>How many change ticks one hot entry holds, the motion segment's included — <see cref="ReplicationHotEntry.GroupTicks"/>.</summary>
    private const int TickSlots = 4;

    /// <summary>Marks a component slot that was never resolved.</summary>
    private const byte NoSlot = 0xFF;

    /// <summary>
    /// Compiles every archetype projection the registry holds.
    /// </summary>
    /// <param name="registry">The frozen registry.</param>
    /// <param name="engine">The engine whose archetypes, component layouts and spatial grid the declarations are resolved against.</param>
    /// <param name="nominalTickPeriodSeconds">The runtime's nominal tick period, <c>1 / BaseTickRate</c>.</param>
    /// <param name="largestTickMultiplier">
    /// The largest tick multiplier the runtime may fall back to, from <see cref="OverloadDetector.MaxTickMultiplier"/> — the last entry of the allowed ladder,
    /// not the raw <c>BaseTickRate / MinTickRateHz</c> ratio, which the ladder caps.
    /// </param>
    /// <returns>One plan per declared archetype, in declaration order.</returns>
    /// <param name="replicationCellM">The replication cell side; the visibility slack is capped at half of it. Zero or less: no cap.</param>
    /// <param name="visibilitySlackOverrideM">Tests only: every Sphere-observed moving archetype's slack; <see cref="double.NaN"/> applies the rule.</param>
    public static CompiledProjectionPlan[] Compile(SubscriptionsRegistry registry, DatabaseEngine engine, double nominalTickPeriodSeconds,
        int largestTickMultiplier, double replicationCellM = 0, double visibilitySlackOverrideM = double.NaN)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(engine);
        if (!double.IsFinite(nominalTickPeriodSeconds) || nominalTickPeriodSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalTickPeriodSeconds), nominalTickPeriodSeconds,
                "The nominal tick period is 1 / RuntimeOptions.BaseTickRate and has to be positive.");
        }

        if (largestTickMultiplier < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(largestTickMultiplier), largestTickMultiplier,
                "The largest allowed tick multiplier is at least 1; read it from OverloadDetector.MaxTickMultiplier.");
        }

        var plans = new CompiledProjectionPlan[registry.Archetypes.Count];
        for (var i = 0; i < plans.Length; i++)
        {
            var slack = VisibilitySlackOf(registry, registry.Archetypes[i].ArchetypeType, replicationCellM, visibilitySlackOverrideM);
            plans[i] = CompileArchetype(registry.Archetypes[i], engine, nominalTickPeriodSeconds, largestTickMultiplier, slack);
        }

        return plans;
    }

    /// <summary>
    /// Derives a <c>vel</c> codec's width <b>and</b> its divisor together: the narrowest of 8, 16, 24 and 32 bits that can carry the widest displacement one
    /// tick allows below the teleport threshold, and within that width the largest power-of-two <c>quantaDiv</c> ≤ 16 that still fits (D2, W5).
    /// </summary>
    /// <param name="maxSpeedMps">The teleport threshold, in metres per second.</param>
    /// <param name="nominalTickPeriodSeconds">The nominal tick period.</param>
    /// <param name="tickMultiplier">The tick multiplier the width must cover: the runtime's largest allowed one, or 1 under the per-archetype opt-out.</param>
    /// <param name="positionStepMetres">The finest position quantum across the axes — the one that produces the largest code.</param>
    /// <returns>The width in bits and the divisor, which travel together in the catalog and are meaningless apart.</returns>
    /// <remarks>
    /// <para>
    /// <c>L ≥ ⌈maxSpeed × period × multiplier / posStep × quantaDiv⌉</c>, with <c>L = 2ᵇ⁻¹ − 1</c> — exactly the inequality W5 states, evaluated
    /// left to right so the intermediate is a displacement in metres, then in position quanta, then in codes. The search runs widths outermost and divisors
    /// inside them, so a byte is never spent to buy resolution: <b>bytes first, then the finest velocity quantum that width affords.</b>
    /// </para>
    /// <para>
    /// <b>Why the divisor is derived rather than fixed at 16.</b> Fixing it made the divisor the constant and the width the variable, which is the expensive
    /// way round — SWG's 20 m/s at 10 Hz over a 2⁻¹⁰ m quantum needs 32 768 codes at <c>quantaDiv = 16</c>, exactly one past what 16 bits carries, so every
    /// creature segment paid 2 B forever to keep a resolution nothing could use. Halving the divisor instead costs an eighth of a position quantum of
    /// velocity resolution — 0.12 mm per tick — which is orders below the float32 jitter the measurement already lives with.
    /// </para>
    /// <para>
    /// <b>The multiplier is in there because the teleport test uses the CURRENT tick period.</b> Under overload the engine ticks less often and each tick
    /// covers more ground, so a displacement-per-tick codec sized for the nominal rate would saturate exactly while the server is overloaded — emitting a
    /// segment per tick for every fast mover at the moment traffic must not grow. An archetype whose movement is bounded per tick rather than per second says
    /// so with <see cref="MotionBuilder.IgnoreTickDilation"/> and pays the nominal width.
    /// </para>
    /// </remarks>
    internal static (int Bits, int QuantaDiv) VelocityCodec(double maxSpeedMps, double nominalTickPeriodSeconds, int tickMultiplier,
        double positionStepMetres)
    {
        var displacementPerTick = maxSpeedMps * nominalTickPeriodSeconds * tickMultiplier;
        var quanta = displacementPerTick / positionStepMetres;
        foreach (var bits in CodecWidths)
        {
            var limit = WireMath.SymmetricLimit(bits);
            for (var quantaDiv = MaxVelocityQuantaDiv; quantaDiv >= 1; quantaDiv >>= 1)
            {
                if (limit >= Math.Ceiling(quanta * quantaDiv))
                {
                    return (bits, quantaDiv);
                }
            }
        }

        throw new InvalidOperationException(
            $"A teleport threshold of {maxSpeedMps} m/s over a {nominalTickPeriodSeconds} s tick at multiplier {tickMultiplier} needs " +
            $"{Math.Ceiling(quanta)} velocity codes on a {positionStepMetres} m position quantum even at quantaDiv 1, and 32 bits carries " +
            $"{WireMath.SymmetricLimit(32)}. Lower the threshold, or widen the position quantum by shrinking the world.");
    }

    /// <summary>
    /// An archetype's visibility slack <c>h_A</c> (09 § 2–3): the smallest slack over the Sphere profiles observing it — a profile's half band, or its
    /// <c>R / 48</c> without one — capped at half a cell, as the anchor slack is, so a cell query's padding stays under a cell. An archetype no Sphere
    /// observes stays exact: nothing tests it against a radius.
    /// </summary>
    private static double VisibilitySlackOf(SubscriptionsRegistry registry, Type archetype, double cellM, double overrideM)
    {
        var slack = double.PositiveInfinity;
        foreach (var profile in registry.Profiles)
        {
            foreach (var observer in profile.Observers)
            {
                if (observer.Kind != ObserverKind.Sphere)
                {
                    continue;
                }

                foreach (var type in observer.Archetypes)
                {
                    if (type == archetype)
                    {
                        slack = Math.Min(slack, observer.VisibilitySlack);
                    }
                }
            }
        }

        if (!double.IsFinite(slack))
        {
            return 0d;
        }

        if (double.IsFinite(overrideM))
        {
            slack = overrideM;
        }

        if (cellM > 0 && double.IsFinite(cellM))
        {
            slack = Math.Min(slack, cellM / 2d);
        }

        return slack > 0 ? slack : 0d;
    }

    private static CompiledProjectionPlan CompileArchetype(ArchetypeProjection projection, DatabaseEngine engine, double nominalTickPeriodSeconds,
        int largestTickMultiplier, double visibilitySlackM)
    {
        var meta = ResolveArchetype(projection);
        var layout = meta.ClusterLayout;
        if (!meta.IsClusterEligible || layout == null)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' is replicated but is not cluster-backed, and replication reads component values only through the cluster " +
                "layout (SUB-01). A replicated archetype needs SingleVersion or Transient components throughout.");
        }

        var groupNames = CanonicalGroups(projection.Groups);
        var ownerGroupNames = CanonicalGroups(projection.OwnerGroups);
        if (projection.IsStatic)
        {
            // A static archetype is sent once, on enter, and never updated — so it has no change groups and no per-entity comparison at all. Folding its
            // fields into the onEnter section here is what makes that true downstream rather than a policy every later stage has to remember.
            groupNames = [];
            if (projection.OwnerFields.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' is declared Static and also declares owner fields. An owner field travels in SELF when its group " +
                    "changes, and a static archetype has no groups; declare it with Archetype<T> instead.");
            }
        }

        var position = CompilePosition(projection, meta, layout, engine, nominalTickPeriodSeconds, largestTickMultiplier);
        var motionTickSlots = position != null && position.Moving ? 1 : 0;
        if (groupNames.Length + motionTickSlots > TickSlots)
        {
            var motion = motionTickSlots == 1 ? "a motion segment" : "no motion";
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' declares {groupNames.Length} change group(s) and {motion}, which needs " +
                $"{groupNames.Length + motionTickSlots} of the {TickSlots} change ticks one replication entry holds (ReplicationHotEntry.GroupTicks). " +
                "The wire allows eight groups; the hot entry's fixed head allows four ticks. Merge fields that change together.");
        }

        var fields = BuildFields(projection, projection.Fields, groupNames, meta, layout, engine, projection.IsStatic);
        var ownerFields = BuildFields(projection, projection.OwnerFields, ownerGroupNames, meta, layout, engine, foldIntoEnter: false);

        var onEnter = SectionOf(fields, 0);
        var groups = new CompiledGroup[groupNames.Length];
        var stateBodyBytes = 0;
        for (var g = 0; g < groupNames.Length; g++)
        {
            var section = SectionOf(fields, g + 1);
            groups[g] = new CompiledGroup { Name = groupNames[g], Bit = g, TickSlot = motionTickSlots + g, Section = section };
            stateBodyBytes += section.MaxBodyBytes;
        }

        var ownerGroups = new CompiledGroup[ownerGroupNames.Length];
        var ownerBodyBytes = 0;
        for (var g = 0; g < ownerGroupNames.Length; g++)
        {
            // The owner section has its own bit space (W17): its groups start again at bit 0. They take NO hot-entry tick slot — those four belong to the
            // public groups and the motion segment, and owner data lives in the block's own owner entry, whose change tracking the projection pass defines.
            var section = SectionOf(ownerFields, g + 1);
            ownerGroups[g] = new CompiledGroup { Name = ownerGroupNames[g], Bit = g, TickSlot = -1, Section = section };
            ownerBodyBytes += section.MaxBodyBytes;
        }

        var ownerEntrySize = ownerBodyBytes == 0 ? 0 : (ownerBodyBytes + 7) & ~7;

        // The block's entries are sized from what THIS archetype produces, not from a struct declaration. A moving archetype reserves its segment in the hot
        // entry and its previous position plus its run start in the cold one; a static or still archetype reserves neither, because there is nothing to
        // extrapolate from and the enter position is re-read from the column. The run start is that same quantized position plus a u32 tick, rounded to four
        // bytes so the tick behind it stays word-addressable.
        var moving = position != null && position.Moving;
        var quantizedPositionBytes = moving ? position.Dims * (position.Pos.Bits / 8) : 0;
        var runStartBytes = moving ? (quantizedPositionBytes + 4 + 3) & ~3 : 0;

        // The enter cache, sized here for the same reason the rest of the block is: what an enter record carries and NOTHING else keeps. A static
        // archetype's position reserves no segment, and the onEnter body appears in no state record — so a session that first sees an entity some ticks
        // after it was projected would have neither, and the frame stage would have to re-encode from the columns per session. Written once when an entry
        // is initialized; see ReplicationBlockLayout.EnterBytes for why it is in the cold entry and why it usually costs nothing.
        var enterPositionBytes = position != null && !moving ? position.Dims * (position.Pos.Bits / 8) : 0;
        var headings = 0;
        foreach (var field in fields)
        {
            headings = Math.Max(headings, field.HeadingPlusOne);
        }

        var blockLayout = ReplicationBlockLayout.ForArchetype(layout.ClusterSize, moving ? position.SegmentBytes : 0, stateBodyBytes, quantizedPositionBytes,
            runStartBytes, ownerEntrySize, enterPositionBytes, onEnter.MaxBodyBytes, headingBytes: 4 * headings);

        // v̂ (09 § 2) gets bytes of its own only for a mover with a slack; at zero it is the previous position.
        var slack = moving ? visibilitySlackM : 0d;
        if (slack > 0)
        {
            blockLayout = blockLayout.WithVisibilityPosition();
        }

        return new CompiledProjectionPlan
        {
            Name = projection.Name,
            ArchetypeType = projection.ArchetypeType,
            ArchetypeCatalogId = meta.ArchetypeId,
            DeclarationIndex = projection.Index,
            IsStatic = projection.IsStatic,
            ClusterLayout = layout,
            SlotCount = layout.ClusterSize,
            Fields = fields,
            OnEnter = onEnter,
            Groups = groups,
            OwnerFields = ownerFields,
            OwnerGroups = ownerGroups,
            Position = position,
            BlockLayout = blockLayout,
            VisibilitySlackM = slack,
            OwnerEntrySize = ownerEntrySize,
            MaxStateBodyBytes = stateBodyBytes,
            TickSlotCount = motionTickSlots + groupNames.Length,
        };
    }

    // ── Fields ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CompiledField[] BuildFields(ArchetypeProjection projection, IReadOnlyList<ProjectedField> declared, string[] groupNames,
        ArchetypeMetadata meta, ArchetypeClusterInfo layout, DatabaseEngine engine, bool foldIntoEnter)
    {
        var ordered = new List<ProjectedField>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            ordered.Add(declared[i]);
        }

        // W11's layout key, and the reason a reversed declaration compiles to the identical plan: (section, packed first, ordinal name). Groups canonicalize
        // first, so a field's section is decided by the group's place in the ordinal sort, never by where the declaration put it.
        ordered.Sort((a, b) =>
        {
            var bySection = SectionIndex(a, groupNames, foldIntoEnter).CompareTo(SectionIndex(b, groupNames, foldIntoEnter));
            if (bySection != 0)
            {
                return bySection;
            }

            var byPacking = (IsPacked(a.Codec) ? 0 : 1).CompareTo(IsPacked(b.Codec) ? 0 : 1);
            return byPacking != 0 ? byPacking : string.CompareOrdinal(a.Name, b.Name);
        });

        var compiled = new CompiledField[ordered.Count];
        var section = -1;
        var packBits = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var field = ordered[i];
            var fieldSection = SectionIndex(field, groupNames, foldIntoEnter);
            if (fieldSection != section)
            {
                section = fieldSection;
                packBits = 0;
            }

            compiled[i] = CompileField(projection, field, meta, layout, engine, fieldSection, i, ref packBits);
        }

        // Each heading gets its index among the archetype's headings, in wire order: where its held code lives in the cold entry (09 § 15).
        var heading = 0;
        for (var i = 0; i < compiled.Length; i++)
        {
            if (compiled[i].HeadingPlusOne > 0)
            {
                compiled[i] = compiled[i] with { HeadingPlusOne = ++heading };
            }
        }

        return compiled;
    }

    private static CompiledField CompileField(ArchetypeProjection projection, ProjectedField field, ArchetypeMetadata meta, ArchetypeClusterInfo layout,
        DatabaseEngine engine, int section, int ordinal, ref int packBits)
    {
        var codec = field.Codec.Catalog;
        RefuseUnsupportedFieldCodec(projection, field, codec);

        var slot = ResolveSlot(projection, meta, field.ComponentTypeId, field.ComponentName, field.Name);
        var definition = ResolveDefinition(meta, slot, engine);
        var source = ResolveField(projection, definition, field.SourceFieldName, field.Name);
        var ratioOffset = -1;
        if (field.MaxSourceFieldName != null)
        {
            ratioOffset = ResolveField(projection, definition, field.MaxSourceFieldName, field.Name).OffsetInComponentStorage;
        }

        var packed = IsPacked(field.Codec);
        var bitCount = !packed ? 0 : codec.Kind == CodecKind.Bool ? 1 : codec.N;
        var bitOffset = packBits;
        packBits += bitCount;

        var (codeMin, codeMax) = IntegerRange(codec);
        var headingTolerance = 0u;
        if (field.IsHeading)
        {
            if (!double.IsFinite(field.HeadingToleranceDeg) || field.HeadingToleranceDeg <= 0 || field.HeadingToleranceDeg >= 180)
            {
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' declares the heading '{field.Name}' with a tolerance of {field.HeadingToleranceDeg}°. A heading is sent when it " +
                    "turns past its tolerance, which must be above 0° and below 180°.");
            }

            if (codec.Kind != CodecKind.Angle || field.Owner || field.OnEnter)
            {
                throw new InvalidOperationException($"Heading '{field.Name}' of archetype '{projection.Name}' must be a public, grouped angle field.");
            }

            // The deadband in code space: a turn of the tolerance is this many codes of the angle's 2^bits per full turn.
            headingTolerance = (uint)Math.Floor(field.HeadingToleranceDeg / 360d * Math.Pow(2, codec.Bits));
        }

        return new CompiledField
        {
            HeadingPlusOne = field.IsHeading ? 1 : 0,
            HeadingToleranceCodes = headingTolerance,
            Name = field.Name,
            ComponentSlot = slot,
            ComponentOffsetInCluster = layout.ComponentOffset(slot),
            ComponentSize = layout.ComponentSize(slot),
            FieldOffsetInComponent = source.OffsetInComponentStorage,
            RatioOffsetInComponent = ratioOffset,
            SourceType = ResolveSourceType(projection, field, source),
            Codec = codec,
            CodecKind = codec.Kind,
            CodecBits = codec.Kind == CodecKind.Bits ? codec.N : codec.Bits,
            EnumType = field.Codec.EnumType,
            Saturating = field.Codec.Saturating,
            CodeMin = codeMin,
            CodeMax = codeMax,
            QuantMin = codec.Kind == CodecKind.Quant ? codec.Min[0] : 0d,
            QuantMax = codec.Kind == CodecKind.Quant ? codec.Max[0] : 0d,
            Section = section,
            GroupBit = section == 0 ? -1 : section - 1,
            Ordinal = ordinal,
            Packed = packed,
            BitOffset = packed ? bitOffset : 0,
            BitCount = bitCount,
            MaxBodyBytes = packed ? 0 : MaxEncodedBytes(codec),
            Owner = field.Owner,
        };
    }

    private static CompiledSection SectionOf(CompiledField[] fields, int section)
    {
        var first = -1;
        var count = 0;
        var packedCount = 0;
        var bits = 0;
        var bytes = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Section != section)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }

            count++;
            if (fields[i].Packed)
            {
                packedCount++;
                bits += fields[i].BitCount;
            }
            else
            {
                bytes += fields[i].MaxBodyBytes;
            }
        }

        var packBytes = (bits + 7) / 8;
        return new CompiledSection
        {
            FirstField = first < 0 ? 0 : first,
            FieldCount = count,
            PackedCount = packedCount,
            PackBytes = packBytes,
            MaxBodyBytes = count == 0 ? 0 : packBytes + bytes,
        };
    }

    // ── Position ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CompiledPosition CompilePosition(ArchetypeProjection projection, ArchetypeMetadata meta, ArchetypeClusterInfo layout, DatabaseEngine engine,
        double nominalTickPeriodSeconds, int largestTickMultiplier)
    {
        var declared = projection.Position;
        if (declared == null)
        {
            return null;
        }

        var grid = engine.Realm0Grid;
        if (grid == null)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' replicates a position, and a position quantizes over the spatial grid's bounds. Call " +
                "DatabaseEngine.ConfigureSpatialGrid before InitializeArchetypes, or drop the position from the projection.");
        }

        var slot = ResolveSlot(projection, meta, declared.ComponentTypeId, declared.ComponentName, "the position");
        var definition = ResolveDefinition(meta, slot, engine);
        var spatial = definition.SpatialField;
        if (spatial == null)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' replicates its position from component '{declared.ComponentName}', which declares no [SpatialIndex] field. " +
                "A replicated position is the component's spatial value, so the two cannot disagree about where an entity is.");
        }

        var dims = spatial.SpatialFieldType.Is3D() ? 3 : 2;
        var bits = Codec.DefaultPositionBits;
        var min = new double[dims];
        var max = new double[dims];
        var step = new double[dims];
        var finest = double.MaxValue;
        ref readonly var config = ref grid.Config;
        for (var axis = 0; axis < dims; axis++)
        {
            min[axis] = axis == 0 ? config.WorldMin.X : axis == 1 ? config.WorldMin.Y : config.WorldMin.Z;
            max[axis] = axis == 0 ? config.WorldMax.X : axis == 1 ? config.WorldMax.Y : config.WorldMax.Z;
            step[axis] = WireMath.QuantStep(min[axis], max[axis], bits);
            finest = Math.Min(finest, step[axis]);
        }

        var pos = new CatalogCodec { Kind = dims == 3 ? CodecKind.Pos3 : CodecKind.Pos2, Bits = bits, Min = min, Max = max };
        var moving = declared.IsMotion;
        var motion = declared.Motion;
        CatalogCodec vel = null;
        var multiplier = 1;
        if (moving)
        {
            if (motion == null || motion.TeleportMaxSpeedMps <= 0)
            {
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' replicates motion but declared no Teleport(maxSpeedMps). The velocity codec's width is derived from it " +
                    "(W5), and the engine has no default speed to derive it from — a threshold belongs to the simulation, not to the engine. Give it about " +
                    "1.5x the fastest thing that legitimately moves.");
            }

            multiplier = motion.IgnoresTickDilation ? 1 : largestTickMultiplier;
            var (velBits, quantaDiv) = VelocityCodec(motion.TeleportMaxSpeedMps, nominalTickPeriodSeconds, multiplier, finest);
            vel = new CatalogCodec { Kind = dims == 3 ? CodecKind.Vel3 : CodecKind.Vel2, Bits = velBits, QuantaDiv = quantaDiv };
        }

        var velocitySlot = NoSlot;
        var velocityOffset = -1;
        var velocityComponentOffset = -1;
        var velocityComponentSize = 0;
        if (motion?.VelocityFieldName != null)
        {
            velocitySlot = ResolveSlot(projection, meta, motion.VelocityComponentTypeId, motion.VelocityComponentName, "the declared velocity");
            velocityComponentOffset = layout.ComponentOffset(velocitySlot);
            velocityComponentSize = layout.ComponentSize(velocitySlot);
            velocityOffset = ResolveField(projection, ResolveDefinition(meta, velocitySlot, engine), motion.VelocityFieldName, "the declared velocity")
                .OffsetInComponentStorage;
        }

        // p0 | v (linear only) | t0 (tickLo, 2 B) | epoch (1 B) — § 5's segment grammar, sized here so the block layout and the record arena agree on it.
        var segmentBytes = (dims * (bits / 8)) + (vel == null ? 0 : dims * (vel.Bits / 8)) + 3;
        return new CompiledPosition
        {
            Moving = moving,
            Linear = vel != null,
            Dims = dims,
            ComponentSlot = slot,
            ComponentOffsetInCluster = layout.ComponentOffset(slot),
            ComponentSize = layout.ComponentSize(slot),
            FieldOffsetInComponent = spatial.OffsetInComponentStorage,
            SpatialFieldType = spatial.SpatialFieldType,
            Pos = pos,
            Vel = vel,
            PositionStep = step,
            FinestPositionStep = finest,
            ToleranceMetres = motion == null || motion.ToleranceMetres <= 0 ? DefaultToleranceMetres : motion.ToleranceMetres,
            TeleportMaxSpeedMps = motion?.TeleportMaxSpeedMps ?? 0d,
            MaxAgeSeconds = motion == null || motion.MaxAgeSeconds <= 0 ? DefaultMaxAgeSeconds : motion.MaxAgeSeconds,
            IgnoresTickDilation = motion?.IgnoresTickDilation ?? false,
            SizedForTickMultiplier = multiplier,
            VelocityComponentSlot = velocitySlot,
            VelocityComponentOffsetInCluster = velocityComponentOffset,
            VelocityComponentSize = velocityComponentSize,
            VelocityFieldOffsetInComponent = velocityOffset,
            SegmentBytes = moving ? segmentBytes : dims * (bits / 8),
        };
    }

    // ── Resolution and refusals ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ArchetypeMetadata ResolveArchetype(ArchetypeProjection projection)
    {
        var type = projection.ArchetypeType;
        if (type == null)
        {
            throw new InvalidOperationException($"Projection '{projection.Name}' names no archetype type.");
        }

        ArchetypeRegistry.EnsureFinalized(type);
        foreach (var candidate in ArchetypeRegistry.GetAllArchetypes())
        {
            if (candidate.ArchetypeType == type)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Archetype '{projection.Name}' is replicated but is not registered with this engine. An archetype reaches the registry by being declared with " +
            "[Archetype] and touched before TyphonRuntime.Start().");
    }

    private static byte ResolveSlot(ArchetypeProjection projection, ArchetypeMetadata meta, int componentTypeId, string componentName, string what)
    {
        if (componentTypeId >= 0 && meta.TryGetSlot(componentTypeId, out var slot))
        {
            return slot;
        }

        throw new InvalidOperationException(
            $"Archetype '{projection.Name}' replicates {what} from component '{componentName}', which is not one of its components. " +
            $"'{projection.Name}' holds: {string.Join(", ", ComponentNames(meta))}. A projection reads through the archetype's own cluster layout " +
            "(SUB-01), so it can only name a component that archetype has.");
    }

    private static DBComponentDefinition ResolveDefinition(ArchetypeMetadata meta, byte slot, DatabaseEngine engine)
    {
        var type = meta._slotToComponentType[slot];
        var table = type == null ? null : engine.GetComponentTable(type);
        if (table?.Definition == null)
        {
            throw new InvalidOperationException(
                $"Component '{type?.Name}' is at slot {slot} of archetype '{meta.Name}' but this engine has no definition for it; call " +
                "DatabaseEngine.RegisterComponentFromAccessor before InitializeArchetypes.");
        }

        return table.Definition;
    }

    private static DBComponentDefinition.Field ResolveField(ArchetypeProjection projection, DBComponentDefinition definition, string fieldName, string what)
    {
        if (definition.FieldsByName.TryGetValue(fieldName, out var field) && !field.IsStatic)
        {
            return field;
        }

        throw new InvalidOperationException(
            $"Archetype '{projection.Name}' replicates {what} from '{definition.Name}.{fieldName}', which the component does not store. " +
            $"'{definition.Name}' stores: {string.Join(", ", StoredFieldNames(definition))}.");
    }

    private static ProjectionSourceType ResolveSourceType(ArchetypeProjection projection, ProjectedField field, DBComponentDefinition.Field source)
    {
        var type = source.DotNetType;
        if (type != null && type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        var resolved = type == null ? ProjectionSourceType.None : Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => ProjectionSourceType.Boolean,
            TypeCode.SByte => ProjectionSourceType.SByte,
            TypeCode.Byte => ProjectionSourceType.Byte,
            TypeCode.Int16 => ProjectionSourceType.Int16,
            TypeCode.UInt16 => ProjectionSourceType.UInt16,
            TypeCode.Int32 => ProjectionSourceType.Int32,
            TypeCode.UInt32 => ProjectionSourceType.UInt32,
            TypeCode.Int64 => ProjectionSourceType.Int64,
            TypeCode.UInt64 => ProjectionSourceType.UInt64,
            TypeCode.Single => ProjectionSourceType.Single,
            TypeCode.Double => ProjectionSourceType.Double,
            _ => ProjectionSourceType.None,
        };

        if (resolved == ProjectionSourceType.None)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' replicates '{field.Name}' from '{field.ComponentName}.{field.SourceFieldName}', which is a " +
                $"'{source.DotNetType?.Name ?? source.Type.ToString()}'. A projected field is one number: the column walk reads a boolean, an integer, a " +
                "float or an enum backed by one of those.");
        }

        return resolved;
    }

    private static void RefuseUnsupportedFieldCodec(ArchetypeProjection projection, ProjectedField field, CatalogCodec codec)
    {
        switch (codec.Kind)
        {
            case CodecKind.Pos2:
            case CodecKind.Pos3:
            case CodecKind.Vel2:
            case CodecKind.Vel3:
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' declares field '{field.Name}' with the position codec '{codec.Type}'. A position is not a field (W15): " +
                    "declare it with Motion(...) or Position(...), which is what carries it in enter records and segments.");
            case CodecKind.Vec2:
            case CodecKind.Vec3:
            case CodecKind.Quat3:
            case CodecKind.Str:
            case CodecKind.Blob:
            case CodecKind.Bytes:
            case CodecKind.List:
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' declares field '{field.Name}' with codec '{codec.Type}', which carries more than one number or a " +
                    "variable-length payload. The projection pass reads one scalar per column; the multi-component and length-prefixed walks are not built " +
                    "yet. Narrow the field, or carry it as an event.");
            default:
                return;
        }
    }

    private static IEnumerable<string> ComponentNames(ArchetypeMetadata meta)
    {
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            var type = meta._slotToComponentType[slot];
            if (type != null)
            {
                yield return type.Name;
            }
        }
    }

    private static IEnumerable<string> StoredFieldNames(DBComponentDefinition definition)
    {
        foreach (var pair in definition.FieldsByName)
        {
            if (!pair.Value.IsStatic)
            {
                yield return pair.Key;
            }
        }
    }

    // ── Codec facts ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string[] CanonicalGroups(IReadOnlyList<string> declared)
    {
        var names = new string[declared.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = declared[i];
        }

        Array.Sort(names, string.CompareOrdinal);
        return names;
    }

    private static int SectionIndex(ProjectedField field, string[] groupNames, bool foldIntoEnter) =>
        foldIntoEnter || field.OnEnter || field.Group == null ? 0 : 1 + Array.IndexOf(groupNames, field.Group);

    private static bool IsPacked(Codec codec) => codec.Catalog != null && CatalogSerializer.IsPacked(codec.Catalog.Kind);

    private static bool IsPacked(CatalogCodec codec) => codec != null && CatalogSerializer.IsPacked(codec.Kind);

    private static (double Min, double Max) IntegerRange(CatalogCodec codec) => codec.Kind switch
    {
        CodecKind.U8 => (0d, byte.MaxValue),
        CodecKind.I8 => (sbyte.MinValue, sbyte.MaxValue),
        CodecKind.U16 => (0d, ushort.MaxValue),
        CodecKind.I16 => (short.MinValue, short.MaxValue),
        CodecKind.U32 or CodecKind.Varu or CodecKind.EntityRef or CodecKind.TickLo => (0d, uint.MaxValue),
        CodecKind.I32 or CodecKind.Vari => (int.MinValue, int.MaxValue),
        CodecKind.Bool => (0d, 1d),
        CodecKind.Bits => (0d, WireMath.Pow2(codec.N) - 1),
        _ => (0d, 0d),
    };

    private static int MaxEncodedBytes(CatalogCodec codec) => codec.Kind switch
    {
        CodecKind.U8 or CodecKind.I8 => 1,
        CodecKind.U16 or CodecKind.I16 or CodecKind.F16 or CodecKind.TickLo => 2,
        CodecKind.U32 or CodecKind.I32 or CodecKind.F32 or CodecKind.Quat3 => 4,
        CodecKind.Varu or CodecKind.Vari or CodecKind.EntityRef => 5,
        CodecKind.Quant or CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle => codec.Bits / 8,
        CodecKind.Bytes => codec.N,
        CodecKind.Str or CodecKind.Blob => 5 + codec.MaxBytes,
        _ => 0,
    };
}

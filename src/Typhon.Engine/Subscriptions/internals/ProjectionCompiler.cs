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
            plans[i] = CompileArchetype(registry.Archetypes[i], engine, nominalTickPeriodSeconds, largestTickMultiplier, slack, registry.Options.FrameBytes);
        }

        return plans;
    }

    /// <summary>
    /// Derives a <c>vel</c> codec (W5, <c>typhon.3</c>, Realms D-3): its absolute unit <c>2^unitExp</c> metres per tick from the drift the motion rule
    /// tolerates, then the narrowest of 8, 16, 24 and 32 bits that carries the widest displacement one tick allows below the teleport threshold.
    /// </summary>
    /// <param name="maxSpeedMps">The teleport threshold, in metres per second.</param>
    /// <param name="nominalTickPeriodSeconds">The nominal tick period.</param>
    /// <param name="tickMultiplier">The tick multiplier the width must cover: the runtime's largest allowed one, or 1 under the per-archetype opt-out.</param>
    /// <param name="toleranceMetres">The motion rule's tolerance (<c>MotionBuilder.Tolerance</c>).</param>
    /// <param name="maxAgeSeconds">The motion rule's heartbeat (<c>MotionBuilder.MaxAge</c>).</param>
    /// <returns>The width in bits and the unit's exponent, which travel together in the catalog.</returns>
    /// <remarks>
    /// <para>
    /// <b>The unit is physical, not a fraction of a position step.</b> The velocity quantum bounds how far extrapolation drifts between two segments: over
    /// <c>MaxAge</c> ticks a half-unit error accrues <c>(u/2)·MaxAge</c>, so <c>u</c> is the largest power of two ≤ <c>Tolerance / MaxAge</c> (ticks at the
    /// nominal period) — the drift then stays within half the tolerance. Derived once per archetype, it is the same in every realm: a realm registered at run
    /// time never changes it, and a cross-realm move re-encodes only the position. SWG's players (5 cm, 5 s at 50 Hz → 250 ticks) get 2⁻¹³ m per tick,
    /// exactly the unit the position-relative codec gave them (2⁻¹⁰ m ÷ 8), so their bytes are unchanged.
    /// </para>
    /// <para>
    /// <b>The multiplier is in the width because the teleport test uses the CURRENT tick period.</b> Under overload the engine ticks less often and each
    /// tick covers more ground, so a displacement-per-tick codec sized for the nominal rate would saturate exactly while the server is overloaded. An
    /// archetype whose movement is bounded per tick rather than per second says so with <see cref="MotionBuilder.IgnoreTickDilation"/>.
    /// </para>
    /// </remarks>
    internal static (int Bits, int UnitExp) VelocityCodec(double maxSpeedMps, double nominalTickPeriodSeconds, int tickMultiplier, double toleranceMetres,
        double maxAgeSeconds)
    {
        var ageTicks = Math.Max(1d, Math.Round(maxAgeSeconds / nominalTickPeriodSeconds, MidpointRounding.AwayFromZero));
        var exp = Math.ILogB(toleranceMetres / ageTicks);   // floor(log2): 2^exp <= tolerance / ageTicks
        exp = Math.Clamp(exp, ProtocolConstants.MinVelocityUnitExp, ProtocolConstants.MaxVelocityUnitExp);
        var codes = Math.Ceiling(Math.ScaleB(maxSpeedMps * nominalTickPeriodSeconds * tickMultiplier, -exp));
        foreach (var bits in CodecWidths)
        {
            if (WireMath.SymmetricLimit(bits) >= codes)
            {
                return (bits, exp);
            }
        }

        throw new InvalidOperationException(
            $"A teleport threshold of {maxSpeedMps} m/s over a {nominalTickPeriodSeconds} s tick at multiplier {tickMultiplier} needs {codes} velocity codes "
            + $"at a 2^{exp} m unit (tolerance {toleranceMetres} m over {ageTicks} ticks), and 32 bits carries {WireMath.SymmetricLimit(32)}. Lower the "
            + "threshold, or raise the tolerance or shorten MaxAge.");
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
        int largestTickMultiplier, double visibilitySlackM, int frameBytes)
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

        // Each reference keeps the netId it last resolved to in the cold entry, public fields first (13 § 5).
        var references = NumberReferences(fields, 0);
        references = NumberReferences(ownerFields, references);
        var collections = ListCollections(fields, ownerFields);

        // Two sizes per section (13 § 6): what it can put on the wire, which sizes the encode scratch and a record's frame space, and what it takes in its
        // entry — the same for an inline section, an 8-byte arena reference for a wide one. Only the second moves an offset, so a scalar archetype's
        // entries are byte for byte what they were (E-9).
        var onEnter = SectionOf(fields, 0);
        var groups = new CompiledGroup[groupNames.Length];
        var stateBodyBytes = 0;
        var storedStateBytes = 0;
        for (var g = 0; g < groupNames.Length; g++)
        {
            var section = SectionOf(fields, g + 1) with { StoredOffset = storedStateBytes };
            groups[g] = new CompiledGroup { Name = groupNames[g], Bit = g, TickSlot = motionTickSlots + g, Section = section };
            stateBodyBytes += section.MaxBodyBytes;
            storedStateBytes += section.StoredBytes;
        }

        var ownerGroups = new CompiledGroup[ownerGroupNames.Length];
        var ownerBodyBytes = 0;
        var storedOwnerBytes = 0;
        for (var g = 0; g < ownerGroupNames.Length; g++)
        {
            // The owner section has its own bit space (W17): its groups start again at bit 0. They take NO hot-entry tick slot — those four belong to the
            // public groups and the motion segment, and owner data lives in the block's own owner entry, whose change tracking the projection pass defines.
            var section = SectionOf(ownerFields, g + 1) with { StoredOffset = storedOwnerBytes };
            ownerGroups[g] = new CompiledGroup { Name = ownerGroupNames[g], Bit = g, TickSlot = -1, Section = section };
            ownerBodyBytes += section.MaxBodyBytes;
            storedOwnerBytes += section.StoredBytes;
        }

        var ownerEntrySize = storedOwnerBytes == 0 ? 0 : (storedOwnerBytes + 7) & ~7;

        // A wide section's worst case has to fit one arena class and a quarter of a frame (13 § 6.3): a body that cannot be stored, or a record that can
        // never be sent, is a declaration to refuse here rather than an entity silently missing at run time.
        var wideLimit = Math.Min(WideBodyArena.MaxClassBytes, frameBytes / 4);
        var hasWide = false;
        foreach (var (section, name) in WideCandidates(onEnter, groups, ownerGroups))
        {
            if (!section.Wide)
            {
                continue;
            }

            hasWide = true;
            if (section.MaxBodyBytes > wideLimit)
            {
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' declares the {name} section with up to {section.MaxBodyBytes} bytes of text and fields, above the " +
                    $"{wideLimit} a wide section may reach (a quarter of the {frameBytes}-byte frame, at most {WideBodyArena.MaxClassBytes}). Lower a " +
                    "Codec.Str cap, or split the text across groups.");
            }
        }

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

        var blockLayout = ReplicationBlockLayout.ForArchetype(layout.ClusterSize, moving ? position.SegmentBytes : 0, storedStateBytes, quantizedPositionBytes,
            runStartBytes, ownerEntrySize, enterPositionBytes, onEnter.StoredBytes, headingBytes: 4 * headings, referenceBytes: 4 * references);

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
            MaxOwnerBodyBytes = ownerBodyBytes,
            HasWideSections = hasWide,
            ReferenceCount = references,
            Collections = collections,
            ResolvesReferences = references > 0 || Array.Exists(collections, static c => c.HasReferences),
            TickSlotCount = motionTickSlots + groupNames.Length,
        };
    }

    // The archetype's collections, numbered in field order, public then owner, each with its row in the code scratch.
    private static CompiledCollection[] ListCollections(CompiledField[] fields, CompiledField[] ownerFields)
    {
        var collections = new List<CompiledCollection>();
        for (var i = 0; i < fields.Length + ownerFields.Length; i++)
        {
            var collection = (i < fields.Length ? fields[i] : ownerFields[i - fields.Length]).Collection;
            if (collection != null)
            {
                collection.Index = collections.Count;
                collection.Row = i;
                collections.Add(collection);
            }
        }

        return collections.ToArray();
    }

    // Gives each reference field its slot in the cold entry's reference region, continuing from `next`; returns the next free slot.
    private static int NumberReferences(CompiledField[] fields, int next)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Path == ColumnPath.EntityRef)
            {
                fields[i] = fields[i] with { ReferenceSlot = next++ };
            }
        }

        return next;
    }

    private static IEnumerable<(CompiledSection Section, string Name)> WideCandidates(CompiledSection onEnter, CompiledGroup[] groups,
        CompiledGroup[] ownerGroups)
    {
        yield return (onEnter, "onEnter");
        foreach (var group in groups)
        {
            yield return (group.Section, $"'{group.Name}' group's");
        }

        foreach (var group in ownerGroups)
        {
            yield return (group.Section, $"owner '{group.Name}' group's");
        }
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

        // A count field (W33) compiles to one sub-field per component — its own code row, read at its member's offset — so the column walk stays one
        // scalar per column (13 § 4). The sub-fields of a field are consecutive, so a section's encode writes them back to back as the wire wants.
        var expanded = new List<CompiledField>(ordered.Count);
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

            CompileField(projection, field, meta, layout, engine, fieldSection, expanded, ref packBits);
        }

        var compiled = expanded.ToArray();

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

    private static void CompileField(ArchetypeProjection projection, ProjectedField field, ArchetypeMetadata meta, ArchetypeClusterInfo layout,
        DatabaseEngine engine, int section, List<CompiledField> into, ref int packBits)
    {
        var codec = field.Codec.Catalog;
        var slot = ResolveSlot(projection, meta, field.ComponentTypeId, field.ComponentName, field.Name);
        var definition = ResolveDefinition(meta, slot, engine);
        var source = ResolveField(projection, definition, field.SourceFieldName, field.Name);
        var shape = FieldShape.Of(source.DotNetType);
        var textCapacity = CodecPairing.TextCapacityOf(source.DotNetType);
        RefuseUnsupportedFieldCodec(projection, field, codec, shape != null, textCapacity > 0);

        var ratioOffset = -1;
        if (field.MaxSourceFieldName != null)
        {
            ratioOffset = ResolveField(projection, definition, field.MaxSourceFieldName, field.Name).OffsetInComponentStorage;
        }

        // A shape compiles to one sub-field per component, which is only the wire's own layout when the codec carries that many values: a vector or
        // quaternion codec, or a count equal to the shape's. Anything else would write N values where the catalog declares one — a desynced stream, not a
        // refusal — so it is refused here, the last place both are known. A Fraction reads two scalars; a shape has no ratio.
        if (shape != null
            && (ratioOffset >= 0 || (codec.Kind is not (CodecKind.Vec2 or CodecKind.Vec3 or CodecKind.Quat3) && codec.Count != shape.Count)))
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' declares field '{field.Name}' over a {source.DotNetType.Name} ({shape.Count} components) with codec " +
                $"'{codec.Type}'{(ratioOffset >= 0 ? " as a Fraction" : "")}. A shape travels as a vector codec or a count of {shape.Count} (W33).");
        }

        var packed = IsPacked(field.Codec);
        var bitCount = !packed ? 0 : codec.Kind == CodecKind.Bool ? 1 : codec.N;
        var bitOffset = packBits;
        packBits += bitCount;

        // The column path, from the same table the registry ran at declaration (13 § 2.3), against the stored field's own type. A Fraction encodes the
        // ratio of two fields as a double, never the field, so it is always the quantizing path.
        var path = ratioOffset >= 0
            ? ColumnPath.Quantizing
            : CodecPairing.Classify(source.DotNetType, codec, field.Codec.Saturating, $"Field '{field.Name}' of archetype '{projection.Name}'");
        if (path == ColumnPath.None)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' declares field '{field.Name}' with codec '{codec.Type}', which the column walk has no path for.");
        }

        // A collection's element (W34): compiled here, where the field's codec carries the element T's fields resolved to.
        var collection = path == ColumnPath.Collection ? CompileCollection(projection.Name, field.Name, source.DotNetType, codec) : null;

        // A reference sent once is never corrected: an onEnter field — and a static archetype's, all of which fold into onEnter — reaches a client in its
        // enter record only, so the netId it names would outlive its holder on every client that entered the entity (SUB-31). It belongs in a group. A
        // collection whose element holds one is a reference too.
        if ((path == ColumnPath.EntityRef || collection is { HasReferences: true }) && section == 0)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' declares the reference '{field.Name}' {(projection.IsStatic ? "on a static archetype" : "OnEnter")}. A " +
                "reference names its target's netId, and once that target is gone the netId is reissued: a field sent only on enter can never be told, so " +
                "its clients would resolve it to whoever holds the number next (SUB-31). Declare it in a change group.");
        }

        // A Fraction over text reads two numbers that are not there: ResolveSourceType refuses it, naming the type.
        var sourceType = path == ColumnPath.EntityRef ? ProjectionSourceType.Reference
            : path == ColumnPath.Collection ? ProjectionSourceType.Collection
            : textCapacity > 0 && ratioOffset < 0 ? ProjectionSourceType.Text
            : shape != null ? (shape.Element == typeof(double) ? ProjectionSourceType.Double : ProjectionSourceType.Single)
            : ResolveSourceType(projection, field, source);
        var (intMin, intMax) = CodecPairing.ClampRange(codec);
        var headingTolerance = 0u;
        if (field.IsHeading)
        {
            if (!double.IsFinite(field.HeadingToleranceDeg) || field.HeadingToleranceDeg <= 0 || field.HeadingToleranceDeg >= 180)
            {
                throw new InvalidOperationException(
                    $"Archetype '{projection.Name}' declares the heading '{field.Name}' with a tolerance of {field.HeadingToleranceDeg}°. A heading is sent when it " +
                    "turns past its tolerance, which must be above 0° and below 180°.");
            }

            if (codec.Kind != CodecKind.Angle || codec.Count > 1 || field.Owner || field.OnEnter)
            {
                throw new InvalidOperationException($"Heading '{field.Name}' of archetype '{projection.Name}' must be a public, grouped, single angle field.");
            }

            // The deadband in code space: a turn of the tolerance is this many codes of the angle's 2^bits per full turn.
            headingTolerance = (uint)Math.Floor(field.HeadingToleranceDeg / 360d * Math.Pow(2, codec.Bits));
        }

        var compiled = new CompiledField
        {
            HeadingPlusOne = field.IsHeading ? 1 : 0,
            HeadingToleranceCodes = headingTolerance,
            Name = field.Name,
            ComponentSlot = slot,
            ComponentOffsetInCluster = layout.ComponentOffset(slot),
            ComponentSize = layout.ComponentSize(slot),
            FieldOffsetInComponent = source.OffsetInComponentStorage,
            RatioOffsetInComponent = ratioOffset,
            SourceType = sourceType,
            Codec = codec,
            CodecKind = codec.Kind,
            CodecBits = codec.Kind == CodecKind.Bits ? codec.N : codec.Bits,
            VectorScale = codec.Kind is CodecKind.Vec2 or CodecKind.Vec3 ? codec.Scale : 0d,
            EnumType = field.Codec.EnumType,
            Saturating = field.Codec.Saturating,
            Path = path,
            IntMin = intMin,
            IntMax = intMax,
            QuantMin = codec.Kind == CodecKind.Quant ? codec.Min[0] : 0d,
            QuantMax = codec.Kind == CodecKind.Quant ? codec.Max[0] : 0d,
            Section = section,
            GroupBit = section == 0 ? -1 : section - 1,
            Ordinal = into.Count,
            Packed = packed,
            BitOffset = packed ? bitOffset : 0,
            BitCount = bitCount,
            MaxBodyBytes = packed ? 0 : collection != null ? CollectionMaxBytes(collection) : MaxEncodedBytes(codec),
            Owner = field.Owner,
            Shape = field.Shape,
            ComponentCount = 1,
            TextCapacity = textCapacity,
            ReferenceTarget = path == ColumnPath.EntityRef ? CodecPairing.ReferenceTarget(source.DotNetType) : null,
            Collection = collection,
        };

        if (shape == null || path == ColumnPath.Quaternion)
        {
            if (path == ColumnPath.Quaternion)
            {
                // quat3 is one 32-bit code of all four components (W8): one row, its reader taking each member at its own offset.
                var offsets = new int[shape.Count];
                for (var k = 0; k < offsets.Length; k++)
                {
                    offsets[k] = source.OffsetInComponentStorage + shape.Offsets[k];
                }

                compiled = compiled with { ShapeOffsets = offsets };
            }

            into.Add(compiled);
            return;
        }

        // A shape: one sub-field per component, in the shape's wire order (13 § 2.1), each a scalar column of the element type.
        for (var k = 0; k < shape.Count; k++)
        {
            into.Add(compiled with
            {
                FieldOffsetInComponent = source.OffsetInComponentStorage + shape.Offsets[k],
                Ordinal = into.Count,
                Component = k,
                ComponentCount = shape.Count,
            });
        }
    }

    private static CompiledSection SectionOf(CompiledField[] fields, int section)
    {
        var first = -1;
        var count = 0;
        var packedCount = 0;
        var bits = 0;
        var bytes = 0;
        var wide = false;
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Section != section)
            {
                continue;
            }

            wide |= fields[i].Path is ColumnPath.Text or ColumnPath.Collection;

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
            Wide = wide,
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
        // Realm 0's frame (R4.2): the catalog codec still carries its bounds, and the two are one computation so they cannot disagree.
        var bits = Codec.DefaultPositionBits;
        var frame = PositionFrame.Over(in grid.Config, dims, bits);
        var pos = new CatalogCodec
        {
            Kind = dims == 3 ? CodecKind.Pos3 : CodecKind.Pos2, Bits = bits, Min = (double[])frame.Min.Clone(), Max = (double[])frame.Max.Clone(),
        };
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
            var tolerance = motion.ToleranceMetres <= 0 ? DefaultToleranceMetres : motion.ToleranceMetres;
            var maxAge = motion.MaxAgeSeconds <= 0 ? DefaultMaxAgeSeconds : motion.MaxAgeSeconds;
            var (velBits, unitExp) = VelocityCodec(motion.TeleportMaxSpeedMps, nominalTickPeriodSeconds, multiplier, tolerance, maxAge);
            vel = new CatalogCodec { Kind = dims == 3 ? CodecKind.Vel3 : CodecKind.Vel2, Bits = velBits, UnitExp = unitExp };
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
            Frame = frame,
            PositionStep = frame.Step,
            FinestPositionStep = frame.FinestStep,
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
        var resolved = SourceTypeOf(source.DotNetType);
        if (resolved == ProjectionSourceType.None)
        {
            throw new InvalidOperationException(
                $"Archetype '{projection.Name}' replicates '{field.Name}' from '{field.ComponentName}.{field.SourceFieldName}', which is a " +
                $"'{source.DotNetType?.Name ?? source.Type.ToString()}'. A projected field is one number: the column walk reads a boolean, an integer, a " +
                "float or an enum backed by one of those.");
        }

        return resolved;
    }

    // The primitive a CLR type is read as: an enum by its underlying type; None for anything that is not one number.
    private static ProjectionSourceType SourceTypeOf(Type type)
    {
        if (type != null && type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        return type == null ? ProjectionSourceType.None : Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => ProjectionSourceType.Boolean,
            TypeCode.SByte => ProjectionSourceType.SByte,
            TypeCode.Byte => ProjectionSourceType.Byte,
            TypeCode.Int16 => ProjectionSourceType.Int16,
            TypeCode.UInt16 or TypeCode.Char => ProjectionSourceType.UInt16,
            TypeCode.Int32 => ProjectionSourceType.Int32,
            TypeCode.UInt32 => ProjectionSourceType.UInt32,
            TypeCode.Int64 => ProjectionSourceType.Int64,
            TypeCode.UInt64 => ProjectionSourceType.UInt64,
            TypeCode.Single => ProjectionSourceType.Single,
            TypeCode.Double => ProjectionSourceType.Double,
            _ => ProjectionSourceType.None,
        };
    }

    // ── Collections ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A collection field's element (W34, 13 § 6.5): <c>T</c>'s fields, as the codec resolved them, in the catalog's wire order and at their offsets in
    /// <c>T</c>. A <see cref="bool"/> or <see cref="char"/> element field is refused: its marshalled width differs from its width in the buffer, so its
    /// offset could not be trusted — the rule a command struct follows for the same reason.
    /// </summary>
    private static CompiledCollection CompileCollection(string archetype, string field, Type collectionType, CatalogCodec codec)
    {
        var elementType = CodecPairing.CollectionElementOf(collectionType);
        var where = $"Archetype '{archetype}' collection '{field}'";
        var declared = codec.Element?.Fields ?? [];

        // The catalog's canonical element order (CatalogSerializer): packed first, then by ordinal name.
        var ordered = (CatalogField[])declared.Clone();
        Array.Sort(ordered, static (a, b) =>
        {
            var byPacking = (IsPacked(a.Codec) ? 0 : 1).CompareTo(IsPacked(b.Codec) ? 0 : 1);
            return byPacking != 0 ? byPacking : string.CompareOrdinal(a.Name, b.Name);
        });

        var size = System.Runtime.InteropServices.Marshal.SizeOf(elementType);
        var fields = new List<CompiledField>();
        var packBits = 0;
        var references = false;
        foreach (var f in ordered)
        {
            var member = elementType.GetField(f.Name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                         ?? throw new InvalidOperationException($"{where}: {elementType.Name} has no field '{f.Name}'.");
            var memberType = member.FieldType;
            if (memberType == typeof(bool) || memberType == typeof(char))
            {
                throw new InvalidOperationException(
                    $"{where}: element field '{f.Name}' is a {memberType.Name}, whose marshalled width differs from its width in the collection's buffer, " +
                    "so its offset cannot be trusted. Store it as a byte (or a ushort) and declare it so.");
            }

            var offset = (int)System.Runtime.InteropServices.Marshal.OffsetOf(elementType, f.Name);
            var path = CodecPairing.Classify(memberType, f.Codec, false, $"{where}, element field '{f.Name}'");
            var shape = FieldShape.Of(memberType);
            var text = CodecPairing.TextCapacityOf(memberType);
            var sourceType = path == ColumnPath.EntityRef ? ProjectionSourceType.Reference
                : text > 0 ? ProjectionSourceType.Text
                : shape != null ? (shape.Element == typeof(double) ? ProjectionSourceType.Double : ProjectionSourceType.Single)
                : SourceTypeOf(memberType);
            if (sourceType == ProjectionSourceType.None)
            {
                throw new InvalidOperationException($"{where}: element field '{f.Name}' is a {memberType.Name}, which has no column path.");
            }

            references |= path == ColumnPath.EntityRef;
            var packed = IsPacked(f.Codec);
            var bitCount = !packed ? 0 : f.Codec.Kind == CodecKind.Bool ? 1 : f.Codec.N;
            var (intMin, intMax) = CodecPairing.ClampRange(f.Codec);
            var compiled = new CompiledField
            {
                Name = f.Name,
                ComponentSize = size,
                FieldOffsetInComponent = offset,
                RatioOffsetInComponent = -1,
                SourceType = sourceType,
                Codec = f.Codec,
                CodecKind = f.Codec.Kind,
                CodecBits = f.Codec.Kind == CodecKind.Bits ? f.Codec.N : f.Codec.Bits,
                VectorScale = f.Codec.Kind is CodecKind.Vec2 or CodecKind.Vec3 ? f.Codec.Scale : 0d,
                EnumType = memberType.IsEnum ? memberType : null,
                Path = path,
                IntMin = intMin,
                IntMax = intMax,
                QuantMin = f.Codec.Kind == CodecKind.Quant ? f.Codec.Min[0] : 0d,
                QuantMax = f.Codec.Kind == CodecKind.Quant ? f.Codec.Max[0] : 0d,
                GroupBit = -1,
                Ordinal = fields.Count,
                Packed = packed,
                BitOffset = packed ? packBits : 0,
                BitCount = bitCount,
                MaxBodyBytes = packed ? 0 : MaxEncodedBytes(f.Codec),
                Shape = f.Shape,
                ComponentCount = 1,
                TextCapacity = text,
                ReferenceTarget = path == ColumnPath.EntityRef ? CodecPairing.ReferenceTarget(memberType) : null,
            };
            packBits += bitCount;

            if (shape == null || path == ColumnPath.Quaternion)
            {
                if (path == ColumnPath.Quaternion)
                {
                    var offsets = new int[shape.Count];
                    for (var k = 0; k < offsets.Length; k++)
                    {
                        offsets[k] = offset + shape.Offsets[k];
                    }

                    compiled = compiled with { ShapeOffsets = offsets };
                }

                fields.Add(compiled);
                continue;
            }

            for (var k = 0; k < shape.Count; k++)
            {
                fields.Add(compiled with
                {
                    FieldOffsetInComponent = offset + shape.Offsets[k], Ordinal = fields.Count, Component = k, ComponentCount = shape.Count,
                });
            }
        }

        var compiledFields = fields.ToArray();
        return new CompiledCollection
        {
            ElementType = elementType,
            ElementSize = size,
            MaxCount = codec.MaxCount,
            Fields = compiledFields,
            Section = SectionOf(compiledFields, 0),
            HasReferences = references,
        };
    }

    // varu total | varu sent | element^maxCount, at its widest. In 64 bits, saturated: a product past int.MaxValue must reach the wide-section limit's
    // refusal, not wrap below it.
    private static int CollectionMaxBytes(CompiledCollection collection) =>
        (int)Math.Min(int.MaxValue, 10L + ((long)collection.MaxCount * collection.Section.MaxBodyBytes));

    private static void RefuseUnsupportedFieldCodec(ArchetypeProjection projection, ProjectedField field, CatalogCodec codec, bool shape, bool text)
    {
        // A vector or quaternion codec carries a shape (W33): legal on a point or a quaternion, which CodecPairing.Resolve checked.
        if (shape && codec.Kind is CodecKind.Vec2 or CodecKind.Vec3 or CodecKind.Quat3)
        {
            return;
        }

        // Text on a string field: its section is wide, stored out of line (13 § 6). The pairing table judges the cap.
        if (text && codec.Kind == CodecKind.Str)
        {
            return;
        }

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

    private static int MaxEncodedBytes(CatalogCodec codec) => codec.Kind switch
    {
        CodecKind.U8 or CodecKind.I8 => 1,
        CodecKind.U16 or CodecKind.I16 or CodecKind.F16 or CodecKind.TickLo => 2,
        CodecKind.U32 or CodecKind.I32 or CodecKind.F32 or CodecKind.Quat3 => 4,
        CodecKind.U64 or CodecKind.I64 or CodecKind.F64 => 8,
        CodecKind.Varu or CodecKind.Vari or CodecKind.EntityRef => 5,
        CodecKind.Varu64 or CodecKind.Vari64 => 10,
        // One component's: a count field compiles to one sub-field per component (13 § 4).
        CodecKind.Quant or CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle or CodecKind.Vec2 or CodecKind.Vec3 => codec.Bits / 8,
        CodecKind.Bytes => codec.N,
        CodecKind.Str or CodecKind.Blob => 5 + codec.MaxBytes,
        _ => 0,
    };
}

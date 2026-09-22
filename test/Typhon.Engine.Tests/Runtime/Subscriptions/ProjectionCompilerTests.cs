using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-02 — resolution: every declared name becomes a pair of byte offsets taken from the engine's own layout, and every name that cannot be resolved is
/// refused at <c>Start</c> by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertions compare against the engine, not against a constant.</b> A test that hard-coded "field <c>level</c> is at offset 4" would pass while the
/// schema changed underneath it and the projection read the wrong bytes. What has to hold is that the compiler's answer IS
/// <see cref="ArchetypeClusterInfo.ComponentOffset"/> at the slot <see cref="ArchetypeMetadata.GetSlot"/> resolves, plus
/// <c>DBComponentDefinition.Field.OffsetInComponentStorage</c> — the same three numbers every other cluster reader uses (SUB-01).
/// </para>
/// <para>
/// <b>Refusals are asserted on their message, not only on their type.</b> A declaration error that says "component not found" and names neither the component
/// nor the archetype costs the reader the same search the compiler already did.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ProjectionCompilerTests : TestBase<ProjectionCompilerTests>
{
    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    /// <summary>
    /// Every field of every declared archetype resolves to the offset pair the engine itself would compute.
    /// </summary>
    [Test]
    public void EveryDeclaredField_ResolvesToTheEnginesOwnOffsets()
    {
        using var dbe = SetupEngine();
        var plans = ProjectionTestSchema.CompileAll(dbe);

        Assert.That(plans.Length, Is.EqualTo(3), "three archetypes were declared");
        foreach (var plan in plans)
        {
            var meta = ArchetypeRegistry.GetMetadata(plan.ArchetypeCatalogId);
            var layout = meta.ClusterLayout;
            Assert.That(plan.ClusterLayout, Is.SameAs(layout), $"'{plan.Name}' holds the archetype's own cluster layout");
            Assert.That(plan.SlotCount, Is.EqualTo(layout.ClusterSize));

            AssertResolved(plan, plan.Fields, meta, layout, dbe);
            AssertResolved(plan, plan.OwnerFields, meta, layout, dbe);
        }
    }

    private static void AssertResolved(CompiledProjectionPlan plan, CompiledField[] fields, ArchetypeMetadata meta, ArchetypeClusterInfo layout,
        DatabaseEngine dbe)
    {
        foreach (var field in fields)
        {
            var componentType = meta._slotToComponentType[field.ComponentSlot];
            var definition = dbe.GetComponentTable(componentType).Definition;

            Assert.That(field.ComponentSlot, Is.EqualTo(meta.GetSlot(ArchetypeRegistry.GetComponentTypeId(componentType))),
                $"'{plan.Name}.{field.Name}' resolves its component through ArchetypeMetadata.GetSlot");
            Assert.That(field.ComponentOffsetInCluster, Is.EqualTo(layout.ComponentOffset(field.ComponentSlot)),
                $"'{plan.Name}.{field.Name}' takes its column offset from ArchetypeClusterInfo.ComponentOffset");
            Assert.That(field.ComponentSize, Is.EqualTo(layout.ComponentSize(field.ComponentSlot)),
                $"'{plan.Name}.{field.Name}' strides its column by the layout's component size");

            // The compiler knows the field only by its wire name; the schema knows it by its C# name. Finding the one field of this component whose measured
            // offset the compiler landed on is what proves the two were joined correctly rather than by coincidence of ordering.
            var matched = false;
            foreach (var pair in definition.FieldsByName)
            {
                if (!pair.Value.IsStatic && pair.Value.OffsetInComponentStorage == field.FieldOffsetInComponent)
                {
                    matched = true;
                    break;
                }
            }

            Assert.That(matched, Is.True,
                $"'{plan.Name}.{field.Name}' resolves to offset {field.FieldOffsetInComponent} of '{definition.Name}', which stores no field there");
            Assert.That(field.SourceType, Is.Not.EqualTo(ProjectionSourceType.None), $"'{plan.Name}.{field.Name}' resolves a source type");
        }
    }

    /// <summary>
    /// The creature's fields land on the exact offsets the schema measured, named field by field.
    /// </summary>
    [Test]
    public void CreatureFields_LandOnTheSchemasMeasuredOffsets()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));

        var meta = ArchetypeRegistry.GetMetadata(plan.ArchetypeCatalogId);
        var aiSlot = meta.GetSlot(ArchetypeRegistry.GetComponentTypeId<ProjAi>());
        var vitalsSlot = meta.GetSlot(ArchetypeRegistry.GetComponentTypeId<ProjVitals>());
        var ai = dbe.GetComponentTable(typeof(ProjAi)).Definition;
        var vitals = dbe.GetComponentTable(typeof(ProjVitals)).Definition;

        var level = ProjectionTestSchema.FieldNamed(plan.Fields, "level");
        Assert.That(level.ComponentSlot, Is.EqualTo(aiSlot));
        Assert.That(level.FieldOffsetInComponent, Is.EqualTo(ai.FieldsByName["Level"].OffsetInComponentStorage));
        Assert.That(level.SourceType, Is.EqualTo(ProjectionSourceType.UInt16));

        var mode = ProjectionTestSchema.FieldNamed(plan.Fields, "mode");
        Assert.That(mode.FieldOffsetInComponent, Is.EqualTo(ai.FieldsByName["Mode"].OffsetInComponentStorage));
        Assert.That(mode.SourceType, Is.EqualTo(ProjectionSourceType.Byte), "a byte-backed enum reads as its underlying type");
        Assert.That(mode.Packed, Is.True, "bits{3} lives in its section's pack (W12)");
        Assert.That(mode.BitCount, Is.EqualTo(3));

        // A Fraction is one column plus a second offset into the SAME component: the ratio is what travels, so the denominator never becomes a field.
        var hp = ProjectionTestSchema.FieldNamed(plan.Fields, "hp");
        Assert.That(hp.ComponentSlot, Is.EqualTo(vitalsSlot));
        Assert.That(hp.FieldOffsetInComponent, Is.EqualTo(vitals.FieldsByName["Health"].OffsetInComponentStorage));
        Assert.That(hp.RatioOffsetInComponent, Is.EqualTo(vitals.FieldsByName["MaxHealth"].OffsetInComponentStorage));

        var template = ProjectionTestSchema.FieldNamed(plan.Fields, "template");
        Assert.That(template.Section, Is.EqualTo(0), "an onEnter field is section 0 (W11)");
        Assert.That(template.GroupBit, Is.EqualTo(-1), "an onEnter field belongs to no change group (W15)");
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "ThinkCooldown").Name, Is.Null,
            "a component field nobody declared never reaches the plan");
    }

    /// <summary>
    /// The position resolves to the component's spatial field, and to the grid's bounds rather than to anything the declaration carried.
    /// </summary>
    [Test]
    public void Position_ResolvesToTheSpatialFieldAndTheGridsBounds()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));
        var meta = ArchetypeRegistry.GetMetadata(plan.ArchetypeCatalogId);
        var bounds = dbe.GetComponentTable(typeof(ProjBounds)).Definition;

        Assert.That(plan.Position, Is.Not.Null);
        Assert.That(plan.Position.Moving, Is.True);
        Assert.That(plan.Position.Linear, Is.True);
        Assert.That(plan.Position.Dims, Is.EqualTo(2), "AABB2F is a 2D spatial field");
        Assert.That(plan.Position.ComponentSlot, Is.EqualTo(meta.GetSlot(ArchetypeRegistry.GetComponentTypeId<ProjBounds>())));
        Assert.That(plan.Position.FieldOffsetInComponent, Is.EqualTo(bounds.SpatialField.OffsetInComponentStorage));
        Assert.That(plan.Position.Pos.Min[0], Is.EqualTo(-ProjectionTestSchema.WorldExtentM));
        Assert.That(plan.Position.Pos.Max[0], Is.EqualTo(ProjectionTestSchema.WorldExtentM));
        Assert.That(plan.Position.FinestPositionStep, Is.EqualTo(ProjectionTestSchema.PositionStepM),
            "24 bits over a 16 384 m world is a 2⁻¹⁰ m quantum");
        Assert.That(plan.Position.ToleranceMetres, Is.EqualTo(0.05));
        Assert.That(plan.Position.MaxAgeSeconds, Is.EqualTo(5.0), "an undeclared heartbeat takes the engine default");
    }

    /// <summary>
    /// The owner section gets its own groups, its own bit space and its own entry bytes in the block.
    /// </summary>
    [Test]
    public void OwnerSection_HasItsOwnBitSpaceAndItsOwnEntry()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjPlayer));

        Assert.That(ProjectionTestSchema.NamesOf(plan.OwnerFields), Is.EqualTo(new[] { "items", "credits" }),
            "owner groups canonicalize on their own: 'bag' before 'owner'");
        Assert.That(plan.OwnerGroups.Length, Is.EqualTo(2));
        Assert.That(plan.OwnerGroups[0].Name, Is.EqualTo("bag"));
        Assert.That(plan.OwnerGroups[0].Bit, Is.EqualTo(0));
        Assert.That(plan.OwnerGroups[1].Name, Is.EqualTo("owner"));
        Assert.That(plan.OwnerGroups[1].Bit, Is.EqualTo(1));

        // The public side has its own bit 0 at the same time, which is the whole point of a separate section (W17).
        Assert.That(plan.Groups[0].Bit, Is.EqualTo(0));
        Assert.That(plan.Groups[0].Name, Is.EqualTo("vitals"));

        Assert.That(plan.OwnerEntrySize, Is.GreaterThan(0), "an archetype with owner fields reserves owner entries in its blocks");
        Assert.That(plan.BlockLayout.OwnerEntrySize, Is.EqualTo(plan.OwnerEntrySize));
        Assert.That(plan.BlockLayout.OwnerOffset, Is.LessThan(plan.BlockLayout.BlockSize));

        var creature = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));
        Assert.That(creature.OwnerEntrySize, Is.EqualTo(0), "an archetype with no owner fields reserves none");
        Assert.That(creature.BlockLayout.OwnerOffset, Is.EqualTo(creature.BlockLayout.BlockSize));
    }

    /// <summary>
    /// A static archetype compiles to no change groups at all: its fields travel in the enter record and nothing compares them per tick.
    /// </summary>
    [Test]
    public void StaticArchetype_CompilesToNoChangeGroups()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjRock));

        Assert.That(plan.IsStatic, Is.True);
        Assert.That(plan.Groups, Is.Empty);
        Assert.That(plan.Fields.Length, Is.EqualTo(1));
        Assert.That(plan.Fields[0].Section, Is.EqualTo(0), "a static archetype's fields are all enter fields");
        Assert.That(plan.TickSlotCount, Is.EqualTo(0), "no groups and a static position need no change tick");
        Assert.That(plan.Position.Moving, Is.False);
        Assert.That(plan.Position.Vel, Is.Null, "a static position carries no velocity");
    }

    /// <summary>
    /// A field whose component is not on the archetype is refused when the plan is compiled, naming the component, the archetype and what it does hold.
    /// </summary>
    [Test]
    public void FieldOnAComponentTheArchetypeDoesNotHave_IsRefusedWithTheNames()
    {
        using var dbe = SetupEngine();
        var subs = new SubscriptionsRegistry();

        // ProjWallet is ProjPlayer's; ProjCreature has Bounds, Ai and Vitals. The declaration itself is legal — a Comp<T> handle carries a component type id
        // and nothing about which archetype may use it — so this can only be caught where the id is resolved against the archetype.
        subs.Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjPlayer.Wallet, w => w.ItemCount, Codec.VarUInt, name: "items"));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => ProjectionCompiler.Compile(subs, dbe, ProjectionTestSchema.TickPeriodSeconds, 1));

        Assert.That(thrown.Message, Does.Contain(nameof(ProjCreature)));
        Assert.That(thrown.Message, Does.Contain(nameof(ProjWallet)));
        Assert.That(thrown.Message, Does.Contain(nameof(ProjVitals)), "the message lists what the archetype does hold");
    }

    /// <summary>
    /// Motion with no teleport speed is refused: the velocity codec's width is derived from it, and the engine has no default speed to invent.
    /// </summary>
    [Test]
    public void MotionWithNoTeleportSpeed_IsRefused()
    {
        using var dbe = SetupEngine();
        var subs = new SubscriptionsRegistry();
        subs.Archetype<ProjCreature>(a => a.Motion(ProjCreature.Bounds));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => ProjectionCompiler.Compile(subs, dbe, ProjectionTestSchema.TickPeriodSeconds, 1));

        Assert.That(thrown.Message, Does.Contain("Teleport"));
        Assert.That(thrown.Message, Does.Contain(nameof(ProjCreature)));
    }

    /// <summary>
    /// The compiled plan carries the block layout the replication state will be carved with, sized for this archetype's clusters.
    /// </summary>
    [Test]
    public void BlockLayout_FollowsTheArchetypesClusterSize()
    {
        using var dbe = SetupEngine();
        var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(dbe), nameof(ProjCreature));

        Assert.That(plan.BlockLayout.SlotCount, Is.EqualTo(plan.SlotCount));
        Assert.That(plan.BlockLayout.HotOffset, Is.EqualTo(ReplicationBlockLayout.HeaderSize));
        Assert.That(plan.BlockLayout.ColdOffset, Is.EqualTo(ReplicationBlockLayout.HeaderSize + (plan.SlotCount * plan.BlockLayout.HotStride)));
        Assert.That(plan.TickSlotCount, Is.EqualTo(3), "motion's segment tick plus 'state' and 'vitals'");
        Assert.That(plan.Groups[0].TickSlot, Is.EqualTo(1), "slot 0 belongs to the motion segment");
    }

    /// <summary>
    /// The block layout reserves exactly what the plan says the archetype produces: the segment and the state body are equalities, not budgets.
    /// </summary>
    /// <remarks>
    /// This is the assertion whose absence let the two drift. The hot entry used to fix a 14 B segment and a 16 B state body; a 15 B segment therefore wrote
    /// its last byte over the state region, and nothing in the suite compared the two numbers. Asserting equality — the layout reserves what the plan
    /// computed, no more and no less — is what makes the sizing verifiable rather than merely plausible.
    /// </remarks>
    [Test]
    public void TheLayoutReservesExactlyWhatThePlanProduces()
    {
        using var dbe = SetupEngine();
        var plans = ProjectionTestSchema.CompileAll(dbe);

        foreach (var plan in plans)
        {
            var layout = plan.BlockLayout;
            var moving = plan.Position is { Moving: true };
            var expectedSegment = moving ? plan.Position.SegmentBytes : 0;
            var expectedPrev = moving ? plan.Position.Dims * (plan.Position.Pos.Bits / 8) : 0;

            Assert.Multiple(() =>
            {
                Assert.That(layout.SegmentBytes, Is.EqualTo(expectedSegment), $"'{plan.Name}' reserves its own segment, byte for byte");
                Assert.That(layout.PackedStateBytes, Is.EqualTo(plan.MaxStateBodyBytes), $"'{plan.Name}' reserves its own widest state body");
                Assert.That(layout.PrevPositionBytes, Is.EqualTo(expectedPrev), $"'{plan.Name}' reserves one quantized position in its cold entry");
                Assert.That(layout.OwnerEntrySize, Is.EqualTo(plan.OwnerEntrySize));
                Assert.That(ReplicationBlockLayout.HotFixedBytes + layout.SegmentBytes + layout.PackedStateBytes, Is.LessThanOrEqualTo(layout.HotStride),
                    $"'{plan.Name}' fits its own hot stride");
                Assert.That(ReplicationBlockLayout.ColdFixedBytes + layout.PrevPositionBytes + layout.RunStartBytes, Is.LessThanOrEqualTo(layout.ColdStride),
                    $"'{plan.Name}' fits its own cold stride");
            });
        }

        var creature = ProjectionTestSchema.PlanFor(plans, nameof(ProjCreature));
        Assert.Multiple(() =>
        {
            // 2 m/s of teleport headroom at 10 Hz over a 2^-10 m quantum: 16 bits at quantaDiv 8, so p0 (6) + v (4) + t0 (2) + epoch (1).
            Assert.That(creature.BlockLayout.SegmentBytes, Is.EqualTo(13));
            Assert.That(creature.BlockLayout.PackedStateBytes, Is.EqualTo(4), "'state' packs mode+alerted into 1 B; 'vitals' is level (2) + hp (1)");
            Assert.That(creature.BlockLayout.HotStride, Is.EqualTo(64), "an SWG-shaped 2D archetype still lands on AC-5's one cache line");
            Assert.That(creature.BlockLayout.ColdStride, Is.EqualTo(32));
        });
    }

    /// <summary>
    /// A 3D archetype with four <c>varu</c> state fields is sized to two cache lines, and the compiler refuses nothing to get there.
    /// </summary>
    /// <remarks>
    /// 18 B of segment (u24 x 3, i16 x 3, t0, epoch) and 20 B of state body is 70 B with the 32 B head — a legal declaration the fixed 64 B entry would have
    /// silently overrun. The answer is a 128 B stride, decided by the layout and visible in the plan, rather than a refusal at <c>Start</c>: the shape of an
    /// application's entities is not the engine's to veto.
    /// </remarks>
    [Test]
    public void ThreeDimensionalArchetype_TakesTwoCacheLinesInsteadOfOverrunningOne()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true);
        var plan = ProjectionTestSchema.CompileFlyer(dbe);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Position.Dims, Is.EqualTo(3), "AABB3F is a 3D spatial field");
            Assert.That(plan.Position.Vel.Bits, Is.EqualTo(16));
            Assert.That(plan.BlockLayout.SegmentBytes, Is.EqualTo(18), "9 B of p0, 6 B of v, 2 B of t0 and an epoch byte");
            Assert.That(plan.BlockLayout.PackedStateBytes, Is.EqualTo(20), "four varu fields reserve their 5-byte worst case each");
            Assert.That(plan.BlockLayout.HotStride, Is.EqualTo(128), "32 + 18 + 20 = 70 B, which is two cache lines");
            Assert.That(plan.BlockLayout.PrevPositionBytes, Is.EqualTo(9));
            Assert.That(plan.BlockLayout.RunStartBytes, Is.EqualTo(16), "the quantized position plus a u32 tick, rounded to four bytes");
            Assert.That(plan.BlockLayout.ColdStride, Is.EqualTo(32), "4 + 9 + 16 = 29 B still fits half a line");
        });
    }

    /// <summary>
    /// The enum behind a field and its saturating flag survive compilation, because a catalog built from the plan needs both.
    /// </summary>
    /// <remarks>
    /// Neither changes a byte the projection pass reads, which is exactly why they went missing: the plan was written for the per-entity loop and the catalog
    /// builder had to go back to the declarations for them. An <c>enum</c> is an attribute on an integer codec (W13), so without the type the catalog emits a
    /// bare <c>bits</c> and every client loses the names; without the flag a <c>varu</c> stops saying its 64-bit narrowing was explicit.
    /// </remarks>
    [Test]
    public void TheEnumTypeAndTheSaturatingFlagSurviveCompilation()
    {
        using var dbe = SetupEngine();
        var plans = ProjectionTestSchema.CompileAll(dbe);

        var mode = ProjectionTestSchema.FieldNamed(ProjectionTestSchema.PlanFor(plans, nameof(ProjCreature)).Fields, "mode");
        var level = ProjectionTestSchema.FieldNamed(ProjectionTestSchema.PlanFor(plans, nameof(ProjCreature)).Fields, "level");
        var owner = ProjectionTestSchema.PlanFor(plans, nameof(ProjPlayer)).OwnerFields;
        var credits = ProjectionTestSchema.FieldNamed(owner, "credits");
        var items = ProjectionTestSchema.FieldNamed(owner, "items");

        Assert.Multiple(() =>
        {
            Assert.That(mode.EnumType, Is.EqualTo(typeof(ProjAiMode)), "the compiled field names the enum whose value set the catalog exports");
            Assert.That(mode.EnumType.Name, Is.EqualTo(nameof(ProjAiMode)), "which is also the catalog key");
            Assert.That(level.EnumType, Is.Null, "a plain integer field carries none");
            Assert.That(credits.Saturating, Is.True, "Codec.VarUInt.Saturate() is what let a long be projected at all");
            Assert.That(items.Saturating, Is.False, "an int needs no narrowing, and must not claim one");
            Assert.That(mode.Saturating, Is.False);
        });
    }
}

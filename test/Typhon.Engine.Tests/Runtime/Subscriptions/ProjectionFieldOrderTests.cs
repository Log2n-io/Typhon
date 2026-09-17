using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-02 — canonical wire order: <c>(section, packed first, ordinal name)</c>, the layout key of W11.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this order and not declaration order.</b> Declaration order would make reordering two C# lines a wire break and a catalog-hash break — precisely
/// what canonicalization exists to prevent. Section first is what makes each group's body contiguous, so S2 can concatenate the bodies S1 encoded once
/// instead of re-encoding per session; packed first is what puts a section's whole bit pack at its start (W12), so a pack can never span two groups.
/// </para>
/// <para>
/// <b>The case that proves it is section-then-name, not name-then-section</b>, is <c>hp</c> against <c>mode</c>: <c>"hp"</c> sorts before <c>"mode"</c>
/// ordinally, yet <c>mode</c> encodes first because <c>state</c> is an earlier group than <c>vitals</c>.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ProjectionFieldOrderTests : TestBase<ProjectionFieldOrderTests>
{
    /// <summary>The order W11 dictates for the creature projection, written out rather than derived.</summary>
    private static readonly string[] ExpectedOrder = ["template", "alerted", "mode", "hp", "level"];

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static CompiledProjectionPlan CompileCreature(DatabaseEngine dbe, bool reversed)
    {
        var subs = new SubscriptionsRegistry();
        if (reversed)
        {
            ProjectionTestSchema.DeclareCreatureReversed(subs);
        }
        else
        {
            ProjectionTestSchema.DeclareCreature(subs);
        }

        return ProjectionCompiler.Compile(subs, dbe, ProjectionTestSchema.TickPeriodSeconds, 1)[0];
    }

    /// <summary>
    /// The compiled order is the layout key: enter section, then each group in canonical order, packed fields leading each section, names ordinal within.
    /// </summary>
    [Test]
    public void CompiledOrder_IsSectionThenPackedThenOrdinalName()
    {
        using var dbe = SetupEngine();
        var plan = CompileCreature(dbe, reversed: false);

        Assert.That(ProjectionTestSchema.NamesOf(plan.Fields), Is.EqualTo(ExpectedOrder));

        // Sections: 0 = onEnter, 1 = "state" (bit 0), 2 = "vitals" (bit 1). Groups canonicalize ordinally, so "state" precedes "vitals" however they were
        // declared, and a field's section follows from the group's place in that sort.
        Assert.That(plan.Groups.Length, Is.EqualTo(2));
        Assert.That(plan.Groups[0].Name, Is.EqualTo("state"));
        Assert.That(plan.Groups[1].Name, Is.EqualTo("vitals"));
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "template").Section, Is.EqualTo(0));
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "mode").Section, Is.EqualTo(1));
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "alerted").Section, Is.EqualTo(1));
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "hp").Section, Is.EqualTo(2));
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "level").Section, Is.EqualTo(2));

        // Section dominates the name: "hp" < "mode" ordinally, and "mode" still encodes first.
        Assert.That(ProjectionTestSchema.FieldNamed(plan.Fields, "mode").Ordinal,
            Is.LessThan(ProjectionTestSchema.FieldNamed(plan.Fields, "hp").Ordinal));
    }

    /// <summary>
    /// A reversed declaration compiles to the identical plan — the same order, the same ordinals, the same bit offsets.
    /// </summary>
    [Test]
    public void AReversedDeclaration_CompilesToTheIdenticalOrder()
    {
        using var dbe = SetupEngine();
        var forward = CompileCreature(dbe, reversed: false);
        var reversed = CompileCreature(dbe, reversed: true);

        Assert.That(ProjectionTestSchema.NamesOf(reversed.Fields), Is.EqualTo(ProjectionTestSchema.NamesOf(forward.Fields)));
        for (var i = 0; i < forward.Fields.Length; i++)
        {
            Assert.That(reversed.Fields[i].Ordinal, Is.EqualTo(forward.Fields[i].Ordinal), $"field {i} keeps its ordinal");
            Assert.That(reversed.Fields[i].Section, Is.EqualTo(forward.Fields[i].Section), $"field {i} keeps its section");
            Assert.That(reversed.Fields[i].BitOffset, Is.EqualTo(forward.Fields[i].BitOffset), $"field {i} keeps its pack bit");
            Assert.That(reversed.Fields[i].FieldOffsetInComponent, Is.EqualTo(forward.Fields[i].FieldOffsetInComponent));
        }

        for (var g = 0; g < forward.Groups.Length; g++)
        {
            Assert.That(reversed.Groups[g].Name, Is.EqualTo(forward.Groups[g].Name));
            Assert.That(reversed.Groups[g].Bit, Is.EqualTo(forward.Groups[g].Bit));
            Assert.That(reversed.Groups[g].Section.FirstField, Is.EqualTo(forward.Groups[g].Section.FirstField));
            Assert.That(reversed.Groups[g].Section.PackBytes, Is.EqualTo(forward.Groups[g].Section.PackBytes));
        }
    }

    /// <summary>
    /// Each section holds one implicit pack at its start, and the bits are assigned in field order (W12).
    /// </summary>
    [Test]
    public void EachSection_PacksItsBitFieldsAtItsStart()
    {
        using var dbe = SetupEngine();
        var plan = CompileCreature(dbe, reversed: false);

        var alerted = ProjectionTestSchema.FieldNamed(plan.Fields, "alerted");
        var mode = ProjectionTestSchema.FieldNamed(plan.Fields, "mode");
        Assert.That(alerted.Packed, Is.True);
        Assert.That(mode.Packed, Is.True);
        Assert.That(alerted.BitOffset, Is.EqualTo(0), "the first packed field of the section starts the pack");
        Assert.That(alerted.BitCount, Is.EqualTo(1), "a bool is one bit of its section's pack");
        Assert.That(mode.BitOffset, Is.EqualTo(1), "bits{3} follows the bool");
        Assert.That(mode.BitCount, Is.EqualTo(3));

        var state = plan.Groups[0].Section;
        Assert.That(state.PackedCount, Is.EqualTo(2));
        Assert.That(state.PackBytes, Is.EqualTo(1), "4 bits round up to one byte");
        Assert.That(state.MaxBodyBytes, Is.EqualTo(1), "the state body is its pack and nothing else");

        // 'vitals' has no packed field, so it carries no pack byte at all — just unorm8 + u16.
        var vitals = plan.Groups[1].Section;
        Assert.That(vitals.PackedCount, Is.EqualTo(0));
        Assert.That(vitals.PackBytes, Is.EqualTo(0));
        Assert.That(vitals.MaxBodyBytes, Is.EqualTo(3));

        var onEnter = plan.OnEnter;
        Assert.That(onEnter.FieldCount, Is.EqualTo(1));
        Assert.That(onEnter.PackBytes, Is.EqualTo(0));
        Assert.That(onEnter.MaxBodyBytes, Is.EqualTo(1), "one u8");
    }
}

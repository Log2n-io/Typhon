using System;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>An <see cref="EntityId"/> as its only field — the shape that used to fail with a LINQ error naming nothing.</summary>
[Component("Typhon.Test.Unmappable.OnlyEntityId", 1, StorageMode = StorageMode.SingleVersion)]
struct UmOnlyEntityId
{
    public EntityId Owner;

    public UmOnlyEntityId(EntityId owner) => Owner = owner;
}

/// <summary>A typed link beside a bare id, so both entity shapes are asserted to map in one component.</summary>
[Component("Typhon.Test.Unmappable.BothLinkShapes", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct UmBothLinkShapes
{
    public EntityId Loose;
    public EntityLink<UmTarget> Typed;
    public int Payload;

    public UmBothLinkShapes(EntityId loose, EntityLink<UmTarget> typed, int payload)
    {
        Loose = loose;
        Typed = typed;
        Payload = payload;
    }
}

[Archetype]
class UmTarget : Archetype<UmTarget>
{
    public static readonly Comp<UmBothLinkShapes> Links = Register<UmBothLinkShapes>();
}

/// <summary>A field the schema has no mapping for, and that is not a fixed buffer — the case that must be refused.</summary>
[Component("Typhon.Test.Unmappable.Unknown", 1, StorageMode = StorageMode.SingleVersion)]
struct UmUnknownField
{
    public int Good;
    public UmNotAComponent Bad;

    public UmUnknownField(int good, UmNotAComponent bad)
    {
        Good = good;
        Bad = bad;
    }
}

/// <summary>A plain struct with no <c>[Component]</c>, so it is not a nested component and has no field type.</summary>
struct UmNotAComponent
{
    public int Whatever;

    public UmNotAComponent(int whatever) => Whatever = whatever;
}

/// <summary>A fixed buffer beside a real field — legitimately unmappable, and must still be skipped rather than refused.</summary>
[Component("Typhon.Test.Unmappable.FixedBuffer", 1, StorageMode = StorageMode.SingleVersion)]
unsafe struct UmFixedBuffer
{
    public fixed byte Raw[16];
    public int After;

    public UmFixedBuffer(int after) => After = after;
}

#endregion

/// <summary>
/// A component field whose CLR type has no schema mapping is refused by name, and <see cref="EntityId"/> now has one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect was silence.</b> <c>FieldType.FromType</c> returns <c>None</c> for a type it does not know, and both schema-build paths dropped such a
/// field with a bare <c>continue</c>. The field's bytes stayed in the struct and stayed readable from C# — component values are copied whole — so nothing
/// looked wrong, while the field had no field id and was therefore invisible to the Workbench, un-indexable, un-foreign-keyable and absent from the
/// projection.
/// </para>
/// <para>
/// <b><see cref="EntityId"/> was the one that mattered</b>, because it is the type the public API hands you: <c>Transaction.Spawn</c> and
/// <c>ctx.Commands.Spawn</c> both return one, so storing it in a component is the obvious next step, and it silently did not work.
/// <c>EntityLink&lt;T&gt;</c> mapped and still does — it carries the target archetype, which is what makes it a checkable foreign key — but it cannot
/// express a polymorphic reference, and those are real: the SWG demo's <c>PlayerSession.Target</c> resolves whatever a client named and classifies the
/// archetype afterwards. So both map, and they mean different things.
/// </para>
/// <para>
/// <b>Why the refusal is narrow.</b> A blanket throw was tried first and broke <c>ReflectedOffsetProvenanceTests</c>'s <c>fixed char Buf[8]</c> case,
/// which is correct to skip: a fixed buffer is inline bytes with no schema type of its own. A probe established that the field carries
/// <c>FixedBufferAttribute</c> and its compiler-generated backing type carries <c>UnsafeValueTypeAttribute</c>, so both build paths identify one exactly
/// rather than by heuristic.
/// </para>
/// </remarks>
[TestFixture]
class UnmappableFieldTypeTests
{
    private static DBComponentDefinition Build<T>() where T : unmanaged => new DatabaseDefinitions().CreateFromAccessor<T>();

    /// <summary>
    /// The regression that started this: a component whose only field is an <see cref="EntityId"/> registers, and the field is in the schema.
    /// </summary>
    [Test]
    public void AnEntityIdIsAMappableFieldType()
    {
        var def = Build<UmOnlyEntityId>();

        Assert.Multiple(() =>
        {
            Assert.That(def, Is.Not.Null, "a component whose only field is an EntityId must register");
            Assert.That(def.FieldsByName.ContainsKey("Owner"), Is.True,
                "and the field must be IN the schema — it was previously dropped, leaving the component with none");
            Assert.That(def.FieldsByName["Owner"].Type, Is.EqualTo(FieldType.Long),
                "an EntityId is eight bytes and maps as Long, the same mapping EntityLink<T> already used");
        });
    }

    /// <summary>Both entity shapes map, side by side, so neither change broke the other.</summary>
    [Test]
    public void ABareIdAndATypedLinkBothMapAsLong()
    {
        var def = Build<UmBothLinkShapes>();

        Assert.Multiple(() =>
        {
            Assert.That(def.FieldsByName["Loose"].Type, Is.EqualTo(FieldType.Long));
            Assert.That(def.FieldsByName["Typed"].Type, Is.EqualTo(FieldType.Long));
            Assert.That(def.FieldsByName.ContainsKey("Payload"), Is.True, "and the ordinary field beside them is unaffected");
        });
    }

    /// <summary>
    /// A genuinely unmappable field is refused, and the message names the component, the field and the type — the three things the old failure named none
    /// of. Asserted on the message because an exception whose text does not locate the problem is barely better than the silence it replaced.
    /// </summary>
    [Test]
    public void AnUnmappableFieldIsRefusedWithAMessageThatNamesIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build<UmUnknownField>());

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("UmUnknownField"), "the component");
            Assert.That(ex.Message, Does.Contain("Bad"), "the field");
            Assert.That(ex.Message, Does.Contain("UmNotAComponent"), "and the type");
            Assert.That(ex.Message, Does.Contain("EntityLink"), "plus the type to reach for instead, which is the likely intent");
        });
    }

    /// <summary>
    /// A fixed buffer stays skipped. This is the case that proves the refusal is scoped: it is unmappable BY DESIGN, and the component's later fields are
    /// laid out after its bytes.
    /// </summary>
    [Test]
    public void AFixedBufferIsStillSkippedRatherThanRefused()
    {
        var def = Build<UmFixedBuffer>();

        Assert.Multiple(() =>
        {
            Assert.That(def, Is.Not.Null, "a fixed buffer must not make a component unregisterable");
            Assert.That(def.FieldsByName.ContainsKey("Raw"), Is.False, "it carries no schema type, so it is not a schema field");
            Assert.That(def.FieldsByName.ContainsKey("After"), Is.True, "but the field after it is");
            Assert.That(def.FieldsByName["After"].OffsetInComponentStorage, Is.EqualTo(16),
                "and sits past the buffer's 16 bytes — the arithmetic the skip exists to preserve");
        });
    }
}

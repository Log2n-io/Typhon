using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema — legal shapes only; every ILLEGAL shape is built as synthetic metadata below

/// <summary>Unique <c>[Index]</c>, one declaring archetype per tree. The shape #678's rule blesses.</summary>
[Component("Typhon.Test.Decl.Acct", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DeclAcct
{
    [Index] public int AccountId;
    public int Pad;
}

/// <summary>No unique index — any number of declaring archetypes is legal.</summary>
[Component("Typhon.Test.Decl.Loot", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DeclLoot
{
    [Index(AllowMultiple = true)] public int Rarity;
    public int Pad;
}

[Archetype]
class DeclBaseArch : Archetype<DeclBaseArch>
{
    public static readonly Comp<DeclAcct> Acct = Register<DeclAcct>();
}

[Archetype]
class DeclLeftArch : Archetype<DeclLeftArch, DeclBaseArch>
{
    public static readonly Comp<DeclLoot> Loot = Register<DeclLoot>();
}

[Archetype]
class DeclRightArch : Archetype<DeclRightArch, DeclBaseArch>
{
    public static readonly Comp<DeclLoot> Loot = Register<DeclLoot>();
}

/// <summary>
/// A tree root that does NOT carry the unique-indexed component, so synthetic children of it can each declare it — the sibling shape the per-tree rule
/// rejects. <see cref="DeclBaseArch"/> cannot serve: it declares the component itself, so any child of it inherits rather than declares.
/// </summary>
[Archetype]
class DeclTreeRootArch : Archetype<DeclTreeRootArch>
{
    public static readonly Comp<DeclLoot> Loot = Register<DeclLoot>();
}

#endregion

/// <summary>
/// The runtime backstop for #678 step 1: <see cref="ArchetypeRegistry.ValidateComponentDeclarations"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the illegal shapes are synthetic metadata rather than real archetypes.</b> The generated <c>[ModuleInitializer]</c> registers every
/// <c>[Archetype]</c> in this assembly at load, and a throw inside that barrier fails ALL registrations in the assembly — so a violating archetype declared
/// here would break the entire suite at load, not just this fixture. The build-time twins (TPH1003 / TPH1004, covered in
/// <c>Typhon.Generators.Tests/ComponentDeclarationDiagnosticTests.cs</c>) are where a violating schema can exist as source; this fixture drives the same rules
/// through the validator directly.
/// </para>
/// <para>
/// <b>What the backstop is for.</b> The diagnostics only see one compilation. A schema assembled from several assemblies, or emitted dynamically, reaches
/// <c>Freeze()</c> without ever passing through the generator — which is exactly the open-world case the cascade validation keeps its runtime twin for. It
/// also sees a coarser component identity than the generator does: the durable <c>componentTypeId</c>, so two CLR structs sharing one
/// <c>[Component("name")]</c> are one component here and two to the compile-time check.
/// </para>
/// <para>
/// <b>The scope is per tree, because the index is stored per archetype.</b> Two declarers under one root own two separate B+Trees with nothing spanning them,
/// so enforcing uniqueness between them would mean probing every sibling tree on each insert — rejected. Two declarers in unrelated trees already have their
/// own trees: independent constraints, nothing to probe — legal.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ComponentDeclarationValidationTests
{
    /// <summary>The real, legal catalog must validate — the rule is worthless if it cannot pass the schema the suite actually runs on.</summary>
    [Test]
    public void RealCatalog_IsValid()
    {
        Assert.DoesNotThrow(() => ArchetypeRegistry.ValidateComponentDeclarations(ArchetypeRegistry.GetAllArchetypes()));
    }

    [Test]
    public void OneDeclarerWithDescendants_IsValid()
    {
        var baseMeta = Archetype<DeclBaseArch>.Metadata;
        var left = Archetype<DeclLeftArch>.Metadata;
        var right = Archetype<DeclRightArch>.Metadata;

        // Precondition: the descendants really do INHERIT the unique-indexed component rather than declaring it.
        Assert.That(left.TryGetSlot(TypeIdOf(baseMeta, typeof(DeclAcct)), out _), Is.True, "the child must carry the inherited component");

        Assert.DoesNotThrow(() => ArchetypeRegistry.ValidateComponentDeclarations([baseMeta, left, right]));
    }

    /// <summary>Two siblings declaring the same AllowMultiple component — ordinary composition, and it stays legal.</summary>
    [Test]
    public void SiblingsSharingAnAllowMultipleComponent_IsValid()
    {
        Assert.DoesNotThrow(() => ArchetypeRegistry.ValidateComponentDeclarations(
            [Archetype<DeclBaseArch>.Metadata, Archetype<DeclLeftArch>.Metadata, Archetype<DeclRightArch>.Metadata]));
    }

    /// <summary>Two siblings of one tree each declaring a unique-indexed component — two B+Trees under one root, nothing spanning them.</summary>
    [Test]
    public void TwoDeclarersOfAUniqueIndexedComponent_SameTree_Throws()
    {
        var root = Archetype<DeclTreeRootArch>.Metadata;
        var left = SyntheticChildDeclarer("Typhon.Engine.Tests.SyntheticLeftArch", root, typeof(DeclAcct));
        var right = SyntheticChildDeclarer("Typhon.Engine.Tests.SyntheticRightArch", root, typeof(DeclAcct));

        var ex = Assert.Throws<InvalidOperationException>(() => ArchetypeRegistry.ValidateComponentDeclarations([root, left, right]));

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("DeclAcct"), "the message must name the component");
            Assert.That(ex.Message, Does.Contain("AccountId"), "the message must name the unique field");
            Assert.That(ex.Message, Does.Contain("DeclTreeRootArch"), "the message must name the tree root — that is what makes the two declarers collide");
            Assert.That(ex.Message, Does.Contain("same tree"), "the message must say WHY the two collide");
            Assert.That(ex.Message, Does.Contain("AllowMultiple"), "the message must state the second of the two fixes");
        });
    }

    /// <summary>
    /// <b>The rule's boundary.</b> Two UNRELATED trees may each declare the same unique-indexed component: each already owns its own B+Tree, so the two
    /// constraints are independent and cost nothing to keep — there is no sibling tree to probe. This is also what keeps the V1/V2 schema-evolution fixtures
    /// legal: their two archetypes are unrelated roots declaring one component (same schema name, different CLR structs, therefore one componentTypeId).
    /// </summary>
    [Test]
    public void TwoDeclarersOfAUniqueIndexedComponent_UnrelatedTrees_DoesNotThrow()
    {
        var real = Archetype<DeclBaseArch>.Metadata;
        var stranger = SyntheticDeclarer("Typhon.Engine.Tests.SyntheticStrangerArch", real, typeof(DeclAcct));

        Assert.DoesNotThrow(() => ArchetypeRegistry.ValidateComponentDeclarations([real, stranger]));
    }

    [Test]
    public void TwoDeclarersOfAnAllowMultipleComponent_DoesNotThrow()
    {
        var real = Archetype<DeclLeftArch>.Metadata;
        var twin = SyntheticDeclarer("Typhon.Engine.Tests.SyntheticLootArch", real, typeof(DeclLoot));

        Assert.DoesNotThrow(() => ArchetypeRegistry.ValidateComponentDeclarations([real, twin]));
    }

    /// <summary>The real catalog exercises this: 4 schema-evolution pairs carry a unique index across two unrelated archetypes each.</summary>
    [Test]
    public void EvolutionTwinArchetypes_AreLegal()
    {
        var v1 = Archetype<EvoMxIdxArch>.Metadata;
        var v2 = Archetype<EvoMxIdxV2Arch>.Metadata;

        Assert.That(v1.ParentArchetypeId, Is.EqualTo(ArchetypeMetadata.NoParent), "precondition: the V1 archetype is a root");
        Assert.That(v2.ParentArchetypeId, Is.EqualTo(ArchetypeMetadata.NoParent), "precondition: the V2 archetype is a root of a DIFFERENT tree");

        Assert.DoesNotThrow(() => ArchetypeRegistry.ValidateComponentDeclarations([v1, v2]),
            "a V1/V2 pair declares one componentTypeId from two unrelated roots — legal, and the suite depends on it");
    }

    [Test]
    public void ArchetypeDeclaringOneComponentTwice_Throws()
    {
        var typeId = TypeIdOf(Archetype<DeclBaseArch>.Metadata, typeof(DeclAcct));
        var duplicate = SyntheticDuplicate("Typhon.Engine.Tests.SyntheticDuplicateArch", typeof(DeclAcct), typeId);

        var ex = Assert.Throws<InvalidOperationException>(() => ArchetypeRegistry.ValidateComponentDeclarations([duplicate]));

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("DeclAcct"), "the message must name the duplicated component");
            Assert.That(ex.Message, Does.Contain("once per inheritance chain"), "the message must state the rule");
        });
    }

    /// <summary>The duplicate check runs first: a re-declaration is also a second declarer, and "you declared it twice" is the actionable message.</summary>
    [Test]
    public void DuplicateAndAmbiguity_ReportsTheDuplicateFirst()
    {
        var real = Archetype<DeclBaseArch>.Metadata;
        var typeId = TypeIdOf(real, typeof(DeclAcct));
        var duplicate = SyntheticDuplicate("Typhon.Engine.Tests.SyntheticBothArch", typeof(DeclAcct), typeId);

        var ex = Assert.Throws<InvalidOperationException>(() => ArchetypeRegistry.ValidateComponentDeclarations([real, duplicate]));
        Assert.That(ex.Message, Does.Contain("once per inheritance chain"), "the duplicate must win over the ambiguous-scope message");
    }

    // ── Synthetic metadata ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static int TypeIdOf(ArchetypeMetadata meta, Type componentType)
    {
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (meta._slotToComponentType[slot] == componentType)
            {
                return meta._componentTypeIds[slot];
            }
        }

        Assert.Fail($"{componentType.Name} is not a component of {meta.ArchetypeType?.Name}");
        return -1;
    }

    /// <summary>
    /// A second ROOT archetype declaring <paramref name="componentType"/> — the shape a cross-assembly schema can reach without the generator seeing both
    /// halves, and the shape the per-tree rule leaves legal.
    /// </summary>
    private static ArchetypeMetadata SyntheticDeclarer(string name, ArchetypeMetadata donor, Type componentType)
    {
        var typeId = TypeIdOf(donor, componentType);
        return Build(name, [typeId], [componentType], ArchetypeMetadata.NoParent);
    }

    /// <summary>
    /// A CHILD of <paramref name="parent"/> that declares <paramref name="componentType"/> as its own. Its slot list is the parent's slots followed by the
    /// new one, exactly as <c>FinalizeArchetypeInternal</c> builds it — which is what makes the component count as declared rather than inherited.
    /// </summary>
    private static ArchetypeMetadata SyntheticChildDeclarer(string name, ArchetypeMetadata parent, Type componentType)
    {
        var typeId = TypeIdOf(Archetype<DeclBaseArch>.Metadata, componentType);

        var typeIds = new int[parent.ComponentCount + 1];
        var slotTypes = new Type[parent.ComponentCount + 1];
        for (var slot = 0; slot < parent.ComponentCount; slot++)
        {
            typeIds[slot] = parent._componentTypeIds[slot];
            slotTypes[slot] = parent._slotToComponentType[slot];
        }

        typeIds[parent.ComponentCount] = typeId;
        slotTypes[parent.ComponentCount] = componentType;

        return Build(name, typeIds, slotTypes, parent.ArchetypeId);
    }

    /// <summary>A root archetype whose slot list holds the same component twice — the ghost-slot shape.</summary>
    private static ArchetypeMetadata SyntheticDuplicate(string name, Type componentType, int typeId) =>
        Build(name, [typeId, typeId], [componentType, componentType], ArchetypeMetadata.NoParent);

    private static ArchetypeMetadata Build(string name, int[] typeIds, Type[] slotTypes, ushort parentId)
    {
        var maxTypeId = 0;
        foreach (var id in typeIds)
        {
            maxTypeId = Math.Max(maxTypeId, id);
        }

        var typeIdToSlot = new byte[maxTypeId + 1];
        Array.Fill(typeIdToSlot, (byte)0xFF);
        for (var i = 0; i < typeIds.Length; i++)
        {
            typeIdToSlot[typeIds[i]] = (byte)i;
        }

        return new ArchetypeMetadata
        {
            ArchetypeId = ArchetypeMetadata.NoParent,   // never registered in the catalog; this metadata only ever reaches the validator
            Name = name,
            ComponentCount = (byte)typeIds.Length,
            // A real ParentArchetypeId puts this synthetic archetype in the donor's TREE, which is what the per-tree rule keys on. Left at NoParent it is a
            // root of its own, and declaring the same unique-indexed component is then legal.
            ParentArchetypeId = parentId,
            ArchetypeType = typeof(SyntheticMarker),
            _componentTypeIds = typeIds,
            _typeIdToSlot = typeIdToSlot,
            _slotToComponentType = slotTypes,
        };
    }

    /// <summary>Stand-in CLR type for synthetic metadata — the validator only reads <c>FullName</c>.</summary>
    private sealed class SyntheticMarker;
}

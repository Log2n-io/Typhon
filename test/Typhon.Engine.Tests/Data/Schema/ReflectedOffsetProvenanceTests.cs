using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Components whose managed and marshalled layouts disagree (#819) ───────────────────────────────────────────────────────────────────────────────────────
//
// The schema records a byte offset per field; every accessor reads the component through the MANAGED layout (`*(T*)`, `Span<T>`, `ref T`). The reflection
// schema path derives those offsets from `Marshal.OffsetOf`, which reports the MARSHALLED layout. They agree for ordinary blittable primitives and diverge for
// exactly two types — `bool` (1 byte managed, 4 marshalled) and `char` (2 managed, 1 marshalled under the default CharSet).
//
// TYPHON011 rejects these at compile time, which is the whole point of it; suppressed here because refusing them at REGISTRATION is what this fixture pins.
#pragma warning disable TYPHON011

/// <summary>
/// Two <c>char</c>s then an <c>int</c>. Managed <c>A@0 B@2 C@4</c>, marshalled <c>A@0 B@1 C@4</c> — both 8 bytes total, so no size check can tell them apart.
/// </summary>
[Component("Typhon.Test.Prov.CharPair", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ProvCharPair
{
    public char A;
    public char B;
    public int C;
}

/// <summary>
/// Two <c>bool</c>s then an <c>int</c>. Managed <c>A@0 B@1 C@4</c> (8 bytes), marshalled <c>A@0 B@4 C@8</c> (12) — here the marshalled extent overruns the
/// struct, which is the half the existing size check already catches.
/// </summary>
[Component("Typhon.Test.Prov.BoolPair", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ProvBoolPair
{
    public bool A;
    public bool B;
    public int C;
}

#pragma warning restore TYPHON011

/// <summary>
/// #819: a definition may only carry field offsets the engine can stand behind. The source generator measures them against a stack probe and gets the managed
/// layout; the reflection fallback has only a <see cref="Type"/> and reads <c>Marshal.OffsetOf</c>, which is a different layout whenever a <c>bool</c> or
/// <c>char</c> is involved. Where it cannot verify, it must refuse rather than record offsets that address the wrong bytes.
/// </summary>
[TestFixture]
class ReflectedOffsetProvenanceTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // The reflection path must refuse what it cannot measure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Feeds <c>BuildFromSpec</c> exactly what runtime reflection produces: offsets read with <see cref="Marshal.OffsetOf(Type, string)"/> and no claim of
    /// managed provenance. Driving the real <c>CreateFromAccessor(Type)</c> would not reach this path — since #819 it consults the generated registry first,
    /// and every component in this assembly has a generated spec.
    /// </summary>
    private static ComponentSchemaSpec ReflectedSpec(Type t, params string[] fieldNames)
    {
        var fields = new ComponentFieldSpec[fieldNames.Length];
        for (var i = 0; i < fieldNames.Length; i++)
        {
            var member = t.GetField(fieldNames[i]);
            fields[i] = new ComponentFieldSpec(member.Name, member.FieldType, Marshal.OffsetOf(t, member.Name).ToInt32());
        }

        // managedOffsets defaults to false — the whole point: reflection cannot claim it.
        return new ComponentSchemaSpec(t.Name, 1, fields, StorageMode.SingleVersion);
    }

    [Test]
    [VerifiesRule("SCHEMA-07")]
    public void ReflectedSpec_CharPair_IsRefused()
    {
        // The case no size check can catch: marshalled and managed are both 8 bytes and only B's offset differs (1 vs 2). Accepting this yields a definition
        // whose every field-addressed consumer — index keys, WAL field decode, recovery, the integrity scanner — reads the wrong bytes.
        var spec = ReflectedSpec(typeof(ProvCharPair), "A", "B", "C");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new DatabaseDefinitions().BuildFromSpec(spec, typeof(ProvCharPair), null));

        // Assert on the PATH, not on the words "bool"/"char" — the message boilerplate names both, so containing either proves nothing about which field the
        // detector actually found.
        Assert.That(ex.Message, Does.Contain("ProvCharPair").And.Contain("'A'"));
    }

    [Test]
    [VerifiesRule("SCHEMA-07")]
    public void ReflectedSpec_BoolPair_IsRefused()
    {
        // NOTE: this shape is ALSO caught by SCHEMA-06's extent check (the marshalled extent, 12, overruns the 8-byte struct), so it stays green even with the
        // divergence detector disabled. It pins the outcome, not the mechanism — `ReflectedSpec_CharPair_IsRefused` is the one that exercises this code.
        var spec = ReflectedSpec(typeof(ProvBoolPair), "A", "B", "C");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new DatabaseDefinitions().BuildFromSpec(spec, typeof(ProvBoolPair), null));

        Assert.That(ex.Message, Does.Contain("ProvBoolPair"));
    }

    [Test]
    [VerifiesRule("SCHEMA-07")]
    public void ReflectedSpec_DivergenceInsideADroppedNestedStruct_IsRefused()
    {
        // The subtle one. `ProvNestedChars` is not a field type the schema models, so a reflected spec drops N entirely and the definition's only field is
        // X:Short — nothing there is bool or char. But N still occupies bytes, and a DIFFERENT number of them in each layout (6 managed, 3 marshalled), so X
        // is recorded at 4 while the engine reads it at 6. The extent check cannot see it either: 4 + 2 fits inside the 8-byte struct. Only a walk of the CLR
        // type, rather than of the definition's field list, catches this.
        var spec = ReflectedSpec(typeof(ProvNestedDrop), "X");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new DatabaseDefinitions().BuildFromSpec(spec, typeof(ProvNestedDrop), null));

        Assert.That(ex.Message, Does.Contain("N.A").And.Contain("char"), "the diagnostic should name the path to the offending field");
    }

    [Test]
    [VerifiesRule("SCHEMA-07")]
    [TestCase(typeof(ProvUnicodeChars), "B", 2, TestName = "CharSet.Unicode marshals char as 2 bytes, so the layouts coincide")]
    [TestCase(typeof(ProvMarshalAsBool), "B", 4, TestName = "[MarshalAs] hands the width to the author, who is taken at their word")]
    [TestCase(typeof(ProvFixedCharBuffer), "After", 16, TestName = "a fixed buffer is element-count times element-width in both layouts")]
    public void ReflectedSpec_DeclarationsThatReconcileTheLayouts_AreAccepted(Type componentType, string fieldName, int expectedOffset)
    {
        // The detector keys on the PRESENCE of bool/char as a proxy for divergence, so it must exclude the declarations that make the two layouts agree —
        // otherwise it rejects components that were being built correctly before. Each offset below was measured against the runtime.
        var names = new List<string>();
        foreach (var f in componentType.GetFields())
        {
            names.Add(f.Name);
        }

        var spec = ReflectedSpec(componentType, names.ToArray());
        var def = new DatabaseDefinitions().BuildFromSpec(spec, componentType, null);

        Assert.That(def, Is.Not.Null, $"{componentType.Name} must not be refused — its two layouts agree");
        Assert.That(def.FieldsByName[fieldName].OffsetInComponentStorage, Is.EqualTo(expectedOffset));
    }

    [Test]
    public void ReflectedSpec_ComponentWithoutBoolOrChar_IsStillAccepted()
    {
        // The refusal must be scoped to the types that can diverge. Every other blittable primitive has one layout, and Marshal.OffsetOf reports it correctly
        // — refusing those would make the reflection fallback useless.
        var spec = ReflectedSpec(typeof(ProvPlain), "A", "B", "D");

        var def = new DatabaseDefinitions().BuildFromSpec(spec, typeof(ProvPlain), null);

        Assert.That(def, Is.Not.Null);
        Assert.That(def.FieldsByName["B"].OffsetInComponentStorage, Is.EqualTo(8));
    }

    [Test]
    public void TypeOverload_PrefersTheGeneratedSpec_SoAGeneratedComponentIsNeverRefused()
    {
        // Regression guard for the half of #819 that was a self-inflicted wound: the non-generic overload used to reflect unconditionally, so adding the
        // refusal above broke `typhon schema` and schema-evolution dry-run for any generator-built component carrying a bool — components that register
        // perfectly well through the generic overload. Both overloads must consult the registry.
        var def = new DatabaseDefinitions().CreateFromAccessor(typeof(ProvCharPair), null);

        Assert.That(def, Is.Not.Null, "a generator-built component must not be refused by the Type overload");
        Assert.That(def.FieldsByName["B"].OffsetInComponentStorage, Is.EqualTo(2), "and it must carry the MANAGED offset");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The generated path measures the managed layout and keeps working
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("SCHEMA-07")]
    public void GeneratedPath_CharPair_RecordsManagedOffsets()
    {
        // The generator measures each field against a stack probe, so it reports the layout the accessors read. A component the reflection path cannot verify
        // is therefore still perfectly usable when it was compiled with the generator — the refusal above is about the measurement, not about the type.
        var def = new DatabaseDefinitions().CreateFromAccessor<ProvCharPair>(null);

        Assert.Multiple(() =>
        {
            Assert.That(def.FieldsByName["A"].OffsetInComponentStorage, Is.EqualTo(0), "A");
            Assert.That(def.FieldsByName["B"].OffsetInComponentStorage, Is.EqualTo(2), "B — 2 managed, 1 marshalled");
            Assert.That(def.FieldsByName["C"].OffsetInComponentStorage, Is.EqualTo(4), "C");
            Assert.That(def.ComponentStorageSize, Is.EqualTo(Unsafe.SizeOf<ProvCharPair>()).And.EqualTo(8));
        });
    }

    [Test]
    [VerifiesRule("SCHEMA-07")]
    public void GeneratedPath_BoolPair_RecordsManagedOffsets()
    {
        var def = new DatabaseDefinitions().CreateFromAccessor<ProvBoolPair>(null);

        Assert.Multiple(() =>
        {
            Assert.That(def.FieldsByName["A"].OffsetInComponentStorage, Is.EqualTo(0), "A");
            Assert.That(def.FieldsByName["B"].OffsetInComponentStorage, Is.EqualTo(1), "B — 1 managed, 4 marshalled");
            Assert.That(def.FieldsByName["C"].OffsetInComponentStorage, Is.EqualTo(4), "C");
            Assert.That(def.ComponentStorageSize, Is.EqualTo(Unsafe.SizeOf<ProvBoolPair>()).And.EqualTo(8));
        });
    }
}

/// <summary>All-wide fields, so the managed and marshalled layouts coincide and the reflection path can measure it.</summary>
[Component("Typhon.Test.Prov.Plain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ProvPlain
{
    public long A;
    public int B;
    public int D;
}

/// <summary>
/// Three <c>char</c>s — 6 bytes managed, 3 marshalled. Not a field type the schema models, so <c>FromType</c> returns <c>None</c> and the field embedding it
/// is dropped from the definition entirely.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct ProvNestedChars
{
    public char A;
    public char B;
    public char C;
}

// ── Declarations that RECONCILE the two layouts, and must therefore keep working ─────────────────────────────────────────────────────────────────────────

/// <summary>Under <c>CharSet.Unicode</c> a char marshals as 2 bytes, the managed width, so both layouts put <c>B</c> at 2.</summary>
[Component("Typhon.Test.Prov.UnicodeChars", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct ProvUnicodeChars
{
    public char A;
    public char B;
    public int C;
}

/// <summary>An explicit <c>[MarshalAs]</c> defines the marshalled width; both layouts put <c>B</c> at 4.</summary>
[Component("Typhon.Test.Prov.MarshalAsBool", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ProvMarshalAsBool
{
    [MarshalAs(UnmanagedType.U1)] public bool A;
    public int B;
}

/// <summary>A <c>fixed</c> buffer occupies element-count times element-width in both layouts, so <c>After</c> lands at 16 either way.</summary>
[Component("Typhon.Test.Prov.FixedCharBuffer", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
unsafe struct ProvFixedCharBuffer
{
    public fixed char Buf[8];
    public int After;
}

// TYPHON011 catches this shape at compile time — it models nested structs and reports X at 6 managed / 4 marshalled. Suppressed because the point of the
// fixture is the RUNTIME half, for an assembly that was never compiled against the analyzer.
#pragma warning disable TYPHON011

/// <summary>
/// The dropped-field hole: <c>N</c> is invisible to the schema, but it still occupies bytes and still displaces <c>X</c> — which lands at 6 managed and 4
/// marshalled. Scanning the definition's own fields would see only <c>X:Short</c> and find nothing to object to.
/// </summary>
[Component("Typhon.Test.Prov.NestedDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ProvNestedDrop
{
    public ProvNestedChars N;
    public short X;
}

#pragma warning restore TYPHON011

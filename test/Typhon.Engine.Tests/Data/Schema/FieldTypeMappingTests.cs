using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// The CLR-type → FieldType table (FromType) and the FieldType → width table (FieldSizeInComp) are maintained by hand, side by side, and nothing ties them
// together. #1449 was both halves missing at once: FieldType.Variant existed, FromType never produced it and FieldSizeInComp sized it 0. These tests close
// that class over every type the schema ships rather than one type at a time.
[TestFixture]
class FieldTypeMappingTests
{
    private const string MissizedMarker = "mapped at a width that is not its CLR size";
    private const string OrphanMarker = "no CLR type maps to";

    // Handles, not inline values: a collection is its 4-byte buffer id, a nested component its 8-byte reference. Neither is the CLR size of its type.
    private static readonly HashSet<FieldType> Handles = [FieldType.Collection, FieldType.Component];

    // The one known violation, outside #1449: VarString is an empty marker struct (1 byte) that FieldSizeInComp sizes as its 32-byte inline handle. Nothing in
    // the engine stores one yet. Remove it from here when VarString gets its real layout or loses its mapping.
    private static readonly HashSet<Type> KnownMissized = [typeof(VarString)];

    // Every CLR type a field can be declared as: the primitives, each public value type the schema assembly exports, and the engine's entity references.
    private static IEnumerable<Type> FieldCandidates()
    {
        Type[] primitives =
        [
            typeof(bool), typeof(sbyte), typeof(byte), typeof(char), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double)
        ];
        foreach (var t in primitives)
        {
            yield return t;
        }

        foreach (var t in typeof(FieldType).Assembly.GetExportedTypes())
        {
            if (t.IsValueType && !t.IsEnum && !t.IsGenericTypeDefinition)
            {
                yield return t;
            }
        }

        yield return typeof(EntityId);
        yield return typeof(EntityLink<object>);
    }

    private static FieldType Map(Type t) => DatabaseSchemaExtensions.FromType(t).field;

    private static void AssertEveryMappedTypeIsSizedAtItsClrWidth(Func<Type, FieldType> map, Func<FieldType, int> sizeOf)
    {
        var wrong = new List<string>();
        foreach (var t in FieldCandidates())
        {
            var field = map(t);

            // A None-mapped type has no width to check, so this verifier cannot see an unmapped type: the real pre-#1449 tables (Variant unmapped AND sized 0)
            // pass it. The orphan check below is the one that catches #1449; this one catches a type that maps but is sized wrong.
            if (field == FieldType.None || Handles.Contains(field) || KnownMissized.Contains(t))
            {
                continue;
            }

            var clrSize = RuntimeHelpers.SizeOf(t.TypeHandle);
            if (sizeOf(field) != clrSize)
            {
                wrong.Add($"{t.Name} → {field}: sized {sizeOf(field)}, CLR size {clrSize}");
            }
        }

        Assert.That(wrong, Is.Empty, MissizedMarker);
    }

    private static void AssertEveryStorageFieldTypeIsProduced(Func<Type, FieldType> map)
    {
        var produced = new HashSet<FieldType>();
        foreach (var t in FieldCandidates())
        {
            produced.Add(map(t));
        }

        var orphans = new List<FieldType>();
        foreach (var value in Enum.GetValues<FieldType>())
        {
            // None is the sentinel, Unsigned / DoubleFloat are flag bits OR-ed onto a base type, and the handle kinds map from generic or attributed types.
            if (value is FieldType.None or FieldType.Unsigned or FieldType.DoubleFloat || Handles.Contains(value))
            {
                continue;
            }

            if (!produced.Contains(value))
            {
                orphans.Add(value);
            }
        }

        Assert.That(orphans, Is.Empty, $"{OrphanMarker} these FieldTypes, so no schema can hold a field of them");
    }

    [Test]
    [VerifiesRule("SCHEMA-08")]
    public void EveryMappedTypeIsSizedAtItsClrWidth() => AssertEveryMappedTypeIsSizedAtItsClrWidth(Map, DatabaseSchemaExtensions.FieldSizeInComp);

    [Test]
    [VerifiesRule("SCHEMA-08")]
    public void EveryStorageFieldTypeIsProducedBySomeClrType() => AssertEveryStorageFieldTypeIsProduced(Map);

    // The genuineness proof, one mutated table per verifier: the width table with Variant sized 0 but still mapped (the size check skips unmapped types, so
    // the mapping has to stay intact for it to look), and the mapping table with Variant unmapped. Each verifier must reject its mutant.
    [Test]
    [RuleMutant("SCHEMA-08")]
    public void Mutant_AMissizedAndAnUnproducedFieldType_AreRejected()
    {
        RuleMutants.AssertDetects("SCHEMA-08", MissizedMarker,
            () => AssertEveryMappedTypeIsSizedAtItsClrWidth(Map, k => k == FieldType.Variant ? 0 : k.FieldSizeInComp()));
        RuleMutants.AssertDetects("SCHEMA-08", OrphanMarker,
            () => AssertEveryStorageFieldTypeIsProduced(t => t == typeof(Variant) ? FieldType.None : Map(t)));
    }
}

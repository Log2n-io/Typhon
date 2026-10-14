using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

internal static class QueryResolverHelper
{
    public static FieldEvaluator[] ResolveEvaluators(FieldPredicate[] predicates, ComponentTable ct, byte componentTag, byte branchIndex = 0)
    {
        var definition = ct.Definition;
        var evaluators = new FieldEvaluator[predicates.Length];

        for (var i = 0; i < predicates.Length; i++)
        {
            var pred = predicates[i];

            if (!definition.FieldsByName.TryGetValue(pred.FieldName, out var field))
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' not found on component '{definition.Name}'.");
            }

            if (!field.HasIndex)
            {
                throw new InvalidOperationException($"Field '{pred.FieldName}' is not indexed. View predicates require indexed fields.");
            }

            var fieldIndex = FindFieldIndex(definition, field);
            var keyType = MapFieldTypeToKeyType(field.Type);
            var threshold = EncodeThreshold(pred.Value, keyType);

            if (fieldIndex >= 64)
            {
                throw new InvalidOperationException(
                    $"Field '{pred.FieldName}' has index {fieldIndex} which exceeds the maximum of 63. Ring buffer delta encoding uses 6 bits for field index.");
            }

            evaluators[i] = new FieldEvaluator
            {
                FieldIndex = (byte)fieldIndex,
                FieldOffset = (ushort)field.OffsetInComponentStorage,
                FieldSize = (byte)field.FieldSize,
                KeyType = keyType,
                CompareOp = pred.Operator,
                Threshold = threshold,
                ComponentTag = componentTag,
                BranchIndex = branchIndex
            };
        }

        return evaluators;
    }

    /// <summary>
    /// Finds the index into IndexedFieldInfos[] by replicating the iteration order from BuildIndexedFieldInfo.
    /// </summary>
    public static int FindFieldIndex(DBComponentDefinition definition, DBComponentDefinition.Field targetField)
    {
        var index = 0;
        for (var i = 0; i < definition.MaxFieldId; i++)
        {
            var f = definition[i];
            if (f == null || !f.HasIndex)
            {
                continue;
            }

            if (f == targetField)
            {
                return index;
            }

            index++;
        }

        throw new InvalidOperationException($"Field '{targetField.Name}' not found in indexed fields.");
    }

    /// <summary>The tree key type of <paramref name="fieldType"/>; false for a type no key encodes (a string).</summary>
    internal static bool TryMapFieldTypeToKeyType(FieldType fieldType, out KeyType keyType)
    {
        (keyType, var known) = fieldType switch
        {
            FieldType.Boolean => (KeyType.Bool, true),
            FieldType.Byte => (KeyType.SByte, true),
            FieldType.UByte => (KeyType.Byte, true),
            FieldType.Short => (KeyType.Short, true),
            FieldType.UShort => (KeyType.UShort, true),
            FieldType.Int => (KeyType.Int, true),
            FieldType.UInt => (KeyType.UInt, true),
            FieldType.Long => (KeyType.Long, true),
            FieldType.ULong => (KeyType.ULong, true),
            FieldType.Float => (KeyType.Float, true),
            FieldType.Double => (KeyType.Double, true),
            _ => (default(KeyType), false)
        };
        return known;
    }

    private static KeyType MapFieldTypeToKeyType(FieldType fieldType) =>
        TryMapFieldTypeToKeyType(fieldType, out var keyType)
            ? keyType
            : throw new NotSupportedException($"Field type {fieldType} is not supported for view predicates.");

    public static long EncodeThreshold(object value, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Bool:
                return Convert.ToBoolean(value) ? 1L : 0L;
            case KeyType.SByte:
                return Convert.ToSByte(value);
            case KeyType.Byte:
                return Convert.ToByte(value);
            case KeyType.Short:
                return Convert.ToInt16(value);
            case KeyType.UShort:
                return Convert.ToUInt16(value);
            case KeyType.Int:
                return Convert.ToInt32(value);
            case KeyType.UInt:
                return Convert.ToUInt32(value);
            case KeyType.Long:
                return Convert.ToInt64(value);
            case KeyType.ULong:
                return (long)Convert.ToUInt64(value);
            case KeyType.Float:
            {
                var f = Convert.ToSingle(value);
                return Unsafe.As<float, int>(ref f);
            }
            case KeyType.Double:
            {
                var d = Convert.ToDouble(value);
                return Unsafe.As<double, long>(ref d);
            }
            default:
                throw new NotSupportedException($"Key type {keyType} is not supported.");
        }
    }
}

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

    private static KeyType MapFieldTypeToKeyType(FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Boolean => KeyType.Bool,
            FieldType.Byte => KeyType.SByte,
            FieldType.UByte => KeyType.Byte,
            FieldType.Short => KeyType.Short,
            FieldType.UShort => KeyType.UShort,
            FieldType.Int => KeyType.Int,
            FieldType.UInt => KeyType.UInt,
            FieldType.Long => KeyType.Long,
            FieldType.ULong => KeyType.ULong,
            FieldType.Float => KeyType.Float,
            FieldType.Double => KeyType.Double,
            _ => throw new NotSupportedException($"Field type {fieldType} is not supported for view predicates.")
        };

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

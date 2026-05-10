// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
public class DBComponentDefinition
{
    public string Name { get; private set; }
    public int Revision { get; private set; }
    public bool AllowMultiple { get; private set; }
    public Type POCOType { get; internal set; }
    public string FullName => FormatFullName(Name, Revision);
    
    internal static string FormatFullName(string componentName, int revision) => $"{componentName}:R{revision}";

    private readonly Dictionary<string, Field> _fieldsByName;
    private Field[] _fieldsById;

    public IReadOnlyDictionary<string, Field> FieldsByName => _fieldsByName;

    public int MaxFieldId { get; private set; }
    public Field this[int index] => _fieldsById[index];

    public int GetFieldId(string fieldName)
    {
        if (!_fieldsByName.TryGetValue(fieldName, out var field))
        {
            return -1;
        }

        return field.FieldId;
    }

    public StorageMode StorageMode { get; internal set; }

    public int ComponentStorageSize { get; private set; }

    /// <summary>
    /// Size of the inline entityPK in the chunk overhead (8 bytes for all SV/Transient components, 0 for Versioned).
    /// Non-versioned components store the entityPK at chunk offset 0 to enable chunkId → entityPK resolution.
    /// </summary>
    public int EntityPKOverheadSize => StorageMode != StorageMode.Versioned ? sizeof(long) : 0;

    public int ComponentStorageOverhead => EntityPKOverheadSize + MultipleIndicesCount * sizeof(int);
    public int ComponentStorageTotalSize => ComponentStorageSize + ComponentStorageOverhead;

    public int IndicesCount { get; private set; }
    public int MultipleIndicesCount { get; private set; }

    /// <summary>Reference to the field with <c>[SpatialIndex]</c>, or null if none.</summary>
    public Field SpatialField { get; private set; }

    [DebuggerDisplay("Id: {FieldId}, Name: {Name}, Type: {Type}, OffsetInComponentStorage: {OffsetInComponentStorage}")]
    [PublicAPI]
    public class Field
    {
        public Field(int fieldId, string name, FieldType type, FieldType underlyingType, int offsetInComponentStorage, Type dotNetType)
        {
            FieldId = fieldId;
            Name = name;
            Type = type;
            DotNetType = dotNetType;
            if (Type == FieldType.Collection)
            {
                DotNetUnderlyingType = dotNetType.GenericTypeArguments[0];
            }
            FieldSize = Type.FieldSizeInComp();
            UnderlyingType = underlyingType;
            OffsetInComponentStorage = offsetInComponentStorage;
        }
        
        public int FieldId { get; }

        public string Name { get; }

        public FieldType Type { get; }
        public FieldType UnderlyingType { get; }

        public int OffsetInComponentStorage { get; }
        public Type DotNetType { get; }
        public Type DotNetUnderlyingType { get; }
        public int FieldSize { get; }

        public bool IsStatic { get; set; }

        public bool HasIndex { get; set; }
            
        public bool IndexAllowMultiple { get; set; }

        public bool IsIndexAuto { get; set; }
            
        public bool IsArray => ArrayLength > 0;
            
        public int ArrayLength { get; set; }

        public bool HasSpatialIndex { get; set; }
        public SpatialFieldType SpatialFieldType { get; set; }
        public float SpatialMargin { get; set; }
        public float SpatialCellSize { get; set; }
        public SpatialMode SpatialMode { get; set; }
        public uint SpatialCategory { get; set; } = uint.MaxValue;

        public bool IsForeignKey { get; set; }
        public Type ForeignKeyTargetType { get; set; }

        public static void CheckName(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || new Regex("^[A-Za-z]+$").IsMatch(fieldName) == false)
            {
                throw new ArgumentException("Field name must be a single word of UTF8 size not exceeding 64 bytes", nameof(fieldName));
            }
            if (Encoding.UTF8.GetByteCount(fieldName) > 63)
            {
                throw new ArgumentException($"The given field name '{fieldName}' is exceeding the size limit of 64 bytes", nameof(fieldName));
            }
        }

        public static void CheckType(FieldType fieldType)
        {
            if (Enum.IsDefined(fieldType) == false || fieldType==FieldType.None)
            {
                throw new ArgumentException($"The given field type is not valid");
            }
        }

        public int SizeInComponentStorage => Type.FieldSizeInComp() * (IsArray ? ArrayLength : 1);
        public bool DoesFieldTypeSupportIndex() => (Type >= FieldType.Byte) && ((FieldType)((int)Type&0xFF) <= FieldType.String64);
    }

    internal DBComponentDefinition(string name, int revision, bool allowMultiple, StorageMode storageMode = StorageMode.Versioned)
    {
        Name = name;
        Revision = revision;
        AllowMultiple = allowMultiple;
        StorageMode = storageMode;
        _fieldsByName = new Dictionary<string, Field>();
    }

    public Field CreateField(int fieldId, string name, FieldType type, FieldType underlyingType, int offset, Type dotNetType)
    {
        if (_fieldsByName.ContainsKey(name))
        {
            throw new ArgumentException($"The field name '{name}' is already taken", nameof(name));
        }
        var field = new Field(fieldId, name, type, underlyingType, offset, dotNetType);
        _fieldsByName.Add(name, field);
        return field;
    }

    internal void Build()
    {
        var fields = _fieldsByName.Values;
        var max = fields.Where(f => f.IsStatic == false).Max(v => v.FieldId);

        MaxFieldId = max + 1;
        _fieldsById = new Field[MaxFieldId];

        var ids = new HashSet<int>();
        var names = new HashSet<string>();
        var offsets = new Dictionary<int, Field>();

        Field lastField = null;
        IndicesCount = 0;

        foreach (var field in fields.Where(f => f.IsStatic == false))
        {
            if (ids.Add(field.FieldId) == false)
            {
                throw new Exception($"Duplicate FieldId {field.FieldId}, defined on both {field.Name} and {_fieldsById[field.FieldId].Name}. Each field must have a unique FieldId.");
            }

            if (names.Add(field.Name) == false)
            {
                throw new Exception($"Duplicate Field's name {field.Name}. Each field must have a unique name.");
            }

            if (offsets.TryGetValue(field.OffsetInComponentStorage, out Field offset))
            {
                throw new Exception($"Duplicate Field's offset {field.OffsetInComponentStorage}, declare in both {field.Name} and {offset.Name}. Each field must have a different.");
            }
            offsets.Add(field.OffsetInComponentStorage, field);

            _fieldsById[field.FieldId] = field;

            if (field.HasIndex)
            {
                if (!field.DoesFieldTypeSupportIndex())
                {
                    throw new Exception($"The field type {field.Type} does not support index for field {field.Name}.");
                }
                ++IndicesCount;
                if (field.IndexAllowMultiple)
                {
                    ++MultipleIndicesCount;
                }
            }

            if (field.HasSpatialIndex)
            {
                SpatialField = field;
            }

            if (lastField == null || lastField.OffsetInComponentStorage < field.OffsetInComponentStorage)
            {
                lastField = field;
            }
        }

        if (lastField == null)
        {
            throw new Exception("We didn't detect at least one field... Fields must be public field (not property), not static and of a compatible data type.");
        }

        ComponentStorageSize = lastField.OffsetInComponentStorage + lastField.SizeInComponentStorage;
    }
}


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

/// <summary>
/// Compiled description of a single component revision: its name, schema revision, storage layout, fields, and index metadata.
/// Built once from a <c>[Component]</c>-annotated struct (via <see cref="DatabaseDefinitions.CreateFromAccessor{T}()"/>) and thereafter immutable.
/// </summary>
[PublicAPI]
public class DBComponentDefinition
{
    /// <summary>Component name — from <see cref="ComponentAttribute.Name"/>, or the struct type name when unset.</summary>
    public string Name { get; private set; }

    /// <summary>Schema revision of this definition, from <see cref="ComponentAttribute.Revision"/>. Distinct revisions of the same name coexist.</summary>
    public int Revision { get; private set; }

    /// <summary>The unmanaged struct type this definition was built from, or the registered POCO type. May be null until set by the builder.</summary>
    public Type POCOType { get; internal set; }

    /// <summary>Unique key combining <see cref="Name"/> and <see cref="Revision"/>, formatted as <c>{Name}:R{Revision}</c>.</summary>
    public string FullName => FormatFullName(Name, Revision);

    internal static string FormatFullName(string componentName, int revision) => $"{componentName}:R{revision}";

    private readonly Dictionary<string, Field> _fieldsByName;
    private Field[] _fieldsById;

    /// <summary>All fields of this component keyed by field name, including static (non-stored) fields.</summary>
    public IReadOnlyDictionary<string, Field> FieldsByName => _fieldsByName;

    /// <summary>One past the highest non-static <see cref="Field.FieldId"/>; equals the length of the FieldId-indexed array backing <see cref="this[int]"/>.</summary>
    public int MaxFieldId { get; private set; }

    /// <summary>The field with the given <see cref="Field.FieldId"/>. Valid indices are <c>0</c> to <see cref="MaxFieldId"/> minus one.</summary>
    public Field this[int index] => _fieldsById[index];

    /// <summary>Returns the <see cref="Field.FieldId"/> of the field with the given name, or <c>-1</c> if no such field exists.</summary>
    public int GetFieldId(string fieldName)
    {
        if (!_fieldsByName.TryGetValue(fieldName, out var field))
        {
            return -1;
        }

        return field.FieldId;
    }

    /// <summary>How this component's data is stored, persisted, and recovered. See <see cref="Typhon.Schema.Definition.StorageMode"/>.</summary>
    public StorageMode StorageMode { get; internal set; }

    /// <summary>
    /// Default durability discipline (from <c>[Component(DefaultDiscipline=…)]</c>). Only meaningful for
    /// <see cref="StorageMode.SingleVersion"/>; <see cref="DurabilityDiscipline.TickFence"/> otherwise.
    /// </summary>
    public DurabilityDiscipline DefaultDiscipline { get; internal set; }

    /// <summary>Byte size of the component's field data (the largest field offset plus its size), excluding per-chunk overhead.</summary>
    public int ComponentStorageSize { get; private set; }

    /// <summary>
    /// Size of the inline entityPK in the chunk overhead (8 bytes for all SV/Transient components, 0 for Versioned).
    /// Non-versioned components store the entityPK at chunk offset 0 to enable chunkId → entityPK resolution.
    /// </summary>
    public int EntityPKOverheadSize => StorageMode != StorageMode.Versioned ? sizeof(long) : 0;

    /// <summary>
    /// Bytes of per-chunk overhead stored alongside the field data: the inline entityPK (<see cref="EntityPKOverheadSize"/>) plus one back-reference
    /// slot per multi-valued index (<see cref="MultipleIndicesCount"/> times <c>sizeof(int)</c>).
    /// </summary>
    public int ComponentStorageOverhead => EntityPKOverheadSize + MultipleIndicesCount * sizeof(int);

    /// <summary>Total per-chunk stride: <see cref="ComponentStorageSize"/> plus <see cref="ComponentStorageOverhead"/>.</summary>
    public int ComponentStorageTotalSize => ComponentStorageSize + ComponentStorageOverhead;

    /// <summary>Number of indexed fields on this component (fields carrying <c>[Index]</c>).</summary>
    public int IndicesCount { get; private set; }

    /// <summary>Number of indexed fields whose index allows multiple entities per key (non-unique); each reserves a back-reference slot in the overhead.</summary>
    public int MultipleIndicesCount { get; private set; }

    /// <summary>Reference to the field with <c>[SpatialIndex]</c>, or null if none.</summary>
    public Field SpatialField { get; private set; }

    /// <summary>
    /// A single field of a component: its identity, type, byte offset within the component storage, and any index / spatial / foreign-key metadata.
    /// </summary>
    [DebuggerDisplay("Id: {FieldId}, Name: {Name}, Type: {Type}, OffsetInComponentStorage: {OffsetInComponentStorage}")]
    [PublicAPI]
    public class Field
    {
        /// <summary>Creates a field descriptor. Index, spatial, and foreign-key metadata are set separately after construction.</summary>
        /// <param name="fieldId">Unique field identifier within the component.</param>
        /// <param name="name">Field name (a single word, at most 63 UTF-8 bytes).</param>
        /// <param name="type">Field type as stored on the component.</param>
        /// <param name="underlyingType">Element type when <paramref name="type"/> is a collection; otherwise <see cref="FieldType.None"/>.</param>
        /// <param name="offsetInComponentStorage">Byte offset of this field within the component's storage.</param>
        /// <param name="dotNetType">The backing CLR type of the field.</param>
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
        
        /// <summary>Unique field identifier within the component. Stable across schema revisions so persisted data can be re-mapped.</summary>
        public int FieldId { get; }

        /// <summary>Field name.</summary>
        public string Name { get; }

        /// <summary>The field's type as stored on the component.</summary>
        public FieldType Type { get; }

        /// <summary>Element type when <see cref="Type"/> is a collection; otherwise <see cref="FieldType.None"/>.</summary>
        public FieldType UnderlyingType { get; }

        /// <summary>Byte offset of this field within the component's storage.</summary>
        public int OffsetInComponentStorage { get; }

        /// <summary>The backing CLR type of the field.</summary>
        public Type DotNetType { get; }

        /// <summary>The CLR element type when the field is a collection; otherwise null.</summary>
        public Type DotNetUnderlyingType { get; }

        /// <summary>Byte width of one element of this field (from <see cref="Type"/>); see <see cref="SizeInComponentStorage"/> for the total including arrays.</summary>
        public int FieldSize { get; }

        /// <summary>True for a static field — carried on the definition but excluded from per-entity component storage and the FieldId layout.</summary>
        public bool IsStatic { get; set; }

        /// <summary>True when the field carries a scalar index (<c>[Index]</c>).</summary>
        public bool HasIndex { get; set; }

        /// <summary>True when the index permits multiple entities per key (non-unique). Only meaningful when <see cref="HasIndex"/> is <c>true</c>.</summary>
        public bool IndexAllowMultiple { get; set; }

        /// <summary>True when the index was created implicitly by the engine rather than declared with an explicit <c>[Index]</c> attribute.</summary>
        public bool IsIndexAuto { get; set; }

        /// <summary>True when this field is a fixed-length array, i.e. <see cref="ArrayLength"/> is greater than <c>0</c>.</summary>
        public bool IsArray => ArrayLength > 0;

        /// <summary>Fixed element count when the field is an array; <c>0</c> for a scalar field.</summary>
        public int ArrayLength { get; set; }

        /// <summary>True when the field carries a spatial index (<c>[SpatialIndex]</c>). At most one spatial field is allowed per component.</summary>
        public bool HasSpatialIndex { get; set; }

        /// <summary>Kind of spatial bounds indexed (AABB / bounding sphere, 2D/3D, single/double precision). Meaningful when <see cref="HasSpatialIndex"/>.</summary>
        public SpatialFieldType SpatialFieldType { get; set; }

        /// <summary>Fat-AABB margin used for movement hysteresis in the dynamic R-Tree, from <see cref="SpatialIndexAttribute.Margin"/>.</summary>
        public float SpatialMargin { get; set; }

        /// <summary>Broadphase cell size for the spatial index, from <see cref="SpatialIndexAttribute.CellSize"/>. <c>0</c> selects the engine default.</summary>
        public float SpatialCellSize { get; set; }

        /// <summary>Whether the spatial index is static or dynamic, from <see cref="SpatialIndexAttribute.Mode"/>.</summary>
        public SpatialMode SpatialMode { get; set; }

        /// <summary>Archetype-level category bitmask for spatial broadphase filtering, from <see cref="SpatialIndexAttribute.Category"/>. Defaults to <see cref="uint.MaxValue"/>.</summary>
        public uint SpatialCategory { get; set; } = uint.MaxValue;

        /// <summary>True when the field is a foreign key (<c>[ForeignKey]</c>) referencing another component's entities.</summary>
        public bool IsForeignKey { get; set; }

        /// <summary>The target component type the foreign key points at, from <see cref="ForeignKeyAttribute.TargetComponentType"/>. Null when <see cref="IsForeignKey"/> is <c>false</c>.</summary>
        public Type ForeignKeyTargetType { get; set; }

        /// <summary>Validates a field name: it must be a non-empty run of ASCII letters at most 63 UTF-8 bytes long.</summary>
        /// <param name="fieldName">The candidate field name.</param>
        /// <exception cref="ArgumentException">The name is empty, contains non-letter characters, or exceeds the byte-length limit.</exception>
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

        /// <summary>Validates that a field type is a defined value other than <see cref="FieldType.None"/>.</summary>
        /// <param name="fieldType">The candidate field type.</param>
        /// <exception cref="ArgumentException">The type is undefined or <see cref="FieldType.None"/>.</exception>
        public static void CheckType(FieldType fieldType)
        {
            if (Enum.IsDefined(fieldType) == false || fieldType==FieldType.None)
            {
                throw new ArgumentException($"The given field type is not valid");
            }
        }

        /// <summary>Total bytes this field occupies in component storage: one element's width times <see cref="ArrayLength"/> for arrays, otherwise one element.</summary>
        public int SizeInComponentStorage => Type.FieldSizeInComp() * (IsArray ? ArrayLength : 1);

        /// <summary>True when <see cref="Type"/> can back an index — the scalar and string types in the indexable range (byte through <see cref="FieldType.String64"/>).</summary>
        public bool DoesFieldTypeSupportIndex() => (Type >= FieldType.Byte) && ((FieldType)((int)Type&0xFF) <= FieldType.String64);
    }

    internal DBComponentDefinition(string name, int revision, StorageMode storageMode = StorageMode.Versioned,
        DurabilityDiscipline defaultDiscipline = DurabilityDiscipline.TickFence)
    {
        Name = name;
        Revision = revision;
        StorageMode = storageMode;
        DefaultDiscipline = defaultDiscipline;
        _fieldsByName = new Dictionary<string, Field>();
    }

    /// <summary>Creates a field, registers it under <paramref name="name"/>, and returns it.</summary>
    /// <param name="fieldId">Unique field identifier within the component.</param>
    /// <param name="name">Field name; must be unique within the component.</param>
    /// <param name="type">Field type as stored on the component.</param>
    /// <param name="underlyingType">Element type when <paramref name="type"/> is a collection; otherwise <see cref="FieldType.None"/>.</param>
    /// <param name="offset">Byte offset of the field within the component's storage.</param>
    /// <param name="dotNetType">The backing CLR type of the field.</param>
    /// <returns>The newly created <see cref="Field"/>.</returns>
    /// <exception cref="ArgumentException">A field with the same name already exists on the component.</exception>
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


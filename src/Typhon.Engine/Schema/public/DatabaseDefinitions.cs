// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
public class DatabaseDefinitions
{
    private readonly Dictionary<string, DBComponentDefinition> _components;
    private Dictionary<string, DBObjectDefinition> _objects;
    private readonly Lock _componentLock = new();

    public int ComponentCount => _components.Count;
    public IEnumerable<string> ComponentNames => _components.Keys;

    public DatabaseDefinitions()
    {
        _components = new Dictionary<string, DBComponentDefinition>();
        _objects = new Dictionary<string, DBObjectDefinition>();
    }

    public IDBComponentDefinitionBuilder CreateComponentBuilder(string name, int revision) => new DBComponentDefinitionBuilder(this, name, revision);

    public interface IDBComponentDefinitionBuilder
    {
        IDbComponentFieldDefinitionBuilder WithField<T>(int fieldId, string name, int offset) where T : unmanaged;
        void Build();
        IDBComponentDefinitionBuilder WithPOCO<T>();
    }

    public interface IDbComponentFieldDefinitionBuilder : IDBComponentDefinitionBuilder
    {
        IDbComponentFieldDefinitionBuilder IsStatic();
        IDbComponentFieldDefinitionBuilder IsArray(int length);
    }

    class DBComponentDefinitionBuilder : IDBComponentDefinitionBuilder
    {
        private readonly DatabaseDefinitions _owner;
        protected readonly DBComponentDefinition Component;

        public DBComponentDefinitionBuilder(DatabaseDefinitions owner, string name, int revision, bool allowMultiple = false)
        {
            _owner = owner;
            Component = new DBComponentDefinition(name, revision, allowMultiple);
        }

        protected DBComponentDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component)
        {
            _owner = owner;
            Component = component;
        }

        public IDBComponentDefinitionBuilder WithPOCO<T>()
        {
            Component.POCOType = typeof(T);
            return this;
        }

        public IDbComponentFieldDefinitionBuilder WithField<T>(int fieldId, string name, int offset) where T : unmanaged 
            => new DBComponentFieldDefinitionBuilder(_owner, Component, fieldId, name, typeof(T), offset);

        public void Build()
        {
            Component.Build();
            _owner.AddComponent(Component);
        }
    }

    public void AddComponent(DBComponentDefinition component)
    {
        lock (_componentLock)
        {
            if (!_components.TryAdd(component.FullName, component))
            {
                throw new ArgumentException($"The component name '{component.Name}' is already taken", nameof(component));
            }
        }
    }

    class DBComponentFieldDefinitionBuilder : DBComponentDefinitionBuilder, IDbComponentFieldDefinitionBuilder
    {
        private readonly DBComponentDefinition.Field _field;

        public IDbComponentFieldDefinitionBuilder IsStatic()
        {
            _field.IsStatic = true;
            return this;
        }

        public IDbComponentFieldDefinitionBuilder IsArray(int length)
        {
            _field.ArrayLength = length;
            return this;
        }

        internal DBComponentFieldDefinitionBuilder(DatabaseDefinitions owner, DBComponentDefinition component, int fieldId, string fieldName,
            Type type, int offset) : base(owner, component)
        {
            var (fieldType, underType) = DatabaseSchemaExtensions.FromType(type);
            DBComponentDefinition.Field.CheckName(fieldName);
            DBComponentDefinition.Field.CheckType(fieldType);
            _field = Component.CreateField(fieldId, fieldName, fieldType, underType, offset, type);
        }

    }

    public DBComponentDefinition GetComponent(string componentName, int revision) => _components.GetValueOrDefault(DBComponentDefinition.FormatFullName(componentName, revision));

    public DBComponentDefinition CreateFromAccessor<T>() where T : unmanaged => CreateFromAccessor<T>(null);

    internal DBComponentDefinition CreateFromAccessor<T>(FieldIdResolver resolver) where T : unmanaged => CreateFromAccessor(typeof(T), resolver);

    /// <summary>
    /// Non-generic overload for dry-run validation where the component type is known only at runtime.
    /// </summary>
    internal DBComponentDefinition CreateFromAccessor(Type t, FieldIdResolver resolver = null)
    {

        var ca = t.GetCustomAttribute<ComponentAttribute>();
        if (ca == null)
        {
            throw new InvalidOperationException($"Missing the ComponentAttribute on the type {t} declaration");
        }

        var compDef = new DBComponentDefinition(ca.Name ?? t.Name, ca.Revision, ca.AllowMultiple, ca.StorageMode) { POCOType = t };

        lock (_componentLock)
        {
            if (_components.TryGetValue(compDef.FullName, out _))
            {
                return null;
            }
        }

        var members = t.GetFields();

        // Validate PreviousName uniqueness: no two runtime fields may claim the same PreviousName
        if (resolver != null)
        {
            var previousNames = new HashSet<string>();
            foreach (var fi in members)
            {
                if (fi.IsStatic)
                {
                    continue;
                }

                var fattr = fi.GetCustomAttribute<FieldAttribute>();
                if (fattr?.PreviousName != null && !previousNames.Add(fattr.PreviousName))
                {
                    throw new InvalidOperationException($"Duplicate PreviousName '{fattr.PreviousName}' declared on multiple fields in component '{ca.Name}'.");
                }
            }
        }

        var fieldId = 0;
        foreach (var fieldInfo in members)
        {
            if (fieldInfo.IsStatic)
            {
                continue;
            }

            var fa = fieldInfo.GetCustomAttribute<FieldAttribute>();
            var ia = fieldInfo.GetCustomAttribute<IndexAttribute>();

            var (fieldType, fieldUnderlyingType) = DatabaseSchemaExtensions.FromType(fieldInfo.FieldType);
            if (fieldType == FieldType.None)
            {
                continue;
            }

            // Name of the field is by default the C# member name, or the one specified by the FieldAttribute
            var fieldName = fa?.Name ?? fieldInfo.Name;
            var fieldOffset = Marshal.OffsetOf(t, fieldInfo.Name).ToInt32();
            var resolvedId = resolver?.ResolveFieldId(fieldName, fa?.PreviousName, fa?.FieldId) ?? (fa?.FieldId ?? fieldId++);

            var field = compDef.CreateField(resolvedId, fieldName, fieldType, fieldUnderlyingType, fieldOffset, fieldInfo.FieldType);

            // Foreign key processing
            var fka = fieldInfo.GetCustomAttribute<ForeignKeyAttribute>();
            if (fka != null)
            {
                if (fieldType != FieldType.Long)
                {
                    throw new InvalidOperationException($"[ForeignKey] on field '{fieldName}' requires type long, but found {fieldType}.");
                }
                field.IsForeignKey = true;
                field.ForeignKeyTargetType = fka.TargetComponentType;
            }

            // Spatial index processing
            var spa = fieldInfo.GetCustomAttribute<SpatialIndexAttribute>();
            if (spa != null)
            {
                if (!IsSpatialFieldType(field.Type))
                {
                    throw new InvalidOperationException($"[SpatialIndex] on field '{field.Name}' requires a spatial type (AABB/BSphere), but found {field.Type}.");
                }
                if (spa.Margin < 0)
                {
                    throw new InvalidOperationException($"[SpatialIndex] margin must be non-negative on field '{field.Name}'.");
                }
                field.HasSpatialIndex = true;
                field.SpatialMargin = spa.Margin;
                field.SpatialCellSize = spa.CellSize;
                field.SpatialMode = spa.Mode;
                field.SpatialCategory = spa.Category;
                field.SpatialFieldType = MapToSpatialFieldType(field.Type);
            }

            // Index related data
            if (ia == null)
            {
                continue;
            }

            field.HasIndex = true;
            field.IndexAllowMultiple = ia.AllowMultiple;
            field.IsIndexAuto = false;
        }

        // Validate spatial index constraints at component level
        {
            int spatialCount = 0;
            foreach (var kvp in compDef.FieldsByName)
            {
                if (kvp.Value.HasSpatialIndex)
                {
                    spatialCount++;
                }
            }
            if (spatialCount > 1)
            {
                throw new InvalidOperationException($"Component '{ca.Name ?? t.Name}' has {spatialCount} [SpatialIndex] fields, but at most one is allowed.");
            }
            if (spatialCount > 0 && ca.StorageMode == StorageMode.Transient)
            {
                throw new InvalidOperationException($"[SpatialIndex] is not supported on Transient component '{ca.Name ?? t.Name}'.");
            }
        }

        resolver?.Complete();

        compDef.Build();

        lock (_componentLock)
        {
            if (!_components.TryAdd(compDef.FullName, compDef))
            {
                return null;
            }
        }

        return compDef;
    }

    private static bool IsSpatialFieldType(FieldType type) => type is FieldType.AABB2F or FieldType.AABB3F or FieldType.BSphere2F or FieldType.BSphere3F
        or FieldType.AABB2D or FieldType.AABB3D or FieldType.BSphere2D or FieldType.BSphere3D;

    private static SpatialFieldType MapToSpatialFieldType(FieldType type) => type switch
    {
        FieldType.AABB2F => SpatialFieldType.AABB2F,
        FieldType.AABB3F => SpatialFieldType.AABB3F,
        FieldType.BSphere2F => SpatialFieldType.BSphere2F,
        FieldType.BSphere3F => SpatialFieldType.BSphere3F,
        FieldType.AABB2D => SpatialFieldType.AABB2D,
        FieldType.AABB3D => SpatialFieldType.AABB3D,
        FieldType.BSphere2D => SpatialFieldType.BSphere2D,
        FieldType.BSphere3D => SpatialFieldType.BSphere3D,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a spatial field type")
    };
}
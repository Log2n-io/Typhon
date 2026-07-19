// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Registry of component definitions for a database. Definitions are keyed by full name (name plus revision) and built either from
/// <c>[Component]</c>-annotated structs via <see cref="CreateFromAccessor{T}()"/> or explicitly via the fluent <see cref="CreateComponentBuilder"/>. Thread-safe.
/// </summary>
[PublicAPI]
public class DatabaseDefinitions
{
    private readonly Dictionary<string, DBComponentDefinition> _components;
    private Dictionary<string, DBObjectDefinition> _objects;
    private readonly Lock _componentLock = new();

    /// <summary>Number of registered component definitions.</summary>
    public int ComponentCount => _components.Count;

    /// <summary>Full names (see <see cref="DBComponentDefinition.FullName"/>) of all registered component definitions.</summary>
    public IEnumerable<string> ComponentNames => _components.Keys;

    /// <summary>Creates an empty definitions registry.</summary>
    public DatabaseDefinitions()
    {
        _components = new Dictionary<string, DBComponentDefinition>();
        _objects = new Dictionary<string, DBObjectDefinition>();
    }

    /// <summary>Starts building a component definition of the given name and revision fluently. Call <see cref="IDBComponentDefinitionBuilder.Build"/> to register it.</summary>
    /// <param name="name">Component name.</param>
    /// <param name="revision">Schema revision.</param>
    /// <returns>A builder for adding fields and a POCO type.</returns>
    public IDBComponentDefinitionBuilder CreateComponentBuilder(string name, int revision) => new DBComponentDefinitionBuilder(this, name, revision);

    /// <summary>Fluent builder for a component definition.</summary>
    public interface IDBComponentDefinitionBuilder
    {
        /// <summary>Adds a field of unmanaged type <typeparamref name="T"/> at the given byte offset.</summary>
        /// <typeparam name="T">The field's unmanaged CLR type.</typeparam>
        /// <param name="fieldId">Unique field identifier within the component.</param>
        /// <param name="name">Field name.</param>
        /// <param name="offset">Byte offset of the field within the component's storage.</param>
        /// <returns>A field-scoped builder for further per-field configuration.</returns>
        IDbComponentFieldDefinitionBuilder WithField<T>(int fieldId, string name, int offset) where T : unmanaged;

        /// <summary>Finalizes the definition and registers it on the owning <see cref="DatabaseDefinitions"/>.</summary>
        void Build();

        /// <summary>Associates a POCO type <typeparamref name="T"/> with the component being built.</summary>
        /// <typeparam name="T">The POCO type.</typeparam>
        /// <returns>This builder.</returns>
        IDBComponentDefinitionBuilder WithPOCO<T>();
    }

    /// <summary>Field-scoped extension of <see cref="IDBComponentDefinitionBuilder"/> for configuring the most recently added field.</summary>
    public interface IDbComponentFieldDefinitionBuilder : IDBComponentDefinitionBuilder
    {
        /// <summary>Marks the current field static (excluded from per-entity storage and the FieldId layout).</summary>
        /// <returns>This builder.</returns>
        IDbComponentFieldDefinitionBuilder IsStatic();

        /// <summary>Marks the current field a fixed-length array of the given element count.</summary>
        /// <param name="length">Number of array elements.</param>
        /// <returns>This builder.</returns>
        IDbComponentFieldDefinitionBuilder IsArray(int length);
    }

    class DBComponentDefinitionBuilder : IDBComponentDefinitionBuilder
    {
        private readonly DatabaseDefinitions _owner;
        protected readonly DBComponentDefinition Component;

        public DBComponentDefinitionBuilder(DatabaseDefinitions owner, string name, int revision)
        {
            _owner = owner;
            Component = new DBComponentDefinition(name, revision);
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

    /// <summary>Registers an already-built component definition under its <see cref="DBComponentDefinition.FullName"/>. Thread-safe.</summary>
    /// <param name="component">The component definition to register.</param>
    /// <exception cref="ArgumentException">A component with the same full name is already registered.</exception>
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

    /// <summary>Returns the registered definition for the given name and revision, or null if none is registered.</summary>
    /// <param name="componentName">Component name.</param>
    /// <param name="revision">Schema revision.</param>
    /// <returns>The matching <see cref="DBComponentDefinition"/>, or null.</returns>
    public DBComponentDefinition GetComponent(string componentName, int revision) => _components.GetValueOrDefault(DBComponentDefinition.FormatFullName(componentName, revision));

    /// <summary>
    /// Builds and registers a component definition by reflecting over the <c>[Component]</c>-annotated unmanaged struct <typeparamref name="T"/>, reading its
    /// fields and any <c>[Field]</c>, <c>[Index]</c>, <c>[SpatialIndex]</c>, and <c>[ForeignKey]</c> attributes.
    /// </summary>
    /// <typeparam name="T">The component struct type.</typeparam>
    /// <returns>The built definition, or null if a definition with the same full name was already registered.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> lacks <see cref="ComponentAttribute"/>, or an attribute constraint is violated (e.g. a non-<c>long</c> foreign key, an
    /// invalid spatial field, or more than one spatial index).
    /// </exception>
    public DBComponentDefinition CreateFromAccessor<T>() where T : unmanaged => CreateFromAccessor<T>(null);

    internal DBComponentDefinition CreateFromAccessor<T>(FieldIdResolver resolver) where T : unmanaged
    {
        // Reflection-free fast path: a source-generated component registered its schema (pure data) via its assembly's [ModuleInitializer], into the
        // schema-contract-level GeneratedSchemaRegistry. We look it up by CLR type — the schema no longer lives on the struct as an interface impl, so components
        // need not be `partial` (feature #514 Phase 5). A component absent from the registry (hand-authored / system schema, or a schema-only assembly whose
        // module-init did not run) falls through to the runtime-reflection build below.
        if (GeneratedSchemaRegistry.TryGetComponentSpec(typeof(T), out var spec))
        {
            return BuildFromSpec(spec, typeof(T), resolver);
        }

        return CreateFromAccessor(typeof(T), resolver);
    }

    /// <summary>
    /// Non-generic overload for dry-run validation where the component type is known only at runtime. Reflects the type's <c>[Component]</c>/<c>[Field]</c>/
    /// <c>[Index]</c>/<c>[SpatialIndex]</c>/<c>[ForeignKey]</c> metadata into a <see cref="ComponentSchemaSpec"/> and builds it through the shared core.
    /// </summary>
    internal DBComponentDefinition CreateFromAccessor(Type t, FieldIdResolver resolver = null) => BuildFromSpec(ReflectComponentSpec(t), t, resolver);

    /// <summary>
    /// Reflects a <c>[Component]</c>-annotated struct into a pure-data <see cref="ComponentSchemaSpec"/>. This is the ONLY place the schema-build path touches
    /// <c>GetFields</c>/<c>GetCustomAttribute</c>; source-generated components bypass it entirely by supplying their spec directly.
    /// </summary>
    private static ComponentSchemaSpec ReflectComponentSpec(Type t)
    {
        var ca = t.GetCustomAttribute<ComponentAttribute>();
        if (ca == null)
        {
            throw new InvalidOperationException($"Missing the ComponentAttribute on the type {t} declaration");
        }

        var members = t.GetFields();
        var fields = new List<ComponentFieldSpec>(members.Length);

        foreach (var fieldInfo in members)
        {
            if (fieldInfo.IsStatic)
            {
                continue;
            }

            var (fieldType, _) = DatabaseSchemaExtensions.FromType(fieldInfo.FieldType);
            if (fieldType == FieldType.None)
            {
                continue;
            }

            var fa = fieldInfo.GetCustomAttribute<FieldAttribute>();
            var ia = fieldInfo.GetCustomAttribute<IndexAttribute>();
            var fka = fieldInfo.GetCustomAttribute<ForeignKeyAttribute>();
            var spa = fieldInfo.GetCustomAttribute<SpatialIndexAttribute>();

            fields.Add(new ComponentFieldSpec(
                name: fa?.Name ?? fieldInfo.Name,
                dotNetType: fieldInfo.FieldType,
                offset: Marshal.OffsetOf(t, fieldInfo.Name).ToInt32(),
                previousName: fa?.PreviousName,
                explicitFieldId: fa?.FieldId,
                isForeignKey: fka != null,
                foreignKeyTargetType: fka?.TargetComponentType,
                hasSpatialIndex: spa != null,
                spatialMargin: spa?.Margin ?? 0f,
                spatialCellSize: spa?.CellSize ?? 0f,
                spatialMode: spa?.Mode ?? SpatialMode.Dynamic,
                spatialCategory: spa?.Category ?? uint.MaxValue,
                hasIndex: ia != null,
                indexAllowMultiple: ia?.AllowMultiple ?? false));
        }

        return new ComponentSchemaSpec(ca.Name ?? t.Name, ca.Revision, fields.ToArray(), ca.StorageMode, ca.DefaultDiscipline);
    }

    /// <summary>
    /// Builds and registers a component definition from a pure-data <see cref="ComponentSchemaSpec"/>. This is the single build core shared by the reflection
    /// path (<see cref="ReflectComponentSpec"/>) and source-generated components, so both produce byte-identical definitions. Applies field-id resolution
    /// (schema migration) via <paramref name="resolver"/> exactly as reflection did. Thread-safe; returns null when a definition with the same full name is
    /// already registered.
    /// </summary>
    /// <param name="spec">The component's schema shape.</param>
    /// <param name="pocoType">The CLR struct backing the component, recorded as <see cref="DBComponentDefinition.POCOType"/>.</param>
    /// <param name="resolver">Optional field-id resolver reconciling runtime fields against a persisted schema; null for a fresh component.</param>
    internal DBComponentDefinition BuildFromSpec(in ComponentSchemaSpec spec, Type pocoType, FieldIdResolver resolver)
    {
        var compDef = new DBComponentDefinition(spec.Name, spec.Revision, spec.StorageMode, spec.DefaultDiscipline) { POCOType = pocoType };

        lock (_componentLock)
        {
            if (_components.ContainsKey(compDef.FullName))
            {
                return null;
            }
        }

        var specFields = spec.Fields;

        // Validate PreviousName uniqueness: no two runtime fields may claim the same PreviousName (only meaningful when reconciling against a persisted schema).
        if (resolver != null)
        {
            var previousNames = new HashSet<string>();
            foreach (var f in specFields)
            {
                if (f.IsStatic)
                {
                    continue;
                }

                if (f.PreviousName != null && !previousNames.Add(f.PreviousName))
                {
                    throw new InvalidOperationException($"Duplicate PreviousName '{f.PreviousName}' declared on multiple fields in component '{spec.Name}'.");
                }
            }
        }

        var fieldId = 0;
        foreach (var f in specFields)
        {
            var (fieldType, fieldUnderlyingType) = DatabaseSchemaExtensions.FromType(f.DotNetType);
            if (fieldType == FieldType.None)
            {
                continue;
            }

            var resolvedId = resolver?.ResolveFieldId(f.Name, f.PreviousName, f.ExplicitFieldId) ?? (f.ExplicitFieldId ?? fieldId++);
            var field = compDef.CreateField(resolvedId, f.Name, fieldType, fieldUnderlyingType, f.Offset, f.DotNetType);

            if (f.IsStatic)
            {
                field.IsStatic = true;
            }

            if (f.ArrayLength > 0)
            {
                field.ArrayLength = f.ArrayLength;
            }

            // Foreign key processing
            if (f.IsForeignKey)
            {
                if (fieldType != FieldType.Long)
                {
                    throw new InvalidOperationException($"[ForeignKey] on field '{f.Name}' requires type long, but found {fieldType}.");
                }
                field.IsForeignKey = true;
                field.ForeignKeyTargetType = f.ForeignKeyTargetType;
            }

            // Spatial index processing
            if (f.HasSpatialIndex)
            {
                if (!IsSpatialFieldType(field.Type))
                {
                    throw new InvalidOperationException($"[SpatialIndex] on field '{field.Name}' requires a spatial type (AABB/BSphere), but found {field.Type}.");
                }
                if (f.SpatialMargin < 0)
                {
                    throw new InvalidOperationException($"[SpatialIndex] margin must be non-negative on field '{field.Name}'.");
                }
                field.HasSpatialIndex = true;
                field.SpatialMargin = f.SpatialMargin;
                field.SpatialCellSize = f.SpatialCellSize;
                field.SpatialMode = f.SpatialMode;
                field.SpatialCategory = f.SpatialCategory;
                field.SpatialFieldType = MapToSpatialFieldType(field.Type);
            }

            // Index processing
            if (f.HasIndex)
            {
                field.HasIndex = true;
                field.IndexAllowMultiple = f.IndexAllowMultiple;
                field.IsIndexAuto = false;
            }
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
                throw new InvalidOperationException($"Component '{spec.Name}' has {spatialCount} [SpatialIndex] fields, but at most one is allowed.");
            }
            if (spatialCount > 0 && spec.StorageMode == StorageMode.Transient)
            {
                throw new InvalidOperationException($"[SpatialIndex] is not supported on Transient component '{spec.Name}'.");
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
using System.Collections.Generic;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Top-level report of a database's persisted schema state.
/// Returned by <see cref="DatabaseSchema.Inspect"/> for operational tooling.
/// </summary>
[PublicAPI]
public class DatabaseSchemaReport
{
    /// <summary>Database name as recorded in the file header.</summary>
    public string DatabaseName { get; init; }

    /// <summary>On-disk file format revision of the database.</summary>
    public int DatabaseFormatRevision { get; init; }

    /// <summary>Revision of the engine's internal (system) schema the database was written with.</summary>
    public int SystemSchemaRevision { get; init; }

    /// <summary>User-defined schema version persisted for the application's own use.</summary>
    public int UserSchemaVersion { get; init; }

    /// <summary>One report per persisted user component.</summary>
    public IReadOnlyList<ComponentSchemaReport> Components { get; init; }
}

/// <summary>
/// Per-component schema report showing persisted metadata, storage layout, and entity count.
/// </summary>
[PublicAPI]
public class ComponentSchemaReport
{
    /// <summary>Component (schema) name.</summary>
    public string Name { get; init; }

    /// <summary>Persisted schema revision of the component.</summary>
    public int Revision { get; init; }

    /// <summary>Byte size of the component's field data, excluding per-chunk overhead.</summary>
    public int StorageSize { get; init; }

    /// <summary>Bytes of per-chunk overhead stored alongside the field data (inline entityPK and multi-index back-references).</summary>
    public int Overhead { get; init; }

    /// <summary>Number of live entities carrying this component.</summary>
    public int EntityCount { get; init; }

    /// <summary>One report per non-static field.</summary>
    public IReadOnlyList<FieldSchemaReport> Fields { get; init; }

    /// <summary>One report per indexed field.</summary>
    public IReadOnlyList<IndexSchemaReport> Indexes { get; init; }
}

/// <summary>
/// Per-field schema report with FieldId, type, offset, and size information.
/// </summary>
[PublicAPI]
public class FieldSchemaReport
{
    /// <summary>Field name.</summary>
    public string Name { get; init; }

    /// <summary>Unique field identifier within the component.</summary>
    public int FieldId { get; init; }

    /// <summary>Field type as stored on the component.</summary>
    public FieldType Type { get; init; }

    /// <summary>Byte offset of the field within the component's storage.</summary>
    public int Offset { get; init; }

    /// <summary>Total bytes the field occupies in component storage (including array length, if any).</summary>
    public int Size { get; init; }

    /// <summary>True when the field carries an index.</summary>
    public bool HasIndex { get; init; }

    /// <summary>True when the field's index permits multiple entities per key (non-unique).</summary>
    public bool IndexAllowMultiple { get; init; }
}

/// <summary>
/// Per-index schema report.
/// </summary>
[PublicAPI]
public class IndexSchemaReport
{
    /// <summary>Name of the indexed field.</summary>
    public string FieldName { get; init; }

    /// <summary>Unique field identifier of the indexed field.</summary>
    public int FieldId { get; init; }

    /// <summary>True when the index permits multiple entities per key (non-unique).</summary>
    public bool AllowMultiple { get; init; }
}

/// <summary>
/// Allows callers to register component types and migrations for dry-run validation.
/// </summary>
[PublicAPI]
public interface ISchemaRegistrar
{
    /// <summary>Registers component type <typeparamref name="T"/> for validation against the persisted schema.</summary>
    /// <typeparam name="T">The component struct type.</typeparam>
    void RegisterComponent<T>() where T : unmanaged;

    /// <summary>Registers a strongly-typed migration between two revisions of the same component.</summary>
    /// <typeparam name="TOld">The source revision's struct type.</typeparam>
    /// <typeparam name="TNew">The target revision's struct type.</typeparam>
    /// <param name="func">Delegate that transforms an old-revision value into a new-revision value.</param>
    void RegisterMigration<TOld, TNew>(MigrationFunc<TOld, TNew> func) where TOld : unmanaged where TNew : unmanaged;

    /// <summary>Registers a byte-level migration for a component whose old struct type is no longer available in code.</summary>
    /// <param name="name">Component (schema) name.</param>
    /// <param name="fromRev">Source revision.</param>
    /// <param name="toRev">Target revision.</param>
    /// <param name="oldSize">Byte size of the old component layout.</param>
    /// <param name="newSize">Byte size of the new component layout.</param>
    /// <param name="func">Delegate that transforms the raw old bytes into the raw new bytes.</param>
    void RegisterByteMigration(string name, int fromRev, int toRev, int oldSize, int newSize, ByteMigrationFunc func);
}

/// <summary>
/// Result of a dry-run schema evolution validation.
/// </summary>
[PublicAPI]
public class EvolutionValidationResult
{
    /// <summary>True when every component either matches or has a viable migration path; false when any breaking change lacks one.</summary>
    public bool IsValid { get; init; }

    /// <summary>Per-component diff and migration results.</summary>
    public IReadOnlyList<EvolutionComponentResult> Components { get; init; }

    /// <summary>Human-readable messages describing each blocking problem; empty when <see cref="IsValid"/> is <c>true</c>.</summary>
    public IReadOnlyList<string> Errors { get; init; }
}

/// <summary>
/// Per-component result of a dry-run evolution validation.
/// </summary>
[PublicAPI]
public class EvolutionComponentResult
{
    /// <summary>Component (schema) name.</summary>
    public string ComponentName { get; init; }

    /// <summary>Computed diff against the persisted schema, or null when the component is new (no persisted data).</summary>
    public SchemaDiff Diff { get; init; }

    /// <summary>True when the change requires a data migration to apply safely.</summary>
    public bool NeedsMigration { get; init; }

    /// <summary>True when a migration path exists (or none is needed); false when breaking changes have no registered migration.</summary>
    public bool HasMigrationPath { get; init; }

    /// <summary>Short human-readable description of the change (e.g. "Identical", "New component", or a diff summary).</summary>
    public string Summary { get; init; }
}

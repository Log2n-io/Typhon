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
    public string DatabaseName { get; init; }
    public int DatabaseFormatRevision { get; init; }
    public int SystemSchemaRevision { get; init; }
    public int UserSchemaVersion { get; init; }
    public IReadOnlyList<ComponentSchemaReport> Components { get; init; }
}

/// <summary>
/// Per-component schema report showing persisted metadata, storage layout, and entity count.
/// </summary>
[PublicAPI]
public class ComponentSchemaReport
{
    public string Name { get; init; }
    public int Revision { get; init; }
    public int StorageSize { get; init; }
    public int Overhead { get; init; }
    public int EntityCount { get; init; }
    public IReadOnlyList<FieldSchemaReport> Fields { get; init; }
    public IReadOnlyList<IndexSchemaReport> Indexes { get; init; }
}

/// <summary>
/// Per-field schema report with FieldId, type, offset, and size information.
/// </summary>
[PublicAPI]
public class FieldSchemaReport
{
    public string Name { get; init; }
    public int FieldId { get; init; }
    public FieldType Type { get; init; }
    public int Offset { get; init; }
    public int Size { get; init; }
    public bool HasIndex { get; init; }
    public bool IndexAllowMultiple { get; init; }
}

/// <summary>
/// Per-index schema report.
/// </summary>
[PublicAPI]
public class IndexSchemaReport
{
    public string FieldName { get; init; }
    public int FieldId { get; init; }
    public bool AllowMultiple { get; init; }
}

/// <summary>
/// Allows callers to register component types and migrations for dry-run validation.
/// </summary>
[PublicAPI]
public interface ISchemaRegistrar
{
    void RegisterComponent<T>() where T : unmanaged;
    void RegisterMigration<TOld, TNew>(MigrationFunc<TOld, TNew> func) where TOld : unmanaged where TNew : unmanaged;
    void RegisterByteMigration(string name, int fromRev, int toRev, int oldSize, int newSize, ByteMigrationFunc func);
}

/// <summary>
/// Result of a dry-run schema evolution validation.
/// </summary>
[PublicAPI]
public class EvolutionValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<EvolutionComponentResult> Components { get; init; }
    public IReadOnlyList<string> Errors { get; init; }
}

/// <summary>
/// Per-component result of a dry-run evolution validation.
/// </summary>
[PublicAPI]
public class EvolutionComponentResult
{
    public string ComponentName { get; init; }
    public SchemaDiff Diff { get; init; }
    public bool NeedsMigration { get; init; }
    public bool HasMigrationPath { get; init; }
    public string Summary { get; init; }
}

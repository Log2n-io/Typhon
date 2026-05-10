using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Static entry point for offline schema inspection and dry-run validation.
/// Opens the database read-only (no user component registration) to extract persisted metadata.
/// </summary>
[PublicAPI]
public static class DatabaseSchema
{
    /// <summary>
    /// Inspects a database file and returns a report of its persisted schema state. Opens a temporary engine instance, reads system tables, and disposes.
    /// </summary>
    /// <param name="path">Path to the database file (with or without .bin extension).</param>
    /// <returns>A <see cref="DatabaseSchemaReport"/> with all persisted component and field metadata.</returns>
    public static DatabaseSchemaReport Inspect(string path)
    {
        var (databaseName, directory) = ResolvePath(path);

        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.None))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(options =>
            {
                options.DatabaseName = databaseName;
                options.DatabaseDirectory = directory;
            })
            .AddMemoryAllocator()
            .AddDatabaseEngine();

        var sp = services.BuildServiceProvider();
        try
        {
            var engine = sp.GetRequiredService<DatabaseEngine>();

            // Read version/schema fields from bootstrap + header
            var bootstrap = engine.MMF.Bootstrap;
            int sysSchemaRevision = bootstrap.GetInt(DatabaseEngine.BK_SystemSchemaRevision);
            int userSchemaVersion = bootstrap.GetInt(DatabaseEngine.BK_UserSchemaVersion);

            int dbFormatRevision;
            string dbName;
            unsafe
            {
                using var guard = EpochGuard.Enter(engine.EpochManager);
                engine.MMF.RequestPageEpoch(0, guard.Epoch, out var memPageIdx);
                var page = engine.MMF.GetPage(memPageIdx);
                ref var h = ref page.StructAt<RootFileHeader>(PagedMMF.PageBaseHeaderSize);
                dbFormatRevision = h.DatabaseFormatRevision;
                dbName = h.DatabaseNameString;
            }

            // Build component reports from persisted data
            var components = new List<ComponentSchemaReport>();
            var persisted = engine.PersistedComponents;
            var fieldsByComp = engine.PersistedFieldsByComponent;

            if (persisted != null)
            {
                foreach (var kvp in persisted)
                {
                    var schemaName = kvp.Key;
                    var comp = kvp.Value.Comp;

                    // Build field reports
                    var fields = new List<FieldSchemaReport>();
                    var indexes = new List<IndexSchemaReport>();

                    if (fieldsByComp != null && fieldsByComp.TryGetValue(schemaName, out var persistedFields))
                    {
                        foreach (var f in persistedFields)
                        {
                            if (f.IsStatic)
                            {
                                continue;
                            }

                            fields.Add(new FieldSchemaReport
                            {
                                Name = f.Name.AsString,
                                FieldId = f.FieldId,
                                Type = f.Type,
                                Offset = f.OffsetInComponentStorage,
                                Size = f.SizeInComponentStorage,
                                HasIndex = f.HasIndex,
                                IndexAllowMultiple = f.IndexAllowMultiple,
                            });

                            if (f.HasIndex)
                            {
                                indexes.Add(new IndexSchemaReport
                                {
                                    FieldName = f.Name.AsString,
                                    FieldId = f.FieldId,
                                    AllowMultiple = f.IndexAllowMultiple,
                                });
                            }
                        }
                    }

                    // Get entity count from the component segment (subtract 1 for the reserved sentinel chunk 0)
                    var entityCount = 0;
                    if (comp.ComponentSPI != 0)
                    {
                        var stride = comp.CompSize + comp.CompOverhead;
                        var seg = engine.MMF.GetOrLoadChunkBasedSegment(comp.ComponentSPI, stride);
                        entityCount = Math.Max(0, seg.AllocatedChunkCount - 1);
                    }

                    components.Add(new ComponentSchemaReport
                    {
                        Name = schemaName,
                        Revision = comp.SchemaRevision,
                        StorageSize = comp.CompSize,
                        Overhead = comp.CompOverhead,
                        EntityCount = entityCount,
                        Fields = fields,
                        Indexes = indexes,
                    });
                }
            }

            // Dispose engine before service provider
            engine.Dispose();

            return new DatabaseSchemaReport
            {
                DatabaseName = dbName,
                DatabaseFormatRevision = dbFormatRevision,
                SystemSchemaRevision = sysSchemaRevision,
                UserSchemaVersion = userSchemaVersion,
                Components = components,
            };
        }
        finally
        {
            (sp as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Performs a dry-run validation of schema evolution against a persisted database.
    /// Opens a temporary engine, captures user component registrations via <paramref name="configure"/>, computes diffs, and checks migration paths — without
    /// actually modifying the database.
    /// </summary>
    /// <param name="path">Path to the database file.</param>
    /// <param name="configure">Action that registers components and migrations on the registrar.</param>
    /// <returns>An <see cref="EvolutionValidationResult"/> with per-component diff results.</returns>
    public static EvolutionValidationResult ValidateEvolution(string path, Action<ISchemaRegistrar> configure)
    {
        var (databaseName, directory) = ResolvePath(path);

        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.None))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(options =>
            {
                options.DatabaseName = databaseName;
                options.DatabaseDirectory = directory;
            })
            .AddMemoryAllocator()
            .AddDatabaseEngine();

        var sp = services.BuildServiceProvider();
        try
        {
            var engine = sp.GetRequiredService<DatabaseEngine>();

            // Capture user registrations
            var registrar = new DryRunRegistrar();
            configure(registrar);

            var componentResults = new List<EvolutionComponentResult>();
            var errors = new List<string>();
            var allValid = true;

            foreach (var componentType in registrar.ComponentTypes)
            {
                var componentAttr = componentType.GetCustomAttribute<ComponentAttribute>();
                var schemaName = componentAttr?.Name ?? componentType.Name;

                // Build definition with resolver if persisted data exists
                FieldIdResolver resolver = null;
                FieldR1[] persistedFields = null;
                if (engine.PersistedFieldsByComponent != null &&
                    engine.PersistedFieldsByComponent.TryGetValue(schemaName, out persistedFields))
                {
                    resolver = new FieldIdResolver(persistedFields);
                }

                var definition = engine.DBD.CreateFromAccessor(componentType, resolver);
                if (definition == null)
                {
                    componentResults.Add(new EvolutionComponentResult
                    {
                        ComponentName = schemaName, Summary = "New component (no persisted data)",
                    });
                    continue;
                }

                if (engine.PersistedComponents == null || !engine.PersistedComponents.TryGetValue(schemaName, out var persisted))
                {
                    componentResults.Add(new EvolutionComponentResult
                    {
                        ComponentName = schemaName, Summary = "New component (no persisted data)",
                    });
                    continue;
                }

                // Compute diff
                var diff = SchemaValidator.ComputeDiff(
                    schemaName, persistedFields ?? [], persisted.Comp, definition, resolver?.Renames ?? (IReadOnlyList<(string, string, int)>)[]);

                var needsMigration = false;
                var hasMigrationPath = true;

                if (diff.HasBreakingChanges)
                {
                    needsMigration = true;
                    var targetRevision = componentAttr?.Revision ?? 1;
                    var chain = registrar.Registry.GetChain(schemaName, persisted.Comp.SchemaRevision, targetRevision);
                    hasMigrationPath = chain != null;

                    if (!hasMigrationPath)
                    {
                        allValid = false;
                        errors.Add($"Component '{schemaName}': breaking changes with no migration path (rev {persisted.Comp.SchemaRevision} → {targetRevision})");
                    }
                }
                else if (!diff.IsIdentical)
                {
                    var oldStride = persisted.Comp.CompSize + persisted.Comp.CompOverhead;
                    var newStride = definition.ComponentStorageTotalSize;
                    needsMigration = SchemaEvolutionEngine.NeedsMigration(diff, oldStride, newStride);
                }

                componentResults.Add(new EvolutionComponentResult
                {
                    ComponentName = schemaName,
                    Diff = diff,
                    NeedsMigration = needsMigration,
                    HasMigrationPath = hasMigrationPath,
                    Summary = diff.IsIdentical ? "Identical" : diff.Summary,
                });
            }

            engine.Dispose();

            return new EvolutionValidationResult
            {
                IsValid = allValid,
                Components = componentResults,
                Errors = errors,
            };
        }
        finally
        {
            sp.Dispose();
        }
    }

    /// <summary>
    /// Resolves a user-provided path into database name and directory, matching the ShellSession pattern.
    /// </summary>
    internal static (string DatabaseName, string Directory) ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        var databaseName = Path.GetFileNameWithoutExtension(fullPath);
        Debug.Assert(directory != null, nameof(directory) + " != null");
        return (databaseName, directory);
    }

    /// <summary>
    /// Captures component type registrations and migrations without actually performing them.
    /// </summary>
    private sealed class DryRunRegistrar : ISchemaRegistrar
    {
        public List<Type> ComponentTypes { get; } = new();
        public MigrationRegistry Registry { get; } = new();

        public void RegisterComponent<T>() where T : unmanaged => ComponentTypes.Add(typeof(T));

        public void RegisterMigration<TOld, TNew>(MigrationFunc<TOld, TNew> func) where TOld : unmanaged where TNew : unmanaged => Registry.Register(func);

        public void RegisterByteMigration(string name, int fromRev, int toRev, int oldSize, int newSize, ByteMigrationFunc func) =>
            Registry.RegisterByte(name, fromRev, toRev, oldSize, newSize, func);
    }
}
